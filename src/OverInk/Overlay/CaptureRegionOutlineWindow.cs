using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using OverInk.Interop;
using OverInk.Services;

namespace OverInk.Overlay;

/// <summary>
/// The dashed frame marking the iPad stream region. It lives in its own click-through window that is
/// excluded from screen capture, so it never shows up in captures or in the frames streamed to the iPad.
/// </summary>
public sealed class CaptureRegionOutlineWindow : Window
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    public CaptureRegionOutlineWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;

        Content = new Rectangle
        {
            Margin = new Thickness(1),
            Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x4C, 0x8D, 0xFF)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Fill = null,
            IsHitTestVisible = false
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExstyle).ToInt64();
            style |= NativeMethods.WsExTransparent | WsExNoActivate | WsExToolWindow;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExstyle, new IntPtr(style));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    /// <summary>Positions the frame over <paramref name="screenDips"/> (screen DIPs).</summary>
    public void Place(Rect screenDips)
    {
        Left = screenDips.Left;
        Top = screenDips.Top;
        Width = screenDips.Width;
        Height = screenDips.Height;
    }
}
