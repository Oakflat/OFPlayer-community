using OFPlayer.WinUI.Core;
using OFPlayer.WinUI.Core.Transport;

if (args.Length != 1) throw new ArgumentException("Pass the current Community host executable.");
var root = Path.Combine(Path.GetTempPath(), "ofplayer-community-csharp-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var transport = new StdioHostTransport(new() { ExecutablePath = Path.GetFullPath(args[0]), DataRoot = root });
await using (var client = new NativeHostClient(transport)) {
    await client.ConnectAsync(CancellationToken.None);
    if (client.Handshake?.DataRoot != root || client.State != NativeHostState.Ready)
        throw new InvalidOperationException("The real frontend handshake did not establish the requested session.");
    var catalog = await client.SendAsync("desktop_catalog_load_structure", null, CancellationToken.None);
    if (catalog.GetProperty("libraries").GetArrayLength() == 0) throw new InvalidOperationException("Missing initial library.");
    await client.SendAsync("desktop_preferences_save", new { patch = new { theme = "dark" } }, CancellationToken.None);
    var preferences = await client.SendAsync("desktop_preferences_load", null, CancellationToken.None);
    if (preferences.GetProperty("preferences").GetProperty("theme").GetString() != "dark") throw new InvalidOperationException("Preferences failed.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await foreach (var update in client.ReadEventsAsync(timeout.Token)) {
        if (update.Name != "playback.state") continue;
        if (update.SessionId != client.SessionId || !update.Data.TryGetProperty("session", out _))
            throw new InvalidOperationException("Invalid real-client playback event.");
        break;
    }
}
if (transport.IsRunning) throw new InvalidOperationException("The frontend left its native child running.");
var resolvedRoot = Path.GetFullPath(root);
var tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!resolvedRoot.StartsWith(tempParent, StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(resolvedRoot).StartsWith("ofplayer-community-csharp-", StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected test cleanup path.");
Directory.Delete(resolvedRoot, recursive: true);
Console.WriteLine("Real C# frontend transport: handshake, library, preferences, ordered playback event and graceful shutdown passed.");
