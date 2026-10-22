using System.Windows;

namespace OverInk.Remote;

/// <summary>Maps between 0-1 positions on a rectangle (the streamed area) and points in that rectangle's own units.</summary>
public static class CoordinateMapper
{
    /// <summary>Maps a 0-1 position on <paramref name="area"/> to a point in the same units as the rectangle. Clamped to it.</summary>
    public static Point NormalizedToDips(double nx, double ny, Rect area)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        return new Point(
            area.Left + nx * area.Width,
            area.Top + ny * area.Height);
    }

    public static (double nx, double ny) DipsToNormalized(Point point, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return (0, 0);
        }

        return (
            (point.X - area.Left) / area.Width,
            (point.Y - area.Top) / area.Height);
    }

    /// <summary>Same mapping when <paramref name="streamPixels"/> is the streamed rectangle in desktop pixels.</summary>
    public static Point NormalizedToPixels(double nx, double ny, Rect streamPixels) =>
        NormalizedToDips(nx, ny, streamPixels);
}
