using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// The library page. Its header names the collection and carries the two page
/// actions (play it, add music to it); the toolbar finds and arranges; selecting
/// several songs turns the toolbar into that selection's command bar. Lyrics of the
/// playing track take the list's place when asked for from the playback bar.
/// The shell (MainWindow) only hosts and navigates; page logic lives here.
/// </summary>
public sealed partial class LibraryPageView : UserControl
{
    private static readonly string[] AudioFileExtensions =
    {
        ".mp3",
        ".mp2",
        ".mp1",
        ".mpa",
        ".wav",
        ".wave",
        ".flac",
        ".ogg",
        ".oga",
        ".m4a",
        ".m4b",
        ".m4r",
        ".mp4",
        ".aac",
        ".adts",
        ".aif",
        ".aiff",
        ".aifc",
        ".caf",
        ".mka",
        ".dsf",
        ".dff",
    };
    private MainViewModel? viewModel;
    private bool applyQueued;
    /// <summary>R4-4: guards the remote library selector against re-entrant SelectionChanged.</summary>
    private bool suppressRemoteLibraryChange;
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("layout.catalog.measure");
        return base.MeasureOverride(availableSize);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("layout.catalog.arrange");
        return base.ArrangeOverride(finalSize);
    }

    public LibraryPageView()
    {
        InitializeComponent();
        Tracks.ContainerContentChanging += OnTrackContainerChanging;
        LyricsList.ContainerContentChanging += OnLyricContainerChanging;
        Tracks.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(TracksKeyDown), true);
        Tracks.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(TrackNavigationUserScroll), true);
        Tracks.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TrackNavigationUserScroll), true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnLoaded");
        viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.MissingFileRequested += MissingFileRequested;
        viewModel.NavigationChanged += Apply;
        Localization.LanguageChanged += OnLanguageChanged;
        ApplyStrings();
        Tracks.ItemsSource = viewModel.Tracks;
        GroupsList.ItemsSource = viewModel.Groups;
        LyricsList.ItemsSource = viewModel.LyricsLines;
        viewModel.ActiveLyricsChanged += OnActiveLyricsChanged;
        viewModel.Tracks.CollectionChanged += OnTracksCollectionChanged;
        // Apply selects the current collection's restored sort option.
        RemoteList.ItemsSource = viewModel.RemoteTrackItems;
        FitSelectorToOptions(SortSelector);
        FitSelectorToOptions(FormatSelector);
        InitializeTrackNavigation();
        Apply();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnUnloaded");
        viewModel?.CancelTrackReorder();
        trackReorderContext = null;
        DisposeTrackNavigation();
        lyricFollow?.Stop();
        Localization.LanguageChanged -= OnLanguageChanged;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.MissingFileRequested -= MissingFileRequested;
            viewModel.NavigationChanged -= Apply;
            viewModel.ActiveLyricsChanged -= OnActiveLyricsChanged;
            viewModel.Tracks.CollectionChanged -= OnTracksCollectionChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnViewModelPropertyChanged");
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.Playback):
                // Progress frames arrive here too; only a different song moves the "go to playing" button.
                if (viewModel?.Playback.ActiveTrackId != locatePlayingId)
                    RefreshLocateTarget();
                break;
            case nameof(MainViewModel.AvailableFormats):
                RebuildFormatSelector();
                // Whether the filter is offered at all depends on how many formats there are.
                QueueApply();
                break;
            case nameof(MainViewModel.TypeFilter):
            case nameof(MainViewModel.SortOption):
            case nameof(MainViewModel.ArtistSortOption):
            case nameof(MainViewModel.IsSavingTrackOrder):
            case nameof(MainViewModel.IsEditingQueue):
            case nameof(MainViewModel.CanEditLocalQueue):
            case nameof(MainViewModel.PageTitle):
            case nameof(MainViewModel.TotalCount):
            case nameof(MainViewModel.LoadedCount):
            case nameof(MainViewModel.SelectedCount):
            case nameof(MainViewModel.QueueTrackCount):
            case nameof(MainViewModel.IsMultiSelect):
            case nameof(MainViewModel.IsLyricsVisible):
            case nameof(MainViewModel.LyricsStatusText):
            case nameof(MainViewModel.LyricsMetaText):
            case nameof(MainViewModel.IsGroupListVisible):
            case nameof(MainViewModel.IsInsideGroup):
            case nameof(MainViewModel.IsLoadingPage):
            case nameof(MainViewModel.HasMore):
            case nameof(MainViewModel.IsImporting):
            case nameof(MainViewModel.ImportPercent):
            case nameof(MainViewModel.ImportStatusText):
            case nameof(MainViewModel.IsConnected):
            case nameof(MainViewModel.SearchText):
            case nameof(MainViewModel.CanImport):
            case nameof(MainViewModel.IsRemoteMode):
            case nameof(MainViewModel.IsRemoteBusy):
            case nameof(MainViewModel.ActiveRemoteName):
            case nameof(MainViewModel.RemoteStatusText):
            case nameof(MainViewModel.CanLoadMoreRemote):
            case nameof(MainViewModel.IsRemoteTruncated):
            case nameof(MainViewModel.HasMultipleRemoteLibraries):
            case nameof(MainViewModel.RemoteLibraries):
            case nameof(MainViewModel.SelectedRemoteLibrary):
            case nameof(MainViewModel.TrackNavigationIdentity):
            case nameof(MainViewModel.TrackWindowOffset):
            case nameof(MainViewModel.CurrentTrackDetail):
            case nameof(MainViewModel.CanEditPlaybackLyrics):
                QueueApply();
                break;
        }
    }

    private void OnLanguageChanged()
    {
        if (!IsLoaded)
            return;
        ApplyStrings();
        RebuildFormatSelector();
        Apply();
    }

    /// <summary>Every fixed string on this page; re-read when the language changes.</summary>
    private void ApplyStrings()
    {
        var text = Localization.Get;
        PlayAllLabel.Text = text("library.playAll");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayAllButton, text("library.playAll"));
        ImportLabelText.Text = text("library.import");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ImportButton, text("library.import"));
        ImportFilesButton.Text = text("library.menu.importFiles");
        ImportFolderButton.Text = text("library.menu.importFolder");
        ImportPlaylistButton.Text = text("library.menu.importPlaylist");
        CancelImportButton.Content = text("library.import.cancel");
        Search.PlaceholderText = text("library.search");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Search, text("library.search"));
        foreach (var item in SortSelector.Items.OfType<ComboBoxItem>())
            item.Content = text("library.sort." + (item.Tag as string));
        foreach (var item in ArtistSortSelector.Items.OfType<ComboBoxItem>())
            item.Content = text("library.artistSort." + (item.Tag as string));
        SetTip(ArtistSortSelector, text("library.sort"));
        FitSelectorToOptions(ArtistSortSelector);
        // A ComboBox keeps showing the old text of its selected item; reselecting re-reads it.
        // (Selecting the same option again is a no-op for the view model.)
        var sortIndex = SortSelector.SelectedIndex;
        if (sortIndex >= 0)
        {
            SortSelector.SelectedIndex = -1;
            SortSelector.SelectedIndex = sortIndex;
        }

        SetTip(SortSelector, text("library.sort"));
        SetTip(FormatSelector, text("library.format"));
        FormatSelector.PlaceholderText = text("library.format.all");
        SelectLabel.Text = text("library.select");
        // The accessible name matches the visible label; the tip says a little more.
        ToolTipService.SetToolTip(SelectButton, text("library.select.tip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectButton, text("library.select"));
        SetTip(MoreButton, text("common.more"));
        SetTip(LocateButton, text("library.locatePlaying"));
        TrackNavigationMenuItem.Text = text("player.navRail.aria");
        ColumnTitle.Text = RemoteColumnTitle.Text = text("track.title");
        ColumnAlbum.Text = text("track.album");
        ColumnDuration.Text = RemoteColumnDuration.Text = text("track.duration");
        ColumnFormat.Text = RemoteColumnFormat.Text = text("track.format");
        RemoteColumnSize.Text = text("track.size");
        SelectionPlayButton.Label = text("library.selection.play");
        SelectionPlayNextButton.Label = text("queue.next");
        SelectionQueueAppendButton.Label = text("queue.append");
        SelectionQueueRemoveButton.Label = text("queue.remove");
        SelectionAddButton.Label = text("track.addToPlaylist");
        SelectionFavoriteButton.Label = text("track.favorite");
        RemoveFromPlaylistBarButton.Label = text("track.removeFromPlaylist");
        SelectionDeleteButton.Label = text("track.deleteFromLibrary");
        SelectAllButton.Label = text("library.selection.all");
        SelectionDoneButton.Label = text("common.done");
        RefreshRemoteButton.Content = text("common.refresh");
        CloseRemoteButton.Content = text("network.back");
        RemoteLibraryLabel.Text = text("network.folder");
        RemoteLibrarySelector.PlaceholderText = text("network.folder.default");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RemoteLibrarySelector, text("network.folder"));
        LoadMoreRemoteButton.Content = text("common.loadMore");
        LoadMoreButton.Content = text("common.loadMore");
        CloseLyricsLabel.Text = text("lyrics.back");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseLyricsButton, text("lyrics.back"));
        LyricsFileButton.Content = text("lyrics.file");
        BindLyricsButton.Text = text("lyrics.file.bind");
        ClearLyricsButton.Text = text("lyrics.file.clear");
        ReparseLyricsButton.Text = text("lyrics.file.reload");
        FitSelectorToOptions(SortSelector);
    }

    private static void SetTip(Control control, string label)
    {
        ToolTipService.SetToolTip(control, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
    }

    private void QueueApply()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.QueueApply");
        if (applyQueued)
            return;
        applyQueued = DispatcherQueue.TryEnqueue(() =>
        {
            applyQueued = false;
            if (!IsLoaded)
                return;
            Apply();
        });
    }

    // ---- toolbar layout ----
    // The default ComboBox template shows the selection inside its Padding beside a
    // fixed 38px chevron column. Fixed selector widths clipped "自定义顺序" and
    // "全部格式" (worse with Windows text scaling), so each selector's minimum width
    // follows its longest option and stays put between selections.
    private const double ComboBoxChevronColumnWidth = 38;
    private const double LayoutRoundingSlack = 2;
    // Below this much room beside the selectors the search box stops being usable,
    // so the selector group wraps under it instead.
    private const double SearchBoxMinUsefulWidth = 160;
    private bool toolbarStacked;
    private static double MeasureTextWidth(string text, Microsoft.UI.Xaml.Media.FontFamily fontFamily, double fontSize)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static void FitSelectorToOptions(ComboBox selector)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.FitSelectorToOptions");
        var widest = MeasureTextWidth(selector.PlaceholderText ?? string.Empty, selector.FontFamily, selector.FontSize);
        foreach (var item in selector.Items.OfType<ComboBoxItem>())
        {
            widest = Math.Max(widest, MeasureTextWidth(item.Content as string ?? string.Empty, selector.FontFamily, selector.FontSize));
        }

        selector.MinWidth = Math.Ceiling(widest + selector.Padding.Left + selector.Padding.Right + ComboBoxChevronColumnWidth + LayoutRoundingSlack);
    }

    private void LibraryToolbarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width != args.PreviousSize.Width)
            ApplyToolbarLayout();
    }

    private void ToolbarActionsSizeChanged(object sender, SizeChangedEventArgs args) => ApplyToolbarLayout();
    private void ApplyToolbarLayout()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ApplyToolbarLayout");
        var width = LibraryToolbar.ActualWidth;
        if (width <= 0)
            return;
        // Only the row placement changes here, never the toolbar's width, so the
        // resulting SizeChanged cannot flip the decision back.
        var stacked = width < ToolbarActions.DesiredSize.Width + LibraryToolbar.ColumnSpacing + SearchBoxMinUsefulWidth;
        if (stacked == toolbarStacked)
            return;
        toolbarStacked = stacked;
        if (stacked)
            LibraryToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else
            LibraryToolbar.RowDefinitions.RemoveAt(LibraryToolbar.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(Search, stacked ? 2 : 1);
        Grid.SetRow(ToolbarActions, stacked ? 1 : 0);
        Grid.SetColumn(ToolbarActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(ToolbarActions, stacked ? 2 : 1);
        Grid.SetRow(ImportPanel, stacked ? 2 : 1);
    }

    // ---- track columns ----
    // The viewport owns the responsive state. Never change a row's column widths
    // in its own SizeChanged callback: that invalidates the layout being arranged
    // while NavigationView is animating, and repeats for every realized row.
    private readonly record struct TrackColumnLayout(int Mode, double NumberWidth);
    private static readonly TrackColumnLayout UnappliedTrackColumns = new(-1, 0);
    private readonly Dictionary<Grid, TrackColumnLayout> trackColumnLayouts = new();
    private bool columnLayoutQueued;
    private int trackNumberDigits;
    private double trackNumberColumnWidth = 24;
    // Row numbers are positions in the whole collection (five digits in a
    // 10,000-track library); the "#" column widens to fit them instead of clipping.
    private static double TrackNumberColumnWidth(int digits)
    {
        var fontFamily = Application.Current.Resources.TryGetValue("ContentControlThemeFontFamily", out var resource) && resource is Microsoft.UI.Xaml.Media.FontFamily family ? family : Microsoft.UI.Xaml.Media.FontFamily.XamlAutoFontFamily;
        return Math.Max(24, Math.Ceiling(MeasureTextWidth(new string ('0', digits), fontFamily, 11) + LayoutRoundingSlack));
    }

    private void TrackColumnsLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TrackColumnsLoaded");
        if (sender is not Grid grid)
            return;
        trackColumnLayouts[grid] = UnappliedTrackColumns;
        QueueTrackColumnLayout();
        ResizeTrackNavigationRail();
    }

    // Rows narrow when multi-select check boxes appear. Only queue the shared pass
    // here; it realigns the header with the rows.
    private void TrackRowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) >= 1)
            QueueTrackColumnLayout();
    }

    private void TrackColumnsUnloaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TrackColumnsUnloaded");
        if (sender is Grid grid)
            trackColumnLayouts.Remove(grid);
    }

    private void TrackViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TrackViewportSizeChanged");
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("ui.catalog.viewportWidth", args.NewSize.Width);
        QueueTrackColumnLayout();
    }

    private void QueueTrackColumnLayout()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.QueueTrackColumnLayout");
        if (columnLayoutQueued)
            return;
        columnLayoutQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("layout.catalog.columnsApply");
            columnLayoutQueued = false;
            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("layout.catalog.realizedRows", trackColumnLayouts.Count);
            // Use the parent constraint, not a child's content-driven desired size.
            var width = Math.Max(0, TrackViewport.ActualWidth - 36);
            var mode = width >= 700 ? 2 : width >= 580 ? 1 : 0;
            var layout = new TrackColumnLayout(mode, trackNumberColumnWidth);
            foreach (var grid in trackColumnLayouts.Keys.ToArray())
            {
                if (trackColumnLayouts[grid] == layout)
                    continue;
                trackColumnLayouts[grid] = layout;
                grid.ColumnDefinitions[0].Width = new GridLength(layout.NumberWidth);
                grid.ColumnDefinitions[3].Width = mode == 2 ? new GridLength(0.65, GridUnitType.Star) : new GridLength(0);
                grid.ColumnDefinitions[5].Width = new GridLength(mode >= 1 ? 56 : 0);
                foreach (var child in grid.Children.OfType<FrameworkElement>())
                {
                    if (Grid.GetColumn(child) == 3)
                        child.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
                    if (Grid.GetColumn(child) == 5)
                        child.Visibility = mode >= 1 ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            AlignTrackListHeader();
        });
    }

    /// <summary>
    /// Multi-select check boxes shift row content inside the item chrome while the
    /// header stays put. The header follows a realized row's layout slot (not its
    /// rendered position, so item transitions are ignored) to stay over its columns.
    /// </summary>
    private void AlignTrackListHeader()
    {
        var first = Math.Max(0, (Tracks.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex ?? 0);
        var row = (Tracks.ContainerFromIndex(first) as ListViewItem)?.ContentTemplateRoot as FrameworkElement;
        var inset = row is null ? 0 : Math.Max(0, Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(row).X);
        var margin = TrackListHeader.Margin;
        if (Math.Abs(margin.Left - inset) >= 0.5)
            TrackListHeader.Margin = new Thickness(inset, margin.Top, margin.Right, margin.Bottom);
    }

    // ---- state mapping ----
    private void Apply()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.Apply");
        if (viewModel is null)
        {
            return;
        }

        var groupListVisible = viewModel.IsGroupListVisible;
        ImportPlaylistButton.IsEnabled = viewModel.CanImport;
        ArtistAlbumsSection.Visibility = viewModel.IsArtistDetail && !viewModel.IsLyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        ArtistAlbumsLabel.Text = Localization.Get("nav.albums");
        ArtistAlbumsList.ItemsSource = viewModel.Groups;
        ArtistSortSelector.Visibility = viewModel.IsArtistList ? Visibility.Visible : Visibility.Collapsed;
        ArtistSortSelector.SelectedIndex = viewModel.ArtistSortOption == "track-count" ? 1 : 0;
        var remoteMode = viewModel.IsRemoteMode;
        // Controls that act on a list of songs are absent where there is none (albums, artists, a
        // network library), not left on screen disabled. A format filter only appears when it can
        // narrow something, or while one is on and has to be switched off.
        var songTools = !groupListVisible && !remoteMode;
        SortSelector.Visibility = songTools && !viewModel.IsArtistList ? Visibility.Visible : Visibility.Collapsed;
        FormatSelector.Visibility = songTools && (viewModel.AvailableFormats.Count > 1 || viewModel.TypeFilter.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Visibility = songTools ? Visibility.Visible : Visibility.Collapsed;
        var lyricsVisible = viewModel.IsLyricsVisible && !groupListVisible && !remoteMode;
        var selecting = viewModel.IsMultiSelect && !lyricsVisible && !groupListVisible && !remoteMode;
        PlayAllButton.IsEnabled = viewModel.IsConnected && !groupListVisible && !remoteMode && viewModel.TotalCount > 0;
        PlayAllButton.Visibility = ImportButton.Visibility = lyricsVisible ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = remoteMode ? Visibility.Visible : Visibility.Collapsed;
        RemoteTitle.Visibility = Visibility.Collapsed;
        RemoteStatus.Text = viewModel.RemoteStatusText;
        RefreshRemoteButton.IsEnabled = remoteMode && !viewModel.IsRemoteBusy;
        // R4-4: library selector. Shown only when the provider actually exposes more
        // than one library, so single-library WebDAV roots do not get a dead control.
        RemoteLibraryRow.Visibility = remoteMode && viewModel.HasMultipleRemoteLibraries ? Visibility.Visible : Visibility.Collapsed;
        SyncRemoteLibrarySelector(viewModel);
        // R4-4: "load more" is only offered while the host reports truncation and the
        // request cap is not yet reached; otherwise say why it is unavailable.
        LoadMoreRemoteButton.Visibility = remoteMode && viewModel.CanLoadMoreRemote ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreRemoteButton.IsEnabled = remoteMode && !viewModel.IsRemoteBusy;
        var showNotice = remoteMode && viewModel.IsRemoteTruncated && !viewModel.CanLoadMoreRemote;
        RemoteTruncationNotice.Text = showNotice ? Localization.Get("network.truncated") : string.Empty;
        RemoteTruncationNotice.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
        GroupsList.Visibility = groupListVisible && !remoteMode ? Visibility.Visible : Visibility.Collapsed;
        Tracks.Visibility = groupListVisible || lyricsVisible || remoteMode ? Visibility.Collapsed : Visibility.Visible;
        TrackListHeader.Visibility = !groupListVisible && !lyricsVisible && !remoteMode ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = lyricsVisible && !groupListVisible && !remoteMode ? Visibility.Visible : Visibility.Collapsed;
        BackToGroupsButton.Visibility = viewModel.CanGoBackGroup && !lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        BackToGroupsLabel.Text = viewModel.GroupBackLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackToGroupsButton, viewModel.GroupBackLabel);
        SortSelector.IsEnabled = !groupListVisible && !remoteMode;
        FormatSelector.IsEnabled = !groupListVisible && !remoteMode;
        Search.IsEnabled = !groupListVisible && !remoteMode;
        SelectButton.IsEnabled = viewModel.IsConnected && !groupListVisible && !remoteMode && viewModel.TotalCount > 0;
        // The toolbar belongs to the list. Lyrics hide it; selecting replaces it with the selection's commands.
        var toolbarShown = !lyricsVisible && !selecting;
        Search.Visibility = toolbarShown && songTools ? Visibility.Visible : Visibility.Collapsed;
        ToolbarActions.Visibility = toolbarShown && (songTools || viewModel.IsArtistList) ? Visibility.Visible : Visibility.Collapsed;
        // With lyrics open the page is about the playing track, so the header names it.
        PageTitle.Text = lyricsVisible ? viewModel.PlaybackTitle : remoteMode ? viewModel.ActiveRemoteName : viewModel.PageTitle;
        var numberDigits = Math.Max(1, viewModel.TotalCount).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        if (numberDigits != trackNumberDigits)
        {
            trackNumberDigits = numberDigits;
            trackNumberColumnWidth = TrackNumberColumnWidth(numberDigits);
            QueueTrackColumnLayout();
        }

        var total = viewModel.TotalCount.ToString("N0", OFPlayer_WinUI.Services.Localization.Culture);
        CountLabel.Text = lyricsVisible ? viewModel.PlaybackArtist : remoteMode ? Localization.Get("source.network") : !viewModel.IsConnected ? string.Empty : Localization.Format(!groupListVisible ? "library.count.songs" : viewModel.ActiveCollectionRef == "view:artists" && !viewModel.CanGoBackGroup ? "library.count.artists" : "library.count.albums", ("count", total));
        LoadMoreButton.IsEnabled = !groupListVisible && viewModel.HasMore && !viewModel.IsLoadingPage;
        LoadMoreButton.Visibility = !remoteMode && !groupListVisible && !lyricsVisible && viewModel.HasMore && viewModel.LoadedCount <= 24 ? Visibility.Visible : Visibility.Collapsed;
        PageProgress.IsActive = viewModel.IsLoadingPage && !lyricsVisible;
        PageProgress.Visibility = PageProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        var canImport = viewModel.CanImport;
        ImportFilesButton.IsEnabled = canImport;
        ImportFolderButton.IsEnabled = canImport;
        ImportButton.IsEnabled = canImport;
        ImportPanel.Visibility = viewModel.IsImporting ? Visibility.Visible : Visibility.Collapsed;
        ImportProgress.Value = viewModel.ImportPercent;
        ImportLabel.Text = viewModel.ImportStatusText;
        LyricsStatus.Text = viewModel.LyricsStatusText;
        LyricsFileButton.IsEnabled = viewModel.CanEditPlaybackLyrics;
        // What the lyric file itself declares (title, artist, language, offset).
        LyricsMeta.Text = viewModel.LyricsMetaText;
        LyricsMeta.Visibility = string.IsNullOrWhiteSpace(viewModel.LyricsMetaText) ? Visibility.Collapsed : Visibility.Visible;
        // Leaving the list (lyrics, albums, a network library) ends a selection in progress.
        if (viewModel.IsMultiSelect && !selecting)
            EndSelection();
        // Alignment: drag reorder inside playlists (old front-end canReorderTracks).
        var canReorder = viewModel.CanReorderTracks && !lyricsVisible && !remoteMode;
        Tracks.CanDragItems = canReorder;
        Tracks.CanReorderItems = canReorder;
        Tracks.AllowDrop = canReorder;
        Tracks.IsHitTestVisible = !viewModel.IsSavingTrackOrder;
        SortSelector.IsEnabled = !groupListVisible && !remoteMode && !viewModel.IsSavingTrackOrder && viewModel.ActiveCollectionRef != "view:current-queue";
        // D3: reflect the persisted sort option in the selector.
        foreach (var sortItem in SortSelector.Items.OfType<ComboBoxItem>())
        {
            if (sortItem.Tag is string sortTag && sortTag == viewModel.SortOption)
            {
                if (!ReferenceEquals(SortSelector.SelectedItem, sortItem))
                {
                    SortSelector.SelectedItem = sortItem;
                }

                break;
            }
        }

        RemoveFromPlaylistBarButton.Visibility = InUserPlaylist ? Visibility.Visible : Visibility.Collapsed;
        var queueView = viewModel.ActiveCollectionRef == "view:current-queue";
        SelectionDeleteButton.Visibility = queueView ? Visibility.Collapsed : Visibility.Visible;
        SelectionQueueRemoveButton.Visibility = queueView ? Visibility.Visible : Visibility.Collapsed;
        var canEditQueue = viewModel.CanEditQueueTracks(SelectedRows().Select(row => row.Id));
        SelectionPlayNextButton.IsEnabled = SelectionQueueAppendButton.IsEnabled = SelectionQueueRemoveButton.IsEnabled = canEditQueue;
        var queueHint = Localization.Get(viewModel.CanEditLocalQueue ? "queue.currentKept" : "queue.localOnly");
        ToolTipService.SetToolTip(SelectionQueueRemoveButton, queueHint);
        SelectionBar.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        // A row with nothing in it takes no room (albums, a network library).
        LibraryToolbar.Visibility = Search.Visibility == Visibility.Visible || ToolbarActions.Visibility == Visibility.Visible || selecting || viewModel.IsImporting ? Visibility.Visible : Visibility.Collapsed;
        var anySelected = viewModel.SelectedCount > 0;
        SelectionPlayButton.IsEnabled = SelectionAddButton.IsEnabled = SelectionFavoriteButton.IsEnabled = RemoveFromPlaylistBarButton.IsEnabled = SelectionDeleteButton.IsEnabled = anySelected;
        SelectionCountText.Text = anySelected ? Localization.Format("library.selection.count", ("count", viewModel.SelectedCount.ToString("N0", OFPlayer_WinUI.Services.Localization.Culture))) : Localization.Get("library.selection.none");
        CatalogFooter.Visibility = LoadMoreButton.Visibility;
        ApplyEmptyState(groupListVisible);
        RefreshTrackNavigation();
        RefreshLocateTarget();
        // Switching between albums, tracks, lyrics and remote content gets one
        // shared entrance instead of a hard swap.
        var panelKey = remoteMode ? "remote" : groupListVisible ? "groups" : lyricsVisible ? "lyrics" : "tracks";
        if (visiblePanelKey is not null && panelKey != visiblePanelKey)
            PlayContentEntrance();
        visiblePanelKey = panelKey;
    }

    private string? visiblePanelKey;
    private string? trackResetKey;
    private void PlayContentEntrance()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.PlayContentEntrance");
        if (!IsLoaded)
            return;
        Motion.Show(TrackViewport, offsetY: 12, durationMs: Motion.SlowMs, restart: true);
    }

    private void ApplyEmptyState(bool groupListVisible)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ApplyEmptyState");
        if (viewModel is null)
        {
            return;
        }

        var connected = viewModel.IsConnected;
        var searching = !string.IsNullOrWhiteSpace(viewModel.SearchText);
        if (viewModel.IsRemoteMode)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (viewModel.IsLyricsVisible)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
        else if (!connected)
        {
            // The status bar above carries the retry; this only explains the empty page.
            ShowEmptyState("\uE783", Localization.Get("library.empty.notReady"), Localization.Get("library.empty.notReady.hint"), null);
        }
        else if (viewModel.TotalCount == 0 && searching)
        {
            ShowEmptyState("\uE721", Localization.Get("library.empty.search"), Localization.Format("library.empty.search.hint", ("query", viewModel.SearchText)), Localization.Get("library.empty.search.clear"));
        }
        else if (viewModel.TotalCount == 0 && !groupListVisible && !viewModel.IsImporting)
        {
            // Only the library itself offers importing; an empty playlist or smart list just says so.
            var collection = viewModel.ActiveCollectionRef ?? string.Empty;
            if (InUserPlaylist)
                ShowEmptyState("\uE8D6", Localization.Get("library.empty.playlist"), Localization.Get("library.empty.playlist.hint"), null);
            else if (collection.StartsWith("view:", StringComparison.Ordinal))
                ShowEmptyState("\uE8D6", Localization.Get("library.empty.view"), string.Empty, null);
            else
                ShowEmptyState("\uE8A5", Localization.Get("library.empty.library"), Localization.Get("library.empty.library.hint"), Localization.Get("library.menu.importFiles"));
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEmptyState(string glyph, string title, string hint, string? actionText)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ShowEmptyState");
        EmptyIcon.Glyph = glyph;
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyActionButton.Content = actionText;
        EmptyActionButton.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    /// <summary>A playlist the user made (the library's own "all songs" list is not one).</summary>
    private bool InUserPlaylist => viewModel?.ActiveCollectionRef is { } collection && collection.StartsWith("playlist:", StringComparison.Ordinal) && viewModel.PlaylistsForLibrary(viewModel.ActiveLibraryId).Any(playlist => playlist.Id == collection["playlist:".Length..]);

    private async void EmptyStateAction(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.EmptyStateAction");
        if (viewModel is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(viewModel.SearchText))
        {
            Search.Text = string.Empty;
            viewModel.SetSearchText(string.Empty);
            return;
        }

        ImportFiles(this, new RoutedEventArgs());
    }

    // ---- remote external library (D4) ----
    private async void RefreshRemoteClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.RefreshRemoteClicked");
        if (viewModel is not null && viewModel.IsRemoteMode)
        {
            await viewModel.RefreshRemoteAsync();
        }
    }

    private void CloseRemoteClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.CloseRemoteClicked");
        viewModel?.CloseRemote();
    }

    // ---- R4-4: remote library selection and honest pagination ----
    /// <summary>
    /// Mirrors the view model's library list into the selector without re-entering
    /// SelectionChanged (which would restart the listing we just loaded).
    /// </summary>
    private void SyncRemoteLibrarySelector(MainViewModel viewModel)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.SyncRemoteLibrarySelector");
        var desired = viewModel.RemoteLibraries.ToList();
        var sameShape = RemoteLibrarySelector.Items.Count == desired.Count;
        if (sameShape)
        {
            for (var index = 0; index < desired.Count; index++)
            {
                if (!ReferenceEquals(RemoteLibrarySelector.Items[index], desired[index]))
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (!sameShape)
        {
            suppressRemoteLibraryChange = true;
            try
            {
                RemoteLibrarySelector.Items.Clear();
                foreach (var library in desired)
                {
                    RemoteLibrarySelector.Items.Add(library);
                }
            }
            finally
            {
                suppressRemoteLibraryChange = false;
            }
        }

        var selected = viewModel.SelectedRemoteLibrary;
        if (!ReferenceEquals(RemoteLibrarySelector.SelectedItem, selected))
        {
            suppressRemoteLibraryChange = true;
            try
            {
                RemoteLibrarySelector.SelectedItem = selected;
            }
            finally
            {
                suppressRemoteLibraryChange = false;
            }
        }
    }

    private void RemoteLibrarySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.RemoteLibrarySelectionChanged");
        if (suppressRemoteLibraryChange || viewModel is null)
        {
            return;
        }

        if (RemoteLibrarySelector.SelectedItem is RemoteLibraryItem library)
        {
            // The setter kicks off the listing for the newly selected library.
            viewModel.SelectedRemoteLibrary = library;
        }
    }

    private async void LoadMoreRemoteClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.LoadMoreRemoteClicked");
        if (viewModel is not null && viewModel.IsRemoteMode)
        {
            await viewModel.LoadMoreRemoteAsync();
        }
    }

    private void PlayRemoteTrack(object sender, ItemClickEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.PlayRemoteTrack");
        if (viewModel is null || args.ClickedItem is not RemoteTrackItem track)
        {
            return;
        }

        _ = viewModel.PlayRemoteTrackAsync(track);
    }

    private void RebuildFormatSelector()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.RebuildFormatSelector");
        if (viewModel is null)
        {
            return;
        }

        var selected = viewModel.TypeFilter;
        FormatSelector.Items.Clear();
        FormatSelector.Items.Add(new ComboBoxItem { Content = Localization.Get("library.format.all"), Tag = string.Empty });
        foreach (var format in viewModel.AvailableFormats)
        {
            FormatSelector.Items.Add(new ComboBoxItem { Content = format.ToUpperInvariant(), Tag = format });
        }

        FitSelectorToOptions(FormatSelector);
        foreach (var item in FormatSelector.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == selected)
            {
                FormatSelector.SelectedItem = item;
                return;
            }
        }

        FormatSelector.SelectedIndex = 0;
    }

    // ---- list interaction ----
    private void GroupArtworkLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.GroupArtworkLoaded");
        if (sender is Image image)
            _ = ApplyGroupArtworkAsync(image);
    }

    private void GroupArtworkChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.GroupArtworkChanged");
        if (sender is Image image)
            _ = ApplyGroupArtworkAsync(image);
    }

    private sealed class ArtworkBinding
    {
        public string? TrackId;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, ArtworkBinding> artworkBindings = new();
    private void ResetArtworkIfTrackChanged(Image image, string? trackId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ResetArtworkIfTrackChanged");
        var binding = artworkBindings.GetOrCreateValue(image);
        if (binding.TrackId == trackId)
            return;
        binding.TrackId = trackId;
        // Loaded can fire again during transitions. Only recycled content needs clearing.
        _ = ArtworkLoader.ApplyAsync(image, null);
    }

    private async Task ApplyGroupArtworkAsync(Image image)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ApplyGroupArtworkAsync");
        var entry = image.DataContext as GroupEntry;
        ResetArtworkIfTrackChanged(image, entry?.SampleTrackId);
        var model = viewModel ?? MainViewModel.Active;
        if (entry?.SampleTrackId is not string trackId || model is null)
            return;
        var artwork = await model.LoadRowArtworkAsync(trackId);
        if (image.IsLoaded && ReferenceEquals(image.DataContext, entry))
            await ArtworkLoader.ApplyAsync(image, artwork);
    }

    private void TrackArtworkLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TrackArtworkLoaded");
        if (sender is Image image)
            QueueRowArtwork(image);
    }

    private void TrackArtworkChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TrackArtworkChanged");
        if (sender is Image image)
            QueueRowArtwork(image);
    }

    private void QueueRowArtwork(Image image)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.QueueRowArtwork");
        var row = image.DataContext;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (!image.IsLoaded || !ReferenceEquals(image.DataContext, row))
                return;
            ResetArtworkIfTrackChanged(image, (row as TrackQueryRow)?.Id);
            await ApplyRowArtworkAsync(image);
        });
    }

    private async Task ApplyRowArtworkAsync(Image image)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ApplyRowArtworkAsync");
        if (image.DataContext is not TrackQueryRow row)
            return;
        var model = viewModel ?? MainViewModel.Active;
        var artwork = !string.IsNullOrWhiteSpace(row.ArtworkPath) ? row.ArtworkPath : model is null ? null : await model.LoadRowArtworkAsync(row.Id);
        if (image.IsLoaded && ReferenceEquals(image.DataContext, row))
            await ArtworkLoader.ApplyAsync(image, artwork);
    }

    private void OnTrackContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnTrackContainerChanging");
        if (args.Phase != 0 || viewModel is null || viewModel.IsGroupListVisible)
        {
            return;
        }

        if (!trackNavigationJumping && args.ItemIndex >= viewModel.Tracks.Count - 24 && viewModel.HasMore && !viewModel.IsLoadingPage)
        {
            _ = viewModel.LoadMoreAsync();
        }
    }

    private async void OpenGroup(object sender, ItemClickEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.OpenGroup");
        if (viewModel is not null && args.ClickedItem is GroupEntry entry)
        {
            await viewModel.OpenGroupAsync(entry);
        }
    }

    private async void BackToGroups(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.BackToGroups");
        if (viewModel is not null)
        {
            await viewModel.BackToGroupsAsync();
        }
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.SearchChanged");
        if (viewModel is not null && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            viewModel.SetSearchText(sender.Text);
        }
    }

    private void SortChanged(object sender, SelectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.SortChanged");
        if (viewModel is not null && SortSelector.SelectedItem is ComboBoxItem item && item.Tag is string option)
        {
            viewModel.SetSortOption(option);
        }
    }

    private async void ArtistSortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (viewModel is not null && ArtistSortSelector.SelectedItem is ComboBoxItem { Tag: string option })
            await viewModel.SetArtistSortOptionAsync(option);
    }

    private void FormatChanged(object sender, SelectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.FormatChanged");
        if (viewModel is not null && FormatSelector.SelectedItem is ComboBoxItem item && item.Tag is string format)
        {
            viewModel.SetTypeFilter(format);
        }
    }

    /// <summary>Ctrl+F: moves the keyboard to this page's search box, when the page has one to offer.</summary>
    internal bool TryFocusSearch() => Visibility == Visibility.Visible && Search.Visibility == Visibility.Visible && Search.IsEnabled && Search.Focus(FocusState.Keyboard);
    private void MultiSelectToggled(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.MultiSelectToggled");
        if (viewModel is null)
        {
            return;
        }

        if (viewModel.IsMultiSelect)
            EndSelection();
        else
        {
            viewModel.ToggleMultiSelect();
            Tracks.SelectionMode = ListViewSelectionMode.Multiple;
        }
    }

    private void EndSelection()
    {
        if (viewModel?.IsMultiSelect != true)
            return;
        Tracks.SelectedItems.Clear();
        viewModel.ToggleMultiSelect();
        Tracks.SelectionMode = ListViewSelectionMode.Single;
    }

    private void TrackSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.TrackSelectionChanged");
        viewModel?.SetSelectedRows(Tracks.SelectedItems.OfType<TrackQueryRow>().ToList());
        _ = viewModel?.SetSelectedRowAsync(Tracks.SelectedItems.OfType<TrackQueryRow>().FirstOrDefault());
        UpdateLocateButton();
    }

    private async void PlayTrack(object sender, ItemClickEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.PlayTrack");
        if (viewModel is not null && !viewModel.IsMultiSelect && args.ClickedItem is TrackQueryRow row)
        {
            await viewModel.PlayTrackAsync(row);
        }
    }

    private async void PlaySelection(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.PlaySelection");
        if (viewModel is null)
        {
            return;
        }

        var rows = SelectedRows();
        if (rows.Count > 0)
        {
            await viewModel.PlayRowsAsync(rows);
        }
    }

    private async void PlayAll(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.PlayAll");
        if (viewModel is not null)
        {
            await viewModel.PlayAllAsync();
        }
    }

    private async void LoadMore(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.LoadMore");
        if (viewModel is not null)
        {
            await viewModel.LoadMoreAsync();
        }
    }

    private void TracksKeyDown(object sender, KeyRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.TracksKeyDown");
        if (viewModel is null)
        {
            return;
        }

        var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var controlDown = controlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (controlDown && args.Key == VirtualKey.A)
        {
            if (!viewModel.IsMultiSelect)
            {
                viewModel.ToggleMultiSelect();
                Tracks.SelectionMode = ListViewSelectionMode.Multiple;
            }

            Tracks.SelectAll();
            args.Handled = true;
            return;
        }

        if (args.Key == VirtualKey.Escape && viewModel.IsMultiSelect)
        {
            EndSelection();
            args.Handled = true;
        }
    }

    // ---- alignment: playlist drag reorder persistence ----
    private MainViewModel.TrackReorderContext? trackReorderContext;
    private void TracksDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.TracksDragStarting");
        trackReorderContext = viewModel?.BeginTrackReorder();
        args.Cancel = trackReorderContext is null;
        if (!args.Cancel)
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void TracksDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.TracksDragCompleted");
        var context = trackReorderContext;
        var moved = args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("catalog.reorder.drop", new { result = args.DropResult.ToString() });
        // Let ListView finish its internal removal/insertion before taking the
        // final order. The source collection may update after this routed event.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (trackReorderContext != context)
                return;
            trackReorderContext = null;
            if (viewModel is not null)
                await viewModel.CompleteTrackReorderAsync(context, moved);
        }))
        {
            trackReorderContext = null;
            viewModel?.CancelTrackReorder();
        }
    }

    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnTracksCollectionChanged");
        // A reset for a different collection means new content is about to be
        // added in this same dispatch; it enters as one piece. Refreshes of the
        // same collection (search, sort, edits) update in place without motion.
        if (args.Action == NotifyCollectionChangedAction.Reset && viewModel is not null)
        {
            var key = $"{viewModel.ActiveLibraryId}|{viewModel.ActiveCollectionRef}|{viewModel.PageTitle}|{viewModel.IsInsideGroup}";
            if (trackResetKey is not null && key != trackResetKey && Tracks.Visibility == Visibility.Visible)
                PlayContentEntrance();
            trackResetKey = key;
        }

        UpdateLocateButton();
        // WinUI's built-in reorder may emit Remove+Add rather than Move.
        // Save only once at DragItemsCompleted; keep row numbers live in either case.
        if (trackReorderContext is not null)
        {
            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("catalog.reorder.collection", new { action = args.Action.ToString(), args.OldStartingIndex, args.NewStartingIndex });
            viewModel?.RenumberTracks();
        }
    }

    // ---- row-level actions (old front-end: inline favorite + more menu) ----
    private async void RowFavoriteClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.RowFavoriteClicked");
        if (viewModel is null || (sender as FrameworkElement)?.DataContext is not TrackQueryRow row)
        {
            return;
        }

        await viewModel.SetFavoriteAsync(row.Id, !row.Favorite);
    }

    private async void RowMoreClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.RowMoreClicked");
        if (viewModel is null || sender is not FrameworkElement anchor || anchor.DataContext is not TrackQueryRow row)
        {
            return;
        }

        BuildTrackMenu(new[] { row }).ShowAt(anchor);
    }

    private void RowFavoriteLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
            SetTip(button, Localization.Get("track.favorite.toggle"));
    }

    private void RowMoreLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
            SetTip(button, Localization.Get("common.more"));
    }

    /// <summary>
    /// Right-click, or the context-menu key on the focused row. The menu acts on the
    /// row it was opened on; when that row is part of a multi-selection it acts on
    /// the whole selection, matching the command bar.
    /// </summary>
    private void TracksContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (viewModel is null)
            return;
        TrackQueryRow? row;
        var hasPoint = args.TryGetPosition(Tracks, out var point);
        if (hasPoint)
            row = (args.OriginalSource as FrameworkElement)?.DataContext as TrackQueryRow;
        else
            row = (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as ListViewItem)?.Content as TrackQueryRow;
        if (row is null)
            return;
        args.Handled = true;
        var selected = SelectedRows();
        IReadOnlyList<TrackQueryRow> targets = viewModel.IsMultiSelect && selected.Count > 1 && selected.Contains(row) ? selected : new[]
        {
            row
        };
        var flyout = BuildTrackMenu(targets);
        if (hasPoint)
            flyout.ShowAt(Tracks, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else if (Tracks.ContainerFromItem(row)is FrameworkElement container)
            flyout.ShowAt(container);
    }

    private MenuFlyout BuildTrackMenu(IReadOnlyList<TrackQueryRow> rows)
    {
        var model = viewModel!;
        var text = Localization.Get;
        var ids = model.Tracks.Where(rows.Contains).Select(row => row.Id).ToList();
        var single = rows.Count == 1 ? rows[0] : null;
        var flyout = new MenuFlyout();
        MenuFlyoutItem Item(string label, string glyph, Func<Task> action)
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon
                {
                    Glyph = glyph
                }
            };
            item.Click += async (_, _) => await action();
            return item;
        }

        flyout.Items.Add(single is not null ? Item(text("player.play"), "\uE768", () => model.PlayTrackAsync(single)) : Item(text("library.selection.play"), "\uE768", () => model.PlayRowsAsync(rows)));
        MenuFlyoutItem QueueItem(string key, string glyph, Func<Task> action)
        {
            var item = Item(text(key), glyph, action);
            item.IsEnabled = model.CanEditQueueTracks(ids);
            if (!item.IsEnabled)
                ToolTipService.SetToolTip(item, text(model.CanEditLocalQueue ? "queue.currentKept" : "queue.localOnly"));
            return item;
        }

        flyout.Items.Add(QueueItem("queue.next", "\uE893", () => model.PlayTracksNextAsync(ids)));
        flyout.Items.Add(QueueItem("queue.append", "\uE710", () => model.AppendTracksToQueueAsync(ids)));
        if (model.ActiveCollectionRef == "view:current-queue")
            flyout.Items.Add(QueueItem("queue.remove", "\uE738", () => model.RemoveTracksFromQueueAsync(ids)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (single?.IsFileMissing == true)
            flyout.Items.Add(Item(text("missing.file.manage"), "\uE838", () => ShowMissingFileAsync(single)));
        var favorite = !rows.All(row => row.Favorite);
        flyout.Items.Add(Item(text(favorite ? "track.favorite" : "track.unfavorite"), favorite ? "\uE734" : "\uE735", () => SetFavoriteAsync(rows, favorite)));
        var add = new MenuFlyoutSubItem
        {
            Text = text("track.addToPlaylist"),
            Icon = new FontIcon
            {
                Glyph = "\uE710"
            }
        };
        AddPlaylistTargets(add.Items, ids);
        flyout.Items.Add(add);
        if (InUserPlaylist)
            flyout.Items.Add(Item(text("track.removeFromPlaylist"), "\uE738", () => RemoveFromPlaylistAsync(ids)));
#if DEBUG || OFPLAYER_DEV
        // Cloud/B features are available in local Debug and the explicit dev channel.
#endif
        if (model.ActiveCollectionRef != "view:current-queue")
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Item(text("track.deleteFromLibrary"), "\uE74D", () => DeleteFromLibraryAsync(ids)));
        }

        return flyout;
    }

    /// <summary>The library's playlists as targets, and a way to start a new one with these songs.</summary>
    private void AddPlaylistTargets(IList<MenuFlyoutItemBase> items, IReadOnlyList<string> trackIds)
    {
        var model = viewModel!;
        var libraryId = model.ActiveLibraryId;
        foreach (var playlist in model.PlaylistsForLibrary(libraryId))
        {
            var target = new MenuFlyoutItem
            {
                Text = playlist.Name
            };
            var playlistId = playlist.Id;
            target.Click += async (_, _) => await model.AddTracksToPlaylistAsync(playlistId, trackIds);
            items.Add(target);
        }

        if (items.Count > 0)
            items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem
        {
            Text = Localization.Get("nav.newPlaylist") + "…",
            Icon = new FontIcon
            {
                Glyph = "\uE710"
            }
        };
        create.Click += async (_, _) =>
        {
            var name = await PromptForNameAsync(Localization.Get("nav.newPlaylist"), Localization.Get("playlist.name"), string.Empty);
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (await model.CreatePlaylistAsync(libraryId, name)is { Length: > 0 } created)
                await model.AddTracksToPlaylistAsync(created, trackIds);
        };
        items.Add(create);
    }

    private async Task SetFavoriteAsync(IReadOnlyList<TrackQueryRow> rows, bool favorite)
    {
        foreach (var row in rows)
            await viewModel!.SetFavoriteAsync(row.Id, favorite);
    }

    private async Task RemoveFromPlaylistAsync(IReadOnlyList<string> trackIds)
    {
        if (viewModel?.ActiveCollectionRef is not { } collection || !InUserPlaylist || trackIds.Count == 0)
            return;
        if (!await ConfirmDangerAsync(Localization.Format("track.removeFromPlaylist.title", ("count", trackIds.Count.ToString())), Localization.Get("track.removeFromPlaylist.body"), Localization.Get("common.remove")))
            return;
        await viewModel.RemoveTracksFromPlaylistAsync(collection["playlist:".Length..], trackIds);
    }

    private async Task DeleteFromLibraryAsync(IReadOnlyList<string> trackIds)
    {
        if (viewModel is null || trackIds.Count == 0 || viewModel.ActiveCollectionRef == "view:current-queue")
            return;
        if (!await ConfirmDangerAsync(Localization.Format("track.delete.title", ("count", trackIds.Count.ToString())), Localization.Get("track.delete.body"), Localization.Get("common.delete")))
            return;
        await viewModel.DeleteTracksAsync(trackIds, Localization.Format("track.delete.done", ("count", trackIds.Count.ToString())));
    }

    private void SelectAllTracks(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.SelectAllTracks");
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.IsMultiSelect)
        {
            viewModel.ToggleMultiSelect();
            Tracks.SelectionMode = ListViewSelectionMode.Multiple;
        }

        Tracks.SelectAll();
    }

    private void ClearSelection(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ClearSelection");
        EndSelection();
    }

    // ---- selection operations ----
    private async void PlaySelectionNext(object sender, RoutedEventArgs args)
    {
        if (viewModel is not null)
            await viewModel.PlayTracksNextAsync(OrderedSelectedIds());
    }

    private async void AppendSelectionToQueue(object sender, RoutedEventArgs args)
    {
        if (viewModel is not null)
            await viewModel.AppendTracksToQueueAsync(OrderedSelectedIds());
    }

    private async void RemoveSelectionFromQueue(object sender, RoutedEventArgs args)
    {
        if (viewModel is not null)
            await viewModel.RemoveTracksFromQueueAsync(OrderedSelectedIds());
    }

    private IReadOnlyList<string> OrderedSelectedIds() => viewModel?.Tracks.Where(row => Tracks.SelectedItems.Contains(row)).Select(row => row.Id).ToArray() ?? Array.Empty<string>();
    private IReadOnlyList<TrackQueryRow> SelectedRows() => Tracks.SelectedItems.OfType<TrackQueryRow>().ToList();
    private async void ToggleFavoriteSelection(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ToggleFavoriteSelection");
        if (viewModel is null)
        {
            return;
        }

        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            return;
        }

        await SetFavoriteAsync(rows, !rows.All(row => row.Favorite));
    }

    private void AddSelectionToPlaylist(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.AddSelectionToPlaylist");
        var rows = SelectedRows();
        if (viewModel is null || rows.Count == 0)
        {
            return;
        }

        var flyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };
        AddPlaylistTargets(flyout.Items, rows.Select(row => row.Id).ToList());
        flyout.ShowAt(sender as FrameworkElement ?? SelectionBar);
    }

    private async void RemoveSelectionFromPlaylist(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.RemoveSelectionFromPlaylist");
        await RemoveFromPlaylistAsync(SelectedRows().Select(row => row.Id).ToList());
    }

    private async void DeleteSelection(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.DeleteSelection");
        await DeleteFromLibraryAsync(SelectedRows().Select(row => row.Id).ToList());
    }

    // ---- import ----
    private async void ImportFiles(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ImportFiles");
        if (viewModel?.CanImport != true)
        {
            return;
        }

        var paths = await PickAudioFilesAsync();
        if (paths is null)
        {
            return;
        }

        await viewModel.StartImportFilesAsync(paths);
    }

    private async void ImportFolder(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ImportFolder");
        if (viewModel?.CanImport != true)
        {
            return;
        }

        var directory = await PickFolderAsync();
        if (directory is null)
        {
            return;
        }

        await viewModel.StartImportDirectoryAsync(directory);
    }

    private async void ImportPlaylist(object sender, RoutedEventArgs args)
    {
        if (viewModel is not null)
            await PlaylistImportUI.RunAsync(viewModel, XamlRoot);
    }

    private async void CancelImport(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.CancelImport");
        if (viewModel is not null)
        {
            await viewModel.CancelImportAsync();
        }
    }

    // ---- lyrics ----
    private async void ToggleLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ToggleLyrics");
        if (viewModel is not null)
        {
            await viewModel.ToggleLyricsAsync();
        }
    }

    private async void BindLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.BindLyrics");
        if (viewModel?.CanEditPlaybackLyrics != true)
        {
            return;
        }

        var path = await PickLyricsFileAsync();
        if (path is not null)
        {
            await viewModel.BindLyricsAsync(path);
        }
    }

    private async void ClearLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ClearLyrics");
        if (viewModel is not null)
        {
            await viewModel.ClearLyricsAsync();
        }
    }

    private async void ReparseLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.ReparseLyrics");
        if (viewModel is not null)
        {
            await viewModel.ReparseLyricsAsync();
        }
    }

    private async void LyricsLineClicked(object sender, ItemClickEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.LyricsLineClicked");
        if (viewModel is not null && args.ClickedItem is LyricLineRow row)
        {
            await viewModel.SeekToLyricLineAsync(row);
        }
    }

    private FrameTimeline? lyricFollow;
    private ScrollViewer? lyricsScrollViewer;
    private LyricLineRow? lyricFollowTarget;
    private double lyricScrollStart;
    private sealed record LyricEmphasis(double Size, double Opacity, double SecondaryOpacity)
    {
        public static LyricEmphasis Of(LyricLineRow row) => new(row.TextSize, row.TextOpacity, row.TranslationOpacity);
        public static LyricEmphasis Read(StackPanel panel) => new(((TextBlock)panel.Children[1]).FontSize, panel.Children[1].Opacity, panel.Children[2].Opacity);
        public static LyricEmphasis Lerp(LyricEmphasis from, LyricEmphasis to, double t) => new(from.Size + (to.Size - from.Size) * t, from.Opacity + (to.Opacity - from.Opacity) * t, from.SecondaryOpacity + (to.SecondaryOpacity - from.SecondaryOpacity) * t);
        public void ApplyTo(StackPanel panel)
        {
            ((TextBlock)panel.Children[1]).FontSize = Size;
            panel.Children[1].Opacity = Opacity;
            panel.Children[2].Opacity = SecondaryOpacity;
            panel.Children[3].Opacity = SecondaryOpacity;
        }
    }

    private readonly List<(StackPanel Panel, LyricEmphasis From, LyricEmphasis To)> lyricTweens = new();
    private static StackPanel? LyricPanel(DependencyObject? container) => (container as ListViewItem)?.ContentTemplateRoot is StackPanel { Children.Count: 4 } panel ? panel : null;
    private void OnLyricContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (LyricPanel(args.ItemContainer)is not StackPanel panel || args.Item is not LyricLineRow row)
            return;
        lyricTweens.RemoveAll(tween => ReferenceEquals(tween.Panel, panel));
        LyricEmphasis.Of(row).ApplyTo(panel);
    }

    /// <summary>
    /// The emphasized line grows while the list glides to keep it centred. The scroll
    /// target is re-measured every frame against the live layout, so the move ends
    /// exactly on the final position instead of correcting at the end.
    /// </summary>
    private void OnActiveLyricsChanged(LyricLineRow? primary)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.OnActiveLyricsChanged");
        lyricTweens.Clear();
        if (LyricsList.ItemsPanelRoot is Panel panelRoot)
        {
            foreach (var container in panelRoot.Children)
            {
                if (LyricPanel(container)is not StackPanel panel || LyricsList.ItemFromContainer(container)is not LyricLineRow row)
                    continue;
                var from = LyricEmphasis.Read(panel);
                var to = LyricEmphasis.Of(row);
                if (from != to)
                    lyricTweens.Add((panel, from, to));
            }
        }

        var visible = IsLoaded && LyricsPanel.Visibility == Visibility.Visible;
        lyricsScrollViewer ??= FindTrackNavigationScrollViewer(LyricsList);
        lyricFollowTarget = visible ? primary : null;
        if (primary is not null && visible && LyricsList.ContainerFromItem(primary)is null)
        {
            // Far off-screen (e.g. after a seek): bring it in directly, then settle.
            ApplyLyricFollow(1);
            LyricsList.ScrollIntoView(primary, ScrollIntoViewAlignment.Leading);
            return;
        }

        lyricScrollStart = lyricsScrollViewer?.VerticalOffset ?? 0;
        lyricFollow ??= new FrameTimeline(0, ApplyLyricFollow);
        lyricFollow.SetValue(0);
        if (visible)
            lyricFollow.AnimateTo(1, 420, Motion.Decelerate);
        else
            lyricFollow.Jump(1);
    }

    private void ApplyLyricFollow(double progress)
    {
        // Emphasis settles in the first part of the move so the final centring is stable.
        var emphasisProgress = Math.Min(1, progress / 0.6);
        foreach (var(panel, from, to)in lyricTweens)
            LyricEmphasis.Lerp(from, to, emphasisProgress).ApplyTo(panel);
        if (progress >= 1)
            lyricTweens.Clear();
        if (lyricFollowTarget is null || lyricsScrollViewer is not ScrollViewer viewer || LyricsList.ContainerFromItem(lyricFollowTarget)is not FrameworkElement container)
            return;
        var top = container.TransformToVisual(viewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + viewer.VerticalOffset;
        var target = Math.Clamp(top - (viewer.ViewportHeight - container.ActualHeight) / 2, 0, viewer.ScrollableHeight);
        viewer.ChangeView(null, lyricScrollStart + (target - lyricScrollStart) * progress, null, true);
    }

    // ---- row hover actions ----
    private void TrackRowPointerEntered(object sender, PointerRoutedEventArgs args) => SetRowActionsVisible(sender, true);
    private void TrackRowPointerExited(object sender, PointerRoutedEventArgs args) => SetRowActionsVisible(sender, false);
    private void TrackRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => SetRowActionsVisible(sender, false);
    private static void SetRowActionsVisible(object sender, bool visible)
    {
        if (sender is not Grid row)
            return;
        var opacity = visible ? 1 : 0;
        foreach (var child in row.Children)
        {
            if (child is Button button)
                Motion.FadeProperty(button, opacity, 150);
            else if (child is Grid cell)
                foreach (var nested in cell.Children.OfType<Button>())
                    Motion.FadeProperty(nested, opacity, 150);
        }
    }

    // ---- pickers and dialogs (page-scoped) ----
    private async Task<IReadOnlyList<string>?> PickAudioFilesAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.PickAudioFilesAsync");
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                ViewMode = PickerViewMode.List,
            };
            InitializePicker(picker);
            foreach (var extension in AudioFileExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            using var pickerWindow = new OFPlayer.Community.Services.FilePickerWindowScope(PickerHost.MainWindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            return files.Count == 0 ? null : files.Select(file => file.Path).ToList();
        }
        catch (Exception error)
        {
            PreviewLog.Write($"file picker failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.PickFolderAsync");
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary
            };
            InitializePicker(picker);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"folder picker failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private async Task<string?> PickLyricsFileAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.PickLyricsFileAsync");
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                ViewMode = PickerViewMode.List,
            };
            InitializePicker(picker);
            picker.FileTypeFilter.Add(".lrc");
            picker.FileTypeFilter.Add(".ttml");
            picker.FileTypeFilter.Add(".txt");
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"lyrics picker failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private static void InitializePicker(object picker)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.InitializePicker");
        var handle = PickerHost.MainWindowHandle;
        if (handle != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        }
    }

    private async Task<string?> PromptForNameAsync(string title, string placeholder, string initial)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.PromptForNameAsync");
        if (XamlRoot is null)
        {
            return null;
        }

        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            SelectionStart = initial.Length,
        };
        input.MinWidth = 320;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, placeholder);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = Localization.Get("common.create"),
            CloseButtonText = Localization.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"name prompt deferred: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private async Task<bool> ConfirmDangerAsync(string title, string message, string primaryText)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("LibraryPageView.xaml.ConfirmDangerAsync");
        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = Localization.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"confirmation deferred: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }
}
