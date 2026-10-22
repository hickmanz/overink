using System.Runtime.InteropServices;
using System.Windows;
using OverInk.Interop;

namespace OverInk.Services;

public static class MonitorHelper
{
    private const uint MonitorInfoFPrimary = 1;

    /// <summary>
    /// Every attached monitor, ordered left to right then top to bottom. Never empty: if enumeration fails, a
    /// single fallback monitor sized from the primary screen metrics is returned.
    /// </summary>
    public static IReadOnlyList<MonitorGeometry> GetAllMonitors()
    {
        var monitors = new List<MonitorGeometry>();

        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT __, IntPtr ___) =>
            {
                var info = new NativeMethods.MonitorInfoEx
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                    szDevice = string.Empty
                };
                if (!NativeMethods.GetMonitorInfoEx(hMonitor, ref info))
                {
                    return true;
                }

                var pixels = new Rect(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top);

                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MonitorDpiType.Effective, out var dpiX, out var dpiY) != 0)
                {
                    dpiX = 96;
                    dpiY = 96;
                }

                monitors.Add(MonitorGeometry.FromPixels(
                    pixels,
                    dpiX,
                    dpiY,
                    hMonitor,
                    info.szDevice ?? string.Empty,
                    (info.dwFlags & MonitorInfoFPrimary) != 0));
                return true;
            },
            IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var fallback = new Rect(
                0,
                0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
            return new[] { new MonitorGeometry(fallback, fallback, IntPtr.Zero, string.Empty, true) };
        }

        monitors.Sort((a, b) =>
        {
            var byLeft = a.Pixels.Left.CompareTo(b.Pixels.Left);
            return byLeft != 0 ? byLeft : a.Pixels.Top.CompareTo(b.Pixels.Top);
        });
        return monitors;
    }

    /// <summary>The primary monitor. Single-monitor code paths still use this.</summary>
    public static MonitorGeometry GetPrimaryMonitorGeometry()
    {
        var monitors = GetAllMonitors();
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors[0];
    }

    /// <summary>Bounding box of all monitors in virtual-desktop pixels (can start at negative coordinates).</summary>
    public static Rect GetVirtualDesktopPixels() => DesktopLayout.VirtualDesktop(GetAllMonitors());

    /// <summary>The monitor under the mouse cursor (the nearest one if the cursor is somehow off every monitor).</summary>
    public static MonitorGeometry GetMonitorAtCursor()
    {
        var monitors = GetAllMonitors();
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return GetPrimaryOf(monitors);
        }

        return DesktopLayout.NearestMonitor(monitors, new Point(cursor.X, cursor.Y)) ?? GetPrimaryOf(monitors);
    }

    /// <summary>The monitor that holds most of a rectangle given in desktop pixels.</summary>
    public static MonitorGeometry GetMonitorForPixelRect(Rect pixels)
    {
        var monitors = GetAllMonitors();
        return DesktopLayout.MonitorForRect(monitors, pixels) ?? GetPrimaryOf(monitors);
    }

    /// <summary>Finds a monitor by its device name (for example \\.\DISPLAY2), or null if it is not attached.</summary>
    public static MonitorGeometry? FindByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var monitor in GetAllMonitors())
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }

        return null;
    }

    private static MonitorGeometry GetPrimaryOf(IReadOnlyList<MonitorGeometry> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors[0];
    }

    [Obsolete("Use GetPrimaryMonitorGeometry().Dips")]
    public static Rect GetPrimaryMonitorBounds() => GetPrimaryMonitorGeometry().Dips;
}
