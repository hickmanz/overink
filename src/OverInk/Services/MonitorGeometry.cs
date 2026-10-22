using System.Windows;

namespace OverInk.Services;

/// <summary>
/// One monitor's bounds. <see cref="Pixels"/> is in the shared virtual-desktop pixel space (the one GDI capture and
/// the Win32 window APIs use; it can have negative coordinates). <see cref="Dips"/> is the same rectangle divided
/// by this monitor's own DPI scale, so it is only meaningful for this one monitor: with mixed DPI there is no
/// single DIP space shared by all monitors, which is why cross-monitor math is done in pixels.
/// </summary>
public readonly record struct MonitorGeometry(
    Rect Dips,
    Rect Pixels,
    IntPtr MonitorHandle,
    string DeviceName = "",
    bool IsPrimary = false)
{
    /// <summary>Physical pixels per DIP on this monitor (1.0 at 100%, 1.5 at 150%).</summary>
    public double ScaleX => Dips.Width > 0 ? Pixels.Width / Dips.Width : 1;

    public double ScaleY => Dips.Height > 0 ? Pixels.Height / Dips.Height : 1;

    /// <summary>Builds a monitor from its pixel rect and effective DPI (96 = 100%).</summary>
    public static MonitorGeometry FromPixels(
        Rect pixels,
        double dpiX,
        double dpiY,
        IntPtr handle = default,
        string deviceName = "",
        bool isPrimary = false)
    {
        var scaleX = dpiX > 0 ? dpiX / 96.0 : 1;
        var scaleY = dpiY > 0 ? dpiY / 96.0 : 1;
        var dips = new Rect(
            pixels.X / scaleX,
            pixels.Y / scaleY,
            pixels.Width / scaleX,
            pixels.Height / scaleY);
        return new MonitorGeometry(dips, pixels, handle, deviceName, isPrimary);
    }
}
