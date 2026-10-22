using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OverInk.Interop;

namespace OverInk.Services;

/// <summary>
/// Places windows by physical pixels in the shared virtual-desktop space. WPF's Left/Top/Width/Height are DIPs
/// converted with the window's own DPI, which is ambiguous when monitors have different scales, so windows that
/// must line up with screen pixels (overlays, pickers, outlines, the pen cursor) are positioned through here.
/// </summary>
public static class WindowPlacement
{
    /// <summary>A window's DPI scale (1.0 = 100%). Reflects the monitor it is currently on.</summary>
    public static double GetScale(Window window)
    {
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return scale > 0 ? scale : 1;
    }

    /// <summary>The window's current rectangle in desktop pixels, or <see cref="Rect.Empty"/> if it has no handle yet.</summary>
    public static Rect GetPixelRect(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var r))
        {
            return Rect.Empty;
        }

        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Moves the window's top-left to a pixel position, keeping its size and without activating it.</summary>
    public static void MoveToPixels(Window window, double x, double y)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(x),
            (int)Math.Round(y),
            0,
            0,
            NativeMethods.SwpNosize | NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
    }

    /// <summary>Moves and sizes the window to a pixel rectangle without activating it.</summary>
    public static void SetPixelRect(Window window, Rect pixels)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(pixels.X),
            (int)Math.Round(pixels.Y),
            Math.Max(1, (int)Math.Round(pixels.Width)),
            Math.Max(1, (int)Math.Round(pixels.Height)),
            NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
    }

    /// <summary>
    /// Centers the window on a monitor. <paramref name="sizeDips"/> is the window size to use (DIPs); when omitted the
    /// window's current layout size is used. Safe on monitors with a different scale than the window started on.
    /// </summary>
    public static void CenterOnMonitor(Window window, MonitorGeometry monitor, Size? sizeDips = null)
    {
        var size = sizeDips ?? new Size(window.ActualWidth, window.ActualHeight);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        PlaceSettling(window, scale =>
        {
            var width = size.Width * scale;
            var height = size.Height * scale;
            return new Rect(
                monitor.Pixels.Left + (monitor.Pixels.Width - width) / 2,
                monitor.Pixels.Top + (monitor.Pixels.Height - height) / 2,
                width,
                height);
        });
    }

    /// <summary>
    /// Places the window at the pixel rectangle <paramref name="rectForScale"/> returns. A window that lands on a
    /// monitor with another scale gets resized by Windows, so the rectangle is recomputed with the new scale and
    /// applied again until the scale stops changing. Returns the final scale.
    /// </summary>
    public static double PlaceSettling(Window window, Func<double, Rect> rectForScale)
    {
        var scale = GetScale(window);
        for (var pass = 0; pass < 3; pass++)
        {
            SetPixelRect(window, rectForScale(scale));
            var after = GetScale(window);
            if (Math.Abs(after - scale) < 0.001)
            {
                return after;
            }

            scale = after;
        }

        return scale;
    }
}
