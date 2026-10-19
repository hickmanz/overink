using System.Windows.Media;

namespace OverInk.Models;

public static class PresetPalette
{
    /// <summary>Default ink colors (hex), shown on toolbar and iPad remote.</summary>
    public static IReadOnlyList<string> HexColors { get; } =
    [
        "#E53935",
        "#E91E63",
        "#9C27B0",
        "#673AB7",
        "#3F51B5",
        "#1E88E5",
        "#00ACC1",
        "#43A047",
        "#CDDC39",
        "#FDD835",
        "#FB8C00",
        "#795548",
        "#FFFFFF",
        "#212121"
    ];

    public static bool TryParseHex(string hex, out Color color) => ColorHex.TryParse(hex, out color);

    public static bool IsPreset(Color color)
    {
        foreach (var hex in HexColors)
        {
            if (!TryParseHex(hex, out var preset))
            {
                continue;
            }

            if (ColorsMatch(preset, color))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ColorsMatch(Color a, Color b) => ColorHex.RgbEquals(a, b);
}
