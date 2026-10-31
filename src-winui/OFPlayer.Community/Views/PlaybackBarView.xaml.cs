using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.ViewModels;
using OFPlayer.WinUI.Core.Playback;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// The persistent playback bar: what is playing, the transport, and the tools that
/// belong to the playing track (lyrics, queue, output, volume, details, immersive).
/// Page switching never swaps the playing item. On a narrow bar the lesser tools
/// move into the More menu instead of crowding or clipping the row.
/// </summary>
public sealed partial class PlaybackBarView : UserControl
{
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("layout.playback.measure");
        return base.MeasureOverride(availableSize);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        using var perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("layout.playback.arrange");
        return base.ArrangeOverride(finalSize);
    }

    private MainViewModel? viewModel;
    private bool updatingControls;
    private readonly PlaybackSeekInteraction seekInteraction = new();
    private bool seekDispatchQueued;
    private int seekPreviewChanges;
    private bool isAdjustingVolume;
    private bool sendingVolume;
    private double? pendingVolume;
    private bool applyQueued;
    private readonly SymbolIcon playIcon = new(Symbol.Play);
    private bool presentationActive = true;
    internal void SetPresentationActive(bool active)
    {
        presentationActive = active;
        if (active)
            Apply();
        else
        {
            seekInteraction.Cancel();
            CancelTrackTransition();
        }
    }

    private double cachedTrackDuration;
    private Microsoft.UI.Xaml.Shapes.Rectangle? seekTrackRect;
    private Microsoft.UI.Xaml.Controls.Primitives.Thumb? seekThumb;
    public event EventHandler? DetailsRequested;
    public event EventHandler? ImmersiveRequested;
    /// <summary>The shell navigates to the queue; the sidebar selection shows where it lives.</summary>
    public event EventHandler? QueueRequested;
    public PlaybackBarView()
    {
        InitializeComponent();
        PlayButton.Content = playIcon;
        // Slider/Thumb handles pointer events internally. Observe those events too.
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekPointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekPointerCaptureLost), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekPointerCaptureLost), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SeekPointerCanceled), true);
        SeekSlider.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SeekKeyDown), true);
        VolumeSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(VolumePointerPressed), true);
        VolumeSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(VolumePointerCaptureLost), true);
        VolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(VolumePointerCaptureLost), true);
        VolumeSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(VolumePointerCaptureLost), true);
        VolumeSlider.Unloaded += (_, _) => isAdjustingVolume = false;
        // The thumb's tooltip reads as a clock, the same as the position text it previews.
        SeekSlider.ThumbToolTipValueConverter = new PlaybackClockConverter();
        BufferStatusLine.OpacityTransition = new ScalarTransition
        {
            Duration = TimeSpan.FromMilliseconds(167)
        };
        ActualThemeChanged += (_, _) =>
        {
            seekTrackRect = null;
            seekThumb = null;
            ApplyCachedTrack();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.OnLoaded");
        viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Localization.LanguageChanged += Apply;
        Apply();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.OnUnloaded");
        seekInteraction.Cancel();
        CancelTrackTransition();
        Localization.LanguageChanged -= Apply;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!presentationActive)
            return;
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.OnViewModelPropertyChanged");
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.Playback):
                ApplyProgress();
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.CanPlay):
            case nameof(MainViewModel.CanSkip):
            case nameof(MainViewModel.PlaybackLabel):
            case nameof(MainViewModel.PlayModeLabel):
            case nameof(MainViewModel.OutputDeviceLabel):
            case nameof(MainViewModel.IsConnected):
            case nameof(MainViewModel.CurrentTrackDetail):
            case nameof(MainViewModel.PlaybackArtwork):
            case nameof(MainViewModel.PlaybackArtist):
            case nameof(MainViewModel.IsPlaybackBuffering):
            case nameof(MainViewModel.IsLyricsVisible):
            case "IsTogetherPlaybackActive":
                if (applyQueued)
                    break;
                applyQueued = DispatcherQueue.TryEnqueue(() =>
                {
                    applyQueued = false;
                    if (IsLoaded)
                        Apply();
                });
                break;
        }
    }

    private void Apply()
    {
        if (!presentationActive)
            return;
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.Apply");
        if (viewModel is null)
        {
            return;
        }

        updatingControls = true;
        seekInteraction.ObserveTrack(viewModel.Playback.ActiveTrackId);
        SeekSlider.Maximum = Math.Max(1, viewModel.Playback.DurationSeconds);
        if (seekInteraction.PreviewSeconds is null)
            SeekSlider.Value = Math.Clamp(viewModel.Playback.PositionSeconds, 0, SeekSlider.Maximum);
        SeekSlider.IsEnabled = SeekControlReady;
        VolumeSlider.IsEnabled = viewModel.IsConnected;
        if (!isAdjustingVolume && !sendingVolume && pendingVolume is null && viewModel.IsConnected)
        {
            VolumeSlider.Value = Math.Clamp(viewModel.Playback.Volume * 100.0, 0, 100);
        }

        updatingControls = false;
        var text = Localization.Get;
        PlayButton.IsEnabled = viewModel.CanPlay;
        BufferingRing.IsActive = viewModel.IsPlaybackBuffering;
        ApplyCachedTrack();
        ApplyBufferStatus();
        ToolTipService.SetToolTip(BufferingRing, viewModel.IsPlaybackBuffering ? text("player.buffering") : null);
        var symbol = viewModel.IsPlaying ? Symbol.Pause : Symbol.Play;
        if (playIcon.Symbol != symbol)
            playIcon.Symbol = symbol;
        // While the other person controls a shared room, the disabled button says why.
        var playLabel = text(viewModel.IsPlaying ? "player.pause" : "player.play");
        SetLabel(PlayButton, playLabel);
        SetLabel(PreviousButton, text("player.previous"));
        SetLabel(NextButton, text("player.next"));
        SetLabel(LyricsButton, text(viewModel.IsLyricsVisible ? "player.lyrics.hide" : "player.lyrics"));
        SetLabel(QueueButton, text("player.queue"));
        SetLabel(VolumeButton, text("player.volume"));
        ToolTipService.SetToolTip(CurrentTrack, text("player.details"));
        SetLabel(ImmersiveButton, text("player.immersive"));
        SetLabel(BarMoreButton, text("common.more"));
        VolumeLabel.Text = text("player.volume");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VolumeSlider, text("player.volume"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SeekSlider, text("player.position"));
        LyricsButton.IsEnabled = viewModel.IsConnected;
        QueueButton.IsEnabled = viewModel.IsConnected;
        // Lyrics on, the icon takes the accent; off, the button's own colour like its neighbours.
        if (viewModel.IsLyricsVisible)
            WindowTheme.Brush(LyricsIcon, "Foreground", "AccentTextFillColorPrimaryBrush");
        else
            LyricsIcon.ClearValue(StyleProperty);
        PreviousButton.IsEnabled = viewModel.CanSkip;
        NextButton.IsEnabled = viewModel.CanSkip;
        PlayModeButton.IsEnabled = viewModel.IsConnected;
        // E8EE RepeatAll, E8ED RepeatOne, F5E7 RepeatOff, E8B1 Shuffle.
        // Show the current mode, including a restored preference, rather than
        // leaving the button on the RepeatAll icon after every click.
        var modeGlyph = viewModel.ShuffleEnabled ? '\uE8B1' : viewModel.RepeatMode switch
        {
            PlaybackOrder.RepeatOne => '\uE8ED',
            PlaybackOrder.RepeatNone => '\uF5E7',
            _ => '\uE8EE',
        };
        if (PlayModeIcon.Glyph != modeGlyph.ToString())
            PlayModeIcon.Glyph = modeGlyph.ToString();
        ToolTipService.SetToolTip(PlayModeButton, viewModel.PlayModeLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayModeButton, Localization.Format("player.mode.named", ("mode", viewModel.PlayModeLabel)));
        DeviceButton.IsEnabled = viewModel.IsConnected;
        DeviceLabel.Text = viewModel.OutputDeviceLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DeviceButton, Localization.Format("player.output.named", ("device", viewModel.OutputDeviceLabel)));
        UpdatePositionText();
        _ = UpdateTrackPresentationAsync();
    }

    // ---- cache track and recovery line ----
    private void CacheTrackSizeChanged(object sender, SizeChangedEventArgs args) => ApplyCachedTrack();
    private void ApplyCachedTrack()
    {
        CachedTrack.Children.Clear();
        var geometry = SeekTrackGeometry();
        var track = WindowTheme.Brush(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = Math.Max(0, geometry.Width), Height = geometry.Height, RadiusX = 2, RadiusY = 2 }, "Fill", "ControlStrongFillColorDefaultBrush");
        Canvas.SetLeft(track, geometry.Left);
        Canvas.SetTop(track, geometry.Top);
        CachedTrack.Children.Add(track);
    }

    /// <summary>
    /// Where the slider's track is inside <see cref = "CachedTrack"/>, and how far the thumb centre
    /// stays from each end of it. Read from the slider's template so the layers cannot drift.
    /// </summary>
    private (double Left, double Top, double Width, double Height, double Inset) SeekTrackGeometry()
    {
        seekTrackRect ??= FindTemplatePart<Microsoft.UI.Xaml.Shapes.Rectangle>(SeekSlider, "HorizontalTrackRect");
        seekThumb ??= FindTemplatePart<Microsoft.UI.Xaml.Controls.Primitives.Thumb>(SeekSlider, "HorizontalThumb");
        var inset = seekThumb is { ActualWidth: > 0 } thumb ? thumb.ActualWidth / 2 : 9;
        if (seekTrackRect is { ActualWidth: > 0, ActualHeight: > 0 } rect && rect.XamlRoot is not null)
        {
            try
            {
                var origin = rect.TransformToVisual(CachedTrack).TransformPoint(new Windows.Foundation.Point(0, 0));
                return (origin.X, origin.Y, rect.ActualWidth, rect.ActualHeight, inset);
            }
            catch (ArgumentException)
            {
            // Not laid out together yet; the next size change draws it in place.
            }
        }

        return (0, (CachedTrack.ActualHeight - 4) / 2, CachedTrack.ActualWidth, 4, inset);
    }

    private static T? FindTemplatePart<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name)
                return match;
            if (FindTemplatePart<T>(child, name)is { } nested)
                return nested;
        }

        return null;
    }

    private void ApplyBufferStatus()
    {
        BufferStatusRow.Visibility = Visibility.Collapsed;
    }

    private void RetryBuffering(object sender, RoutedEventArgs args)
    {
    }

    private void ApplyProgress()
    {
        if (!presentationActive)
            return;
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.ApplyProgress");
        if (viewModel is null)
            return;
        seekInteraction.ObserveTrack(viewModel.Playback.ActiveTrackId);
        updatingControls = true;
        SeekSlider.Maximum = Math.Max(1, viewModel.Playback.DurationSeconds);
        if (seekInteraction.PreviewSeconds is null)
            SeekSlider.Value = Math.Clamp(viewModel.Playback.PositionSeconds, 0, SeekSlider.Maximum);
        if (!isAdjustingVolume && !sendingVolume && pendingVolume is null && viewModel.IsConnected)
            VolumeSlider.Value = Math.Clamp(viewModel.Playback.Volume * 100, 0, 100);
        updatingControls = false;
        UpdatePositionText();
        // Cached ranges are placed by duration; a new song's duration can arrive with progress alone.
        if (viewModel.Playback.DurationSeconds != cachedTrackDuration)
        {
            cachedTrackDuration = viewModel.Playback.DurationSeconds;
            ApplyCachedTrack();
        }
    }

    private void UpdatePositionText()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.UpdatePositionText");
        if (viewModel is null)
            return;
        var position = seekInteraction.PreviewSeconds ?? viewModel.Playback.PositionSeconds;
        var text = $"{FormatTime(position)} / {FormatTime(viewModel.Playback.DurationSeconds)}";
        if (PositionText.Text != text)
            PositionText.Text = text;
    }

    internal static string FormatTime(double seconds)
    {
        var safe = seconds > 0 && double.IsFinite(seconds) ? seconds : 0;
        return TimeSpan.FromSeconds(safe).ToString(@"m\:ss");
    }

    private async void TogglePlayback(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.TogglePlayback");
        if (viewModel is not null)
        {
            await viewModel.TogglePlaybackAsync();
        }
    }

    private async void PlayNext(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.PlayNext");
        if (viewModel is not null)
        {
            await viewModel.PlayNextAsync();
        }
    }

    private async void PlayPrevious(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.PlayPrevious");
        if (viewModel is not null)
        {
            await viewModel.PlayPreviousAsync();
        }
    }

    private void CyclePlayMode(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.CyclePlayMode");
        viewModel?.CyclePlayMode();
    }

    private void NowPlayingTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.NowPlayingTapped");
        if (viewModel?.Playback.ActiveTrackId is null)
            return;
        DetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestImmersive(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.RequestImmersive");
        ImmersiveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestQueue(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.RequestQueue");
        QueueRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void ToggleLyrics(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.ToggleLyrics");
        if (viewModel is not null)
            await viewModel.ToggleLyricsAsync();
    }

    private static void SetLabel(Control control, string label)
    {
        if (ToolTipService.GetToolTip(control) as string != label)
            ToolTipService.SetToolTip(control, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
    }

    // ---- narrow bar: lesser tools move into the More menu ----
    private int toolTier = 2;
    private void BarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // 2: lyrics, queue, volume, immersive; 1: lyrics, volume, immersive; 0: volume only. The transport never yields.
        var tier = args.NewSize.Width >= 780 ? 2 : args.NewSize.Width >= 560 ? 1 : 0;
        if (tier == toolTier)
            return;
        toolTier = tier;
        QueueButton.Visibility = tier == 2 ? Visibility.Visible : Visibility.Collapsed;
        var some = tier >= 1 ? Visibility.Visible : Visibility.Collapsed;
        LyricsButton.Visibility = ImmersiveButton.Visibility = some;
    }

    private bool SeekControlReady => viewModel is { } model && (model.CanPlay);

    private void SeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.SeekChanged");
        if (updatingControls || viewModel is null || !SeekControlReady || viewModel.Playback.ActiveTrackId is not string trackId)
            return;
        seekInteraction.Change(trackId, args.NewValue);
        if (seekInteraction.PreviewSeconds is null)
        {
            ApplyProgress();
            return;
        }

        seekPreviewChanges++;
        UpdatePositionText();
        QueueSeekDispatch();
    }

    private void SeekPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.SeekPointerPressed");
        if (viewModel is null || !SeekControlReady || viewModel.Playback.ActiveTrackId is not string trackId)
            return;
        seekInteraction.BeginPointer(trackId, SeekSlider.Value);
        seekPreviewChanges = 0;
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("playback.seek.pointer.begin");
    }

    private void SeekPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.SeekPointerCaptureLost");
        if (!seekInteraction.PointerActive)
            return;
        seekInteraction.EndPointer(viewModel?.Playback.ActiveTrackId);
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("playback.seek.pointer.end", new { previewChanges = seekPreviewChanges, seconds = seekInteraction.PreviewSeconds });
        QueueSeekDispatch();
    }

    private void SeekPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.SeekPointerCanceled");
        seekInteraction.Cancel();
        ApplyProgress();
    }

    private void SeekKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || !seekInteraction.CancelPendingInput())
            return;
        args.Handled = true;
        ApplyProgress();
    }

    private void QueueSeekDispatch()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.QueueSeekDispatch");
        if (seekDispatchQueued || seekInteraction.PointerActive)
            return;
        // Track clicks can raise ValueChanged before the handled PointerPressed.
        // Let that routed event finish before deciding whether this is a commit.
        seekDispatchQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            seekDispatchQueued = false;
            _ = CommitSeekAsync();
        });
    }

    private async Task CommitSeekAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.CommitSeekAsync");
        if (!IsLoaded || viewModel is null || !SeekControlReady)
        {
            seekInteraction.Cancel();
            return;
        }

        seekInteraction.ObserveTrack(viewModel.Playback.ActiveTrackId);
        var request = seekInteraction.TakePending();
        if (request is null)
            return;
        using var operation = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("playback.seek.commit");
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("playback.seek.commit", new { request.Id, request.Seconds });
        try
        {
            await viewModel.SeekAsync(request.Seconds);
        }
        finally
        {
            // MainViewModel posts the authoritative reply onto this UI queue.
            // Release the preview after that post, so old progress cannot snap
            // the thumb back between mouse-up and acknowledgement.
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                seekInteraction.Complete(request);
                if (!IsLoaded)
                    return;
                ApplyProgress();
                QueueSeekDispatch();
            }))
                seekInteraction.Complete(request);
        }
    }

    private async void VolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.VolumeChanged");
        if (updatingControls || viewModel?.IsConnected != true)
        {
            return;
        }

        await QueueVolumeAsync(args.NewValue / 100.0);
    }

    private async Task QueueVolumeAsync(double volume)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.QueueVolumeAsync");
        pendingVolume = volume;
        if (sendingVolume)
            return;
        sendingVolume = true;
        try
        {
            while (pendingVolume is double next && viewModel?.IsConnected == true)
            {
                pendingVolume = null;
                await viewModel.SetVolumePersistedAsync(next);
            }
        }
        finally
        {
            pendingVolume = null;
            sendingVolume = false;
        }
    }

    private void VolumePointerPressed(object sender, PointerRoutedEventArgs args) => isAdjustingVolume = true;
    private async void VolumePointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.VolumePointerCaptureLost");
        if (!isAdjustingVolume)
        {
            return;
        }

        isAdjustingVolume = false;
        if (viewModel is not null)
        {
            await QueueVolumeAsync(VolumeSlider.Value / 100.0);
        }
    }

    // ---- More menu ----
    private async Task OpenListeningStatsAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.xaml.OpenListeningStatsAsync");
        if (viewModel is null)
        {
            return;
        }

        // Fetch first, then show: the dialog renders synchronously from the snapshot.
        var dialog = new ListeningStatsDialog
        {
            XamlRoot = XamlRoot
        };
        dialog.Initialize(viewModel.LoadStatsSnapshotAsync);
        await dialog.ShowAsync();
    }

    private void OpenMoreMenu(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.OpenMoreMenu");
        if (viewModel is null)
        {
            return;
        }

        var text = Localization.Get;
        var flyout = new MenuFlyout();
        MenuFlyoutItem Item(string label, string glyph, Action action)
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon
                {
                    Glyph = glyph
                }
            };
            item.Click += (_, _) => action();
            return item;
        }

        // Tools the bar had no room for keep their place here, in the bar's order.
        if (toolTier < 1)
            flyout.Items.Add(Item(text(viewModel.IsLyricsVisible ? "player.lyrics.hide" : "player.lyrics"), "\uED1E", () => ToggleLyrics(this, new RoutedEventArgs())));
        if (toolTier < 2)
            flyout.Items.Add(Item(text("player.queue"), "\uE8FD", () => QueueRequested?.Invoke(this, EventArgs.Empty)));
        if (toolTier < 1)
            flyout.Items.Add(Item(text("player.immersive"), "\uE740", () => ImmersiveRequested?.Invoke(this, EventArgs.Empty)));
        // The bar has no button for details: the playing song opens them, and this is the keyboard way.
        flyout.Items.Add(Item(text("player.details"), "\uE946", () => DetailsRequested?.Invoke(this, EventArgs.Empty)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var stats = new MenuFlyoutItem
        {
            Text = text("player.stats")
        };
        stats.Click += async (_, _) => await OpenListeningStatsAsync();
        flyout.Items.Add(stats);
        // Desktop lyric capsule: a separate always-on-top window.
        var capsule = new ToggleMenuFlyoutItem
        {
            Text = text("player.capsule"),
            IsChecked = CapsuleWindow.IsOpen,
        };
        capsule.Click += (_, _) =>
        {
            if (CapsuleWindow.IsOpen)
            {
                CapsuleWindow.HideIfOpen();
            }
            else
            {
                CapsuleWindow.ShowOrActivate();
            }
        };
        flyout.Items.Add(capsule);
        flyout.ShowAt(BarMoreButton);
    }

    private async void OpenDevices(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("PlaybackBarView.xaml.OpenDevices");
        if (viewModel is null)
        {
            return;
        }

        // Open at once from the known list and refresh it for the next open; only
        // the very first open waits, because there is nothing to show yet.
        if (viewModel.OutputDevices.Count == 0)
            await viewModel.RefreshOutputDevicesAsync();
        else
            _ = viewModel.RefreshOutputDevicesAsync();
        var flyout = new MenuFlyout();
        AddDeviceItems(flyout.Items);
        flyout.ShowAt(DeviceButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedBottom, });
    }

    private void AddDeviceItems(IList<MenuFlyoutItemBase> items)
    {
        if (viewModel is not { } model)
            return;
        var systemDefault = new RadioMenuFlyoutItem
        {
            Text = Localization.Get("player.output.system"),
            GroupName = "output-device",
            IsChecked = model.OutputUsesSystemDefault,
        };
        systemDefault.Click += async (_, _) => await model.SetOutputDeviceAsync(null);
        items.Add(systemDefault);
        var activeId = model.ActiveOutputDeviceId;
        foreach (var device in model.OutputDevices)
        {
            // Default-only engines expose a virtual endpoint for status, not a
            // second physical choice. The localized system-default row above
            // already represents it (and remains valid when the engine changes).
            if (device.Id is "wasapi-exclusive:default" or "xaudio2:default")
                continue;
            var item = new RadioMenuFlyoutItem
            {
                Text = device.DisplayName,
                GroupName = "output-device",
                IsChecked = !model.OutputUsesSystemDefault && device.Id == activeId,
            };
            var deviceId = device.Id;
            item.Click += async (_, _) => await model.SetOutputDeviceAsync(deviceId);
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());
        var recover = new MenuFlyoutItem
        {
            Text = Localization.Get("player.output.restart")
        };
        recover.Click += async (_, _) => await model.RecoverOutputAsync();
        items.Add(recover);
    }
}

/// <summary>Shows the seek thumb's value as a clock rather than raw seconds.</summary>
internal sealed partial class PlaybackClockConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => PlaybackBarView.FormatTime(value is double seconds ? seconds : 0);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
