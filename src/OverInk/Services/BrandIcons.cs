using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverInk.Interop;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace OverInk.Services;

/// <summary>Shared OverInk squid mascot for tray and UI.</summary>
public static class BrandIcons
{
    public static Icon CreateTrayIcon()
    {
        using var bitmap = CreateBitmap(32, 32);
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public static ImageSource CreateToolbarImage(int size = 24)
    {
        using var bitmap = CreateBitmap(size, size);
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

    private static Bitmap CreateBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        DrawSquid(g, width, height);
        return bitmap;
    }

    private static void DrawSquid(Graphics g, int w, int h)
    {
        var scale = Math.Min(w, h) / 32f;
        g.TranslateTransform(w * 0.5f, h * 0.52f);
        g.ScaleTransform(scale, scale);

        var body = Color.FromArgb(255, 120, 170, 255);
        var bodyDark = Color.FromArgb(255, 76, 130, 230);
        var ink = Color.FromArgb(255, 45, 95, 200);
        var eyeWhite = Color.White;
        var pupil = Color.FromArgb(255, 30, 40, 60);
        var blush = Color.FromArgb(90, 255, 160, 200);

        // Mantle (round head)
        using (var brush = new SolidBrush(body))
        {
            g.FillEllipse(brush, -11, -14, 22, 18);
        }

        using (var brush = new SolidBrush(bodyDark))
        {
            g.FillEllipse(brush, -9, -12, 18, 14);
        }

        // Cute eyes
        using (var brush = new SolidBrush(eyeWhite))
        {
            g.FillEllipse(brush, -7, -10, 6, 7);
            g.FillEllipse(brush, 1, -10, 6, 7);
        }

        using (var brush = new SolidBrush(pupil))
        {
            g.FillEllipse(brush, -5, -8, 3.5f, 4.5f);
            g.FillEllipse(brush, 3, -8, 3.5f, 4.5f);
        }

        // Eye shine
        using (var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
        {
            g.FillEllipse(brush, -4.5f, -9, 1.5f, 1.5f);
            g.FillEllipse(brush, 3.5f, -9, 1.5f, 1.5f);
        }

        // Blush
        using (var brush = new SolidBrush(blush))
        {
            g.FillEllipse(brush, -10, -4, 4, 2.5f);
            g.FillEllipse(brush, 6, -4, 4, 2.5f);
        }

        // Tentacles
        using var tentaclePen = new Pen(ink, 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        DrawTentacle(g, tentaclePen, -8, 2, -10, 12);
        DrawTentacle(g, tentaclePen, -4, 3, -5, 13);
        DrawTentacle(g, tentaclePen, 0, 3, 0, 14);
        DrawTentacle(g, tentaclePen, 4, 3, 5, 13);
        DrawTentacle(g, tentaclePen, 8, 2, 10, 12);

        // Little ink drop
        using (var brush = new SolidBrush(ink))
        {
            g.FillEllipse(brush, 10, -6, 3, 4);
        }
    }

    private static void DrawTentacle(Graphics g, Pen pen, float x1, float y1, float x2, float y2)
    {
        using var path = new GraphicsPath();
        var midX = (x1 + x2) / 2f + (x1 < 0 ? -2 : x1 > 0 ? 2 : 0);
        path.AddBezier(x1, y1, midX, y1 + 4, midX, y2 - 3, x2, y2);
        g.DrawPath(pen, path);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
