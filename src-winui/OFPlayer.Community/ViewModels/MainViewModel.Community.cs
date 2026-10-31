using System.Text.Json;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private OFPlayer.Community.Services.DailyActivity? activity;
    private void StartDailyActivity(string root)
    {
        activity = new(root);
        activity.Record();
    }

    internal Task SuspendForVideoAsync() => client is null ? Task.CompletedTask : client.SendAsync("playback.suspend", null, CancellationToken.None);
    internal async Task<JsonElement> CommunityCallAsync(string command, object? args = null) => client is null ? default : await client.SendAsync(command, args, CancellationToken.None);
    internal string PlaybackPresentationScope => client?.SessionId ?? string.Empty;

    internal bool UsesCurrentPlaybackPresentation(string? trackId) => trackId == "external";
    public bool IsPlaybackBuffering => IsRemoteBusy;
}
