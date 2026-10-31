using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.ViewModels;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Track details. Shows the selected row when there is one, otherwise the playing
/// track; a badge says which. It is the side panel on wide windows and the same
/// view inside a dialog on narrower ones.
/// </summary>
public sealed partial class TrackInspectorView : UserControl
{
    private MainViewModel? viewModel;
    /// <summary>Hosted in a dialog: the dialog closes it, so the panel's own close button goes.</summary>
    public bool Embedded
    {
        get => CloseButton.Visibility == Visibility.Collapsed;
        set
        {
            CloseButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            RootGrid.Padding = new Thickness(value ? 0 : 14);
        }
    }

    public TrackInspectorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("TrackInspectorView.xaml.OnLoaded");
        viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Services.Localization.LanguageChanged += Apply;
        Apply();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("TrackInspectorView.xaml.OnUnloaded");
        Services.Localization.LanguageChanged -= Apply;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("TrackInspectorView.xaml.OnViewModelPropertyChanged");
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.SelectedTrackDetail):
            case nameof(MainViewModel.HasSelectedTrack):
            case nameof(MainViewModel.CurrentTrackDetail):
            case nameof(MainViewModel.PlaybackArtwork):
            case nameof(MainViewModel.SelectedTrackIsFavorite):
            case nameof(MainViewModel.IsConnected):
                Apply();
                break;
        }
    }

    private void Apply()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("TrackInspectorView.xaml.Apply");
        if (viewModel is null)
        {
            return;
        }

        var text = Services.Localization.Get;
        HeaderText.Text = text("inspector.title");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseButton, text("inspector.close"));
        ToolTipService.SetToolTip(CloseButton, text("inspector.close"));
        LyricsButton.Content = text("player.lyrics");
        var selected = viewModel.SelectedTrackDetail;
        var nowPlaying = viewModel.CurrentTrackDetail;
        var detail = selected ?? nowPlaying;
        if (detail is null)
        {
            ModeBadge.Visibility = Visibility.Collapsed;
            TitleText.Text = text(viewModel.IsConnected ? "inspector.empty" : "inspector.notReady");
            FavoriteText.Text = text("track.favorite");
            ArtistText.Text = string.Empty;
            MetaPanel.Children.Clear();
            CoverImage.Source = null;
            FavoriteButton.IsEnabled = false;
            LyricsButton.IsEnabled = false;
            return;
        }

        ModeText.Text = text(selected is not null ? "inspector.selected" : "inspector.playing");
        ModeBadge.Visibility = Visibility.Visible;
        TitleText.Text = detail.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(detail.Artist) ? text("track.unknownArtist") : detail.Artist;
        FillMeta(detail);
        _ = ArtworkLoader.ApplyAsync(CoverImage, detail.Artwork);
        var selectedMode = selected is not null;
        FavoriteButton.IsEnabled = selectedMode;
        FavoriteButton.Opacity = selectedMode ? 1.0 : 0.5;
        FavoriteIcon.Glyph = viewModel.SelectedTrackIsFavorite ? "\uE735" : "\uE734";
        FavoriteText.Text = text(viewModel.SelectedTrackIsFavorite ? "track.favorited" : "track.favorite");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FavoriteButton, text(viewModel.SelectedTrackIsFavorite ? "track.unfavorite" : "track.favorite"));
        LyricsButton.IsEnabled = viewModel.IsConnected;
    }

    private void FillMeta(TrackDetail detail)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("TrackInspectorView.xaml.FillMeta");
        MetaPanel.Children.Clear();
        var text = Services.Localization.Get;
        MetaPanel.Children.Add(MetaLine(text("track.album"), detail.Album));
        MetaPanel.Children.Add(MetaLine(text("track.duration"), detail.DurationText));
        MetaPanel.Children.Add(MetaLine(text("track.format"), detail.Format));
        MetaPanel.Children.Add(MetaLine(text("track.bitrate"), detail.BitrateText));
        MetaPanel.Children.Add(MetaLine(text("track.sampleRate"), detail.SampleRateText));
        MetaPanel.Children.Add(MetaLine(text("track.bitDepth"), detail.BitDepthText));
        MetaPanel.Children.Add(MetaLine(text("track.file"), detail.FileName));
        if (!string.IsNullOrWhiteSpace(detail.AlbumArtist))
            MetaPanel.Children.Add(MetaLine(text("track.albumArtist"), detail.AlbumArtist));
        if (!string.IsNullOrWhiteSpace(detail.Genre))
            MetaPanel.Children.Add(MetaLine(text("track.genre"), detail.Genre));
        if (!string.IsNullOrWhiteSpace(detail.Date))
            MetaPanel.Children.Add(MetaLine(text("track.date"), detail.Date));
        if (!string.IsNullOrWhiteSpace(detail.Track))
            MetaPanel.Children.Add(MetaLine(text("track.number"), detail.Track));
        if (detail.Channels > 0)
            MetaPanel.Children.Add(MetaLine(text("track.channels"), detail.Channels.ToString(Services.Localization.Culture)));
    }

    private static Grid MetaLine(string label, string? value)
    {
        var row = new Grid
        {
            ColumnSpacing = 12
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(WindowTheme.Brush(new TextBlock { Text = label, FontSize = 12 }, "Foreground", "TextFillColorSecondaryBrush"));
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void CloseInspector(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("TrackInspectorView.xaml.CloseInspector");
        viewModel?.CloseInspector();
    }

    private async void ToggleFavorite(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("TrackInspectorView.xaml.ToggleFavorite");
        if (viewModel is not null)
        {
            await viewModel.ToggleInspectorFavoriteAsync();
        }
    }

    private async void OpenLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("TrackInspectorView.xaml.OpenLyrics");
        if (viewModel is not null && !viewModel.IsLyricsVisible)
        {
            await viewModel.ToggleLyricsAsync();
        }
    }
}
