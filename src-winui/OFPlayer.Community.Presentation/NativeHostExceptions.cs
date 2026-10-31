namespace OFPlayer.WinUI.Core;

/// <summary>Base type for native host failures surfaced to the UI.</summary>
public abstract class NativeHostException : Exception
{
    protected NativeHostException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The transport could not be started or the handshake did not complete.</summary>
public sealed class HostConnectionException : NativeHostException
{
    public HostConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Protocol version or message-shape violation. The client fails closed.</summary>
public sealed class HostProtocolException : NativeHostException
{
    public HostProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The host answered a request with a structured error.</summary>
public sealed class HostCommandException : NativeHostException
{
    public HostCommandException(string code, string message, string? command = null, long? requestId = null) : base(message)
    {
        Code = code;
        Command = command;
        RequestId = requestId;
    }

    public string Code { get; }
    public string? Command { get; }
    public long? RequestId { get; }

    public override string ToString() => $"{base.ToString()}{Environment.NewLine}Host code: {Code}; command: {Command ?? "unknown"}; request: {RequestId?.ToString() ?? "unknown"}";
}

/// <summary>
/// The request did not receive a response in time. The mutation outcome is unknown:
/// callers must query state instead of repeating play/seek/write commands.
/// </summary>
public sealed class HostRequestTimeoutException : NativeHostException
{
    public HostRequestTimeoutException(string command, TimeSpan timeout)
        : base($"The native host did not answer '{command}' within {timeout.TotalSeconds:0.#}s. The outcome is unknown; query state before retrying.")
    {
        Command = command;
    }

    public string Command { get; }
}

/// <summary>The host process or connection is no longer available.</summary>
public sealed class HostClosedException : NativeHostException
{
    public HostClosedException(string message) : base(message) { }
}
