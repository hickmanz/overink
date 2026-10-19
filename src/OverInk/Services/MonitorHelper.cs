using System.Runtime.InteropServices;
using System.Windows;
using OverInk.Interop;

namespace OverInk.Services;

public static class MonitorHelper
{
    private const uint MonitorInfoFPrimary = 1;

    public static MonitorGeometry GetPrimaryMonitorGeometry()
    {
        IntPtr primaryMonitor = IntPtr.Zero;
        NativeMethods.RECT primaryRect = default;
        var found = false;

        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT __, IntPtr ___) =>
            {
                var info = new NativeMethods.MonitorInfo
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>()
                };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    return true;
                }

                if ((info.dwFlags & MonitorInfoFPrimary) == 0)
                {
                    return true;
                }

                primaryMonitor = hMonitor;
                primaryRect = info.rcMonitor;
                found = true;
                return false;
            },
            IntPtr.Zero);

        if (!found)
        {
            var fallback = new Rect(
                0,
                0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
            return new MonitorGeometry(fallback, fallback, IntPtr.Zero);
        }

        var pixels = new Rect(
            primaryRect.Left,
            primaryRect.Top,
            primaryRect.Right - primaryRect.Left,
            primaryRect.Bottom - primaryRect.Top);

        if (primaryMonitor == IntPtr.Zero ||
            NativeMethods.GetDpiForMonitor(primaryMonitor, NativeMethods.MonitorDpiType.Effective, out var dpiX, out var dpiY) != 0)
        {
            dpiX = 96;
            dpiY = 96;
        }

        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        var dips = new Rect(
            pixels.X / scaleX,
            pixels.Y / scaleY,
            pixels.Width / scaleX,
            pixels.Height / scaleY);

        return new MonitorGeometry(dips, pixels, primaryMonitor);
    }

    [Obsolete("Use GetPrimaryMonitorGeometry().Dips")]
    public static Rect GetPrimaryMonitorBounds() => GetPrimaryMonitorGeometry().Dips;
}
