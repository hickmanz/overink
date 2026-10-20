using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OverInk.Editor;
using OverInk.Interop;

namespace OverInk.Overlay;

/// <summary>
/// The pen cursor (reticle + stroke-size ring, see <see cref="BrushCursor"/>) for draw mode on the PC screen.
/// It lives in its own small click-through window that is excluded from screen capture, so it never shows
/// up in captures or in the frames streamed to the iPad, and it never takes input from the pen.
/// </summary>
public sealed class PenCursorWindow : Window
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    // Room for the biggest pen (24) plus the reticle ticks around it.
    private const double Size = 96;

    private readonly BrushCursor _cursor = new();

    public PenCursorWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Width = Size;
        Height = Size;

        var canvas = new Canvas { IsHitTestVisible = false };
        Canvas.SetLeft(_cursor, Size / 2);
        Canvas.SetTop(_cursor, Size / 2);
        canvas.Children.Add(_cursor);
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExstyle).ToInt64();
            style |= NativeMethods.WsExTransparent | WsExNoActivate | WsExToolWindow;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExstyle, new IntPtr(style));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    /// <summary>Centers the cursor on <paramref name="screenDips"/> and sizes the ring to <paramref name="diameter"/>.</summary>
    public void ShowAt(Point screenDips, double diameter)
    {
        _cursor.SetDiameter(diameter);
        Left = screenDips.X - Size / 2;
        Top = screenDips.Y - Size / 2;
        if (!IsVisible)
        {
            Show();
        }
    }

    public void HideCursor()
    {
        if (IsVisible)
        {
            Hide();
        }
    }
}
