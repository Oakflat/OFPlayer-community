using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace OFPlayer.WinUI.Core.Transport;

/// <summary>Options for the stdio child-process transport.</summary>
public sealed record StdioHostOptions
{
    public required string ExecutablePath { get; init; }

    /// <summary>Explicit data root passed to the host via --data-root (required by the host).</summary>
    public required string DataRoot { get; init; }

    /// <summary>Extra CLI arguments (e.g. --allow-formal-data-root). Not used by the preview.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// stdio transport: one private child process, newline-delimited JSON on stdout,
/// logs on stderr. The child is never created with shell execution and never opens
/// a network port.
/// </summary>
public sealed class StdioHostTransport : INativeHostTransport
{
    private const int OutputChannelCapacity = 256;

    private readonly StdioHostOptions options;
    private readonly Channel<string> outputLines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(OutputChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private Process? process;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int exitRaised;

    public StdioHostTransport(StdioHostOptions options)
    {
        this.options = options;
    }

    public bool IsRunning => process is { HasExited: false };

    public ChannelReader<string> OutputLines => outputLines.Reader;

    public event Action<string>? StderrLine;

    public event Action? Exited;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (process is not null)
        {
            throw new InvalidOperationException("The stdio transport was already started.");
        }

        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException(
                $"The native host executable was not found at '{options.ExecutablePath}'. Build it with: cargo build --bin ofplayer-native-host.",
                options.ExecutablePath);
        }

        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(options.DataRoot);

        foreach (var argument in options.ExtraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => RaiseExited();
        process.Start();
        OwnedChildProcessGroup.Attach(process);
        if (OperatingSystem.IsWindows()) StderrLine?.Invoke($"[process-owner] pid={process.Id}; group={OwnedChildProcessGroup.Name}");
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                StderrLine?.Invoke(args.Data);
            }
        };
        process.BeginErrorReadLine();
        _ = ReadStdoutAsync(process.StandardOutput, process);

        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("transport.write");
        // A completed WaitAsync does not switch threads. Process.HasExited can
        // pump COM messages on an STA, reentering XAML during item realization.
        // Force the process/pipe work off the caller's UI thread first.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using (OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("transport.writeGate"))
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = process;
            if (target is null || target.HasExited)
                throw new IOException("The native host process is not running.");

            // Once a frame starts, finish it atomically. Cancelling half a JSON
            // line corrupts the next request. Caller cancellation still abandons
            // its response, and never retries the operation.
            await target.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await target.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var target = process;
        if (target is null)
        {
            throw new InvalidOperationException("The stdio transport was not started.");
        }

        await target.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return target.ExitCode;
    }

    public void Kill()
    {
        var target = process;
        if (target is null)
        {
            return;
        }

        try
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already gone: nothing to terminate.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The owner job remains a last-resort cleanup if the parent exits.
        }
    }

    public async ValueTask DisposeAsync()
    {
        var target = process;

        if (target is null)
        {
            outputLines.Writer.TryComplete();
            return;
        }

        try
        {
            target.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await target.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            await target.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            process = null;
            target.Dispose();
            outputLines.Writer.TryComplete();
            RaiseExited();
        }
    }

    private async Task ReadStdoutAsync(StreamReader stdout, Process owner)
    {
        try
        {
            while (true)
            {
                var line = await stdout.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                await outputLines.Writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!owner.HasExited)
        {
            // Reading failed while the process is alive: treat as transport death below.
        }
        finally
        {
            outputLines.Writer.TryComplete();
            RaiseExited();
        }
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref exitRaised, 1) == 0)
        {
            Exited?.Invoke();
        }
    }
}
