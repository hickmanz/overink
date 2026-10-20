using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverInk.Services;

/// <summary>Asks Windows 10 (1809+) / 11 to draw a window's title bar in dark mode.</summary>
public static class DarkTitleBar
{
    private const int DwmaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(handle, DwmaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
        }
        catch (Exception)
        {
            // Cosmetic only; older Windows versions simply keep the light title bar.
        }
    }
}
