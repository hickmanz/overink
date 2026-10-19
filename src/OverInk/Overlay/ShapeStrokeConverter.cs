using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OverInk.Overlay;

/// <summary>
/// Turns the overlay's shape elements (line, arrow, rectangle, ellipse) into polyline ink strokes in the same
/// coordinate space as the element, so a capture can keep them as editable ink instead of baked-in pixels.
/// </summary>
public static class ShapeStrokeConverter
{
    private const int EllipseSteps = 72;

    public static IEnumerable<Stroke> ToStrokes(UIElement element)
    {
        switch (element)
        {
            case Canvas canvas when canvas.Children.Count > 0 && canvas.Children[0] is Line line:
            {
                if (!TryStyle(line, out var color, out var thickness))
                {
                    yield break;
                }

                yield return Polyline(color, thickness, new Point(line.X1, line.Y1), new Point(line.X2, line.Y2));

                // Arrow head: Polygon(tip, left, right). Drawn as a closed outline at the line's thickness.
                if (canvas.Children.Count > 1 && canvas.Children[1] is Polygon { Points.Count: 3 } head)
                {
                    yield return Polyline(color, thickness, head.Points[1], head.Points[0], head.Points[2], head.Points[1]);
                }

                break;
            }
            case Line line:
            {
                if (TryStyle(line, out var color, out var thickness))
                {
                    yield return Polyline(color, thickness, new Point(line.X1, line.Y1), new Point(line.X2, line.Y2));
                }

                break;
            }
            case Rectangle rect:
            {
                if (!TryStyle(rect, out var color, out var thickness))
                {
                    yield break;
                }

                var (l, t, r, b) = Bounds(rect);
                yield return Polyline(color, thickness, new Point(l, t), new Point(r, t), new Point(r, b), new Point(l, b), new Point(l, t));
                break;
            }
            case Ellipse ellipse:
            {
                if (!TryStyle(ellipse, out var color, out var thickness))
                {
                    yield break;
                }

                var (l, t, r, b) = Bounds(ellipse);
                double cx = (l + r) / 2, cy = (t + b) / 2, rx = (r - l) / 2, ry = (b - t) / 2;
                var points = new Point[EllipseSteps + 1];
                for (var i = 0; i <= EllipseSteps; i++)
                {
                    var angle = 2 * Math.PI * i / EllipseSteps;
                    points[i] = new Point(cx + Math.Cos(angle) * rx, cy + Math.Sin(angle) * ry);
                }

                yield return Polyline(color, thickness, points);
                break;
            }
        }
    }

    private static (double L, double T, double R, double B) Bounds(FrameworkElement shape)
    {
        var left = Canvas.GetLeft(shape);
        var top = Canvas.GetTop(shape);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left, top, left + shape.Width, top + shape.Height);
    }

    private static bool TryStyle(Shape shape, out Color color, out double thickness)
    {
        thickness = Math.Max(0.5, shape.StrokeThickness);
        if (shape.Stroke is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private static Stroke Polyline(Color color, double thickness, params Point[] points)
    {
        var stylus = new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y)));
        return new Stroke(stylus, new DrawingAttributes
        {
            Color = color,
            Width = thickness,
            Height = thickness,
            FitToCurve = false,
            IgnorePressure = true,
            StylusTip = StylusTip.Ellipse
        });
    }
}
