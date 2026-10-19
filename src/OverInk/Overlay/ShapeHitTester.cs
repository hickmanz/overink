using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OverInk.Overlay;

/// <summary>
/// Geometry-accurate eraser hit test for shape elements created by <see cref="ShapeDrawingHelper"/>.
/// Shapes are tested by their outline (like stroke erasing), not by bounding box, and lines/arrows
/// (which are Canvases holding absolutely-positioned children) work as well as rectangles/ellipses.
/// </summary>
internal static class ShapeHitTester
{
    private const int EllipseSegments = 48;

    /// <summary>True if an eraser of the given radius dragged along <paramref name="path"/> touches the shape.</summary>
    public static bool Hits(UIElement element, IReadOnlyList<Point> path, double eraserRadius)
    {
        if (path.Count == 0)
        {
            return false;
        }

        var segments = GetOutline(element, out var thickness);
        var reach = eraserRadius + thickness / 2;

        for (var i = 0; i < path.Count; i++)
        {
            var a = path[i];
            var b = i + 1 < path.Count ? path[i + 1] : path[i];

            foreach (var (c, d) in segments)
            {
                if (SegmentDistance(a, b, c, d) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<(Point A, Point B)> GetOutline(UIElement element, out double thickness)
    {
        var segments = new List<(Point, Point)>();
        thickness = 1;

        switch (element)
        {
            case Canvas canvas:
                foreach (var child in canvas.Children)
                {
                    switch (child)
                    {
                        case Line line:
                            thickness = Math.Max(thickness, line.StrokeThickness);
                            segments.Add((new Point(line.X1, line.Y1), new Point(line.X2, line.Y2)));
                            break;
                        case Polygon polygon:
                            AddPolygon(segments, polygon.Points);
                            break;
                    }
                }

                break;

            case Ellipse ellipse:
            {
                thickness = ellipse.StrokeThickness;
                var r = BoxOf(ellipse);
                var cx = r.Left + r.Width / 2;
                var cy = r.Top + r.Height / 2;
                var prev = new Point(cx + r.Width / 2, cy);
                for (var i = 1; i <= EllipseSegments; i++)
                {
                    var t = 2 * Math.PI * i / EllipseSegments;
                    var next = new Point(cx + Math.Cos(t) * r.Width / 2, cy + Math.Sin(t) * r.Height / 2);
                    segments.Add((prev, next));
                    prev = next;
                }

                break;
            }

            case Rectangle rectangle:
                thickness = rectangle.StrokeThickness;
                AddRectOutline(segments, BoxOf(rectangle));
                break;

            default:
                AddRectOutline(segments, BoxOf(element));
                break;
        }

        return segments;
    }

    private static Rect BoxOf(UIElement element)
    {
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        if (double.IsNaN(top))
        {
            top = 0;
        }

        var fe = element as FrameworkElement;
        var w = fe is null ? 0 : (fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width);
        var h = fe is null ? 0 : (fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height);
        return new Rect(left, top, double.IsNaN(w) ? 0 : w, double.IsNaN(h) ? 0 : h);
    }

    private static void AddRectOutline(List<(Point, Point)> segments, Rect r)
    {
        var tl = r.TopLeft;
        var tr = r.TopRight;
        var br = r.BottomRight;
        var bl = r.BottomLeft;
        segments.Add((tl, tr));
        segments.Add((tr, br));
        segments.Add((br, bl));
        segments.Add((bl, tl));
    }

    private static void AddPolygon(List<(Point, Point)> segments, PointCollection points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            segments.Add((points[i], points[(i + 1) % points.Count]));
        }
    }

    private static double SegmentDistance(Point a, Point b, Point c, Point d)
    {
        if (ProperlyIntersect(a, b, c, d))
        {
            return 0;
        }

        return Math.Min(
            Math.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
            Math.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
    }

    private static bool ProperlyIntersect(Point a, Point b, Point c, Point d)
    {
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static double Cross(Point o, Point p, Point q) =>
        (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);

    private static double PointSegmentDistance(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 < 1e-12)
        {
            return (p - a).Length;
        }

        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        var closest = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
        return (p - closest).Length;
    }
}
