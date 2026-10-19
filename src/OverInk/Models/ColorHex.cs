using System.Globalization;
using System.Windows.Media;

namespace OverInk.Models;

/// <summary>WPF <see cref="Color.ToString"/> is #AARRGGBB; HTML canvas/CSS uses #RRGGBB or #RRGGBBAA.</summary>
public static class ColorHex
{
    public static string ToRgbHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Parse(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new FormatException("Color hex is empty.");
        }

        var s = hex.Trim();
        if (s.Length == 9 && s[0] == '#')
        {
            var a = ParseByte(s, 1);
            var r = ParseByte(s, 3);
            var g = ParseByte(s, 5);
            var b = ParseByte(s, 7);
            return Color.FromArgb(a, r, g, b);
        }

        return (Color)ColorConverter.ConvertFromString(s)!;
    }

    public static bool TryParse(string? hex, out Color color)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                color = Colors.Transparent;
                return false;
            }

            color = Parse(hex);
            return true;
        }
        catch
        {
            color = Colors.Transparent;
            return false;
        }
    }

    public static bool RgbEquals(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static byte ParseByte(string s, int start) =>
        byte.Parse(s.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
