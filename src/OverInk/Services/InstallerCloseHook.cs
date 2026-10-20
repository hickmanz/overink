using System.Windows;
using System.Windows.Interop;

namespace OverInk.Services;

/// <summary>
/// Inno Setup's silent upgrade asks the running app to exit (Restart Manager sends
/// WM_QUERYENDSESSION). ShutdownMode is OnExplicitShutdown, so the process has to leave on its own
/// or the installer cannot replace OverInk.exe.
/// </summary>
public static class InstallerCloseHook
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    public static void Watch(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(Hook);
            }
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmQueryEndSession or WmEndSession)
        {
            Application.Current?.Shutdown();
        }

        return IntPtr.Zero;
    }
}
