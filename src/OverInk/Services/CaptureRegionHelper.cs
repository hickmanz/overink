using System.Windows;
using OverInk.Models;

namespace OverInk.Services;

public static class CaptureRegionHelper
{
    public const double MinNormSize = 0.08;

    public static Rect GetStreamDips(AppSettings settings)
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        if (!settings.RemoteRegionEnabled)
        {
            return geometry.Dips;
        }

        return NormRectToDips(
            settings.RemoteRegionX,
            settings.RemoteRegionY,
            settings.RemoteRegionW,
            settings.RemoteRegionH,
            geometry.Dips);
    }

    public static Rect GetStreamPixels(AppSettings settings)
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        var dipRect = GetStreamDips(settings);
        return DipsRectToPixels(dipRect, geometry);
    }

    public static void SetFromDipsRect(AppSettings settings, Rect dipRect)
    {
        var monitor = MonitorHelper.GetPrimaryMonitorGeometry().Dips;
        dipRect = ClampDipsRect(dipRect, monitor);

        settings.RemoteRegionEnabled = true;
        settings.RemoteRegionX = (dipRect.Left - monitor.Left) / monitor.Width;
        settings.RemoteRegionY = (dipRect.Top - monitor.Top) / monitor.Height;
        settings.RemoteRegionW = dipRect.Width / monitor.Width;
        settings.RemoteRegionH = dipRect.Height / monitor.Height;
    }

    public static void Clear(AppSettings settings)
    {
        settings.RemoteRegionEnabled = false;
        settings.RemoteRegionX = 0;
        settings.RemoteRegionY = 0;
        settings.RemoteRegionW = 1;
        settings.RemoteRegionH = 1;
    }

    public static Rect NormRectToDips(double nx, double ny, double nw, double nh, Rect monitor)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        nw = Math.Clamp(nw, MinNormSize, 1);
        nh = Math.Clamp(nh, MinNormSize, 1);

        var left = monitor.Left + nx * monitor.Width;
        var top = monitor.Top + ny * monitor.Height;
        var width = Math.Min(nw * monitor.Width, monitor.Right - left);
        var height = Math.Min(nh * monitor.Height, monitor.Bottom - top);
        return new Rect(left, top, Math.Max(40, width), Math.Max(40, height));
    }

    public static Rect DipsRectToPixels(Rect dipRect, MonitorGeometry geometry)
    {
        if (geometry.Dips.Width <= 0 || geometry.Dips.Height <= 0)
        {
            return geometry.Pixels;
        }

        var scaleX = geometry.Pixels.Width / geometry.Dips.Width;
        var scaleY = geometry.Pixels.Height / geometry.Dips.Height;
        return new Rect(
            geometry.Pixels.Left + (dipRect.Left - geometry.Dips.Left) * scaleX,
            geometry.Pixels.Top + (dipRect.Top - geometry.Dips.Top) * scaleY,
            dipRect.Width * scaleX,
            dipRect.Height * scaleY);
    }

    private static Rect ClampDipsRect(Rect rect, Rect monitor)
    {
        var left = Math.Max(monitor.Left, rect.Left);
        var top = Math.Max(monitor.Top, rect.Top);
        var right = Math.Min(monitor.Right, rect.Right);
        var bottom = Math.Min(monitor.Bottom, rect.Bottom);
        var width = Math.Max(40, right - left);
        var height = Math.Max(40, bottom - top);
        if (left + width > monitor.Right)
        {
            left = monitor.Right - width;
        }

        if (top + height > monitor.Bottom)
        {
            top = monitor.Bottom - height;
        }

        return new Rect(left, top, width, height);
    }
}
