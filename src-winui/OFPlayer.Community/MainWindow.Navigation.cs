using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Views;

namespace OFPlayer.Community;

// The mainline local-library navigation and motion, with Community's available destinations.
public sealed partial class MainWindow
{
    private const string CreatePlaylistTag = "action:create-playlist";
    private const string SmartGroupTag = "group:smart";
    private const double SidebarOpenWidth = 224;
    private const double SidebarCompactWidth = 48;
    private sealed record NavEntry(string Tag, string Label, string? IconKey = null, string? Glyph = null,
        bool Header = false, bool Selectable = true, string? PlaylistId = null, IReadOnlyList<NavEntry>? Children = null);

    private static readonly (string View, string Label, string Icon)[] LibraryViews =
    {
        ("albums", "nav.albums", "MusicIcon.Albums"),
        ("artists", "nav.artists", "MusicIcon.Artists"),
        ("all-favorites", "nav.favorites", "MusicIcon.Favorites"),
        ("all-plays", "nav.recentPlays", "MusicIcon.History"),
        ("recent-imports", "nav.recentImports", "MusicIcon.RecentImports"),
        ("current-queue", "nav.queue", "MusicIcon.Queue"),
    };

    private bool navigationRefreshQueued, sidebarExpanded = true, createPlaylistOpen;
    private FrameTimeline? sidebarWidth;
    private CompositionPropertySet? sidebarMotion;
    private ScrollViewer? navigationScroller;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? smartHoverTimer;
    private bool smartHoverOpened, smartProgrammatic, smartEventsHooked;
    private bool SmartCollapsed => model.SmartPlaylistsPreference == "collapsed";

    private void ApplyNavigationStrings()
    {
        model.RefreshLocalizedText();
        ApplySidebarToggleLabel();
        BuildNavigation();
    }

    private void ApplySidebarToggleLabel()
    {
        var label = Localization.Get(sidebarExpanded ? "nav.collapse" : "nav.expand");
        AutomationProperties.SetName(SidebarToggle, label);
        ToolTipService.SetToolTip(SidebarToggle, label);
    }

    private List<NavEntry> DesiredNavigation()
    {
        if (videoMode) return [new("video:library", Localization.Get("source.video"), Glyph: "\uE173")];
        var smart = new List<NavEntry>();
        foreach (var playlist in model.PlaylistsForLibrary(model.ActiveLibraryId, includeSystem: true).Where(p => p.Kind == "system"))
            smart.Add(new($"playlist:{playlist.Id}", playlist.SystemKey == "all-tracks" ? Localization.Get("nav.allSongs") : playlist.Name,
                IconKey: "MusicIcon.AllTracks"));
        foreach (var (view, label, icon) in LibraryViews)
            smart.Add(new($"view:{view}", Localization.Get(label), IconKey: icon));
        var entries = SmartCollapsed
            ? new List<NavEntry> { new(SmartGroupTag, Localization.Get("nav.smartPlaylists"), Glyph: "\uE71D", Selectable: false, Children: smart) }
            : smart;
        entries.Add(new("header:playlists", Localization.Get("nav.playlists"), Header: true));
        entries.Add(new(CreatePlaylistTag, Localization.Get("nav.newPlaylist"), Glyph: "\uE710", Selectable: false));
        foreach (var playlist in model.PlaylistsForLibrary(model.ActiveLibraryId))
            entries.Add(new($"playlist:{playlist.Id}", playlist.Name, IconKey: "MusicIcon.EighthPair", PlaylistId: playlist.Id));
        return entries;
    }

    private static string? TagOf(object item) => (item as FrameworkElement)?.Tag as string;

    private IEnumerable<NavigationViewItem> AllNavigationItems()
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            yield return item;
            foreach (var child in item.MenuItems.OfType<NavigationViewItem>()) yield return child;
        }
    }

    private void BuildNavigation()
    {
        if (navigationRefreshQueued || closing) return;
        navigationRefreshQueued = true;
        // Native selection finishes before any structural changes. Reuse rows to preserve
        // their animation state, focus, scroll position and selection indicator.
        DispatcherQueue.TryEnqueue(() =>
        {
            navigationRefreshQueued = false;
            if (closing) return;
            navigating = true;
            try
            {
                var desired = DesiredNavigation();
                var offset = NavigationScroller()?.VerticalOffset;
                SyncNavigationItems(Navigation.MenuItems, desired);
                foreach (var entry in desired.Where(e => e.Children is not null))
                    if (Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => TagOf(i) == entry.Tag) is { } group)
                        SyncNavigationItems(group.MenuItems, entry.Children!);
                if (Navigation.SettingsItem is NavigationViewItem settings)
                {
                    settings.Content = Localization.Get("settings.title");
                    AutomationProperties.SetName(settings, Localization.Get("settings.title"));
                    ToolTipService.SetToolTip(settings, Localization.Get("settings.title"));
                }
                SyncNavigationSelection();
                if (offset is > 0)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (!closing && NavigationScroller() is { } scroller && Math.Abs(scroller.VerticalOffset - offset.Value) > 1)
                            scroller.ChangeView(null, Math.Min(offset.Value, scroller.ScrollableHeight), null, true);
                    });
            }
            finally { navigating = false; }
        });
    }

    private void SyncNavigationItems(IList<object> items, IReadOnlyList<NavEntry> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var entry = desired[index];
            if (index < items.Count && TagOf(items[index]) == entry.Tag)
            {
                ApplyNavEntry(items[index], entry);
                continue;
            }
            var existing = -1;
            for (var probe = index + 1; probe < items.Count; probe++)
                if (TagOf(items[probe]) == entry.Tag) { existing = probe; break; }
            if (existing >= 0)
            {
                var moved = items[existing];
                items.RemoveAt(existing);
                items.Insert(index, moved);
                ApplyNavEntry(moved, entry);
            }
            else items.Insert(index, CreateNavItem(entry));
        }
        while (items.Count > desired.Count) items.RemoveAt(items.Count - 1);
    }

    private object CreateNavItem(NavEntry entry)
    {
        if (entry.Header)
            return new NavigationViewItemHeader { Tag = entry.Tag, Content = entry.Label, IsTabStop = false, Margin = new(0, 8, 0, 0) };
        var item = new NavigationViewItem
        {
            Tag = entry.Tag,
            Content = new TextBlock { Text = entry.Label, TextTrimming = TextTrimming.CharacterEllipsis },
            SelectsOnInvoked = entry.Selectable,
            Icon = entry.IconKey is not null
                ? AnimatedMusicIcon.TryCreate(entry.IconKey) ?? MusicIconGeometryLoader.CreatePathIcon(entry.IconKey)
                : new FontIcon { Glyph = entry.Glyph ?? "\uE8D6" },
        };
        if (entry.PlaylistId is not null) item.ContextFlyout = PlaylistMenu(entry.PlaylistId);
        if (entry.Tag == SmartGroupTag) WireSmartGroup(item);
        ApplyNavEntry(item, entry);
        return item;
    }

    private void ApplyNavEntry(object item, NavEntry entry)
    {
        if (item is NavigationViewItemHeader header)
        {
            if (header.Content as string != entry.Label) header.Content = entry.Label;
            return;
        }
        if (item is not NavigationViewItem row) return;
        if (row.Content is TextBlock text && text.Text != entry.Label) text.Text = entry.Label;
        AutomationProperties.SetName(row, entry.Label);
        if (entry.Tag == CreatePlaylistTag) row.IsEnabled = model.IsConnected;
        var count = entry.Tag.StartsWith("playlist:", StringComparison.Ordinal) ? model.Counts.ForPlaylist(entry.Tag[9..])
            : entry.Tag.StartsWith("view:", StringComparison.Ordinal) ? model.Counts.ForView(entry.Tag[5..]) : -1;
        var tip = count < 0 ? entry.Label : Localization.Format(entry.Tag is "view:albums" or "view:artists" ? "nav.tip.groups" : "nav.tip.songs",
            ("name", entry.Label), ("count", count.ToString("N0", Localization.Culture)));
        if (ToolTipService.GetToolTip(row) as string != tip) ToolTipService.SetToolTip(row, tip);
    }

    private void SyncNavigationSelection()
    {
        var outer = navigating;
        navigating = true;
        try
        {
            object? target;
            if (SettingsPage.Visibility == Visibility.Visible) target = Navigation.SettingsItem;
            else
            {
                var tag = videoMode ? "video:library" : model.IsRemoteMode ? null : model.ActiveCollectionRef;
                var selected = AllNavigationItems().FirstOrDefault(item => TagOf(item) == tag);
                if (selected is not null && Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(group => group.MenuItems.Contains(selected)) is { IsExpanded: false } closed)
                    selected = closed;
                target = selected;
            }
            if (!ReferenceEquals(Navigation.SelectedItem, target)) Navigation.SelectedItem = target;
        }
        finally { navigating = outer; }
    }

    private async void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (navigating) return;
        if (e.IsSettingsSelected) { await ShowSettings(); return; }
        if (e.SelectedItem is not NavigationViewItem { Tag: string tag } || tag is SmartGroupTag or CreatePlaylistTag) return;
        if (tag == "video:library") { VideoClicked(this, new()); return; }
        model.CloseRemote();
        ShowLibrary();
        if (model.IsLyricsVisible) await model.ToggleLyricsAsync();
        if (tag.StartsWith("playlist:", StringComparison.Ordinal)) await model.SelectPlaylistAsync(model.ActiveLibraryId, tag[9..]);
        else if (tag.StartsWith("view:", StringComparison.Ordinal)) await model.SelectViewAsync(tag[5..]);
        BuildNavigation();
    }

    private async void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (TagOf(e.InvokedItemContainer) != CreatePlaylistTag || createPlaylistOpen) return;
        createPlaylistOpen = true;
        try
        {
            if (await AskName(Localization.Get("nav.newPlaylist")) is { } name)
                await model.CreatePlaylistAsync(model.ActiveLibraryId, name);
        }
        finally { createPlaylistOpen = false; BuildNavigation(); }
    }

    private MenuFlyout PlaylistMenu(string id)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Localization.Get("common.rename") };
        rename.Click += async (_, _) =>
        {
            var playlist = model.PlaylistsForLibrary(model.ActiveLibraryId).FirstOrDefault(p => p.Id == id);
            if (playlist is not null && await AskName("重命名歌单", playlist.Name) is { } name)
                await model.RenamePlaylistAsync(id, name);
        };
        menu.Items.Add(rename);
        var delete = new MenuFlyoutItem { Text = Localization.Get("common.delete") };
        delete.Click += async (_, _) =>
        {
            var playlist = model.PlaylistsForLibrary(model.ActiveLibraryId).FirstOrDefault(p => p.Id == id);
            if (playlist is not null && await Confirm("删除歌单", "仅删除歌单，媒体文件会保留。"))
                await model.DeletePlaylistAsync(id, playlist.Name);
        };
        menu.Items.Add(delete);
        return menu;
    }

    private void WireSmartGroup(NavigationViewItem group)
    {
        if (!smartEventsHooked)
        {
            smartEventsHooked = true;
            Navigation.Expanding += (_, args) =>
            {
                if (!smartProgrammatic && TagOf(args.ExpandingItemContainer) == SmartGroupTag) smartHoverOpened = false;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SyncNavigationSelection);
            };
            Navigation.Collapsed += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SyncNavigationSelection);
        }
        group.PointerEntered += (_, _) => ScheduleSmartGroup(group, true);
        group.PointerExited += (_, _) => ScheduleSmartGroup(group, false);
    }

    private void ScheduleSmartGroup(NavigationViewItem group, bool open)
    {
        smartHoverTimer?.Stop();
        if (open ? group.IsExpanded : !(group.IsExpanded && smartHoverOpened)) return;
        var timer = smartHoverTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(open ? 250 : 450);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!SmartCollapsed || group.IsExpanded == open || closing) return;
            smartProgrammatic = true;
            try { smartHoverOpened = open; group.IsExpanded = open; }
            finally { smartProgrammatic = false; }
        };
        timer.Start();
    }

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        sidebarExpanded = !sidebarExpanded;
        ApplySidebarWidth(animate: true);
    }

    private void ApplySidebarWidth(bool animate = false)
    {
        var target = sidebarExpanded ? SidebarOpenWidth : SidebarCompactWidth;
        EnsureSidebarMotion();
        ApplySidebarToggleLabel();
        void Settle() { if (!sidebarExpanded) SetPaneCompact(true); }
        if (sidebarExpanded) SetPaneCompact(false);
        if (animate && SidebarViewport.IsLoaded && !closing && Motion.Enabled)
            sidebarWidth!.AnimateTo(target, sidebarExpanded ? Motion.NormalMs : 200, Motion.Decelerate, Settle);
        else { sidebarWidth!.SetValue(target); Settle(); }
    }

    private void SetPaneCompact(bool compact)
    {
        ModeSwitch.Compact = compact;
        Sources.Compact = compact;
        Sources.Width = (compact ? SidebarCompactWidth : SidebarOpenWidth) - 8;
        var mode = compact ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        if (Navigation.PaneDisplayMode != mode) Navigation.PaneDisplayMode = mode;
        if (Navigation.IsPaneOpen == compact) Navigation.IsPaneOpen = !compact;
    }

    private void EnsureSidebarMotion()
    {
        if (sidebarMotion is not null) return;
        var sidebar = ElementCompositionPreview.GetElementVisual(SidebarViewport);
        var compositor = sidebar.Compositor;
        var motion = sidebarMotion = compositor.CreatePropertySet();
        motion.InsertScalar("Width", (float)SidebarSpace.Width);
        var clip = compositor.CreateInsetClip();
        var reveal = compositor.CreateExpressionAnimation("224 - motion.Width");
        reveal.SetReferenceParameter("motion", motion);
        clip.StartAnimation("RightInset", reveal);
        sidebar.Clip = clip;
        sidebarWidth = new FrameTimeline(SidebarSpace.Width, width =>
        {
            SidebarSpace.Width = width;
            motion.InsertScalar("Width", (float)width);
        });
    }

    private ScrollViewer? NavigationScroller() => navigationScroller ??= FindDescendant<ScrollViewer>(Navigation, viewer => viewer.Name == "MenuItemsScrollViewer");

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && match(typed)) return typed;
            if (FindDescendant(child, match) is { } nested) return nested;
        }
        return null;
    }

    private void TitleBarEntryChanged(object sender, RoutedEventArgs e) => UpdateTitleBarRegions();

    private void UpdateTitleBarRegions()
    {
        if (TitleBar.XamlRoot is not { } root || !SidebarToggle.IsLoaded) return;
        var bounds = SidebarToggle.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, SidebarToggle.ActualWidth, SidebarToggle.ActualHeight));
        var scale = root.RasterizationScale;
        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(
            Microsoft.UI.Input.NonClientRegionKind.Passthrough,
            [new Windows.Graphics.RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale))]);
    }
}
