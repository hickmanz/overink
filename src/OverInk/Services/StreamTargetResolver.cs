using System.Windows;
using OverInk.Models;

namespace OverInk.Services;

/// <summary>
/// Decides which rectangle of the virtual desktop (in pixels) the iPad streams and sketches on. Everything that
/// needs "the stream" (frames, coordinate mapping, remote capture, the region outline) asks here and never
/// checks the setting itself, so a new target mode only has to be added in this class.
/// </summary>
public static class StreamTargetResolver
{
    public const double MinRegionNormSize = 0.08;
    public const double MinRegionPixels = 40;

    private static string _followDevice = string.Empty;

    /// <summary>Raised when the toolbar moved to a different monitor (only matters for <see cref="StreamTarget.FollowToolbar"/>).</summary>
    public static event Action? FollowMonitorChanged;

    /// <summary>Device name of the monitor the toolbar was last seen on. Kept while the toolbar is hidden.</summary>
    public static string FollowMonitorDevice => _followDevice;

    /// <summary>Tells the resolver where the toolbar is (desktop pixels). The monitor under its center wins.</summary>
    public static void NotifyToolbarMoved(Rect toolbarPixels) =>
        NotifyToolbarMoved(toolbarPixels, MonitorHelper.GetAllMonitors());

    public static void NotifyToolbarMoved(Rect toolbarPixels, IReadOnlyList<MonitorGeometry> monitors)
    {
        if (toolbarPixels.IsEmpty || toolbarPixels.Width <= 0 || toolbarPixels.Height <= 0)
        {
            return;
        }

        var center = new Point(toolbarPixels.X + toolbarPixels.Width / 2, toolbarPixels.Y + toolbarPixels.Height / 2);
        if (DesktopLayout.NearestMonitor(monitors, center) is not { } monitor)
        {
            return;
        }

        if (string.Equals(_followDevice, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _followDevice = monitor.DeviceName;
        FollowMonitorChanged?.Invoke();
    }

    /// <summary>The streamed rectangle in virtual-desktop pixels.</summary>
    public static Rect GetStreamPixels(AppSettings settings) =>
        GetStreamPixels(settings, MonitorHelper.GetAllMonitors(), _followDevice);

    public static Rect GetStreamPixels(AppSettings settings, IReadOnlyList<MonitorGeometry> monitors, string? followDevice)
    {
        if (settings.RemoteRegionEnabled)
        {
            var monitor = FindMonitor(monitors, settings.RemoteRegionMonitor) ?? PrimaryOf(monitors);
            return RegionToPixels(settings, monitor.Pixels);
        }

        return GetTargetPixels(settings, monitors, followDevice);
    }

    /// <summary>The whole target (ignoring any region): a monitor, or the entire virtual desktop.</summary>
    public static Rect GetTargetPixels(AppSettings settings) =>
        GetTargetPixels(settings, MonitorHelper.GetAllMonitors(), _followDevice);

    public static Rect GetTargetPixels(AppSettings settings, IReadOnlyList<MonitorGeometry> monitors, string? followDevice)
    {
        switch (settings.StreamTarget)
        {
            case StreamTarget.AllMonitors:
                return DesktopLayout.VirtualDesktop(monitors);
            case StreamTarget.SpecificMonitor:
                return (FindMonitor(monitors, settings.StreamMonitor) ?? FindMonitor(monitors, followDevice) ?? PrimaryOf(monitors)).Pixels;
            default:
                return (FindMonitor(monitors, followDevice) ?? PrimaryOf(monitors)).Pixels;
        }
    }

    /// <summary>
    /// The monitors visible inside the streamed rectangle as 0-1 rectangles of it (left to right). Empty unless two
    /// or more monitors show, since a single screen needs no "zoom to monitor" shortcut.
    /// </summary>
    public static IReadOnlyList<(Rect Normalized, bool IsPrimary)> GetStreamMonitorRects(
        Rect stream, IReadOnlyList<MonitorGeometry> monitors)
    {
        var parts = DesktopLayout.Intersections(monitors, stream);
        if (parts.Count < 2 || stream.Width <= 0 || stream.Height <= 0)
        {
            return Array.Empty<(Rect, bool)>();
        }

        var result = new List<(Rect, bool)>(parts.Count);
        foreach (var (monitor, pixels) in parts)
        {
            result.Add((
                new Rect(
                    (pixels.Left - stream.Left) / stream.Width,
                    (pixels.Top - stream.Top) / stream.Height,
                    pixels.Width / stream.Width,
                    pixels.Height / stream.Height),
                monitor.IsPrimary));
        }

        return result;
    }

    /// <summary>A value that changes whenever the streamed rectangle (position or size) changes.</summary>
    public static int GetStreamEpoch(Rect stream) =>
        HashCode.Combine((int)stream.X, (int)stream.Y, (int)stream.Width, (int)stream.Height);

    /// <summary>Stores a region picked on screen. It is clamped inside the monitor holding most of it.</summary>
    public static void SetRegionFromPixels(AppSettings settings, Rect pixels) =>
        SetRegionFromPixels(settings, pixels, MonitorHelper.GetAllMonitors());

    public static void SetRegionFromPixels(AppSettings settings, Rect pixels, IReadOnlyList<MonitorGeometry> monitors)
    {
        var monitor = DesktopLayout.MonitorForRect(monitors, pixels) ?? PrimaryOf(monitors);
        var area = monitor.Pixels;
        var clamped = ClampInside(pixels, area);

        settings.RemoteRegionEnabled = true;
        settings.RemoteRegionMonitor = monitor.DeviceName;
        settings.RemoteRegionX = (clamped.Left - area.Left) / area.Width;
        settings.RemoteRegionY = (clamped.Top - area.Top) / area.Height;
        settings.RemoteRegionW = clamped.Width / area.Width;
        settings.RemoteRegionH = clamped.Height / area.Height;
    }

    public static void ClearRegion(AppSettings settings)
    {
        settings.RemoteRegionEnabled = false;
        settings.RemoteRegionMonitor = string.Empty;
        settings.RemoteRegionX = 0;
        settings.RemoteRegionY = 0;
        settings.RemoteRegionW = 1;
        settings.RemoteRegionH = 1;
    }

    /// <summary>Region fractions applied to a monitor's pixel rect, with minimum sizes enforced and kept inside it.</summary>
    public static Rect RegionToPixels(AppSettings settings, Rect monitorPixels)
    {
        var nx = Math.Clamp(settings.RemoteRegionX, 0, 1);
        var ny = Math.Clamp(settings.RemoteRegionY, 0, 1);
        var nw = Math.Clamp(settings.RemoteRegionW, MinRegionNormSize, 1);
        var nh = Math.Clamp(settings.RemoteRegionH, MinRegionNormSize, 1);

        var left = monitorPixels.Left + nx * monitorPixels.Width;
        var top = monitorPixels.Top + ny * monitorPixels.Height;
        var width = Math.Min(nw * monitorPixels.Width, monitorPixels.Right - left);
        var height = Math.Min(nh * monitorPixels.Height, monitorPixels.Bottom - top);
        return new Rect(left, top, Math.Max(MinRegionPixels, width), Math.Max(MinRegionPixels, height));
    }

    /// <summary>Moves and shrinks <paramref name="rect"/> so it lies inside <paramref name="area"/> (minimum size enforced).</summary>
    public static Rect ClampInside(Rect rect, Rect area)
    {
        var left = Math.Max(area.Left, rect.Left);
        var top = Math.Max(area.Top, rect.Top);
        var right = Math.Min(area.Right, rect.Right);
        var bottom = Math.Min(area.Bottom, rect.Bottom);
        var width = Math.Max(MinRegionPixels, right - left);
        var height = Math.Max(MinRegionPixels, bottom - top);
        if (left + width > area.Right)
        {
            left = area.Right - width;
        }

        if (top + height > area.Bottom)
        {
            top = area.Bottom - height;
        }

        return new Rect(left, top, width, height);
    }

    private static MonitorGeometry? FindMonitor(IReadOnlyList<MonitorGeometry> monitors, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }

        return null;
    }

    private static MonitorGeometry PrimaryOf(IReadOnlyList<MonitorGeometry> monitors)
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
}
