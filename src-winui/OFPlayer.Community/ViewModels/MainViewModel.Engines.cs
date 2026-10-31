using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private AudioEngineStatus? audioEngineStatus;
    private bool audioEngineBusy;
    private string? audioEngineRequestError;
    private long audioEngineRequestVersion;
    public AudioEngineStatus? AudioEngineStatus => audioEngineStatus;
    public bool AudioEngineBusy => audioEngineBusy;
    /// <summary>String-table key for the last refused or unconfirmed change; the host's text goes to the log.</summary>
    public string? AudioEngineRequestError => audioEngineRequestError;

    /// <summary>A replaced host connection starts with no output observations at all.</summary>
    private void ResetAudioEngineStatus()
    {
        Interlocked.Increment(ref audioEngineRequestVersion);
        audioEngineStatus = null;
        audioEngineRequestError = null;
        audioEngineBusy = false;
        outputDevices = Models.PlaybackOutputDevicesSnapshot.Empty;
        if (playback.SignalPath is not null)
        {
            playback = playback with
            {
                SignalPath = null
            };
            RaisePropertyChanged(nameof(Playback));
        }

        RaisePropertyChanged(nameof(AudioEngineStatus));
        RaisePropertyChanged(nameof(AudioEngineRequestError));
        RaisePropertyChanged(nameof(AudioEngineBusy));
        RaisePropertyChanged(nameof(OutputDevices));
        RaisePropertyChanged(nameof(OutputDeviceLabel));
    }

    public async Task RefreshAudioEngineAsync(bool clearRequestError = true)
    {
        if (client is null)
            return;
        var requestVersion = Interlocked.Increment(ref audioEngineRequestVersion);
        try
        {
            var result = await client.SendAsync("playback_get_audio_engine", null, CancellationToken.None);
            var status = OFPlayer.WinUI.Core.Playback.AudioEngineStatus.Parse(result);
            post(() =>
            {
                if (requestVersion != Interlocked.Read(ref audioEngineRequestVersion))
                    return;
                audioEngineStatus = status;
                if (clearRequestError)
                    audioEngineRequestError = null;
                RaisePropertyChanged(nameof(AudioEngineStatus));
                RaisePropertyChanged(nameof(AudioEngineRequestError));
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"audio engine status failed: {error.GetType().Name}: {error.Message}");
            post(() =>
            {
                if (requestVersion != Interlocked.Read(ref audioEngineRequestVersion))
                    return;
                audioEngineRequestError = AudioOutputPresentation.FailureKey(error);
                RaisePropertyChanged(nameof(AudioEngineRequestError));
            });
        }
    }

    public async Task SetAudioEngineAsync(string engine)
    {
        if (client is null || audioEngineBusy || audioEngineStatus is null || !audioEngineStatus.CanSelect(engine))
            return;
        audioEngineBusy = true;
        var requestVersion = Interlocked.Increment(ref audioEngineRequestVersion);
        RaisePropertyChanged(nameof(AudioEngineBusy));
        try
        {
            var result = await client.SendAsync("playback_set_audio_engine", new { engine }, CancellationToken.None);
            var status = OFPlayer.WinUI.Core.Playback.AudioEngineStatus.Parse(result);
            post(() =>
            {
                if (requestVersion != Interlocked.Read(ref audioEngineRequestVersion))
                    return;
                audioEngineStatus = status;
                audioEngineRequestError = null;
                RaisePropertyChanged(nameof(AudioEngineStatus));
                RaisePropertyChanged(nameof(AudioEngineRequestError));
            });
            await RefreshOutputDevicesAsync();
            try
            {
                var snapshot = await client.SendAsync("playback_snapshot", null, CancellationToken.None);
                post(() => ApplyCommandResult(snapshot));
            }
            catch (Exception error)
            {
                PreviewLog.Write($"audio engine playback refresh failed: {error.Message}");
            }
        // The host owns audioEngine persistence. Never write it from the UI.
        }
        catch (Exception error)
        {
            PreviewLog.Write($"audio engine change to {engine} failed: {error.GetType().Name}: {error.Message}");
            // A timed-out mutation may have completed in the host. Query its state.
            await RefreshAudioEngineAsync(false);
            var failedVersion = Interlocked.Read(ref audioEngineRequestVersion);
            post(() =>
            {
                if (failedVersion != Interlocked.Read(ref audioEngineRequestVersion))
                    return;
                audioEngineRequestError = AudioOutputPresentation.FailureKey(error);
                RaisePropertyChanged(nameof(AudioEngineRequestError));
            });
        }
        finally
        {
            post(() =>
            {
                audioEngineBusy = false;
                RaisePropertyChanged(nameof(AudioEngineBusy));
            });
        }
    }
}
