using System.Windows.Interop;
using OverInk.Interop;

namespace OverInk.Services;

public sealed class HotkeyService : IDisposable
{
    public const int ToggleDrawHotkeyId = 1;
    public const int CaptureRegionHotkeyId = 2;

    private HwndSource? _source;
    private IntPtr _handle;

    public event EventHandler? ToggleDrawRequested;
    public event EventHandler? CaptureRegionRequested;

    public void Attach(IntPtr windowHandle)
    {
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint virtualKey) => Register(ToggleDrawHotkeyId, modifiers, virtualKey);

    /// <summary>Binds the region-capture shortcut; a virtual key of 0 just clears it.</summary>
    public bool RegisterCapture(uint modifiers, uint virtualKey) =>
        Register(CaptureRegionHotkeyId, modifiers, virtualKey);

    private bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.UnregisterHotKey(_handle, id);
        if (virtualKey == 0)
        {
            return true;
        }

        return NativeMethods.RegisterHotKey(_handle, id, modifiers, virtualKey);
    }

    public void Unregister()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_handle, ToggleDrawHotkeyId);
            NativeMethods.UnregisterHotKey(_handle, CaptureRegionHotkeyId);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case ToggleDrawHotkeyId:
                    ToggleDrawRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case CaptureRegionHotkeyId:
                    CaptureRegionRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }
}
