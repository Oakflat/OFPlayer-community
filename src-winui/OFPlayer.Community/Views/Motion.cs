using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Shared motion vocabulary for the shell. A transition either runs one of these
/// curves to its end or is applied instantly; nothing animates and then snaps.
/// Reversals always start from the value currently on screen.
/// </summary>
internal static class Motion
{
    public const double FastMs = 167;
    public const double NormalMs = 250;
    public const double SlowMs = 333;
    public const double ExitMs = 150;
    /// <summary>Entering / settling: moves immediately, lands with a long soft tail.</summary>
    public static readonly Vector4 Decelerate = new(0.1f, 0.9f, 0.2f, 1f);
    /// <summary>Leaving: holds briefly, then clears out of the way.</summary>
    public static readonly Vector4 Accelerate = new(0.7f, 0f, 1f, 0.5f);
    private static readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    /// <summary>Follows the Windows "Animation effects" setting.</summary>
    public static bool Enabled => uiSettings.AnimationsEnabled;

    private sealed class State
    {
        public int Revision;
    }

    private static readonly ConditionalWeakTable<UIElement, State> states = new();
    /// <summary>
    /// Makes an element visible and fades it in, optionally gliding from an offset.
    /// A hidden element starts from transparent; one mid-way through a hide
    /// reverses from where it is.
    /// </summary>
    public static void Show(UIElement element, float offsetX = 0, float offsetY = 0, double durationMs = NormalMs, double delayMs = 0, bool restart = false)
    {
        var state = states.GetOrCreateValue(element);
        state.Revision++;
        var fromHidden = restart || element.Visibility != Visibility.Visible;
        element.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        if (!Enabled)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var compositor = visual.Compositor;
        var easing = Ease(compositor, Decelerate);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        if (fromHidden)
            opacity.InsertKeyFrame(0, 0);
        else
            opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(durationMs);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        if (fromHidden)
            translation.InsertKeyFrame(0, new Vector3(offsetX, offsetY, 0));
        else
            translation.InsertExpressionKeyFrame(0, "this.StartingValue");
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.Duration = opacity.Duration;
        if (delayMs > 0)
        {
            opacity.DelayTime = translation.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            opacity.DelayBehavior = translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    /// <summary>
    /// Fades an element out, optionally drifting toward an offset. By default it is
    /// collapsed once the fade has finished; <paramref name = "completed"/> runs only if
    /// no later Show/Hide superseded this one.
    /// </summary>
    public static void Hide(UIElement element, float offsetX = 0, float offsetY = 0, double durationMs = ExitMs, bool collapse = true, Action? completed = null)
    {
        var state = states.GetOrCreateValue(element);
        var revision = ++state.Revision;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        void Finish()
        {
            if (state.Revision != revision)
                return;
            if (collapse)
                element.Visibility = Visibility.Collapsed;
            visual.Opacity = 0;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            completed?.Invoke();
        }

        if (!Enabled || element.Visibility != Visibility.Visible)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            Finish();
            return;
        }

        var compositor = visual.Compositor;
        var easing = Ease(compositor, Accelerate);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacity.InsertKeyFrame(1, 0, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(durationMs);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertExpressionKeyFrame(0, "this.StartingValue");
        translation.InsertKeyFrame(1, new Vector3(offsetX, offsetY, 0), easing);
        translation.Duration = opacity.Duration;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
        batch.End();
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            Finish();
        };
    }

    /// <summary>Sets the composition opacity without animating (e.g. a warm, invisible surface).</summary>
    public static void SetOpacity(UIElement element, float opacity)
    {
        states.GetOrCreateValue(element).Revision++;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = opacity;
    }

    private static readonly ConditionalWeakTable<UIElement, Microsoft.UI.Xaml.Media.Animation.Storyboard> opacityStoryboards = new();
    /// <summary>
    /// Eases the XAML Opacity property. For elements owned by NavigationView and similar
    /// hosts, whose visuals must not be touched through composition.
    /// </summary>
    public static void FadeProperty(UIElement element, double opacity, double durationMs = FastMs)
    {
        if (opacityStoryboards.TryGetValue(element, out var running))
        {
            // Pin the current animated value, then release the old timeline.
            var current = element.Opacity;
            running.Stop();
            element.Opacity = current;
            opacityStoryboards.Remove(element);
        }

        if (!Enabled || Math.Abs(element.Opacity - opacity) < 0.001)
        {
            element.Opacity = opacity;
            return;
        }

        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = opacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = opacity > element.Opacity ? Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut : Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!opacityStoryboards.TryGetValue(element, out var active) || !ReferenceEquals(active, storyboard))
                return;
            storyboard.Stop();
            element.Opacity = opacity;
            opacityStoryboards.Remove(element);
        };
        opacityStoryboards.AddOrUpdate(element, storyboard);
        storyboard.Begin();
    }

    /// <summary>A short opacity-only reveal for content that has just finished loading.</summary>
    public static void FadeIn(UIElement element, double durationMs = FastMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!Enabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            return;
        }

        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, Ease(compositor, Decelerate));
        opacity.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", opacity);
    }

    /// <summary>Cancels any running opacity animation and shows the element fully.</summary>
    public static void ResetOpacity(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }

    public static CubicBezierEasingFunction Ease(Compositor compositor, Vector4 curve) => compositor.CreateCubicBezierEasingFunction(new Vector2(curve.X, curve.Y), new Vector2(curve.Z, curve.W));
    /// <summary>Samples a cubic-bezier timing curve at time fraction <paramref name = "x"/>.</summary>
    public static double Evaluate(Vector4 curve, double x)
    {
        if (x <= 0)
            return 0;
        if (x >= 1)
            return 1;
        double lower = 0, upper = 1, t = x;
        for (var iteration = 0; iteration < 24; iteration++)
        {
            t = (lower + upper) / 2;
            if (Bezier(curve.X, curve.Z, t) < x)
                lower = t;
            else
                upper = t;
        }

        return Bezier(curve.Y, curve.W, t);
    }

    private static double Bezier(double p1, double p2, double t)
    {
        var inverse = 1 - t;
        return 3 * inverse * inverse * t * p1 + 3 * inverse * t * t * p2 + t * t * t;
    }
}

/// <summary>
/// A per-frame scalar timeline for properties that must go through layout (widths,
/// heights, scroll offsets). Retargeting continues from the value on screen, and
/// the final frame always applies the exact target.
/// </summary>
internal sealed class FrameTimeline
{
    private readonly Action<double> apply;
    private double from;
    private double to;
    private double durationMs;
    private System.Numerics.Vector4 curve;
    private long started;
    private bool running;
    private Action? completed;
    public FrameTimeline(double initial, Action<double> apply)
    {
        Value = initial;
        this.apply = apply;
    }

    public double Value { get; private set; }
    public double Target => running ? to : Value;
    public bool IsRunning => running;

    public void AnimateTo(double target, double durationMs, System.Numerics.Vector4 curve, Action? onCompleted = null)
    {
        completed = onCompleted;
        if (!Motion.Enabled || durationMs <= 0 || Math.Abs(target - Value) < 0.5)
        {
            Jump(target);
            return;
        }

        from = Value;
        to = target;
        this.durationMs = durationMs;
        this.curve = curve;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (running)
            return;
        running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Applies <paramref name = "target"/> immediately and completes any pending callback.</summary>
    public void Jump(double target)
    {
        Stop();
        Value = target;
        apply(target);
        var callback = completed;
        completed = null;
        callback?.Invoke();
    }

    /// <summary>Re-bases the timeline on a known value, dropping any pending callback.</summary>
    public void SetValue(double value)
    {
        Stop();
        completed = null;
        Value = value;
        apply(value);
    }

    /// <summary>Stops where it is without running the completion callback.</summary>
    public void Stop()
    {
        if (!running)
            return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / durationMs, 0, 1);
        if (progress >= 1)
        {
            Jump(to);
            return;
        }

        Value = from + (to - from) * Motion.Evaluate(curve, progress);
        apply(Value);
    }
}
