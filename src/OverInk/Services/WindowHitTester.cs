using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OverInk.Services;

/// <summary>
/// Snapshot of the visible top-level windows (front to back) so a region picker can
/// highlight whichever window sits under the cursor, like Snagit's window snap.
/// Bounds are in physical screen pixels.
/// </summary>
public sealed class WindowHitTester
{
    private readonly List<Rect> _windows = new();

    private WindowHitTester()
    {
    }

    public static WindowHitTester Capture(IntPtr excludeHwnd)
    {
        var tester = new WindowHitTester();
        var ownPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return true;
            }

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            // Click-through overlays and tool windows (tooltips, popups) aren't things you'd snap to.
            if ((exStyle & WsExTransparent) != 0 || (exStyle & WsExToolWindow) != 0)
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            // Our own always-on-top chrome (toolbar, overlay, region frame) is hidden during capture.
            if (pid == ownPid && (exStyle & WsExTopmost) != 0)
            {
                return true;
            }

            var classBuilder = new StringBuilder(64);
            GetClassName(hwnd, classBuilder, classBuilder.Capacity);
            var className = classBuilder.ToString();
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }

            // Extended frame bounds exclude the invisible resize borders Windows 10/11 adds.
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect frame, Marshal.SizeOf<NativeRect>()) != 0 &&
                !GetWindowRect(hwnd, out frame))
            {
                return true;
            }

            var rect = new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            if (rect.Width < 24 || rect.Height < 24)
            {
                return true;
            }

            tester._windows.Add(rect);
            return true;
        }, IntPtr.Zero);

        return tester;
    }

    /// <summary>Topmost window rect (physical pixels) containing the point, if any.</summary>
    public Rect? HitTest(Point screenPixel)
    {
        foreach (var rect in _windows)
        {
            if (rect.Contains(screenPixel))
            {
                return rect;
            }
        }

        return null;
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExTopmost = 0x8;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}
