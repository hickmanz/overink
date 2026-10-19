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

    public void ApplyRegion(Rect dipRect)
    {
        const double headerHeight = 40;
        Width = Math.Max(220, dipRect.Width);
        Left = dipRect.Left + (dipRect.Width - Width) / 2;
        Top = dipRect.Top - headerHeight;
        var monitor = MonitorHelper.GetPrimaryMonitorGeometry().Dips;
        if (Top < monitor.Top)
        {
            Top = dipRect.Top + 8;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
    }

    private void Adjust_Click(object sender, RoutedEventArgs e) => AdjustRequested?.Invoke(this, EventArgs.Empty);

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
}
