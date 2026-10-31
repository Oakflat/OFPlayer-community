using OFPlayer.WinUI.Core;
using OFPlayer.WinUI.Core.Transport;

namespace OFPlayer_WinUI.Services;
public sealed class NativeHostService
{
    public string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ofplayer-community");

    public async Task<NativeHostClient> ConnectAsync(CancellationToken token)
    {
        await using (var migration = new OFPlayer.Community.Services.CommunityHost())
            await migration.MigrateLegacyAsync();
        var transport = new StdioHostTransport(new() { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "ofplayer-community-host.exe"), DataRoot = DataRoot });
        var client = new NativeHostClient(transport, new() { RequestTimeout = TimeSpan.FromMinutes(5) });
        transport.StderrLine += PreviewLog.Write;
        try
        {
            await client.ConnectAsync(token);
            if (client.Handshake?.HostVersion is null)
                throw new InvalidDataException("Invalid Community host");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}
