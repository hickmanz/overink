using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OverInk.Services;

public static class ScreenCaptureService
{
    public static BitmapSource Capture(Rect bounds, params Window[] hideDuringCapture)
    {
        var priorVisibility = hideDuringCapture
            .Select(w => (Window: w, Visibility: w.Visibility))
            .ToArray();

        foreach (var window in hideDuringCapture)
        {
            window.Visibility = Visibility.Hidden;
        }

        try
        {
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(80);

            var width = (int)Math.Max(1, bounds.Width);
            var height = (int)Math.Max(1, bounds.Height);
            var x = (int)bounds.X;
            var y = (int)bounds.Y;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            var hBitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                // Frozen so it can be encoded on a worker thread while the UI keeps going.
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            foreach (var (window, visibility) in priorVisibility)
            {
                window.Visibility = visibility;
            }
        }
    }

    /// <summary>
    /// True when the capture is entirely black, which is what the desktop yields while the session is locked,
    /// the display is off, or a secure desktop (UAC) is showing.
    /// </summary>
    public static bool IsBlank(BitmapSource bitmap)
    {
        var scale = Math.Min(1.0, 64.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        BitmapSource sample = scale < 1.0
            ? new TransformedBitmap(bitmap, new System.Windows.Media.ScaleTransform(scale, scale))
            : bitmap;
        var converted = new FormatConvertedBitmap(sample, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
