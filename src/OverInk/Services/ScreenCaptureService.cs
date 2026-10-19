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
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
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

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
