using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OFPlayer.WinUI.Core.Appearance;
using OFPlayer_WinUI.ViewModels;

namespace OFPlayer.Community;
using Views = OFPlayer_WinUI.Views;

/// <summary>
/// Paints the backdrop chosen in Settings › Appearance behind the pages. Only the content
/// area changes: the sidebar and title bar stay on the system glass, and the pages' own
/// surfaces let part of the backdrop through.
/// </summary>
public sealed partial class MainWindow
{
    private readonly record struct BackdropPicture(string Source, int Width, int Radius);
    private static readonly string[] BackdropSurfaceThemes =
    {
        "Default",
        "Light"
    };
    private readonly Windows.UI.ViewManagement.AccessibilitySettings backdropAccessibility = new();
    private CancellationTokenSource? contentBackdropLoad;
    private BackdropPicture? contentBackdropPicture;
    private (string Source, byte[] Bytes)? contentBackdropBytes;
    private bool contentBackdropVisible;
    private (double Tint, double Luminosity)? playbackBarFrost;
    private static bool AffectsContentBackdrop(string? property) => property is nameof(MainViewModel.ShownContentBackdrop) or nameof(MainViewModel.PlaybackArtwork) or nameof(MainViewModel.CurrentTrackDetail);
    private void ApplyContentBackdrop(string? property = null)
    {
        if (model is null || closing || inTray)
            return;
        // Pro bought from the preview bar keeps the look that was being previewed.
        var backdrop = model.ShownContentBackdrop;
        SetPlaybackBarFrost(backdrop.BarFrost);
        // High contrast keeps the system's solid colours.
        var source = backdropAccessibility.HighContrast ? null : backdrop.Kind switch
        {
            ContentBackdrop.Cover => model.PlaybackArtwork,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(source))
        {
            ClearContentBackdrop();
            return;
        }

        var(width, radius) = backdrop.Kind == ContentBackdrop.Image ? BackdropPixels.ForImage(backdrop.Blur) : (BackdropPixels.CoverWidth, BackdropPixels.CoverRadius);
        var picture = new BackdropPicture(source, width, radius);
        if (contentBackdropPicture == picture)
        {
            // Same picture: only how much of it shows may have changed.
            if (contentBackdropVisible)
                SetPageSurfaceOpacity(backdrop.SurfaceOpacity);
            return;
        }

        contentBackdropPicture = picture;
        _ = LoadContentBackdropAsync(picture);
    }

    private async Task LoadContentBackdropAsync(BackdropPicture picture)
    {
        contentBackdropLoad?.Cancel();
        var load = contentBackdropLoad = new CancellationTokenSource();
        try
        {
            byte[] bytes;
            if (contentBackdropBytes is { } kept && kept.Source == picture.Source)
            {
                // Same picture with another blur: wait for the slider to settle, then reuse the bytes.
                await Task.Delay(120, load.Token);
                bytes = kept.Bytes;
            }
            else
            {
                bytes = await Views.ArtworkLoader.ReadBytesAsync(picture.Source, load.Token);
                contentBackdropBytes = (picture.Source, bytes);
            }

            var bitmap = await Views.BackdropBitmap.CreateAsync(bytes, picture.Width, picture.Radius, load.Token);
            if (load.IsCancellationRequested || closing)
                return;
            if (bitmap is null)
                ClearContentBackdrop(keepPicture: true);
            else
                ShowContentBackdrop(bitmap);
        }
        catch (OperationCanceledException)when (load.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"content backdrop load failed: {error.GetType().Name}: {error.Message}");
            // Keep the failed picture as current, so the next track update does not retry it.
            if (!load.IsCancellationRequested)
                ClearContentBackdrop(keepPicture: true);
        }
    }

    /// <summary>A new picture fades in over the one it replaces.</summary>
    private void ShowContentBackdrop(ImageSource bitmap)
    {
        ContentBackdropPrevious.Background = contentBackdropVisible ? ContentBackdropCurrent.Background : null;
        ContentBackdropCurrent.Background = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.UniformToFill
        };
        ContentBackdropLayer.Visibility = Visibility.Visible;
        contentBackdropVisible = true;
        SetPageSurfaceOpacity(model?.ShownContentBackdrop.SurfaceOpacity ?? 1);
        Views.Motion.FadeIn(ContentBackdropCurrent, Views.Motion.SlowMs);
    }

    private void ClearContentBackdrop(bool keepPicture = false)
    {
        contentBackdropLoad?.Cancel();
        if (!keepPicture)
        {
            contentBackdropPicture = null;
            contentBackdropBytes = null;
        }

        if (!contentBackdropVisible)
            return;
        contentBackdropVisible = false;
        ContentBackdropLayer.Visibility = Visibility.Collapsed;
        ContentBackdropPrevious.Background = null;
        ContentBackdropCurrent.Background = null;
        SetPageSurfaceOpacity(1);
    }

    /// <summary>
    /// The page surfaces are OFPlayer's own shared brushes (App.xaml) with fixed light and
    /// dark colours, so one opacity reaches every page at once. Only the opacity is written
    /// and no colour is read back, which keeps clear of the WindowTheme rule; the high
    /// contrast set is left alone. Every surface takes the same opacity, and the sheet inside
    /// a chrome page steps aside, so the background shows evenly from header to playback bar.
    /// The playback bar is glass of its own and keeps its frost at any strength.
    /// </summary>
    private static void SetPageSurfaceOpacity(double opacity)
    {
        var plain = opacity >= 1;
        foreach (var theme in BackdropSurfaceThemes)
        {
            if (!Application.Current.Resources.ThemeDictionaries.TryGetValue(theme, out var entry) || entry is not ResourceDictionary surfaces)
                continue;
            SetOpacity(surfaces, "OFPlayerContentSurfaceBrush", opacity);
            SetOpacity(surfaces, "OFPlayerChromeSurfaceBrush", opacity);
            SetOpacity(surfaces, "OFPlayerSheetSurfaceBrush", plain ? 1 : 0);
        }

        static void SetOpacity(ResourceDictionary surfaces, string key, double value)
        {
            if (surfaces.TryGetValue(key, out var brush) && brush is Brush surface)
                surface.Opacity = value;
        }
    }

    /// <summary>
    /// How much the playback bar's glass lets the songs beneath show. Like the page surfaces,
    /// only the shared light and dark brushes are written; high contrast keeps its solid bar.
    /// </summary>
    private void SetPlaybackBarFrost((double Tint, double Luminosity) frost)
    {
        if (playbackBarFrost == frost)
            return;
        playbackBarFrost = frost;
        foreach (var theme in BackdropSurfaceThemes)
        {
            if (!Application.Current.Resources.ThemeDictionaries.TryGetValue(theme, out var entry) || entry is not ResourceDictionary surfaces || !surfaces.TryGetValue("OFPlayerPlaybackSurfaceBrush", out var brush) || brush is not AcrylicBrush glass)
                continue;
            glass.TintOpacity = frost.Tint;
            glass.TintLuminosityOpacity = frost.Luminosity;
        }
    }
}
