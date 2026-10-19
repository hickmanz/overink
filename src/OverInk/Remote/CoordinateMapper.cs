using System.Windows;

namespace OverInk.Remote;

public static class CoordinateMapper
{
    public static Point NormalizedToDips(double nx, double ny, Rect monitorDips)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        return new Point(
            monitorDips.Left + nx * monitorDips.Width,
            monitorDips.Top + ny * monitorDips.Height);
    }

    public static (double nx, double ny) DipsToNormalized(Point dips, Rect monitorDips)
    {
        if (monitorDips.Width <= 0 || monitorDips.Height <= 0)
        {
            return (0, 0);
        }

        return (
            (dips.X - monitorDips.Left) / monitorDips.Width,
            (dips.Y - monitorDips.Top) / monitorDips.Height);
    }
}
