using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OverInk.Remote;
using OverInk.Services;

namespace OverInk.Toolbar;

/// <summary>
/// Keeps the toolbar at the spot the user left it when the display turns off and on again. Windows moves windows
/// around while a monitor is off, and (on a single monitor) can move them again some time after it is back, so a
/// one-off restore is not enough: for a short while after any display wake, every move the user did not make is
/// undone.
/// </summary>
public partial class ToolbarWindow
{
    private static readonly TimeSpan HomeWatchLength = TimeSpan.FromSeconds(20);
    private const int MaxWatchRestores = 8;

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0;

    // GUID_CONSOLE_DISPLAY_STATE: data 0 = display off, 1 = on, 2 = dimmed.
    private static Guid _consoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private DateTime _homeWatchUntil = DateTime.MinValue;
    private int _watchRestores;
    private bool _restoring;
    private DispatcherTimer? _homeTimer;
    private IntPtr _powerNotification;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data;
    }

    /// <summary>Starts listening for the display turning back on. Call once the window has a handle.</summary>
    private void StartDisplayStateWatch()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(DisplayStateHook);
        _powerNotification = RegisterPowerSettingNotification(handle, ref _consoleDisplayState, DeviceNotifyWindowHandle);
        Closed += (_, _) =>
        {
            if (_powerNotification != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_powerNotification);
                _powerNotification = IntPtr.Zero;
            }
        };
    }

    private IntPtr DisplayStateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == _consoleDisplayState)
            {
                // 0 = off. Watch from the moment it goes off (Windows may already shuffle windows then) and keep
                // watching once it is back on.
                BeginHomeWatch(setting.Data == 0 ? "display off" : "display on");
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Opens a short window during which any move the user did not make sends the toolbar back home, and
    /// restores it once now.
    /// </summary>
    public void BeginHomeWatch(string reason)
    {
        _homeWatchUntil = DateTime.UtcNow + HomeWatchLength;
        _watchRestores = 0;
        RemoteDebugLog.Write($"toolbar: home watch ({reason}) at {DescribeRect()}");
        RestoreHomePosition();
    }

    /// <summary>Called for every LocationChanged: schedules a restore if the move looks like Windows' doing.</summary>
    private void OnLocationChangedWhileWatching()
    {
        if (_restoring || _dragging || DateTime.UtcNow > _homeWatchUntil || _watchRestores >= MaxWatchRestores)
        {
            return;
        }

        RemoteDebugLog.Write($"toolbar: moved by system to {DescribeRect()}");
        _homeTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _homeTimer.Tick -= HomeTimerTick;
        _homeTimer.Tick += HomeTimerTick;
        _homeTimer.Stop();
        _homeTimer.Start();
    }

    private void HomeTimerTick(object? sender, EventArgs e)
    {
        _homeTimer?.Stop();
        if (_dragging || DateTime.UtcNow > _homeWatchUntil)
        {
            return;
        }

        _watchRestores++;
        RestoreHomePosition();
    }

    private string DescribeRect()
    {
        var rect = WindowPlacement.GetPixelRect(this);
        return rect.IsEmpty
            ? "(no window)"
            : $"{rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}";
    }
}
