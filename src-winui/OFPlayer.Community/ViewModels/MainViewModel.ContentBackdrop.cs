using System.Text.Json;
using OFPlayer.WinUI.Core.Appearance;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// The content area's backdrop, edited in Settings › Appearance. An own picture is a Pro
/// look: without Pro it is only previewed while Settings is open, and a saved one shows as
/// the artwork wash until Pro is back. Plan state comes from the core's verified
/// entitlements, never from the preference.
/// </summary>
public sealed partial class MainViewModel
{
    private const string AppearanceFolder = "appearance";
    /// <summary>The artwork reader's limit, so every imported picture can also be shown.</summary>
    private const long MaxBackdropImageBytes = 32L * 1024 * 1024;
    private CancellationTokenSource? contentBackdropSave;
    /// <summary>The saved choice.</summary>
    public ContentBackdrop ContentBackdrop { get; private set; } = ContentBackdrop.Default;
    /// <summary>A Pro look tried without Pro; shown until Settings is left.</summary>
    public ContentBackdrop? ContentBackdropPreview { get; private set; }
    /// <summary>What the window shows now.</summary>
    public ContentBackdrop ShownContentBackdrop => ContentBackdropPreview ?? ContentBackdrop.Effective(false);
    public bool CanUseProLooks => false;

    /// <summary>Full path of a backdrop's picture, or null when it has none.</summary>
    public string? ContentBackdropImagePath(ContentBackdrop backdrop) => hostService is null || !ContentBackdrop.IsSafeFileName(backdrop.ImageFile) ? null : Path.Combine(hostService.DataRoot, AppearanceFolder, backdrop.ImageFile);
    public async Task ChooseContentBackdropAsync(ContentBackdrop requested)
    {
        var(saved, preview) = ContentBackdrop.Choose(ContentBackdrop, requested, CanUseProLooks);
        var savedChanged = saved != ContentBackdrop;
        if (!savedChanged && preview == ContentBackdropPreview)
            return;
        ContentBackdrop = saved;
        ContentBackdropPreview = preview;
        RaiseContentBackdropChanged();
        if (!savedChanged)
            return;
        contentBackdropSave?.Cancel();
        var save = contentBackdropSave = new CancellationTokenSource();
        try
        {
            // Sliders report every step; the profile is written once the value settles.
            await Task.Delay(300, save.Token);
            await PersistPreferencesAsync(new { contentBackdrop = saved.ToJson() });
            RemoveUnusedBackdropImages();
        }
        catch (OperationCanceledException)
        {
        // A newer change supersedes this write.
        }
    }

    /// <summary>Pro arrived while its look was being previewed (bought from the preview bar): keep it.</summary>
    public Task AdoptContentBackdropPreviewAsync() => ContentBackdropPreview is { } preview && CanUseProLooks ? ChooseContentBackdropAsync(preview) : Task.CompletedTask;
    /// <summary>Leaving Settings or "换回原来的": the saved look comes back.</summary>
    public void EndContentBackdropPreview()
    {
        if (ContentBackdropPreview is null)
            return;
        ContentBackdropPreview = null;
        RaiseContentBackdropChanged();
    }

    /// <summary>
    /// Copies a picked picture into the app's own folder, so moving or deleting the original
    /// does not break the backdrop. Returns the stored file name, or null when it cannot be used.
    /// </summary>
    public async Task<string?> ImportContentBackdropImageAsync(string sourcePath)
    {
        if (hostService is null)
            return null;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBackdropImageBytes)
                return null;
            var folder = Path.Combine(hostService.DataRoot, AppearanceFolder);
            var name = $"backdrop-{DateTime.UtcNow:yyyyMMddHHmmssfff}{info.Extension.ToLowerInvariant()}";
            if (!ContentBackdrop.IsSafeFileName(name))
                return null;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                File.Copy(info.FullName, Path.Combine(folder, name), overwrite: false);
            });
            return name;
        }
        catch (Exception error)when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            PreviewLog.Write($"backdrop image import failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private void LoadContentBackdrop(JsonElement preferences)
    {
        ContentBackdrop = ContentBackdrop.FromPreferences(preferences);
        RaiseContentBackdropChanged();
    }

    private void RaiseContentBackdropChanged()
    {
        RaisePropertyChanged(nameof(ContentBackdrop));
        RaisePropertyChanged(nameof(ContentBackdropPreview));
        RaisePropertyChanged(nameof(ShownContentBackdrop));
    }

    /// <summary>Pictures replaced since; the saved one and any open preview stay.</summary>
    private void RemoveUnusedBackdropImages()
    {
        if (hostService is null)
            return;
        var folder = Path.Combine(hostService.DataRoot, AppearanceFolder);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ContentBackdrop.ImageFile
        };
        if (ContentBackdropPreview is { } preview)
            keep.Add(preview.ImageFile);
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;
                foreach (var file in Directory.EnumerateFiles(folder, "backdrop-*"))
                    if (!keep.Contains(Path.GetFileName(file)))
                        File.Delete(file);
            }
            catch (Exception error)when (error is IOException or UnauthorizedAccessException)
            {
                PreviewLog.Write($"backdrop cleanup skipped: {error.GetType().Name}: {error.Message}");
            }
        });
    }
}
