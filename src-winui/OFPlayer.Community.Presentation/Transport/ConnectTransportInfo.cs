using System.Text.Json;

namespace OFPlayer.WinUI.Core.Transport;

public static class ConnectTransportPolicies
{
    public static string Validate(string policy) => policy is "auto" or "relay-only" ? policy : throw new ArgumentException("connect-transport-policy-invalid");
}

public sealed record ConnectTransportLane(string State,string Carrier,string LocalFamily,string RemoteFamily);
public sealed record ConnectTransportInfo(int SchemaVersion,string SessionId,string Policy,string Encoding,ConnectTransportLane Control,ConnectTransportLane Media)
{
    public static ConnectTransportInfo Parse(JsonElement value)
    {
        Exact(value,"schemaVersion","sessionId","policy","encoding","control","media");
        if(value.GetProperty("schemaVersion").ValueKind!=JsonValueKind.Number || value.GetProperty("schemaVersion").GetInt32()!=1)throw new IOException("connect-transport-schema-invalid");
        var id=Text(value,"sessionId");
        if(id.Length is <1 or >128 || !id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-'))throw new IOException("connect-transport-session-invalid");
        var policy=Text(value,"policy");
        if(policy is not ("auto" or "relay-only"))throw new IOException("connect-transport-policy-invalid");
        var encoding=Text(value,"encoding");
        if(encoding is not ("original-file-bytes" or "opus-vbr" or "unknown"))throw new IOException("connect-transport-encoding-invalid");
        return new(1,id,policy,encoding,Lane(value.GetProperty("control"),false),Lane(value.GetProperty("media"),true));
    }
    private static ConnectTransportLane Lane(JsonElement value,bool media)
    {
        Exact(value,"state","carrier","localFamily","remoteFamily");
        var state=Text(value,"state");var carrier=Text(value,"carrier");
        var local=Text(value,"localFamily");var remote=Text(value,"remoteFamily");
        if(state is not ("ready" or "pending" or "resetting" or "unavailable")
            || carrier is not ("rtc-direct" or "rtc-relay" or "unavailable" or "lan-quic" or "wss")
            || media && carrier == "wss"
            || local is not ("ipv4" or "ipv6" or "unknown") || remote is not ("ipv4" or "ipv6" or "unknown")
            || state=="ready" && carrier=="unavailable")throw new IOException("connect-transport-lane-invalid");
        return new(state,carrier,local,remote);
    }
    private static string Text(JsonElement value,string key)=>value.GetProperty(key).ValueKind==JsonValueKind.String?value.GetProperty(key).GetString()!:throw new IOException("connect-transport-field-invalid");
    private static void Exact(JsonElement value,params string[] keys)
    {
        if(value.ValueKind!=JsonValueKind.Object || value.EnumerateObject().Count()!=keys.Length || !keys.All(key=>value.TryGetProperty(key,out _)))throw new IOException("connect-transport-fields-invalid");
    }
}

/// Local presentation generation. Closing/replacing a session fences late snapshots.
public sealed class ConnectTransportInfoCache
{
    private long generation;
    private string? session,policy;
    public ConnectTransportInfo? Value { get; private set; }
    public long Begin(string sessionId,string transportPolicy) { Clear();session=sessionId;policy=ConnectTransportPolicies.Validate(transportPolicy);return generation; }
    public void Clear() { generation++;session=policy=null;Value=null; }
    public void Forget(long ticket) { if(ticket==generation)Value=null; }
    public bool Adopt(long ticket,ConnectTransportInfo value)
    {
        if(ticket!=generation || value.SessionId!=session || value.Policy!=policy)return false;
        Value=value;return true;
    }
}
