using OFPlayer.WinUI.Core.Playback;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    public bool IsPlaying => playback.Playing;
    public bool CanPlay => IsConnected && playback.ActiveTrackId is not null;
    public bool CanSkip => CanPlay;

    public Task SeekAsync(double seconds) => RunPlaybackCommandAsync("playback_seek", new { seconds });
    /// <summary>Engine volume + debounced preferences write (N2 volume memory).</summary>
    public Task SetVolumeAsync(double volume) => SetVolumePersistedAsync(volume);
    private async Task RunPlaybackCommandAsync(string command, object args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.RunPlaybackCommandAsync");
        if (client is null || !IsConnected)
            return;
        try
        {
            var result = await client.SendAsync(command, args, CancellationToken.None);
            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    public async Task TogglePlaybackAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.TogglePlaybackAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var command = playback.Playing ? "playback_session_pause" : "playback_session_play_current";
            var result = await client.SendAsync(command, null, CancellationToken.None);
            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    // Explicit system transport commands must not toggle when repeated by Windows.
    public Task SetPlaybackPlayingAsync(bool playing) => RunPlaybackCommandAsync(playing ? "playback_session_play_current" : "playback_session_pause", new { });
    /// <summary>
    /// Old front-end semantics: the next track id is resolved client-side from
    /// the queue + repeat/shuffle preferences, then selected on the host.
    /// </summary>
    public Task PlayNextAsync() => PlaySkipAsync(1, OFPlayer.WinUI.Core.Playback.PlaybackOrder.ReasonUser);
    /// <summary>Old front-end: restart past 3s, otherwise step back one queue track.</summary>
    public Task PlayPreviousAsync() => PlayPreviousInternalAsync();
}
