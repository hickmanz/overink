using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace OverInk.Editor;

/// <summary>
/// Hit testing and grouping for the editor's Select tool. Pure geometry over a <see cref="StrokeCollection"/>,
/// kept apart from the window so it can be tested without any UI.
/// </summary>
public static class StrokeSelection
{
    /// <summary>The topmost stroke whose drawn area lies within <paramref name="tolerance"/> of <paramref name="point"/>.</summary>
    public static Stroke? HitTop(StrokeCollection strokes, Point point, double tolerance)
    {
        var probe = new EllipseGeometry(point, tolerance, tolerance);
        var probeBounds = probe.Bounds;
        for (var i = strokes.Count - 1; i >= 0; i--)
        {
            var stroke = strokes[i];
            if (Touches(stroke, probe, probeBounds))
            {
                return stroke;
            }
        }

        return null;
    }

    /// <summary>Every stroke whose drawn area touches <paramref name="rect"/>.</summary>
    public static List<Stroke> Intersecting(StrokeCollection strokes, Rect rect)
    {
        var result = new List<Stroke>();
        var probe = new RectangleGeometry(rect);
        foreach (var stroke in strokes)
        {
            if (Touches(stroke, probe, rect))
            {
                result.Add(stroke);
            }
        }

        return result;
    }

    /// <summary>
    /// The seed strokes plus every other stroke that shares a group with one of them (an arrow's shaft and head
    /// are two strokes but one entity). Result is in canvas order.
    /// </summary>
    public static List<Stroke> ExpandGroups(StrokeCollection all, IEnumerable<Stroke> seeds)
    {
        var seedSet = new HashSet<Stroke>(seeds);
        var groups = new HashSet<string>();
        foreach (var seed in seedSet)
        {
            var group = StrokeGroups.Get(seed);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        var result = new List<Stroke>();
        foreach (var stroke in all)
        {
            if (seedSet.Contains(stroke) || (StrokeGroups.Get(stroke) is { } group && groups.Contains(group)))
            {
                result.Add(stroke);
            }
        }

        return result;
    }

    /// <summary>Union of the strokes' drawn bounds, or <see cref="Rect.Empty"/> when there are none.</summary>
    public static Rect Bounds(IEnumerable<Stroke> strokes)
    {
        var bounds = Rect.Empty;
        foreach (var stroke in strokes)
        {
            bounds.Union(stroke.GetBounds());
        }

        return bounds;
    }

    private static bool Touches(Stroke stroke, Geometry probe, Rect probeBounds)
    {
        var bounds = stroke.GetBounds();
        if (bounds.IsEmpty || !bounds.IntersectsWith(probeBounds))
        {
            return false;
        }

        var geometry = stroke.GetGeometry();
        return geometry.FillContainsWithDetail(probe) != IntersectionDetail.Empty;
    }
}
