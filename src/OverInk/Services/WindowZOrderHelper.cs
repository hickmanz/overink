using System.Windows;
using System.Windows.Interop;
using OverInk.Interop;

namespace OverInk.Services;

public static class WindowZOrderHelper
{
    /// <summary>
    /// Places <paramref name="above"/> directly above <paramref name="below"/> in the Z-order
    /// so the toolbar stays clickable while the overlay is in draw mode.
    /// </summary>
    public static void PlaceAbove(Window above, Window below)
    {
        var aboveHandle = new WindowInteropHelper(above).Handle;
        var belowHandle = new WindowInteropHelper(below).Handle;
        if (aboveHandle == IntPtr.Zero || belowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            aboveHandle,
            belowHandle,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove |
            NativeMethods.SwpNosize |
            NativeMethods.SwpNoactivate);
    }
}
