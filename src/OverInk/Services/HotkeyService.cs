using System.Windows.Interop;
using OverInk.Interop;

namespace OverInk.Services;

public sealed class HotkeyService : IDisposable
{
    public const int ToggleDrawHotkeyId = 1;

    private HwndSource? _source;
    private IntPtr _handle;

    public event EventHandler? ToggleDrawRequested;

    public void Attach(IntPtr windowHandle)
    {
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint virtualKey)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        Unregister();
        return NativeMethods.RegisterHotKey(_handle, ToggleDrawHotkeyId, modifiers, virtualKey);
    }

    public void Unregister()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_handle, ToggleDrawHotkeyId);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == ToggleDrawHotkeyId)
        {
            ToggleDrawRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }
}
