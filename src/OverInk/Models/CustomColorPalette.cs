using System.Windows.Media;

namespace OverInk.Models;

/// <summary>
/// The user's saved custom colors. Backed by the list in <see cref="AppSettings"/> so it persists with the
/// rest of the settings; shared by the toolbar, the PC editor and the iPad remote.
/// </summary>
public sealed class CustomColorPalette
{
    public const int MaxColors = 28;

    private readonly List<string> _hexes;

    public CustomColorPalette(List<string> backing)
    {
        _hexes = backing;
        Normalize();
    }

    /// <summary>Raised after a color was added or removed.</summary>
    public event Action? Changed;

    /// <summary>Saved colors as #RRGGBB, oldest first.</summary>
    public IReadOnlyList<string> Colors => _hexes;

    public bool Contains(Color color) => IndexOf(ColorHex.ToRgbHex(color)) >= 0;

    /// <summary>True for a color that already has a swatch (a preset or a saved one).</summary>
    public bool HasSwatch(Color color) => PresetPalette.IsPreset(color) || Contains(color);

    /// <summary>Saves a color. Returns false for presets, duplicates, and when the palette is full.</summary>
    public bool Add(Color color)
    {
        if (HasSwatch(color) || _hexes.Count >= MaxColors)
        {
            return false;
        }

        _hexes.Add(ColorHex.ToRgbHex(color));
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string hex)
    {
        var index = IndexOf(hex);
        if (index < 0)
        {
            return false;
        }

        _hexes.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    private int IndexOf(string hex) =>
        _hexes.FindIndex(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cleans a hand-edited or older settings file: valid #RRGGBB only, no duplicates, within the cap.</summary>
    private void Normalize()
    {
        var cleaned = new List<string>();
        foreach (var raw in _hexes)
        {
            if (!ColorHex.TryParse(raw, out var color))
            {
                continue;
            }

            var hex = ColorHex.ToRgbHex(color);
            if (PresetPalette.IsPreset(color) ||
                cleaned.Exists(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            cleaned.Add(hex);
            if (cleaned.Count >= MaxColors)
            {
                break;
            }
        }

        _hexes.Clear();
        _hexes.AddRange(cleaned);
    }
}
