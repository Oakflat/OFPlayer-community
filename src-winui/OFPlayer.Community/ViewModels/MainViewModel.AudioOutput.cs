using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    public AudioPathInput AudioPathInput
    {
        get
        {
            var detail = CurrentTrackDetail;
            return new(IsConnected, playback.SignalPath, playback.ActiveTrackId is null ? AudioPlaybackOrigin.None : AudioPlaybackOrigin.Local, null, detail?.Format, detail?.SampleRate ?? 0, detail?.BitDepth ?? 0, audioEngineStatus?.ActiveEngine);
        }
    }

    public string? OutputPreferredDeviceId => outputDevices.PreferredDeviceId;
    public bool OutputPrefersSystemDefault => outputDevices.PrefersSystemDefault;
    public bool OutputPreferredDeviceAvailable => outputDevices.PreferredDeviceAvailable;
    public string? OutputActiveDeviceName => outputDevices.ActiveDeviceName;

    internal static string AudioText(string key, params (string Name, string Value)[] args) => args.Length == 0 ? Localization.Get(key) : Localization.Format(key, args);
}
