using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OverInk.Models;

namespace OverInk.Overlay;

public static class ShapeDrawingHelper
{
    public static UIElement CreatePreview(SketchTool tool, Point start, Point end, Color color, double thickness)
    {
        return CreateShapeElement(tool, start, end, color, thickness);
    }

    public static UIElement CreateShapeElement(SketchTool tool, Point start, Point end, Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        return tool switch
        {
            SketchTool.Line => CreateLine(start, end, brush, thickness, false),
            SketchTool.Arrow => CreateLine(start, end, brush, thickness, true),
            SketchTool.Rectangle => CreateRectangle(start, end, brush, thickness),
            SketchTool.Ellipse => CreateEllipse(start, end, brush, thickness),
            _ => CreateLine(start, end, brush, thickness, false)
        };
    }

    private static UIElement CreateLine(Point start, Point end, Brush brush, double thickness, bool arrow)
    {
        var canvas = new Canvas();
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(line);

        if (arrow)
        {
            AddArrowHead(canvas, start, end, brush, thickness);
        }

        return canvas;
    }

    private static void AddArrowHead(Canvas canvas, Point start, Point end, Brush brush, double thickness)
    {
        var dir = end - start;
        if (dir.Length < 0.001)
        {
            return;
        }

        dir.Normalize();
        var size = Math.Max(8, thickness * 3);
        var ortho = new Vector(-dir.Y, dir.X);
        var tip = end;
        var left = tip - dir * size + ortho * (size * 0.45);
        var right = tip - dir * size - ortho * (size * 0.45);

        var head = new Polygon
        {
            Points = new PointCollection { tip, left, right },
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 1
        };
        canvas.Children.Add(head);
    }

    private static UIElement CreateRectangle(Point start, Point end, Brush brush, double thickness)
    {
        var rect = NormalizeRect(start, end);
        return new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
    }

    private static UIElement CreateEllipse(Point start, Point end, Brush brush, double thickness)
    {
        var rect = NormalizeRect(start, end);
        return new Ellipse
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
    }

    public static void PositionShape(UIElement element, Point start, Point end)
    {
        var rect = NormalizeRect(start, end);
        if (element is Line line)
        {
            line.X1 = start.X;
            line.Y1 = start.Y;
            line.X2 = end.X;
            line.Y2 = end.Y;
            return;
        }

        if (element is Canvas canvas && canvas.Children.Count > 0)
        {
            if (canvas.Children[0] is Line canvasLine)
            {
                canvasLine.X1 = start.X;
                canvasLine.Y1 = start.Y;
                canvasLine.X2 = end.X;
                canvasLine.Y2 = end.Y;
            }

            if (canvas.Children.Count > 1 && canvas.Children[1] is Polygon)
            {
                canvas.Children.RemoveAt(1);
                var brush = ((Shape)canvas.Children[0]).Stroke;
                AddArrowHead(canvas, start, end, brush, ((Shape)canvas.Children[0]).StrokeThickness);
            }

            return;
        }

        Canvas.SetLeft(element, rect.Left);
        Canvas.SetTop(element, rect.Top);
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
