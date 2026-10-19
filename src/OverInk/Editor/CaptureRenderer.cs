using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverInk.Editor;

/// <summary>
/// Renders a capture's original image plus its ink into the flattened PNG. Shared by the PC editor and
/// the remote (iPad) library so both produce identical output from the same stroke data.
/// Must run on a thread with a WPF dispatcher (the UI thread).
/// </summary>
public static class CaptureRenderer
{
    public static BitmapSource Flatten(BitmapSource original, StrokeCollection strokes)
    {
        var width = original.PixelWidth;
        var height = original.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(original, new Rect(0, 0, width, height));
            strokes.Draw(dc);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
