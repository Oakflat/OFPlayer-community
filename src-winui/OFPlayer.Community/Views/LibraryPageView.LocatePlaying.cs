using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// The "go to the playing song" button. The list keeps the row the listener last picked selected
/// while playback moves on by itself, so the playing song can be a few rows away, or hundreds. The
/// button sits at the lower right whenever the playing song is in this list but is not what the list
/// is showing as selected, or is out of view; it brings the song to the middle and selects it.
/// </summary>
public sealed partial class LibraryPageView
{
    // The playing song's place in the whole collection, read when playback or the list changes.
    private int locateIndex = -1;
    private string? locatePlayingId;
    /// <summary>The song may have changed, or the list it is in: read where it is, then decide about the button.</summary>
    private void RefreshLocateTarget()
    {
        locatePlayingId = viewModel?.Playback.ActiveTrackId;
        locateIndex = viewModel?.PlayingTrackIndex ?? -1;
        UpdateLocateButton();
    }

    private void UpdateLocateButton()
    {
        var show = false;
        if (viewModel is { IsMultiSelect: false } model && locateIndex >= 0 && Tracks.Visibility == Visibility.Visible)
        {
            var local = locateIndex - model.TrackWindowOffset;
            var row = local >= 0 && local < model.Tracks.Count ? model.Tracks[local] : null;
            // Out of the loaded window counts as out of view. Selected elsewhere is Fig. "chosen row left behind".
            var inView = row is not null && PlayingRowInView(local);
            var selectedElsewhere = Tracks.SelectedItems.Count > 0 && (row is null || !Tracks.SelectedItems.Contains(row));
            show = !inView || selectedElsewhere;
        }

        LocateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the row's middle is inside the part of the list the playback bar leaves uncovered.</summary>
    private bool PlayingRowInView(int local)
    {
        if (Tracks.ContainerFromIndex(local)is not FrameworkElement container || container.ActualHeight <= 0)
            return false;
        try
        {
            var middle = container.TransformToVisual(Tracks).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + container.ActualHeight / 2;
            return middle > 0 && middle < Math.Max(0, Tracks.ActualHeight - playbackBarInset);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async void LocatePlayingClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("LibraryPageView.xaml.LocatePlayingClicked");
        if (viewModel is null || locateIndex < 0)
            return;
        // The jump to a marker on the rail and this one share the list's scroll and layout helpers.
        CancelTrackNavigationJump();
        var cancellation = new CancellationTokenSource();
        trackNavigationJumpCancellation = cancellation;
        var identity = viewModel.TrackNavigationIdentity;
        trackNavigationJumping = true;
        try
        {
            var index = viewModel.PlayingTrackIndex;
            var row = await viewModel.LocatePlayingTrackAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity || row is null)
                return;
            trackNavigationPreviousSuppressedUntil = DateTimeOffset.UtcNow.AddMilliseconds(600);
            await WaitForTrackNavigationLayoutAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                return;
            var local = Math.Clamp(index - viewModel.TrackWindowOffset, 0, Math.Max(0, viewModel.Tracks.Count - 1));
            var scrollViewer = trackNavigationScrollViewer ?? FindTrackNavigationScrollViewer(Tracks);
            if (scrollViewer is not null && !PlayingRowInView(local))
            {
                var clear = Math.Max(0, Tracks.ActualHeight - playbackBarInset);
                var firstVisible = Math.Max(0, (Tracks.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex ?? 0);
                var realized = Tracks.ContainerFromIndex(firstVisible) as FrameworkElement;
                var rowHeight = realized is { ActualHeight: > 0 } ? realized.ActualHeight : 64;
                // First by estimate, so the rows around it exist; then by where its own row landed.
                await AnimateTrackNavigationScrollAsync(scrollViewer, local * rowHeight - Math.Max(0, (clear - rowHeight) / 2), cancellation.Token);
                if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                    return;
                if (Tracks.ContainerFromItem(row)is FrameworkElement landed)
                {
                    var top = landed.TransformToVisual(scrollViewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    await AnimateTrackNavigationScrollAsync(scrollViewer, scrollViewer.VerticalOffset + top - Math.Max(0, (clear - landed.ActualHeight) / 2), cancellation.Token, 180);
                }
                else
                    Tracks.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            }

            if (cancellation.IsCancellationRequested || identity != viewModel.TrackNavigationIdentity)
                return;
            // The selection box moves to the song that is playing; nothing is played or changed.
            Tracks.SelectedItem = row;
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
            UpdateLocateButton();
        }
    }
}
