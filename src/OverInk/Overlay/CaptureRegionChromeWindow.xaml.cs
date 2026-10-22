using System.Windows;
using System.Windows.Interop;
using OverInk.Interop;
using OverInk.Services;

namespace OverInk.Overlay;

public partial class CaptureRegionChromeWindow : Window
{
    public CaptureRegionChromeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler? AdjustRequested;
    public event EventHandler? ClearRequested;

    /// <summary>
    /// Puts the header bar centered above the region (desktop pixels), or just inside its top edge when there is
    /// no room above on that monitor. Placed in pixels so it lines up on a monitor with a different scale.
    /// Call again after <see cref="Window.Show"/>: WPF positions a window by its own Left/Top the first time it shows.
    /// </summary>
    public void ApplyRegion(Rect regionPixels)
    {
        var monitor = MonitorHelper.GetMonitorForPixelRect(regionPixels).Pixels;
        WindowPlacement.PlaceSettling(this, scale =>
        {
            const double headerHeightDips = 40;
            var headerHeight = (IsLoaded && ActualHeight > 0 ? ActualHeight : headerHeightDips) * scale;
            var width = Math.Max(220 * scale, regionPixels.Width);
            var left = regionPixels.Left + (regionPixels.Width - width) / 2;
            var top = regionPixels.Top - headerHeight;
            if (top < monitor.Top)
            {
                top = regionPixels.Top + 8 * scale;
            }

            return new Rect(left, top, width, headerHeight);
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
    }

    private void Adjust_Click(object sender, RoutedEventArgs e) => AdjustRequested?.Invoke(this, EventArgs.Empty);

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
}
