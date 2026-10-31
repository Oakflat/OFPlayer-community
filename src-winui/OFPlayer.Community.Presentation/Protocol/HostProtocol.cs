namespace OFPlayer.WinUI.Core.Protocol;

/// <summary>
/// Frozen host protocol constants. The native host speaks protocol v1; any change
/// here must be mirrored in crates/player-core/src/native_host.rs and must keep older
/// clients failing cleanly via the handshake version check.
/// </summary>
public static class HostProtocol
{
    public const int Version = 1;

    public static class ErrorCodes
    {
        public const string ProtocolViolation = "protocol-violation";
        public const string UnsupportedProtocolVersion = "unsupported-protocol-version";
        public const string MissingProtocolVersion = "missing-protocol-version";
        public const string UnknownCommand = "unknown-command";
        public const string InvalidArguments = "invalid-arguments";
        public const string CommandFailed = "command-failed";
    }

    public static class Capabilities
    {
        public const string CatalogRead = "catalog.read";
        public const string PlaybackSession = "playback.session";
        public const string PlaybackOutput = "playback.output";
        public const string PlaybackVolume = "playback.volume";
    }
}
