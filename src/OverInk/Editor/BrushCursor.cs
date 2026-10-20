using System.Windows;
using System.Windows.Media;

namespace OverInk.Editor;

/// <summary>
/// Pen-tool cursor that can't get lost: an outline of the real stroke size (when it's big enough to see)
/// plus four reticle ticks around it. Everything is drawn twice — dark underlay, light line — so it
/// reads on any background. Drawn around its own (0,0); position it with Canvas.Left/Top at the pointer.
/// </summary>
public sealed class BrushCursor : FrameworkElement
{
    private static readonly Pen DarkTick = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen LightTick = Freeze(new Pen(Brushes.White, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen DarkRing = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3));
    private static readonly Pen LightRing = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255)), 1));

    private const double TickGap = 3;
    private const double TickLength = 7;

    private double _radius = 1;

    public BrushCursor()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    /// <summary>On-screen diameter of the stroke (device-independent pixels, zoom already applied).</summary>
    public void SetDiameter(double diameter)
    {
        var radius = Math.Max(0.5, diameter / 2);
        if (Math.Abs(radius - _radius) < 0.05)
        {
            return;
        }

        _radius = radius;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(0, 0);

        if (_radius >= 2.5)
        {
            // Draw the ring just outside the stroke so the ink stays visible while drawing.
            var ring = _radius + 0.5;
            dc.DrawEllipse(null, DarkRing, center, ring, ring);
            dc.DrawEllipse(null, LightRing, center, ring, ring);
        }

        var inner = Math.Max(_radius, 2) + TickGap;
        var outer = inner + TickLength;
        foreach (var (dx, dy) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
        {
            var a = new Point(dx * inner, dy * inner);
            var b = new Point(dx * outer, dy * outer);
            dc.DrawLine(DarkTick, a, b);
            dc.DrawLine(LightTick, a, b);
        }
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
