using System.Threading.Channels;

namespace OFPlayer.WinUI.Core.Transport;

/// <summary>
/// Transport boundary for the native host. The first implementation is a private
/// stdio child process; keeping requests/events behind this interface allows tests
/// with a controlled fake and a later transport swap without touching the client.
/// </summary>
public interface INativeHostTransport : IAsyncDisposable
{
    /// <summary>True while the transport can still carry protocol lines.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the transport (spawns the host process).</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Writes one protocol line to the host stdin.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>stdout lines: protocol JSON only, in arrival order.</summary>
    ChannelReader<string> OutputLines { get; }

    /// <summary>stderr log lines (never protocol data).</summary>
    event Action<string>? StderrLine;

    /// <summary>Raised once when the host process exits or the transport dies.</summary>
    event Action? Exited;

    /// <summary>Waits for the host process to exit and returns its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Forced termination. Reserved for disconnect timeouts, never for request cancellation.</summary>
    void Kill();
}
