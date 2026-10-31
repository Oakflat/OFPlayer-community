using System.Text.Json;
using OFPlayer.WinUI.Core.Playback;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>Lyric capsule look, edited in Settings and pushed live to the capsule page.</summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? capsuleAppearanceSave;
    public CapsuleAppearance CapsuleAppearance { get; private set; } = CapsuleAppearance.Default;

    /// <summary>
    /// The capsule follows at once; the profile write waits until the value settles,
    /// so dragging the opacity slider does not save on every step.
    /// </summary>
    public async Task SetCapsuleAppearanceAsync(CapsuleAppearance value)
    {
        value = value.Normalize();
        if (value == CapsuleAppearance)
            return;
        CapsuleAppearance = value;
        RaisePropertyChanged(nameof(CapsuleAppearance));
        capsuleAppearanceSave?.Cancel();
        var save = capsuleAppearanceSave = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, save.Token);
            await PersistPreferencesAsync(new { lyricCapsule = value.ToJson() });
        }
        catch (OperationCanceledException)
        {
        // A newer change supersedes this write.
        }
    }

    private void LoadCapsuleAppearance(JsonElement preferences)
    {
        CapsuleAppearance = CapsuleAppearance.FromPreferences(preferences);
        RaisePropertyChanged(nameof(CapsuleAppearance));
    }
}
