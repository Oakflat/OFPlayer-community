namespace OFPlayer.WinUI.Core.Transport;

/// What one observed lane may be shown as. Derived only from the lane itself:
/// the requested policy never fills in a path that was not confirmed.
public enum ConnectTransportPath { Pending, Resetting, Unavailable, Quic, Direct, Relay, Server }

public static class ConnectTransportPresentation
{
    public static ConnectTransportPath Path(ConnectTransportLane lane)=>lane.State switch {
        "ready"=>lane.Carrier switch {
            // The historical carrier name does not prove a physical LAN: its
            // private-address candidates also include VPN and overlay adapters.
            "lan-quic"=>ConnectTransportPath.Quic,"rtc-direct"=>ConnectTransportPath.Direct,
            "rtc-relay"=>ConnectTransportPath.Relay,"wss"=>ConnectTransportPath.Server,_=>ConnectTransportPath.Unavailable
        },
        "pending"=>ConnectTransportPath.Pending,"resetting"=>ConnectTransportPath.Resetting,
        _=>ConnectTransportPath.Unavailable
    };

    /// Language-neutral detail. Direct names the confirmed address families; the
    /// peer-to-peer lane and the relay need none: the relay's own label already
    /// says the media passes through the relay server, and naming the protocol it
    /// travels on would tell the user nothing about their connection.
    public static string Detail(ConnectTransportLane lane)=>Path(lane) switch {
        ConnectTransportPath.Quic=>string.Empty,
        ConnectTransportPath.Relay=>string.Empty,
        ConnectTransportPath.Server=>string.Empty,
        ConnectTransportPath.Direct=>string.Join(" / ",new[]{lane.LocalFamily,lane.RemoteFamily}.Select(Family).Where(name=>name.Length!=0).Distinct()),
        _=>string.Empty
    };

    /// Container/format name for a verified MIME. Unknown stays empty; `audio/mp4`
    /// is named by its container because it does not identify AAC or ALAC.
    public static string Format(string? mime)=>(mime??string.Empty).Split(';')[0].Trim().ToLowerInvariant() switch {
        "audio/flac" or "audio/x-flac"=>"FLAC",
        "audio/mpeg" or "audio/mp3"=>"MP3",
        "audio/wav" or "audio/wave" or "audio/x-wav"=>"WAV",
        "audio/aac" or "audio/aacp" or "audio/x-aac"=>"AAC",
        "audio/mp4" or "audio/x-m4a"=>"M4A",
        "audio/ogg"=>"OGG",
        "audio/aif" or "audio/aiff" or "audio/x-aiff"=>"AIFF",
        _=>string.Empty
    };

    private static string Family(string family)=>family switch { "ipv4"=>"IPv4","ipv6"=>"IPv6",_=>string.Empty };
}
