using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OFPlayer_WinUI.Views;
internal sealed record MusicIconContour(Point Start, bool Closed, IReadOnlyList<Point[]> Segments);
/// <summary>Creates independent WinUI geometries from the shared SVG export.</summary>
public static class MusicIconGeometryLoader
{
    // The export draws every icon on a 20-unit artboard. PathIcon paints its data unscaled
    // from its top-left corner, so any other size has to scale the artboard itself.
    internal const double ArtboardSize = 20;
    private static readonly Dictionary<string, string> Payloads = LoadPayloads();
    public static IconElement CreatePathIcon(string key, double size = ArtboardSize)
    {
        try
        {
            var geometry = BuildGeometry(key);
            if (geometry is not null)
            {
                if (size != ArtboardSize)
                {
                    var scale = size / ArtboardSize;
                    geometry.Transform = new ScaleTransform
                    {
                        ScaleX = scale,
                        ScaleY = scale
                    };
                }

                return new PathIcon
                {
                    Data = geometry,
                    Width = size,
                    Height = size
                };
            }
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"music icon failed ({key}): {error.GetType().Name}: {error.Message}");
        }

        OFPlayer_WinUI.Services.PreviewLog.Write($"music icon missing: {key}");
        return new SymbolIcon(Symbol.Library);
    }

    public static Geometry? BuildGeometry(string key)
    {
        var geometry = new PathGeometry
        {
            FillRule = FillRule.Nonzero
        };
        foreach (var contour in ReadContours(key))
        {
            var figure = new PathFigure
            {
                IsClosed = contour.Closed,
                IsFilled = true,
                StartPoint = contour.Start
            };
            foreach (var segment in contour.Segments)
            {
                if (segment.Length == 1)
                {
                    figure.Segments.Add(new LineSegment { Point = segment[0] });
                    continue;
                }

                for (var index = 0; index + 2 < segment.Length; index += 3)
                {
                    figure.Segments.Add(new BezierSegment { Point1 = segment[index], Point2 = segment[index + 1], Point3 = segment[index + 2], });
                }
            }

            geometry.Figures.Add(figure);
        }

        return geometry.Figures.Count > 0 ? geometry : null;
    }

    /// <summary>
    /// The icon's contours in artboard units, in export order. A segment holds one point for a
    /// line and three per Bézier. Empty when the icon is unknown.
    /// </summary>
    internal static IReadOnlyList<MusicIconContour> ReadContours(string key)
    {
        var contours = new List<MusicIconContour>();
        if (!Payloads.TryGetValue(key, out var payload))
        {
            return contours;
        }

        using var document = JsonDocument.Parse(payload);
        foreach (var item in document.RootElement.GetProperty("figures").EnumerateArray())
        {
            var segments = new List<Point[]>();
            foreach (var segment in item.GetProperty("segments").EnumerateArray())
            {
                var values = segment.GetProperty("p");
                var line = segment.GetProperty("t").GetString() == "L";
                var points = new Point[line ? 1 : values.GetArrayLength() / 6 * 3];
                for (var index = 0; index < points.Length; index++)
                {
                    points[index] = new Point(values[index * 2].GetDouble(), values[index * 2 + 1].GetDouble());
                }

                segments.Add(points);
            }

            contours.Add(new MusicIconContour(ReadPoint(item.GetProperty("start")), item.TryGetProperty("closed", out var closed) && closed.GetBoolean(), segments));
        }

        return contours;
    }

    private static Point ReadPoint(JsonElement point) => new(point[0].GetDouble(), point[1].GetDouble());
    private static Dictionary<string, string> LoadPayloads()
    {
        var payloads = new Dictionary<string, string>();
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "MusicIcons.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var icon in document.RootElement.GetProperty("icons").EnumerateArray())
            {
                var key = icon.GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(key))
                {
                    payloads[key] = icon.GetRawText();
                }
            }
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"music icon payload failed: {error.GetType().Name}: {error.Message}");
        }

        return payloads;
    }
}
