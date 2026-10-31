using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OFPlayer.WinUI.Core.Protocol;
using OFPlayer.WinUI.Core.Transport;

namespace OFPlayer.WinUI.Core;

/// <summary>Connection lifecycle states surfaced to the UI.</summary>
public enum NativeHostState
{
    Disconnected,
    Connecting,
    Ready,
    Failed,
    Closed,
}

/// <summary>Client options.</summary>
public sealed record NativeHostClientOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Protocol client for the native host (W1.2/W1.4).
///
/// Invariants:
/// - one read loop owns stdout; responses are dispatched strictly by request id and
///   events are filtered by session id + monotonic sequence (stale events are dropped).
/// - any protocol violation fails closed: pending requests fail, the event stream
///   completes with the error and the transport is terminated - the client never
///   continues with misassociated responses.
/// - timeouts and cancellations abandon the request id (late responses are ignored);
///   the client never re-executes mutations automatically, callers must query state.
/// - DisposeAsync is idempotent and prefers the graceful `host_shutdown` path before
///   falling back to process termination.
/// </summary>
public sealed class NativeHostClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = HostJson.Options;

    private readonly INativeHostTransport transport;
    private readonly NativeHostClientOptions options;
    private readonly ConcurrentDictionary<long, PendingRequest> pending = new();
    private readonly ConcurrentDictionary<long, byte> abandoned = new();
    private readonly Channel<HostEvent> events = Channel.CreateBounded<HostEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private CancellationTokenSource lifetime = new();
    private long nextRequestId;
    private long lastEventSequence;
    private int disposed;
    private int disposeStarted;
    private int readLoopStarted;
    private long lateResponseCount;
    private long staleEventCount;
    private Task? playbackOwnershipRelease;
    private readonly object playbackOwnershipGate = new();
    private Func<Task>? confirmPlaybackOwnershipRelease;

    public NativeHostClient(INativeHostTransport transport, NativeHostClientOptions? options = null)
    {
        this.transport = transport;
        this.options = options ?? new NativeHostClientOptions();
        transport.Exited += OnTransportExited;
    }

    public NativeHostState State { get; private set; } = NativeHostState.Disconnected;

    public HandshakeInfo? Handshake { get; private set; }

    public string? SessionId => Handshake?.SessionId;

    public string? LastError { get; private set; }

    public long DroppedStaleEventCount => Interlocked.Read(ref staleEventCount);
    public long LastEventSequence => Interlocked.Read(ref lastEventSequence);

    public long IgnoredLateResponseCount => Interlocked.Read(ref lateResponseCount);

    public event Action<NativeHostState>? StateChanged;

    public event Action<string>? LogLine;
    /// <summary>Validated events before UI dispatch, for bounded retired-owner receipt observation.</summary>
    public event Action<HostEvent>? EventReceived;
    public Func<CancellationToken, Task<IDisposable?>>? AcquirePlaybackStartLeaseAsync { get; set; }
    public bool PlaybackOwnershipRetiring => Volatile.Read(ref playbackOwnershipRelease) is { IsCompletedSuccessfully: false };
    /// <summary>Do not let a local playback mutation overtake a retired Together owner's restoration.</summary>
    public void FencePlaybackUntil(Task release, Func<Task>? confirmRelease = null)
    {
        lock (playbackOwnershipGate) {
            confirmPlaybackOwnershipRelease = confirmRelease;
            Volatile.Write(ref playbackOwnershipRelease, release);
        }
    }
    private async Task AwaitPlaybackOwnershipAsync(CancellationToken cancel)
    {
        var release = Volatile.Read(ref playbackOwnershipRelease);
        if (release is null) return;
        try { await release.WaitAsync(cancel).ConfigureAwait(false); }
        catch (Exception) when (!cancel.IsCancellationRequested) {
            Task confirmation;
            lock (playbackOwnershipGate) {
                // Only one confirmation for simultaneous local actions; never replay the room write.
                if (!ReferenceEquals(release, playbackOwnershipRelease)) confirmation = playbackOwnershipRelease!;
                else {
                    if (confirmPlaybackOwnershipRelease is null) throw;
                    confirmation = confirmPlaybackOwnershipRelease();
                    Volatile.Write(ref playbackOwnershipRelease, confirmation);
                }
            }
            await confirmation.WaitAsync(cancel).ConfigureAwait(false);
        }
    }
    public Task ConfirmPlaybackOwnershipReleaseAsync(CancellationToken cancel) => AwaitPlaybackOwnershipAsync(cancel);

    /// <summary>Starts the transport and performs the versioned handshake.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (State is NativeHostState.Ready or NativeHostState.Connecting)
        {
            throw new InvalidOperationException($"ConnectAsync cannot run in state {State}.");
        }

        SetState(NativeHostState.Connecting);
        LastError = null;

        try
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);

            if (Interlocked.Exchange(ref readLoopStarted, 1) == 0)
            {
                _ = Task.Run(() => ReadLoopAsync(lifetime.Token));
            }

            var handshakeResponse = await SendAsync("handshake", null, options.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);
            var handshake = handshakeResponse.Deserialize<HandshakeInfo>(JsonOptions)
                ?? throw new HostConnectionException("The native host handshake carried no payload.");

            if (handshake.ProtocolVersion != HostProtocol.Version)
            {
                throw new HostProtocolException(
                    $"The native host speaks protocol v{handshake.ProtocolVersion}, but this build requires v{HostProtocol.Version}.");
            }

            if (string.IsNullOrWhiteSpace(handshake.SessionId))
            {
                throw new HostProtocolException("The native host handshake did not include a session id.");
            }

            Handshake = handshake;
            SetState(NativeHostState.Ready);
        }
        catch (Exception error)
        {
            LastError = error.Message;
            SetState(NativeHostState.Failed);
            FailPending(new HostClosedException($"The native host connection failed: {error.Message}"));
            transport.Kill();
            throw;
        }
    }

    /// <summary>Sends a request and waits for the correlated response.</summary>
    public Task<JsonElement> SendAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken) =>
        SendAsync(command, arguments, options.RequestTimeout, cancellationToken);

    /// <summary>Sends a request with an explicit timeout.</summary>
    public async Task<JsonElement> SendAsync(
        string command,
        object? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var perf = Diagnostics.PerformanceTrace.Begin("rpc." + command);
        if (command.StartsWith("playback_", StringComparison.Ordinal) && command is not ("playback_snapshot" or "playback_session_snapshot" or "playback_list_output_devices"))
            await AwaitPlaybackOwnershipAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new HostClosedException("The native host client was disposed.");
        }

        if (State != NativeHostState.Ready && command != "handshake")
        {
            throw new HostClosedException($"The native host is not ready (state: {State}).");
        }

        using var playbackLease = Playback.MediaStartCommands.RequiresOutput(command)
            && AcquirePlaybackStartLeaseAsync is { } acquire
            ? await acquire(cancellationToken).ConfigureAwait(false) : null;
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new PendingRequest(command, completion, perf?.Id);

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                id,
                command,
                protocolVersion = HostProtocol.Version,
                args = arguments,
            }, JsonOptions);

            try
            {
                // Cancellation can happen after a line reached the child but before
                // FlushAsync completes. Retain that id just like a cancelled wait.
                using (Diagnostics.PerformanceTrace.Begin("rpc.write"))
                    await transport.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
                using (Diagnostics.PerformanceTrace.Begin("rpc.responseWait"))
                    return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                perf?.Outcome("timeout");
                Abandon(id);
                throw new HostRequestTimeoutException(command, timeout);
            }
            catch (OperationCanceledException)
            {
                perf?.Outcome("cancelled");
                Abandon(id);
                throw;
            }
            catch (IOException error)
            {
                FailClosed($"The native host write failed: {error.Message}");
                throw;
            }
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException && error is not HostRequestTimeoutException)
                perf?.Outcome("error");
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>Event stream (playback.changed, playback.progress, playback.ended, session.changed).</summary>
    public IAsyncEnumerable<HostEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
        events.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Idempotent: only the first caller runs the shutdown sequence. The graceful
        // path must run before the disposed flag is set so `host_shutdown` still passes
        // the request guard.
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        if (State == NativeHostState.Ready)
        {
            LogLine?.Invoke("native host dispose: requesting host_shutdown");

            try
            {
                await SendAsync("host_shutdown", null, options.ShutdownTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                using var exitTimeout = new CancellationTokenSource(options.ShutdownTimeout);
                await transport.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                LogLine?.Invoke("native host dispose: graceful shutdown completed");
            }
            catch (Exception exception)
            {
                // Graceful shutdown is best effort; the fallback below terminates the child.
                LogLine?.Invoke($"native host dispose: graceful shutdown skipped ({exception.GetType().Name})");
            }
        }

        Interlocked.Exchange(ref disposed, 1);
        lifetime.Cancel();
        FailPending(new HostClosedException("The native host client was disposed."));
        events.Writer.TryComplete();
        SetState(NativeHostState.Closed);

        try
        {
            using var disposeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await transport.DisposeAsync().AsTask().WaitAsync(disposeTimeout.Token).ConfigureAwait(false);
            LogLine?.Invoke("native host dispose: transport released");
        }
        catch (Exception exception)
        {
            LogLine?.Invoke($"native host dispose: forcing termination ({exception.GetType().Name})");
            transport.Kill();
        }
    }

    private void Abandon(long id)
    {
        // Keep the abandoned id so a late response is discarded instead of being
        // misassociated. The set is bounded to keep long sessions memory-safe.
        abandoned[id] = 0;

        if (abandoned.Count > 1024)
        {
            var smallest = long.MaxValue;

            foreach (var candidate in abandoned.Keys)
            {
                smallest = Math.Min(smallest, candidate);
            }

            abandoned.TryRemove(smallest, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in transport.OutputLines.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                JsonDocument document;

                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException error)
                {
                    FailClosed($"The native host emitted a non-JSON protocol line: {error.Message}", error);
                    return;
                }

                using (document)
                {
                    var root = document.RootElement;

                    if (root.TryGetProperty("event", out _))
                    {
                        await DispatchEventAsync(root, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!root.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.Number ||
                        !idElement.TryGetInt64(out var id))
                    {
                        FailClosed("The native host emitted a response without a numeric id.");
                        return;
                    }

                    DispatchResponse(id, root);
                }
            }

            // stdout closed: the host is gone unless this was a planned dispose.
            if (Volatile.Read(ref disposed) == 0)
            {
                FailPending(new HostClosedException("The native host closed its output stream."));
                events.Writer.TryComplete(new HostClosedException("The native host closed its output stream."));
                SetState(NativeHostState.Failed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Planned shutdown.
        }
        catch (Exception error)
        {
            FailClosed($"The native host read loop failed: {error.Message}", error);
        }
    }

    private void DispatchResponse(long id, JsonElement response)
    {
        if (!pending.TryGetValue(id, out var request))
        {
            if (abandoned.TryRemove(id, out _))
            {
                Interlocked.Increment(ref lateResponseCount);
                return;
            }

            FailClosed($"The native host answered unknown request id {id}.");
            return;
        }

        if (response.TryGetProperty("_performance", out var timing) && timing.ValueKind == JsonValueKind.Object)
        {
            foreach (var (field, metric) in new[] { ("queueMs", "rpc.hostQueueMs"), ("dispatchMs", "rpc.hostDispatchMs") })
                if (timing.TryGetProperty(field, out var value) && value.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0)
                    Diagnostics.PerformanceTrace.Sample(metric, ms, request.TraceId);
        }

        if (response.TryGetProperty("error", out var errorElement))
        {
            var error = errorElement.Deserialize<HostError>(JsonOptions);

            if (error is null || string.IsNullOrWhiteSpace(error.Code) || string.IsNullOrWhiteSpace(error.Message))
            {
                FailClosed($"The native host returned a malformed error for {request.Command} (request {id}).");
                return;
            }

            if (error.Code == HostProtocol.ErrorCodes.ProtocolViolation)
            {
                FailClosed($"The native host reported a protocol violation while answering {request.Command}: {error.Message}");
                return;
            }

            request.Completion.TrySetException(new HostCommandException(error.Code, error.Message, request.Command, id));
            return;
        }

        // Community v1 uses `ok` + `data`; keep the independent host envelope.
        if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("data", out var result))
        {
            FailClosed($"The native host response for {request.Command} carried neither result nor error (request {id}).");
            return;
        }

        request.Completion.TrySetResult(result.Clone());
    }

    private async Task DispatchEventAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        HostEvent? hostEvent;

        try
        {
            hostEvent = envelope.Deserialize<HostEvent>(JsonOptions);
        }
        catch (JsonException error)
        {
            FailClosed($"The native host emitted a malformed event envelope: {error.Message}");
            return;
        }

        if (hostEvent is null)
        {
            return;
        }

        var currentSession = SessionId;

        if (currentSession is not null && hostEvent.SessionId != currentSession)
        {
            Interlocked.Increment(ref staleEventCount);
            return;
        }

        if (hostEvent.Sequence <= Interlocked.Read(ref lastEventSequence))
        {
            Interlocked.Increment(ref staleEventCount);
            return;
        }

        Interlocked.Exchange(ref lastEventSequence, hostEvent.Sequence);
        if (EventReceived is { } observers) {
            foreach (Action<HostEvent> observer in observers.GetInvocationList()) {
                try { observer(hostEvent); }
                catch (Exception) { /* A presentation observer cannot fail the protocol read loop. */ }
            }
        }

        if (hostEvent.IsCritical)
        {
            // Critical events are never dropped; backpressure is preferable to loss.
            await events.Writer.WriteAsync(hostEvent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Progress events may coalesce: drop the newest when the consumer lags.
            events.Writer.TryWrite(hostEvent);
        }
    }

    private void OnTransportExited()
    {
        if (Volatile.Read(ref disposed) != 0 || State == NativeHostState.Closed)
        {
            return;
        }

        FailPending(new HostClosedException("The native host process exited."));
        events.Writer.TryComplete(new HostClosedException("The native host process exited."));
        SetState(NativeHostState.Failed);
    }

    private void FailClosed(string message, Exception? cause = null)
    {
        LastError = message;
        var failure = new HostProtocolException(message, cause);
        // Awaiting callers must observe the failed state before their requests fault.
        SetState(NativeHostState.Failed);
        FailPending(failure);
        events.Writer.TryComplete(failure);
        transport.Kill();
    }

    private void FailPending(Exception error)
    {
        foreach (var entry in pending)
        {
            entry.Value.Completion.TrySetException(error);
        }
    }

    private void SetState(NativeHostState state)
    {
        State = state;

        if (state is NativeHostState.Ready)
        {
            LogLine?.Invoke(
                $"native host ready: session={SessionId} dataRoot={Handshake?.DataRoot} hostVersion={Handshake?.HostVersion}");
        }
        else if (state is NativeHostState.Failed && LastError is not null)
        {
            LogLine?.Invoke($"native host failure: {LastError}");
        }

        StateChanged?.Invoke(state);
    }

    private sealed record PendingRequest(string Command, TaskCompletionSource<JsonElement> Completion, long? TraceId);
}
