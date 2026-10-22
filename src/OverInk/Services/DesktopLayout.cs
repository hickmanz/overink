using System.Windows;

namespace OverInk.Services;

/// <summary>
/// Pure geometry over a set of monitors in the shared virtual-desktop pixel space. Nothing here calls Win32, so
/// the same maths is unit tested with made-up layouts (mixed DPI, monitors left of or above the primary...).
/// </summary>
public static class DesktopLayout
{
    /// <summary>Smallest rectangle containing every rect, or <see cref="Rect.Empty"/> for none.</summary>
    public static Rect Union(IEnumerable<Rect> rects)
    {
        var result = Rect.Empty;
        foreach (var rect in rects)
        {
            if (rect.IsEmpty)
            {
                continue;
            }

            result = result.IsEmpty ? rect : Rect.Union(result, rect);
        }

        return result;
    }

    /// <summary>Bounding box of all monitors (the virtual desktop), in pixels.</summary>
    public static Rect VirtualDesktop(IEnumerable<MonitorGeometry> monitors) =>
        Union(monitors.Select(m => m.Pixels));

    /// <summary>The monitor whose pixel rect contains the point. Edges are half-open so neighbours never both match.</summary>
    public static MonitorGeometry? MonitorAt(IReadOnlyList<MonitorGeometry> monitors, Point pixel)
    {
        foreach (var monitor in monitors)
        {
            var r = monitor.Pixels;
            if (pixel.X >= r.Left && pixel.X < r.Right && pixel.Y >= r.Top && pixel.Y < r.Bottom)
            {
                return monitor;
            }
        }

        return null;
    }

    /// <summary>The monitor containing the point, or the closest one when it lies in a gap or off the desktop.</summary>
    public static MonitorGeometry? NearestMonitor(IReadOnlyList<MonitorGeometry> monitors, Point pixel)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (MonitorAt(monitors, pixel) is { } hit)
        {
            return hit;
        }

        MonitorGeometry best = monitors[0];
        var bestDistance = double.MaxValue;
        foreach (var monitor in monitors)
        {
            var r = monitor.Pixels;
            var dx = Math.Max(Math.Max(r.Left - pixel.X, 0), pixel.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - pixel.Y, 0), pixel.Y - r.Bottom);
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monitor;
            }
        }

        return best;
    }

    /// <summary>The monitor sharing the most area with the rect (a window straddling two monitors belongs to one).</summary>
    public static MonitorGeometry? MonitorForRect(IReadOnlyList<MonitorGeometry> monitors, Rect pixels)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        MonitorGeometry? best = null;
        var bestArea = 0.0;
        foreach (var monitor in monitors)
        {
            var overlap = Rect.Intersect(monitor.Pixels, pixels);
            var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        if (best is not null)
        {
            return best;
        }

        var center = new Point(pixels.X + pixels.Width / 2, pixels.Y + pixels.Height / 2);
        return NearestMonitor(monitors, center);
    }

    /// <summary>Whether two sets of monitors are the same layout (same bounds, order ignored).</summary>
    public static bool SameLayout(IReadOnlyList<MonitorGeometry> a, IReadOnlyList<MonitorGeometry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var monitor in a)
        {
            var match = false;
            foreach (var other in b)
            {
                if (monitor.Pixels == other.Pixels &&
                    monitor.Dips == other.Dips &&
                    monitor.DeviceName == other.DeviceName &&
                    monitor.IsPrimary == other.IsPrimary)
                {
                    match = true;
                    break;
                }
            }

            if (!match)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Maps a pixel point to 0-1 fractions of <paramref name="area"/> (not clamped).</summary>
    public static (double Nx, double Ny) PixelsToNormalized(Point pixel, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return (0, 0);
        }

        return ((pixel.X - area.Left) / area.Width, (pixel.Y - area.Top) / area.Height);
    }

    /// <summary>Maps 0-1 fractions of <paramref name="area"/> to a pixel point (clamped to the area).</summary>
    public static Point NormalizedToPixels(double nx, double ny, Rect area)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        return new Point(area.Left + nx * area.Width, area.Top + ny * area.Height);
    }

    /// <summary>
    /// Converts a point in one monitor's local DIPs (origin at the monitor's top-left) to desktop pixels.
    /// </summary>
    public static Point MonitorLocalDipsToPixels(MonitorGeometry monitor, Point localDips) =>
        new(monitor.Pixels.Left + localDips.X * monitor.ScaleX, monitor.Pixels.Top + localDips.Y * monitor.ScaleY);

    /// <summary>Converts desktop pixels to a point in the monitor's local DIPs (origin at its top-left).</summary>
    public static Point PixelsToMonitorLocalDips(MonitorGeometry monitor, Point pixel) =>
        new((pixel.X - monitor.Pixels.Left) / monitor.ScaleX, (pixel.Y - monitor.Pixels.Top) / monitor.ScaleY);
}
