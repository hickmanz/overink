using System.Windows.Threading;
using Microsoft.Win32;

namespace OverInk.Services;

/// <summary>
/// Raises <see cref="LayoutChanged"/> (on the UI thread, debounced) when the set of monitors really changed:
/// one unplugged or attached, a resolution or DPI change, or a monitor becoming primary. Windows fires several
/// display events in a row for one change, and again after wake from sleep, so events are coalesced and
/// compared against the last known layout.
/// </summary>
public sealed class MonitorWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<MonitorGeometry> _last;
    private bool _disposed;

    public MonitorWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _last = MonitorHelper.GetAllMonitors();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Debounce };
        _timer.Tick += OnTimerTick;
        SystemEvents.DisplaySettingsChanged += OnSystemDisplayEvent;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>The monitors as of the last notification.</summary>
    public IReadOnlyList<MonitorGeometry> Monitors => _last;

    public event Action<IReadOnlyList<MonitorGeometry>>? LayoutChanged;

    /// <summary>
    /// Raised after a display or wake event has settled, whether or not the layout really changed. Windows can
    /// move windows around on wake even when the monitors come back identical.
    /// </summary>
    public event Action? Settled;

    /// <summary>Re-reads the monitors now and raises <see cref="LayoutChanged"/> if they differ.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var current = MonitorHelper.GetAllMonitors();
        if (DesktopLayout.SameLayout(_last, current))
        {
            return;
        }

        _last = current;
        LayoutChanged?.Invoke(current);
    }

    // SystemEvents raises these on its own thread; hop to the UI thread and restart the debounce.
    private void OnSystemDisplayEvent(object? sender, EventArgs e) => Schedule();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _timer.Stop();
            _timer.Start();
        });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Refresh();
        if (!_disposed)
        {
            Settled?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplayEvent;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
    }
}
