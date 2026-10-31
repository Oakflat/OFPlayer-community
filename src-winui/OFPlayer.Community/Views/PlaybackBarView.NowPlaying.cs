using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using OFPlayer.WinUI.Core.Diagnostics;

namespace OFPlayer_WinUI.Views;
public sealed partial class PlaybackBarView
{
    private sealed record TrackPresentation(string? Id, string Title, string Artist, string? Artwork);
    private TrackPresentation? requestedTrack;
    private TrackPresentation? displayedTrack;
    private long trackPresentationRevision;
    private long trackAnimationRevision;
    private async Task UpdateTrackPresentationAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.NowPlaying.UpdateTrackPresentationAsync");
        if (viewModel is null)
            return;
        var next = new TrackPresentation(viewModel.Playback.ActiveTrackId, viewModel.PlaybackTitle, viewModel.PlaybackArtist, viewModel.PlaybackArtwork);
        if (next == requestedTrack)
            return; // Progress/control updates never restart the animation.
        requestedTrack = next;
        var revision = ++trackPresentationRevision;
        using var operation = PerformanceTrace.BeginOperation("playback.trackPresentation.prepare");
        // The playback event can precede the detail/artwork reply. Give that
        // reply a short head start so a normal switch never fades to a blank
        // cover and then fades a second time when the same song's cover arrives.
        if (next.Id is not null and not "external" && next.Artwork is null && viewModel.CurrentTrackDetail?.Id != next.Id)
        {
            await Task.Delay(120);
            if (revision != trackPresentationRevision || !IsLoaded)
                return;
        }

        // Decode into a staging image. Keep the previous complete track visible
        // until its replacement is ready; late covers cannot overwrite a newer song.
        var staged = new Image
        {
            Width = 48,
            Height = 48
        };
        await ArtworkLoader.ApplyAsync(staged, next.Artwork);
        if (revision != trackPresentationRevision || !IsLoaded)
            return;
        var animate = displayedTrack?.Id is not null && displayedTrack != next && Motion.Enabled;
        var oldVisual = ElementCompositionPreview.GetElementVisual(PreviousTrack);
        var newVisual = ElementCompositionPreview.GetElementVisual(CurrentTrack);
        ElementCompositionPreview.SetIsTranslationEnabled(PreviousTrack, true);
        ElementCompositionPreview.SetIsTranslationEnabled(CurrentTrack, true);
        oldVisual.StopAnimation("Opacity");
        oldVisual.StopAnimation("Translation");
        newVisual.StopAnimation("Opacity");
        newVisual.StopAnimation("Translation");
        // Stopping keeps the on-screen value, so a switch during a running
        // transition hands off from what is actually visible.
        var currentOpacity = newVisual.Opacity;
        var animationRevision = ++trackAnimationRevision;
        PreviousArt.Source = NowArt.Source;
        PreviousLabel.Text = NowLabel.Text;
        PreviousSub.Text = NowSub.Text;
        NowArt.Source = staged.Source;
        NowLabel.Text = next.Title;
        NowSub.Text = next.Artist;
        displayedTrack = next;
        if (!animate)
        {
            PreviousTrack.Visibility = Visibility.Collapsed;
            PreviousArt.Source = null;
            newVisual.Opacity = 1;
            newVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            PerformanceTrace.Mark("playback.trackPresentation.applied", new { animated = false });
            return;
        }

        // Staggered hand-off: the outgoing row clears quickly and lifts away, then the
        // incoming row settles in from below. The two titles never sit on top of
        // each other at half opacity.
        PreviousTrack.Visibility = Visibility.Visible;
        oldVisual.Opacity = currentOpacity;
        oldVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        newVisual.Opacity = 0;
        var compositor = newVisual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var leaveEase = Motion.Ease(compositor, Motion.Accelerate);
        var enterEase = Motion.Ease(compositor, Motion.Decelerate);
        var leaveDuration = TimeSpan.FromMilliseconds(120);
        var enterDelay = TimeSpan.FromMilliseconds(80);
        var enterDuration = TimeSpan.FromMilliseconds(Motion.NormalMs);
        var leave = compositor.CreateScalarKeyFrameAnimation();
        leave.InsertExpressionKeyFrame(0, "this.StartingValue");
        leave.InsertKeyFrame(1, 0, leaveEase);
        leave.Duration = leaveDuration;
        var lift = compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1, new Vector3(0, -4, 0), leaveEase);
        lift.Duration = leaveDuration;
        var enter = compositor.CreateScalarKeyFrameAnimation();
        enter.InsertKeyFrame(0, 0);
        enter.InsertKeyFrame(1, 1, enterEase);
        enter.Duration = enterDuration;
        enter.DelayTime = enterDelay;
        enter.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var settle = compositor.CreateVector3KeyFrameAnimation();
        settle.InsertKeyFrame(0, new Vector3(0, 6, 0));
        settle.InsertKeyFrame(1, Vector3.Zero, enterEase);
        settle.Duration = enterDuration;
        settle.DelayTime = enterDelay;
        settle.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        batch.Completed += (_, _) =>
        {
            if (trackAnimationRevision == animationRevision)
            {
                PreviousTrack.Visibility = Visibility.Collapsed;
                PreviousArt.Source = null;
                oldVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                PerformanceTrace.Mark("playback.trackPresentation.completed", new { durationMs = (enterDelay + enterDuration).TotalMilliseconds });
            }

            batch.Dispose();
        };
        oldVisual.StartAnimation("Opacity", leave);
        oldVisual.StartAnimation("Translation", lift);
        newVisual.StartAnimation("Opacity", enter);
        newVisual.StartAnimation("Translation", settle);
        batch.End();
        PerformanceTrace.Mark("playback.trackPresentation.started", new { durationMs = (enterDelay + enterDuration).TotalMilliseconds });
    }

    private void CancelTrackTransition()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("PlaybackBarView.NowPlaying.CancelTrackTransition");
        trackPresentationRevision++;
        trackAnimationRevision++;
        requestedTrack = null;
        ElementCompositionPreview.GetElementVisual(PreviousTrack).StopAnimation("Opacity");
        var previous = ElementCompositionPreview.GetElementVisual(PreviousTrack);
        previous.StopAnimation("Translation");
        var visual = ElementCompositionPreview.GetElementVisual(CurrentTrack);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        if (visual.Properties.TryGetVector3("Translation", out _) == CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        PreviousTrack.Visibility = Visibility.Collapsed;
        PreviousArt.Source = null;
    }
}
