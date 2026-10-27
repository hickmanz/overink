using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OverInk.Models;
using OverInk.Overlay;

namespace OverInk.Services;

public sealed class GlobalKeyboardService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkEscape = 0x1B;
    private const int VkZ = 0x5A;
    private const int VkY = 0x59;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly SketchSession _session;
    private readonly Func<bool> _isOverlayVisible;
    private readonly Action _undo;
    private readonly Action _redo;
    private readonly Window _overlayWindow;
    private readonly Window _toolbarWindow;

    private IntPtr _hookHandle;
    private HookProc? _hookProc;

    public GlobalKeyboardService(
        SketchSession session,
        Window overlayWindow,
        Window toolbarWindow,
        Func<bool> isOverlayVisible,
        Action undo,
        Action redo)
    {
        _session = session;
        _overlayWindow = overlayWindow;
        _toolbarWindow = toolbarWindow;
        _isOverlayVisible = isOverlayVisible;
        _undo = undo;
        _redo = redo;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // Capture mode: Esc cancels the region picker, even when it never got keyboard focus
        // or the overlay is hidden.
        if (RegionPickerWindow.IsOpen &&
            (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown) &&
            Marshal.ReadInt32(lParam) == VkEscape)
        {
            Application.Current.Dispatcher.BeginInvoke(RegionPickerWindow.CancelActive);
            return (IntPtr)1;
        }

        if (!_isOverlayVisible())
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var isKeyDown = wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown;
        if (!isKeyDown)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // The draw-mode right-click menu needs Esc, arrows and Enter itself.
        if (_session.IsOverlayMenuOpen)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var vk = Marshal.ReadInt32(lParam);
        var ctrl = IsKeyDown(VkControl);
        var shift = IsKeyDown(VkShift);

        if (vk == VkEscape)
        {
            if (!_session.IsHotkeyCaptureActive && _session.Mode == InteractionMode.Draw)
            {
                Application.Current.Dispatcher.BeginInvoke(() => _session.SetMode(InteractionMode.PassThrough));
                return (IntPtr)1;
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!ShouldHandleEditShortcuts())
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (vk == VkZ && ctrl && !shift && _session.UndoStack.CanUndo)
        {
            Application.Current.Dispatcher.BeginInvoke(_undo);
            return (IntPtr)1;
        }

        if (ctrl && _session.UndoStack.CanRedo && ((vk == VkZ && shift) || vk == VkY))
        {
            Application.Current.Dispatcher.BeginInvoke(_redo);
            return (IntPtr)1;
        }

        // Same single-letter tool keys as the editor (P pen, E eraser, L line, A arrow, R rectangle, O ellipse).
        if (_session.Mode == InteractionMode.Draw &&
            !_session.IsHotkeyCaptureActive &&
            !ctrl && !shift && !IsKeyDown(VkMenu) && !IsKeyDown(VkLWin) && !IsKeyDown(VkRWin) &&
            DrawShortcutMap.TryGetTool(vk, out var tool))
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _session.SetTool(tool);
                _session.PersistSettings();
            });
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool ShouldHandleEditShortcuts()
    {
        if (_session.Mode == InteractionMode.Draw)
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var overlayHandle = new WindowInteropHelper(_overlayWindow).Handle;
        var toolbarHandle = new WindowInteropHelper(_toolbarWindow).Handle;
        return foreground == overlayHandle || foreground == toolbarHandle;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
