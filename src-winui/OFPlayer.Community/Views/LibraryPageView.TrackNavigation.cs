using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;
using OFPlayer.WinUI.Core.Catalog;
using Windows.Foundation;

namespace OFPlayer_WinUI.Views;
public sealed partial class LibraryPageView
{
    private const int NavigationPreviewCacheLimit = 20;
    // A marker slot must be a whole number of pixels at every display scale (24 x 1.25 = 30), so
    // the rail is exactly as tall as its slots add up to. A size that layout rounds up (22 px is
    // 22.4 at 125%) makes the list taller than the rail and hides the last marker.
    private const double TrackNavigationMarkerSlot = 24;
    // The rail's border (1) and padding (7), top and bottom.
    private const double TrackNavigationRailChrome = 16;
    private const double TrackNavigationPreviewArtwork = 28;
    private const double TrackNavigationPreviewTitleFontSize = 12;
    private readonly Windows.UI.ViewManagement.AccessibilitySettings trackNavigationAccessibilitySettings = new();
    private readonly DispatcherTimer trackNavigationHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private readonly Dictionary<int, IReadOnlyList<TrackQueryRow>> trackNavigationPreviewCache = new();
    private readonly Queue<int> trackNavigationPreviewOrder = new();
    private readonly List<TrackNavMarker> trackNavigationMarkers = new();
    private readonly List<Button> trackNavigationButtons = new();
    private readonly List<Border> trackNavigationMarkerSurfaces = new();
    private readonly List<IconElement> trackNavigationMarkerIcons = new();
    private static readonly string[] TrackNavigationIconKeys =
    {
        "MusicIcon.EighthNote",
        "MusicIcon.TrebleClef",
        "MusicIcon.EighthPair",
        "MusicIcon.Sharp",
        "MusicIcon.QuarterNote",
        "MusicIcon.BassClef",
        "MusicIcon.SixteenthPair",
        "MusicIcon.Flat",
    };
    private ScrollViewer? trackNavigationScrollViewer;
    private CancellationTokenSource? trackNavigationPreviewCancellation;
    private CancellationTokenSource? trackNavigationJumpCancellation;
    private string? trackNavigationIdentity;
    private int trackNavigationTotal;
    private int trackNavigationPreviewRequest;
    private double trackNavigationPreviewHeight;
    private bool trackNavigationPinned;
    private bool trackNavigationPointerInside;
    private bool trackNavigationJumping;
    private bool trackNavigationLoadingPrevious;
    private bool trackNavigationFocusAfterMenuClose;
    private DateTimeOffset trackNavigationPreviousSuppressedUntil;
    private int trackNavigationPreviewedIndex = -1;
    private int trackNavigationActiveIndex = -1;
    private Storyboard? trackNavigationRailStoryboard;
    private Storyboard? trackNavigationPreviewStoryboard;
    private int trackNavigationRailAnimationRevision;
    private int trackNavigationPreviewAnimationRevision;
    private bool trackNavigationRailHiding;
    private bool trackNavigationPreviewHiding;
    private TaskCompletionSource<bool>? trackNavigationScrollCompletion;
    private TaskCompletionSource<bool>? trackNavigationLayoutCompletion;
    private EventHandler<object>? trackNavigationLayoutHandler;
    private long trackNavigationScrollStarted;
    private double trackNavigationScrollStart;
    private double trackNavigationScrollTarget;
    private double trackNavigationScrollDuration;
    private void InitializeTrackNavigation()
    {
        trackNavigationHideTimer.Tick -= TrackNavigationHideTimerTick;
        trackNavigationHideTimer.Tick += TrackNavigationHideTimerTick;
        ActualThemeChanged -= TrackNavigationThemeChanged;
        ActualThemeChanged += TrackNavigationThemeChanged;
        Localization.LanguageChanged -= RefreshTrackNavigationLanguage;
        Localization.LanguageChanged += RefreshTrackNavigationLanguage;
        Tracks.Loaded -= TrackNavigationTracksLoaded;
        Tracks.Loaded += TrackNavigationTracksLoaded;
        TrackNavigationMenuItem.Text = Localization.Get("player.navRail.aria");
        trackNavigationScrollViewer = FindTrackNavigationScrollViewer(Tracks);
        if (trackNavigationScrollViewer is not null)
            trackNavigationScrollViewer.ViewChanged += TrackNavigationViewChanged;
        ResizeTrackNavigationRail();
    }

    private void DisposeTrackNavigation()
    {
        trackNavigationHideTimer.Stop();
        trackNavigationHideTimer.Tick -= TrackNavigationHideTimerTick;
        ActualThemeChanged -= TrackNavigationThemeChanged;
        Localization.LanguageChanged -= RefreshTrackNavigationLanguage;
        Tracks.Loaded -= TrackNavigationTracksLoaded;
        if (trackNavigationScrollViewer is not null)
            trackNavigationScrollViewer.ViewChanged -= TrackNavigationViewChanged;
        trackNavigationScrollViewer = null;
        StopTrackNavigationLayoutWait();
        StopTrackNavigationScrollAnimation();
        trackNavigationRailAnimationRevision++;
        trackNavigationPreviewAnimationRevision++;
        trackNavigationRailStoryboard?.Stop();
        trackNavigationPreviewStoryboard?.Stop();
        CancelTrackNavigationPreview();
        CancelTrackNavigationJump();
        HideTrackNavigation();
    }

    private void TrackNavigationTracksLoaded(object sender, RoutedEventArgs args)
    {
        var scrollViewer = FindTrackNavigationScrollViewer(Tracks);
        if (ReferenceEquals(scrollViewer, trackNavigationScrollViewer))
            return;
        if (trackNavigationScrollViewer is not null)
            trackNavigationScrollViewer.ViewChanged -= TrackNavigationViewChanged;
        trackNavigationScrollViewer = scrollViewer;
        if (scrollViewer is not null)
            scrollViewer.ViewChanged += TrackNavigationViewChanged;
    }

    private static ScrollViewer? FindTrackNavigationScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer)
            return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var found = FindTrackNavigationScrollViewer(VisualTreeHelper.GetChild(parent, index));
            if (found is not null)
                return found;
        }

        return null;
    }

    private bool CanShowTrackNavigation => viewModel is { IsConnected: true, IsRemoteMode: false, IsMultiSelect: false, IsGroupListVisible: false, IsLyricsVisible: false } model && TrackNavRailModel.IsVisible(model.TotalCount);

    private void RefreshTrackNavigation()
    {
        // The menu exists for keyboard access to the rail; without the rail there is nothing in it.
        TrackNavigationMenuItem.Visibility = MoreButton.Visibility = CanShowTrackNavigation ? Visibility.Visible : Visibility.Collapsed;
        if (!CanShowTrackNavigation || viewModel is null)
        {
            HideTrackNavigation();
            return;
        }

        var identity = viewModel.TrackNavigationIdentity;
        var total = viewModel.TotalCount;
        if (identity != trackNavigationIdentity || total != trackNavigationTotal)
        {
            if (identity != trackNavigationIdentity)
                CancelTrackNavigationJump();
            trackNavigationIdentity = identity;
            trackNavigationTotal = total;
            CancelTrackNavigationPreview();
            trackNavigationPreviewCache.Clear();
            trackNavigationPreviewOrder.Clear();
            BuildTrackNavigationMarkers(total);
            TrackNavigationPreview.Visibility = Visibility.Collapsed;
        }

        UpdateTrackNavigationActiveMarker();
    }

    private void BuildTrackNavigationMarkers(int total)
    {
        trackNavigationMarkers.Clear();
        trackNavigationButtons.Clear();
        trackNavigationMarkerSurfaces.Clear();
        trackNavigationMarkerIcons.Clear();
        trackNavigationActiveIndex = -1;
        trackNavigationPreviewedIndex = -1;
        TrackNavigationMarkers.Children.Clear();
        TrackNavigationRailScroller.ChangeView(null, 0, null, true);
        var markers = TrackNavRailModel.BuildMarkers(total);
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            trackNavigationMarkers.Add(marker);
            var icon = MusicIconGeometryLoader.CreatePathIcon(TrackNavigationIconKeys[index % TrackNavigationIconKeys.Length], 18);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            // 22 and 18 px are not whole pixels at 125% or 175%. Snapped, the plate sits half a
            // pixel off in its slot and the glyph half a pixel off in the plate, so both stay unrounded.
            icon.UseLayoutRounding = false;
            var surface = new Border
            {
                Width = 22,
                Height = 22,
                UseLayoutRounding = false,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BackgroundTransition = new BrushTransition
                {
                    Duration = TimeSpan.FromMilliseconds(Motion.FastMs)
                },
                Child = icon,
            };
            var button = new Button
            {
                Content = surface,
                Style = (Style)Resources["TrackNavigationMarkerButtonStyle"],
                Width = TrackNavigationMarkerSlot,
                MinWidth = TrackNavigationMarkerSlot,
                Height = TrackNavigationMarkerSlot,
                MinHeight = TrackNavigationMarkerSlot,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                CenterPoint = new System.Numerics.Vector3((float)TrackNavigationMarkerSlot / 2, (float)TrackNavigationMarkerSlot / 2, 0),
                ScaleTransition = new Vector3Transition
                {
                    Duration = TimeSpan.FromMilliseconds(Motion.FastMs)
                },
                Tag = index,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Localization.Format("player.navRail.marker", ("start", marker.StartTrack.ToString()), ("end", marker.EndTrack.ToString())));
            button.PointerEntered += TrackNavigationMarkerEntered;
            button.PointerExited += TrackNavigationMarkerExited;
            button.GotFocus += TrackNavigationMarkerFocused;
            button.LostFocus += TrackNavigationMarkerLostFocus;
            button.Click += TrackNavigationMarkerClicked;
            TrackNavigationMarkers.Children.Add(button);
            trackNavigationButtons.Add(button);
            trackNavigationMarkerSurfaces.Add(surface);
            trackNavigationMarkerIcons.Add(icon);
        }

        ResizeTrackNavigationRail();
        UpdateTrackNavigationActiveMarker();
    }

    private void ResizeTrackNavigationRail()
    {
        var rowHeight = Math.Max(64, TrackViewportClearHeight - TrackListHeader.ActualHeight);
        var available = Math.Max(48, rowHeight - 16);
        // The footer can disappear after a deep jump. Keep the hovered marker
        // under the pointer while the viewport grows; a real shrink still fits.
        if (TrackNavigationRail.Visibility == Visibility.Visible && trackNavigationPinned && TrackNavigationRail.ActualHeight > 0 && available >= TrackNavigationRail.ActualHeight)
            return;
        TrackNavigationRail.MaxHeight = available;
        // Every group keeps its marker; a short list shrinks how many show at once, not how many there are.
        var fitting = (int)Math.Floor((available - TrackNavigationRailChrome) / TrackNavigationMarkerSlot);
        var viewport = TrackNavRailModel.ViewportSlots(trackNavigationMarkers.Count, fitting) * TrackNavigationMarkerSlot;
        TrackNavigationRailScroller.MaxHeight = viewport;
        var top = Math.Max(8, (rowHeight - Math.Min(viewport + TrackNavigationRailChrome, available)) / 2);
        TrackNavigationRail.Margin = new Thickness(6, top, 0, 0);
    }

    /// <summary>Scrolls the rail just enough to show a marker, centring it when it was out of view.</summary>
    private void RevealTrackNavigationMarker(int index, bool animate)
    {
        if (index < 0 || index >= trackNavigationMarkers.Count)
            return;
        var scroller = TrackNavigationRailScroller;
        var viewport = Math.Min(scroller.MaxHeight, trackNavigationMarkers.Count * TrackNavigationMarkerSlot);
        var target = TrackNavRailModel.RevealOffset(index, trackNavigationMarkers.Count, TrackNavigationMarkerSlot, viewport, scroller.VerticalOffset);
        if (Math.Abs(target - scroller.VerticalOffset) >= 0.5)
            scroller.ChangeView(null, target, null, !animate || !TrackNavigationMotionEnabled);
    }

    // The card follows its marker while the rail scrolls, and goes once the marker has left the rail.
    private void TrackNavigationRailViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        var index = trackNavigationPreviewedIndex;
        if (TrackNavigationPreview.Visibility != Visibility.Visible || trackNavigationPreviewHiding || index < 0 || index >= trackNavigationButtons.Count)
            return;
        var button = trackNavigationButtons[index];
        var y = button.TransformToVisual(TrackNavigationRailScroller).TransformPoint(new Point(0, 0)).Y;
        if (y + TrackNavigationMarkerSlot > 0 && y < TrackNavigationRailScroller.ViewportHeight)
        {
            TrackNavigationPreview.Margin = new Thickness(48, TrackNavigationPreviewTop(button), 0, 0);
            return;
        }

        trackNavigationPreviewedIndex = -1;
        UpdateTrackNavigationMarkerAppearance();
        CancelTrackNavigationPreview();
        trackNavigationPreviewHiding = true;
        AnimateTrackNavigationOverlay(TrackNavigationPreview, TrackNavigationPreviewTransform, false, true);
    }

    private void TrackNavigationPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!CanShowTrackNavigation)
            return;
        var point = args.GetCurrentPoint(TrackViewport).Position;
        if (point.X >= 0 && point.X <= 56 && point.Y >= TrackListHeader.ActualHeight)
            ShowTrackNavigation();
        else if (!trackNavigationPinned)
            ScheduleTrackNavigationHide();
    }

    private void TrackNavigationPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!trackNavigationPinned)
            ScheduleTrackNavigationHide();
    }

    private void TrackNavigationOverlayEntered(object sender, PointerRoutedEventArgs args)
    {
        trackNavigationPointerInside = true;
        trackNavigationPinned = true;
        ShowTrackNavigation();
    }

    private void TrackNavigationOverlayExited(object sender, PointerRoutedEventArgs args)
    {
        trackNavigationPointerInside = false;
        trackNavigationPinned = HasTrackNavigationKeyboardFocus();
        ScheduleTrackNavigationHide();
    }

    private void ShowTrackNavigation()
    {
        if (!CanShowTrackNavigation)
            return;
        trackNavigationHideTimer.Stop();
        if (TrackNavigationRail.Visibility == Visibility.Visible && !trackNavigationRailHiding)
            return;
        trackNavigationRailHiding = false;
        TrackNavigationRail.Visibility = Visibility.Visible;
        ResizeTrackNavigationRail();
        AnimateTrackNavigationOverlay(TrackNavigationRail, TrackNavigationRailTransform, true, false);
        // Open on the part of the rail that matches the list, once the rail has its size.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (TrackNavigationRail.Visibility == Visibility.Visible && !trackNavigationPointerInside)
                RevealTrackNavigationMarker(ActiveTrackNavigationMarkerIndex(), false);
        });
    }

    private void ScheduleTrackNavigationHide()
    {
        if (trackNavigationPinned || TrackNavigationRail.Visibility != Visibility.Visible)
            return;
        if (trackNavigationHideTimer.IsEnabled)
            return;
        trackNavigationHideTimer.Start();
    }

    private void TrackNavigationHideTimerTick(object? sender, object args)
    {
        trackNavigationHideTimer.Stop();
        if (!trackNavigationPinned)
            HideTrackNavigation(true);
    }

    private void HideTrackNavigation(bool animate = false)
    {
        trackNavigationHideTimer.Stop();
        trackNavigationPinned = false;
        trackNavigationPointerInside = false;
        trackNavigationPreviewedIndex = -1;
        UpdateTrackNavigationMarkerAppearance();
        if (animate)
        {
            trackNavigationRailHiding = true;
            trackNavigationPreviewHiding = true;
            AnimateTrackNavigationOverlay(TrackNavigationRail, TrackNavigationRailTransform, false, false);
            AnimateTrackNavigationOverlay(TrackNavigationPreview, TrackNavigationPreviewTransform, false, true);
        }
        else
        {
            trackNavigationRailStoryboard?.Stop();
            trackNavigationPreviewStoryboard?.Stop();
            trackNavigationRailHiding = false;
            trackNavigationPreviewHiding = false;
            TrackNavigationRail.Visibility = Visibility.Collapsed;
            TrackNavigationPreview.Visibility = Visibility.Collapsed;
        }

        CancelTrackNavigationPreview();
        if (!animate)
            CancelTrackNavigationJump();
    }

    private static bool TrackNavigationMotionEnabled => Motion.Enabled;

    private void AnimateTrackNavigationOverlay(Border overlay, CompositeTransform transform, bool show, bool preview)
    {
        var previous = preview ? trackNavigationPreviewStoryboard : trackNavigationRailStoryboard;
        previous?.Stop();
        var revision = preview ? ++trackNavigationPreviewAnimationRevision : ++trackNavigationRailAnimationRevision;
        if (overlay.Visibility != Visibility.Visible && !show)
            return;
        if (!TrackNavigationMotionEnabled)
        {
            overlay.Opacity = show ? 1 : 0;
            transform.TranslateX = transform.TranslateY = 0;
            transform.ScaleX = transform.ScaleY = 1;
            if (!show)
                overlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (show)
        {
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 0;
            transform.TranslateX = preview ? 0 : -8;
            transform.TranslateY = preview ? 6 : 0;
            transform.ScaleX = transform.ScaleY = preview ? 0.97 : 1;
        }

        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(show ? Motion.NormalMs : Motion.ExitMs);
        AddTrackNavigationTween(storyboard, overlay, "Opacity", show ? 1 : 0, duration, show);
        AddTrackNavigationTween(storyboard, transform, preview ? "TranslateY" : "TranslateX", show ? 0 : preview ? 3 : -4, duration, show);
        if (preview)
        {
            AddTrackNavigationTween(storyboard, transform, "ScaleX", show ? 1 : 0.98, duration, show);
            AddTrackNavigationTween(storyboard, transform, "ScaleY", show ? 1 : 0.98, duration, show);
        }

        storyboard.Completed += (_, _) =>
        {
            if (revision != (preview ? trackNavigationPreviewAnimationRevision : trackNavigationRailAnimationRevision))
                return;
            if (!IsLoaded && show)
                return;
            if (!show)
                overlay.Visibility = Visibility.Collapsed;
            if (preview)
                trackNavigationPreviewHiding = false;
            else
                trackNavigationRailHiding = false;
        };
        if (preview)
            trackNavigationPreviewStoryboard = storyboard;
        else
            trackNavigationRailStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddTrackNavigationTween(Storyboard storyboard, DependencyObject target, string property, double to, TimeSpan duration, bool entering = true)
    {
        var tween = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = entering ? new ExponentialEase
            {
                EasingMode = EasingMode.EaseOut,
                Exponent = 5
            }

            : new CubicEase
            {
                EasingMode = EasingMode.EaseIn
            },
        };
        Storyboard.SetTarget(tween, target);
        Storyboard.SetTargetProperty(tween, property);
        storyboard.Children.Add(tween);
    }

    private void OpenTrackNavigationFromMenu(object sender, RoutedEventArgs args)
    {
        if (!CanShowTrackNavigation)
            return;
        trackNavigationFocusAfterMenuClose = true;
        ShowTrackNavigation();
    }

    private void TrackOptionsFlyoutClosed(object sender, object args)
    {
        if (!trackNavigationFocusAfterMenuClose)
            return;
        trackNavigationFocusAfterMenuClose = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || !CanShowTrackNavigation)
                return;
            ShowTrackNavigation();
            FocusActiveTrackNavigationMarker();
        });
    }

    private void FocusActiveTrackNavigationMarker()
    {
        if (trackNavigationButtons.Count == 0)
            return;
        var index = ActiveTrackNavigationMarkerIndex();
        trackNavigationButtons[Math.Max(0, index)].Focus(FocusState.Programmatic);
    }

    private void TrackNavigationRailKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape)
            return;
        trackNavigationPinned = false;
        HideTrackNavigation();
        MoreButton.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void TrackNavigationMarkerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button { Tag: int index } button)
        {
            trackNavigationPinned = true;
            trackNavigationPreviewedIndex = index;
            UpdateTrackNavigationMarkerAppearance();
            _ = OpenTrackNavigationPreviewAsync(index, button);
        }
    }

    private void TrackNavigationMarkerExited(object sender, PointerRoutedEventArgs args)
    {
    // Keep the selected marker while crossing the gap to the preview card.
    }

    private void TrackNavigationMarkerFocused(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: int index } button)
        {
            trackNavigationPinned = true;
            ShowTrackNavigation();
            trackNavigationPreviewedIndex = index;
            UpdateTrackNavigationMarkerAppearance();
            _ = OpenTrackNavigationPreviewAsync(index, button);
        }
    }

    private void TrackNavigationMarkerLostFocus(object sender, RoutedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            trackNavigationPinned = trackNavigationPointerInside || HasTrackNavigationKeyboardFocus();
            if (!trackNavigationPinned)
                ScheduleTrackNavigationHide();
        });
    }

    private bool HasTrackNavigationKeyboardFocus() => trackNavigationButtons.Any(button => button.FocusState is FocusState.Keyboard or FocusState.Programmatic);
    private async void TrackNavigationMarkerClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: int index } || index >= trackNavigationMarkers.Count || viewModel is null)
            return;
        CancelTrackNavigationJump();
        var cancellation = new CancellationTokenSource();
        trackNavigationJumpCancellation = cancellation;
        var identity = trackNavigationIdentity;
        var targetIndex = trackNavigationMarkers[index].StartIndex;
        trackNavigationJumping = true;
        try
        {
            var row = await viewModel.NavigateToTrackIndexAsync(targetIndex, cancellation.Token);
            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity || row is null)
                return;
            trackNavigationPreviousSuppressedUntil = DateTimeOffset.UtcNow.AddMilliseconds(600);
            await WaitForTrackNavigationLayoutAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                return;
            var scrollViewer = trackNavigationScrollViewer ?? FindTrackNavigationScrollViewer(Tracks);
            if (scrollViewer is not null)
            {
                if (!ReferenceEquals(scrollViewer, trackNavigationScrollViewer))
                {
                    if (trackNavigationScrollViewer is not null)
                        trackNavigationScrollViewer.ViewChanged -= TrackNavigationViewChanged;
                    trackNavigationScrollViewer = scrollViewer;
                    scrollViewer.ViewChanged += TrackNavigationViewChanged;
                }

                var localIndex = Math.Clamp(targetIndex - viewModel.TrackWindowOffset, 0, Math.Max(0, viewModel.Tracks.Count - 1));
                var firstVisible = Math.Max(0, (Tracks.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex ?? 0);
                var realized = Tracks.ContainerFromIndex(firstVisible) as FrameworkElement;
                var rowHeight = realized is { ActualHeight: > 0 } ? realized.ActualHeight : 64;
                var targetOffset = Math.Clamp(localIndex * rowHeight, 0, scrollViewer.ScrollableHeight);
                await AnimateTrackNavigationScrollAsync(scrollViewer, targetOffset, cancellation.Token);
                if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                    return;
                if (Tracks.ContainerFromItem(row)is FrameworkElement landed)
                {
                    var exact = Math.Clamp(scrollViewer.VerticalOffset + landed.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y, 0, scrollViewer.ScrollableHeight);
                    await AnimateTrackNavigationScrollAsync(scrollViewer, exact, cancellation.Token, 180);
                }
                else
                    Tracks.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            }
            else
                Tracks.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                return;
            UpdateTrackNavigationActiveMarker();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(trackNavigationJumpCancellation, cancellation))
            {
                trackNavigationJumpCancellation = null;
                trackNavigationJumping = false;
            }

            cancellation.Dispose();
        }
    }

    private void TrackNavigationUserScroll(object sender, PointerRoutedEventArgs args) => CancelTrackNavigationJump();
    private void CancelTrackNavigationJump()
    {
        trackNavigationJumpCancellation?.Cancel();
        trackNavigationJumpCancellation = null;
        trackNavigationJumping = false;
        StopTrackNavigationLayoutWait();
        StopTrackNavigationScrollAnimation();
    }

    private async Task WaitForTrackNavigationLayoutAsync(CancellationToken cancellation)
    {
        StopTrackNavigationLayoutWait();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? onRendered = null;
        onRendered = (_, _) =>
        {
            if (!ReferenceEquals(trackNavigationLayoutCompletion, completion))
                return;
            CompositionTarget.Rendering -= onRendered;
            trackNavigationLayoutHandler = null;
            trackNavigationLayoutCompletion = null;
            completion.TrySetResult(true);
        };
        trackNavigationLayoutCompletion = completion;
        trackNavigationLayoutHandler = onRendered;
        CompositionTarget.Rendering += onRendered;
        using var registration = cancellation.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(trackNavigationLayoutCompletion, completion))
                StopTrackNavigationLayoutWait();
        }));
        await completion.Task;
    }

    private void StopTrackNavigationLayoutWait()
    {
        if (trackNavigationLayoutHandler is not null)
            CompositionTarget.Rendering -= trackNavigationLayoutHandler;
        trackNavigationLayoutHandler = null;
        var completion = trackNavigationLayoutCompletion;
        trackNavigationLayoutCompletion = null;
        completion?.TrySetResult(false);
    }

    private async Task AnimateTrackNavigationScrollAsync(ScrollViewer viewer, double target, CancellationToken cancellation, double? durationMs = null)
    {
        StopTrackNavigationScrollAnimation();
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        var start = viewer.VerticalOffset;
        var distance = Math.Abs(target - start);
        if (!TrackNavigationMotionEnabled || distance < 2)
        {
            viewer.ChangeView(null, target, null, true);
            return;
        }

        trackNavigationScrollStart = start;
        trackNavigationScrollTarget = target;
        trackNavigationScrollDuration = durationMs ?? TrackNavRailModel.ScrollDurationMs(distance);
        trackNavigationScrollStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        trackNavigationScrollCompletion = completion;
        using var registration = cancellation.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(trackNavigationScrollCompletion, completion))
                StopTrackNavigationScrollAnimation();
        }));
        CompositionTarget.Rendering += RenderTrackNavigationScroll;
        await completion.Task;
    }

    private void RenderTrackNavigationScroll(object? sender, object args)
    {
        var viewer = trackNavigationScrollViewer;
        if (viewer is null || !IsLoaded)
        {
            StopTrackNavigationScrollAnimation();
            return;
        }

        if (!TrackNavigationMotionEnabled)
        {
            viewer.ChangeView(null, trackNavigationScrollTarget, null, true);
            CompleteTrackNavigationScrollAnimation();
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(trackNavigationScrollStarted).TotalMilliseconds;
        var frame = TrackNavRailModel.ScrollFrame(trackNavigationScrollStart, trackNavigationScrollTarget, elapsed, trackNavigationScrollDuration);
        viewer.ChangeView(null, frame.ScrollTop, null, true);
        if (frame.Done)
            CompleteTrackNavigationScrollAnimation();
    }

    private void CompleteTrackNavigationScrollAnimation()
    {
        CompositionTarget.Rendering -= RenderTrackNavigationScroll;
        var completion = trackNavigationScrollCompletion;
        trackNavigationScrollCompletion = null;
        completion?.TrySetResult(true);
    }

    private void StopTrackNavigationScrollAnimation()
    {
        CompositionTarget.Rendering -= RenderTrackNavigationScroll;
        var completion = trackNavigationScrollCompletion;
        trackNavigationScrollCompletion = null;
        completion?.TrySetResult(false);
    }

    private async Task OpenTrackNavigationPreviewAsync(int index, Button anchor)
    {
        if (index < 0 || index >= trackNavigationMarkers.Count || viewModel is null)
            return;
        CancelTrackNavigationPreview();
        var request = ++trackNavigationPreviewRequest;
        var marker = trackNavigationMarkers[index];
        var identity = trackNavigationIdentity;
        if (TrackViewport.ActualWidth < 136)
            return;
        TrackNavigationPreview.Width = Math.Min(236, TrackViewport.ActualWidth - 56);
        TrackNavigationRange.Text = $"#{marker.StartTrack}–{marker.EndTrack}";
        TrackNavigationEmpty.Text = "…";
        TrackNavigationEmpty.Visibility = Visibility.Visible;
        TrackNavigationPreviewRows.Children.Clear();
        // The card stays 8 px clear of the list's edges. Within that it is as tall as its rows
        // need; the full height is reserved up front so it does not move when the rows arrive.
        var room = Math.Max(48, TrackViewportClearHeight - TrackListHeader.ActualHeight - 16);
        trackNavigationPreviewHeight = Math.Min(room, TrackNavigationPreviewFullHeight());
        var wasVisible = TrackNavigationPreview.Visibility == Visibility.Visible && !trackNavigationPreviewHiding;
        var previousTop = TrackNavigationPreview.Margin.Top + TrackNavigationPreviewTransform.TranslateY;
        var nextTop = TrackNavigationPreviewTop(anchor);
        TrackNavigationPreview.Margin = new Thickness(48, nextTop, 0, 0);
        TrackNavigationPreview.MaxHeight = room;
        trackNavigationPreviewHiding = false;
        TrackNavigationPreview.Visibility = Visibility.Visible;
        if (!wasVisible)
            AnimateTrackNavigationOverlay(TrackNavigationPreview, TrackNavigationPreviewTransform, true, true);
        else if (TrackNavigationMotionEnabled && Math.Abs(previousTop - nextTop) > 1)
        {
            trackNavigationPreviewStoryboard?.Stop();
            TrackNavigationPreview.Opacity = 1;
            TrackNavigationPreviewTransform.ScaleX = TrackNavigationPreviewTransform.ScaleY = 1;
            TrackNavigationPreviewTransform.TranslateY = previousTop - nextTop;
            var move = new Storyboard();
            AddTrackNavigationTween(move, TrackNavigationPreviewTransform, "TranslateY", 0, TimeSpan.FromMilliseconds(Motion.NormalMs));
            trackNavigationPreviewStoryboard = move;
            move.Begin();
        }
        else
            TrackNavigationPreviewTransform.TranslateY = 0;
        if (trackNavigationPreviewCache.TryGetValue(marker.GroupIndex, out var cached))
        {
            RenderTrackNavigationPreview(cached, request, identity);
            return;
        }

        var cancellation = new CancellationTokenSource();
        trackNavigationPreviewCancellation = cancellation;
        try
        {
            var rows = await viewModel.LoadTrackNavigationPreviewAsync(marker.StartIndex, marker.Count, cancellation.Token);
            if (cancellation.IsCancellationRequested || request != trackNavigationPreviewRequest || identity != viewModel.TrackNavigationIdentity)
                return;
            trackNavigationPreviewCache[marker.GroupIndex] = rows;
            trackNavigationPreviewOrder.Enqueue(marker.GroupIndex);
            while (trackNavigationPreviewOrder.Count > NavigationPreviewCacheLimit)
                trackNavigationPreviewCache.Remove(trackNavigationPreviewOrder.Dequeue());
            RenderTrackNavigationPreview(rows, request, identity);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (request == trackNavigationPreviewRequest)
                TrackNavigationEmpty.Text = Localization.Get("player.navRail.previewEmpty");
        }
        finally
        {
            if (ReferenceEquals(trackNavigationPreviewCancellation, cancellation))
                trackNavigationPreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>The card's top, below the list header: level with its marker, kept 8 px inside the list.</summary>
    private double TrackNavigationPreviewTop(Button anchor)
    {
        var header = TrackListHeader.ActualHeight;
        var point = anchor.TransformToVisual(TrackViewport).TransformPoint(new Point(0, 0));
        var top = Math.Clamp(point.Y - 8, header + 8, Math.Max(header + 8, TrackViewportClearHeight - trackNavigationPreviewHeight - 8));
        return top - header;
    }

    /// <summary>
    /// Height of a card holding a whole group: the range label, the rows with their gaps, and the
    /// card's padding and border. Text follows the system text size, so both lines are measured;
    /// a fixed height cuts into the last row once the text is enlarged.
    /// </summary>
    private double TrackNavigationPreviewFullHeight()
    {
        static double LineHeight(double fontSize, Windows.UI.Text.FontWeight weight)
        {
            var probe = new TextBlock
            {
                Text = "Ag",
                FontSize = fontSize,
                FontWeight = weight
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }

        var gap = TrackNavigationPreviewRows.Spacing;
        var row = Math.Max(TrackNavigationPreviewArtwork, LineHeight(TrackNavigationPreviewTitleFontSize, Microsoft.UI.Text.FontWeights.Normal));
        var chrome = TrackNavigationPreview.Padding.Top + TrackNavigationPreview.Padding.Bottom + TrackNavigationPreview.BorderThickness.Top + TrackNavigationPreview.BorderThickness.Bottom;
        return Math.Ceiling(chrome + LineHeight(TrackNavigationRange.FontSize, TrackNavigationRange.FontWeight) + TrackNavRailModel.GroupSize * (row + gap));
    }

    private void RenderTrackNavigationPreview(IReadOnlyList<TrackQueryRow> rows, int request, string? identity)
    {
        if (request != trackNavigationPreviewRequest || identity != trackNavigationIdentity)
            return;
        TrackNavigationPreviewRows.Children.Clear();
        TrackNavigationEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrackNavigationEmpty.Text = Localization.Get("player.navRail.previewEmpty");
        foreach (var row in rows.Take(TrackNavRailModel.GroupSize))
        {
            var art = new Image
            {
                Width = TrackNavigationPreviewArtwork,
                Height = TrackNavigationPreviewArtwork,
                Stretch = Stretch.UniformToFill,
                DataContext = row,
            };
            var artworkContent = new Grid();
            artworkContent.Children.Add(new TextBlock { Text = row.Monogram, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, });
            artworkContent.Children.Add(art);
            var imageBorder = WindowTheme.Brush(new Border { Width = TrackNavigationPreviewArtwork, Height = TrackNavigationPreviewArtwork, CornerRadius = new CornerRadius(6), Child = artworkContent, }, "Background", "AccentFillColorSecondaryBrush");
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            line.Children.Add(imageBorder);
            line.Children.Add(new TextBlock { Text = row.Title, Width = Math.Max(48, TrackNavigationPreview.Width - 66), FontSize = TrackNavigationPreviewTitleFontSize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, });
            TrackNavigationPreviewRows.Children.Add(line);
            _ = ApplyTrackNavigationArtworkAsync(art, row, request, identity);
        }
    }

    private async Task ApplyTrackNavigationArtworkAsync(Image image, TrackQueryRow row, int request, string? identity)
    {
        try
        {
            var model = viewModel;
            var artwork = !string.IsNullOrWhiteSpace(row.ArtworkPath) ? row.ArtworkPath : model is null ? null : await model.LoadRowArtworkAsync(row.Id);
            if (request == trackNavigationPreviewRequest && identity == trackNavigationIdentity && ReferenceEquals(image.DataContext, row) && TrackNavigationPreview.Visibility == Visibility.Visible)
                await ArtworkLoader.ApplyAsync(image, artwork);
        }
        catch
        { /* Missing artwork must not invalidate the preview. */
        }
    }

    private void CancelTrackNavigationPreview()
    {
        trackNavigationPreviewRequest++;
        trackNavigationPreviewCancellation?.Cancel();
        trackNavigationPreviewCancellation = null;
    }

    private void TrackNavigationViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        UpdateTrackNavigationActiveMarker();
        UpdateLocateButton();
        if (trackNavigationJumping || trackNavigationLoadingPrevious || DateTimeOffset.UtcNow < trackNavigationPreviousSuppressedUntil || viewModel is not { HasPrevious: true } || trackNavigationScrollViewer is null || trackNavigationScrollViewer.VerticalOffset > 48 || Tracks.Visibility != Visibility.Visible)
            return;
        _ = LoadPreviousTracksWithAnchorAsync();
    }

    private async Task LoadPreviousTracksWithAnchorAsync()
    {
        var model = viewModel;
        if (model is null || trackNavigationLoadingPrevious || !model.HasPrevious)
            return;
        trackNavigationLoadingPrevious = true;
        var identity = model.TrackNavigationIdentity;
        var index = Math.Max(0, (Tracks.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex ?? 0);
        var anchor = index < model.Tracks.Count ? model.Tracks[index] : null;
        try
        {
            await model.LoadPreviousAsync();
            if (anchor is not null && identity == model.TrackNavigationIdentity && model.Tracks.Contains(anchor))
            {
                Tracks.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
                trackNavigationPreviousSuppressedUntil = DateTimeOffset.UtcNow.AddMilliseconds(300);
            }
        }
        catch (Exception error)
        {
            PreviewLog.Write($"track navigation previous page failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            trackNavigationLoadingPrevious = false;
        }
    }

    private int ActiveTrackNavigationMarkerIndex()
    {
        if (trackNavigationMarkers.Count == 0 || viewModel is null)
            return -1;
        var local = (Tracks.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex ?? 0;
        return TrackNavRailModel.ActiveMarkerIndex(trackNavigationMarkers, Math.Max(0, viewModel.TrackWindowOffset + local));
    }

    private void UpdateTrackNavigationActiveMarker()
    {
        var active = ActiveTrackNavigationMarkerIndex();
        if (active == trackNavigationActiveIndex)
            return;
        trackNavigationActiveIndex = active;
        UpdateTrackNavigationMarkerAppearance();
        // The rail follows the list, but never moves out from under a pointer that is on it.
        if (TrackNavigationRail.Visibility == Visibility.Visible && !trackNavigationPointerInside)
            RevealTrackNavigationMarker(active, true);
    }

    private void UpdateTrackNavigationMarkerAppearance()
    {
        var theme = trackNavigationAccessibilitySettings.HighContrast ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var palette = (ResourceDictionary)Resources.ThemeDictionaries[theme];
        // Solid colours (opacity folded into alpha) so BrushTransition can blend them;
        // the resting colour is the same hue at zero alpha, so it fades rather than
        // passing through black.
        var softColor = palette["TrackNavigationAccentSoftBrush"] is SolidColorBrush softBrush ? Windows.UI.Color.FromArgb((byte)Math.Round(softBrush.Color.A * softBrush.Opacity), softBrush.Color.R, softBrush.Color.G, softBrush.Color.B) : Microsoft.UI.Colors.Transparent;
        var restColor = Windows.UI.Color.FromArgb(0, softColor.R, softColor.G, softColor.B);
        // High contrast puts the page's own text colour on the highlight; otherwise the icons take theme brushes.
        var contrastForeground = theme == "HighContrast" ? (Brush)palette["TrackNavigationAccentForegroundBrush"] : null;
        for (var index = 0; index < trackNavigationButtons.Count; index++)
        {
            var button = trackNavigationButtons[index];
            var selected = index == trackNavigationPreviewedIndex || index == trackNavigationActiveIndex;
            var surface = trackNavigationMarkerSurfaces[index];
            var color = selected ? softColor : restColor;
            if (surface.Background is not SolidColorBrush current || current.Color != color)
                surface.Background = new SolidColorBrush(color);
            var icon = WindowTheme.Brush(trackNavigationMarkerIcons[index], "Foreground", selected ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            if (selected && contrastForeground is not null)
                icon.Foreground = contrastForeground;
            else
                icon.ClearValue(IconElement.ForegroundProperty);
            var scale = index == trackNavigationActiveIndex ? 1.08f : 1f;
            if (button.Scale.X != scale)
                button.Scale = new System.Numerics.Vector3(scale, scale, 1);
        }
    }

    private void TrackNavigationThemeChanged(FrameworkElement sender, object args) => UpdateTrackNavigationMarkerAppearance();
    private void RefreshTrackNavigationLanguage()
    {
        TrackNavigationMenuItem.Text = Localization.Get("player.navRail.aria");
        for (var index = 0; index < trackNavigationButtons.Count; index++)
        {
            var marker = trackNavigationMarkers[index];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(trackNavigationButtons[index], Localization.Format("player.navRail.marker", ("start", marker.StartTrack.ToString()), ("end", marker.EndTrack.ToString())));
        }
    }
}
