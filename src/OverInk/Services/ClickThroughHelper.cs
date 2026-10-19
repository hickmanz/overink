using System.Windows;
using System.Windows.Interop;
using OverInk.Interop;

namespace OverInk.Services;

public static class ClickThroughHelper
{
    public static void SetClickThrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExstyle).ToInt64();
        if (enabled)
        {
            style |= NativeMethods.WsExTransparent;
        }
        else
        {
            style &= ~NativeMethods.WsExTransparent;
        }

        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExstyle, new IntPtr(style));
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove |
            NativeMethods.SwpNosize |
            NativeMethods.SwpNozorder |
            NativeMethods.SwpFrameChanged |
            NativeMethods.SwpNoactivate);
    }
}
