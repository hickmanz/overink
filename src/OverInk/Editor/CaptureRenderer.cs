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
    /// <param name="veil">
    /// Opacity (0–1) of a white wash laid between the image and the ink, so the sketch stands out. It is
    /// stored beside the capture rather than baked into the original, which keeps it adjustable.
    /// </param>
    public static BitmapSource Flatten(BitmapSource original, StrokeCollection strokes, double veil = 0)
    {
        var width = original.PixelWidth;
        var height = original.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(original, new Rect(0, 0, width, height));
            if (veil > 0)
            {
                var alpha = (byte)Math.Round(Math.Clamp(veil, 0, 1) * 255);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), null, new Rect(0, 0, width, height));
            }

            strokes.Draw(dc);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
