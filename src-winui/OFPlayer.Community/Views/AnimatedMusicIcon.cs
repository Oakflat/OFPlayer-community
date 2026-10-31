using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace OFPlayer_WinUI.Views;

/// <summary>
/// Sidebar icons that move once when the pointer reaches them and give a little when pressed.
/// The artwork is the shared icon export, split into the contours that move together; nothing is
/// redrawn and every motion ends on the still icon. The navigation item drives the states the way
/// it drives its own settings gear, and the motion runs in the compositor.
/// </summary>
internal static partial class AnimatedMusicIcon
{
    private const float Artboard = (float)MusicIconGeometryLoader.ArtboardSize;
    // Room around the artboard for parts that travel past its edge.
    private const float Bleed = 4;
    private const double PressMs = 90;
    private const double ReleaseMs = 260;
    private const float PressedScale = 0.86f;

    private static readonly Vector2 Centre = new(Artboard / 2);
    /// <summary>Steady travel into a soft stop.</summary>
    private static readonly Vector4 Glide = new(0.55f, 0.55f, 0f, 1f);

    private enum Channel { Turn, Move, Stretch }

    /// <summary>A value reached at a point of the arrival motion (0 to 1) and the curve leading into it.</summary>
    private readonly record struct Key(float At, float X, float Y, Vector4 Curve);

    private sealed record Track(Channel Channel, params Key[] Keys);

    /// <summary>Contours that move as one; they turn and stretch about the pivot.</summary>
    private sealed record Part(int[] Contours, Vector2 Pivot, params Track[] Tracks);

    private sealed record Design(int Contours, double ArrivalMs, params Part[] Parts);

    // Contour numbers follow the export order in MusicIcons.json. An icon whose export no longer
    // has the expected number of contours stays still rather than moving the wrong pieces.
    private static readonly Dictionary<string, Design> Designs = new()
    {
        // The note leans toward its list while the two lines are drawn again, one after the other.
        ["MusicIcon.AllTracks"] = new(4, 600,
            new Part([3, 2], new Vector2(3.76f, 14.37f), Sway(12)),
            new Part([1], new Vector2(12.36f, 8.48f), Redraw(0.1f, 0.7f)),
            new Part([0], new Vector2(12.33f, 13.04f), Redraw(0.3f, 1f))),
        // The record turns once; the highlight between its rings is what shows it.
        ["MusicIcon.Albums"] = new(6, 640,
            new Part([0, 1, 2, 3, 4, 5], Centre, Turn(360))),
        // The head bobs, then the note beside it pops up.
        ["MusicIcon.Artists"] = new(4, 600,
            new Part([3, 2], default, Hop(0f, 0.6f, 1.5f)),
            new Part([1], default),
            new Part([0], default, Hop(0.3f, 1f, 1.8f))),
        // The heart beats twice.
        ["MusicIcon.Favorites"] = new(1, 620,
            new Part([0], Centre, Beat(0.15f))),
        // The note is the clock's hand and goes round once.
        ["MusicIcon.History"] = new(3, 700,
            new Part([0, 1], default),
            new Part([2], Centre, Turn(360))),
        // The arrow lifts and drops into the tray.
        ["MusicIcon.RecentImports"] = new(3, 520,
            new Part([0, 2], default),
            new Part([1], default, Hop(0f, 1f, 2.8f))),
        // The small note hops while the two loose lines are drawn again, one after the other.
        ["MusicIcon.Queue"] = new(6, 560,
            new Part([5, 4], default, Hop(0f, 0.6f, 2.2f)),
            new Part([3, 2], default),
            new Part([0], new Vector2(10.89f, 6.48f), Redraw(0.05f, 0.7f)),
            new Part([1], new Vector2(10.88f, 14.13f), Redraw(0.25f, 1f))),
        // A playlist's pair of notes rocks on its feet.
        ["MusicIcon.EighthPair"] = new(4, 520,
            new Part([0, 1, 2, 3], new Vector2(10f, 16.5f), Sway(9))),
    };

    /// <summary>Even swing from one extreme to the other.</summary>
    private static readonly Vector4 Swing = new(0.45f, 0f, 0.55f, 1f);

    private static Track Turn(float degrees) =>
        new(Channel.Turn, new Key(1, degrees, 0, Glide));

    /// <summary>Leans by <paramref name="degrees"/>, swings back past upright and settles.</summary>
    private static Track Sway(float degrees) =>
        new(Channel.Turn,
            new Key(0.3f, degrees, 0, Motion.Decelerate),
            new Key(0.68f, -degrees * 0.45f, 0, Swing),
            new Key(1, 0, 0, Swing));

    /// <summary>Swells twice like a heartbeat, the second beat smaller.</summary>
    private static Track Beat(float swell) =>
        new(Channel.Stretch,
            new Key(0.24f, 1 + swell, 1 + swell, Motion.Decelerate),
            new Key(0.5f, 1, 1, Swing),
            new Key(0.72f, 1 + swell * 0.5f, 1 + swell * 0.5f, Motion.Decelerate),
            new Key(1, 1, 1, Swing));

    /// <summary>Rises by <paramref name="height"/> artboard units and lands again.</summary>
    private static Track Hop(float from, float to, float height) =>
        new(Channel.Move,
            new Key(from, 0, 0, Glide),
            new Key(from + (to - from) * 0.4f, 0, -height, Motion.Decelerate),
            new Key(to, 0, 0, Motion.Accelerate));

    /// <summary>Pulls back toward the pivot, then extends to full length.</summary>
    private static Track Redraw(float from, float to) =>
        new(Channel.Stretch,
            new Key(from, 1, 1, Glide),
            new Key(from + (to - from) * 0.3f, 0.3f, 1, Motion.Accelerate),
            new Key(to, 1, 1, Motion.Decelerate));

    /// <summary>The moving icon for <paramref name="key"/>, or null when it has none and the still icon applies.</summary>
    public static IconElement? TryCreate(string key)
    {
        if (!Designs.TryGetValue(key, out var design))
        {
            return null;
        }

        try
        {
            var contours = MusicIconGeometryLoader.ReadContours(key);
            if (contours.Count != design.Contours)
            {
                OFPlayer_WinUI.Services.PreviewLog.Write($"animated music icon skipped ({key}): {contours.Count} contours");
                return null;
            }

            var paths = design.Parts
                .Select(part => CompositionIconPath.Create(part.Contours.Select(index => contours[index])))
                .ToArray();
            return new AnimatedIcon
            {
                Source = new Source(design, paths),
                FallbackIconSource = new PathIconSource { Data = MusicIconGeometryLoader.BuildGeometry(key) },
                Width = Artboard,
                Height = Artboard,
            };
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write(
                $"animated music icon failed ({key}): {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// One timeline per icon: the arrival motion, then pressing in, then letting go. Every stretch
    /// starts and ends on the still icon, so leaving a state never needs a motion of its own.
    /// </summary>
    private sealed partial class Source : IAnimatedVisualSource2
    {
        private readonly Design design;
        private readonly CompositionPath[] paths;
        private readonly TimeSpan duration;
        private readonly float arrivalEnd;
        private readonly float pressEnd;
        private Color color;
        private CompositionColorBrush? brush;

        public Source(Design design, CompositionPath[] paths)
        {
            this.design = design;
            this.paths = paths;
            var total = design.ArrivalMs + PressMs + ReleaseMs;
            duration = TimeSpan.FromMilliseconds(total);
            arrivalEnd = (float)(design.ArrivalMs / total);
            pressEnd = (float)((design.ArrivalMs + PressMs) / total);
            Markers = new Dictionary<string, double>
            {
                ["NormalToPointerOver_Start"] = 0,
                ["NormalToPointerOver_End"] = arrivalEnd,
                ["PointerOverToNormal"] = 0,
                ["NormalToPressed_Start"] = arrivalEnd,
                ["NormalToPressed_End"] = pressEnd,
                ["PointerOverToPressed_Start"] = arrivalEnd,
                ["PointerOverToPressed_End"] = pressEnd,
                ["PressedToPointerOver_Start"] = pressEnd,
                ["PressedToPointerOver_End"] = 1,
                ["PressedToNormal_Start"] = pressEnd,
                ["PressedToNormal_End"] = 1,
            };
        }

        public IReadOnlyDictionary<string, double> Markers { get; }

        public void SetColorProperty(string propertyName, Color value)
        {
            if (propertyName != "Foreground")
            {
                return;
            }

            color = value;
            if (brush is not null)
            {
                brush.Color = value;
            }
        }

        public IAnimatedVisual? TryCreateAnimatedVisual(Compositor compositor, out object? diagnostics)
        {
            diagnostics = null;
            try
            {
                return Build(compositor);
            }
            catch (Exception error)
            {
                OFPlayer_WinUI.Services.PreviewLog.Write(
                    $"animated music icon visual failed: {error.GetType().Name}: {error.Message}");
                return null;
            }
        }

        private IconVisual Build(Compositor compositor)
        {
            // The icon control animates Progress on the root; everything below follows it.
            var root = compositor.CreateContainerVisual();
            root.Size = new Vector2(Artboard);
            root.Properties.InsertScalar("Progress", 0);
            var progress = compositor.CreateExpressionAnimation("root.Progress");
            progress.SetReferenceParameter("root", root.Properties);

            var canvas = compositor.CreateShapeVisual();
            canvas.Size = new Vector2(Artboard + Bleed * 2);
            canvas.Offset = new Vector3(-Bleed, -Bleed, 0);
            root.Children.InsertAtTop(canvas);

            var stage = compositor.CreateContainerShape();
            stage.Offset = new Vector2(Bleed);
            stage.CenterPoint = Centre;
            canvas.Shapes.Add(stage);

            var press = compositor.CreateVector2KeyFrameAnimation();
            press.InsertKeyFrame(0, Vector2.One);
            press.InsertKeyFrame(arrivalEnd, Vector2.One);
            press.InsertKeyFrame(pressEnd, new Vector2(PressedScale), Motion.Ease(compositor, Motion.Decelerate));
            press.InsertKeyFrame(1, Vector2.One, Motion.Ease(compositor, Motion.Decelerate));
            Drive(stage, "Scale", press, progress);

            brush ??= compositor.CreateColorBrush(color);
            for (var index = 0; index < design.Parts.Length; index++)
            {
                var part = design.Parts[index];
                var shape = compositor.CreateSpriteShape(compositor.CreatePathGeometry(paths[index]));
                shape.FillBrush = brush;
                shape.CenterPoint = part.Pivot;
                foreach (var track in part.Tracks)
                {
                    Drive(shape, track, compositor, progress);
                }

                stage.Shapes.Add(shape);
            }

            return new IconVisual(root, duration);
        }

        private void Drive(CompositionShape shape, Track track, Compositor compositor, ExpressionAnimation progress)
        {
            if (track.Channel == Channel.Turn)
            {
                var turn = compositor.CreateScalarKeyFrameAnimation();
                turn.InsertKeyFrame(0, 0);
                foreach (var key in track.Keys)
                {
                    turn.InsertKeyFrame(key.At * arrivalEnd, key.X, Motion.Ease(compositor, key.Curve));
                }

                Drive(shape, "RotationAngleInDegrees", turn, progress);
                return;
            }

            var travel = compositor.CreateVector2KeyFrameAnimation();
            travel.InsertKeyFrame(0, track.Channel == Channel.Stretch ? Vector2.One : Vector2.Zero);
            foreach (var key in track.Keys)
            {
                travel.InsertKeyFrame(key.At * arrivalEnd, new Vector2(key.X, key.Y), Motion.Ease(compositor, key.Curve));
            }

            Drive(shape, track.Channel == Channel.Stretch ? "Scale" : "Offset", travel, progress);
        }

        /// <summary>Starts the animation paused and lets the shared progress value scrub it.</summary>
        private void Drive(CompositionObject target, string property, KeyFrameAnimation animation, ExpressionAnimation progress)
        {
            animation.Duration = duration;
            target.StartAnimation(property, animation);
            var controller = target.TryGetAnimationController(property);
            controller.Pause();
            controller.StartAnimation("Progress", progress);
        }
    }

    private sealed partial class IconVisual(ContainerVisual root, TimeSpan duration) : IAnimatedVisual
    {
        public TimeSpan Duration => duration;

        public Visual RootVisual => root;

        public Vector2 Size => new(Artboard);

        public void Dispose() => root.Dispose();
    }
}
