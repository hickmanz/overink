using System.Windows.Media;

namespace OverInk.Models;

public static class ColorSpaces
{
    public static (double H, double S, double V) RgbToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double h;
        if (delta < 0.0001)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            h = 60 * (((r - g) / delta) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }

        var s = max < 0.0001 ? 0 : delta / max;
        return (h, s, max);
    }

    public static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60)
        {
            (r, g, b) = (c, x, 0);
        }
        else if (h < 120)
        {
            (r, g, b) = (x, c, 0);
        }
        else if (h < 180)
        {
            (r, g, b) = (0, c, x);
        }
        else if (h < 240)
        {
            (r, g, b) = (0, x, c);
        }
        else if (h < 300)
        {
            (r, g, b) = (x, 0, c);
        }
        else
        {
            (r, g, b) = (c, 0, x);
        }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
