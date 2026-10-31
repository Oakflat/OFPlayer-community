using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// The playback bar is frosted glass laid over the bottom of this page, so the song list runs
/// on underneath it. What must stay reachable keeps clear by the bar's height: the lists scroll
/// their last rows above it, their scroll bars stop where it begins, and fixed panels end there.
/// </summary>
public sealed partial class LibraryPageView
{
    private double playbackBarInset;
    private (Thickness Tracks, Thickness Groups, Thickness Footer)? playbackBarInsetBase;
    /// <summary>The part of the track viewport the playback bar leaves uncovered.</summary>
    private double TrackViewportClearHeight => Math.Max(0, TrackViewport.ActualHeight - playbackBarInset);

    public void SetPlaybackBarInset(double inset)
    {
        inset = Math.Max(0, Math.Round(inset));
        if (playbackBarInsetBase is null)
        {
            playbackBarInsetBase = (Tracks.Padding, GroupsList.Padding, CatalogFooter.Margin);
            // A list's scroll bar exists once the list is first laid out, which may be later.
            Tracks.SizeChanged += (_, _) => InsetScrollBar(Tracks);
            GroupsList.SizeChanged += (_, _) => InsetScrollBar(GroupsList);
        }
        else if (Math.Abs(inset - playbackBarInset) < 0.5)
            return;
        playbackBarInset = inset;
        var(tracks, groups, footer) = playbackBarInsetBase.Value;
        Tracks.Padding = tracks with
        {
            Bottom = tracks.Bottom + inset
        };
        GroupsList.Padding = groups with
        {
            Bottom = groups.Bottom + inset
        };
        CatalogFooter.Margin = footer with
        {
            Bottom = footer.Bottom + inset
        };
        LocateButton.Margin = new Thickness(0, 0, 36, 16 + inset);
        // Lyrics centre their current line, and the remote and empty panels end in buttons:
        // these stop above the bar rather than run under it.
        var clear = new Thickness(0, 0, 0, inset);
        LyricsPanel.Margin = RemotePanel.Margin = EmptyStatePanel.Margin = clear;
        InsetScrollBar(Tracks);
        InsetScrollBar(GroupsList);
        ResizeTrackNavigationRail();
    }

    private void InsetScrollBar(ListViewBase list)
    {
        if (FindTrackNavigationScrollViewer(list)is not { } viewer)
            return;
        if (FindScrollBar(viewer, "VerticalScrollBar")is { } bar)
            bar.Margin = new Thickness(0, 0, 0, playbackBarInset);
    }

    private static ScrollBar? FindScrollBar(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollBar { Name: var found } bar && found == name)
                return bar;
            // The rows live under the content presenter; the scroll bars are its siblings.
            if (child is ScrollContentPresenter)
                continue;
            if (FindScrollBar(child, name)is { } nested)
                return nested;
        }

        return null;
    }
}
