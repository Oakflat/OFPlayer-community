using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace OFPlayer.WinUI.Core.Diagnostics;

/// <summary>Opt-in, bounded JSONL tracing. Call sites supply static names, never payloads.</summary>
public static class PerformanceTrace
{
    private static readonly AsyncLocal<Span?> Current = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly object Gate = new();
    private static Channel<object>? channel;
    private static Task? writer;
    private static long sequence;
    private static long dropped;
    public static string? FilePath { get; private set; }
    public static bool Enabled => channel is not null;
    public static long Dropped => Interlocked.Read(ref dropped);
    public static long? CurrentId => Current.Value?.Id;
    public static double NowMs => Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
    public static bool Detailed { get; private set; } = true;
    public static event Action<CompletedOperation>? OperationCompleted;
    public sealed record CompletedOperation(long Id, string Name, double StartedMs, double CompletedMs);

    public static void Start(string directory, bool detailed = true, long partBytes = 64 * 1024 * 1024, int maxParts = 8)
    {
        lock (Gate)
        {
            if (channel is not null) return;
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"perf-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl");
            var path = FilePath;
            Detailed = detailed;
            Interlocked.Exchange(ref dropped, 0);
            var queue = Channel.CreateBounded<object>(new BoundedChannelOptions(8192)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            channel = queue;
            writer = Task.Run(async () =>
            {
                try
                {
                    var part = 0;
                    var file = new StreamWriter(path);
                    var session = JsonSerializer.Serialize(new { kind = "session", schema = 2,
                        pid = Environment.ProcessId, utc = DateTime.UtcNow, atMs = NowMs,
                        qpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, detailed });
                    await file.WriteLineAsync(session);
                    long bytes = 0;
                    var lastFlush = Stopwatch.GetTimestamp();
                    try
                    {
                        while (await queue.Reader.WaitToReadAsync())
                        {
                            // Batch disk flushes; a producer must never wait for disk I/O.
                            var count = 0;
                            while (count++ < 1024 && queue.Reader.TryRead(out var item))
                            {
                                if (bytes >= partBytes)
                                {
                                    if (part + 1 >= maxParts) { Interlocked.Increment(ref dropped); continue; }
                                    await file.DisposeAsync();
                                    file = new StreamWriter(Path.ChangeExtension(path, $"part{++part:D2}.jsonl"));
                                    await file.WriteLineAsync(session);
                                    bytes = 0;
                                }
                                var line = JsonSerializer.Serialize(item);
                                bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 2;
                                await file.WriteLineAsync(line);
                            }
                            if (Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= 250)
                            {
                                await file.FlushAsync();
                                lastFlush = Stopwatch.GetTimestamp();
                            }
                            if (!queue.Reader.Completion.IsCompleted) await Task.Delay(20).ConfigureAwait(false);
                        }
                        await file.WriteLineAsync(JsonSerializer.Serialize(new { kind = "end", dropped = Dropped }));
                    }
                    finally { await file.DisposeAsync(); }
                }
                catch { Interlocked.Increment(ref dropped); }
            });
        }
    }

    public static Span? Begin(string name, long? parentId = null)
    {
        if (!Enabled || !Detailed) return null;
        var span = new Span(name, Current.Value, parentId, false);
        Current.Value = span;
        return span;
    }

    public static Span? BeginOperation(string name)
    {
        if (!Enabled) return null;
        var span = new Span(name, Current.Value, null, true);
        Current.Value = span;
        return span;
    }

    // For synchronous callbacks and long-lived native gestures, without keeping
    // AsyncLocal scopes open across unrelated window messages.
    public static long Activity(string name, double startedMs, string category = "sync", long? parent = null)
    {
        if (!Enabled) return 0;
        var id = Interlocked.Increment(ref sequence);
        Emit(new { kind = "span", id, parent = parent ?? CurrentId, name, category,
            atMs = startedMs, durationMs = Math.Max(0, NowMs - startedMs), outcome = "completed",
            thread = Environment.CurrentManagedThreadId });
        return id;
    }

    public static void Mark(string name, object? data = null)
    {
        if (Enabled) Emit(new { kind = "mark", name, atMs = NowMs, data, thread = Environment.CurrentManagedThreadId });
    }

    public static void Sample(string name, double value, long? operation = null)
    {
        if (!Enabled) return;
        Emit(new { kind = "sample", name, value, atMs = NowMs, parent = operation ?? Current.Value?.Id,
            thread = Environment.CurrentManagedThreadId });
    }

    private static void Emit(object item)
    {
        var queue = channel;
        if (queue is not null && !queue.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
    }

    public static async Task FlushAndStopAsync()
    {
        Task? completion;
        lock (Gate)
        {
            channel?.Writer.TryComplete();
            channel = null;
            completion = writer;
        }
        if (completion is not null) await completion.ConfigureAwait(false);
    }

    public sealed class Span : IDisposable
    {
        internal Span(string name, Span? parent, long? parentId, bool operation)
        {
            Name = name; Parent = parent; Id = Interlocked.Increment(ref sequence); Start = NowMs;
            IsOperation = operation;
            ParentId = parentId ?? parent?.Id;
            Emit(new { kind = "begin", id = Id, parent = ParentId, name = Name, atMs = Start,
                category = operation ? "operation" : "method", thread = Environment.CurrentManagedThreadId });
        }
        public long Id { get; }
        private string Name { get; }
        private Span? Parent { get; }
        private long? ParentId { get; }
        private double Start { get; }
        private bool IsOperation { get; }
        private int disposed;
        private string outcome = "completed";
        public void Outcome(string value) => outcome = value;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (ReferenceEquals(Current.Value, this)) Current.Value = Parent;
            var end = NowMs;
            Emit(new { kind = "endSpan", id = Id, parent = ParentId, name = Name, atMs = end,
                durationMs = end - Start, outcome, category = IsOperation ? "operation" : "method",
                thread = Environment.CurrentManagedThreadId });
            if (IsOperation)
            {
                try { OperationCompleted?.Invoke(new(Id, Name, Start, end)); }
                catch { /* Diagnostics must not change command outcomes. */ }
            }
        }
    }
}
