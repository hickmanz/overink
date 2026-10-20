using System.Windows.Media;
using OverInk.Models;
using Xunit;

namespace OverInk.Tests;

public class CustomColorPaletteTests
{
    private static CustomColorPalette Create(params string[] saved) => new(new List<string>(saved));

    [Fact]
    public void Adds_a_new_color_and_raises_Changed()
    {
        var palette = Create();
        var raised = 0;
        palette.Changed += () => raised++;

        Assert.True(palette.Add(Color.FromRgb(0x12, 0x34, 0x56)));

        Assert.Equal(new[] { "#123456" }, palette.Colors);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Ignores_duplicates()
    {
        var palette = Create("#123456");
        var raised = 0;
        palette.Changed += () => raised++;

        Assert.False(palette.Add(Color.FromRgb(0x12, 0x34, 0x56)));

        Assert.Single(palette.Colors);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Ignores_preset_colors()
    {
        var palette = Create();

        Assert.True(PresetPalette.TryParseHex(PresetPalette.HexColors[0], out var preset));
        Assert.False(palette.Add(preset));
        Assert.Empty(palette.Colors);
    }

    [Fact]
    public void Removes_a_saved_color_case_insensitively()
    {
        var palette = Create("#ABCDEF", "#123456");
        var raised = 0;
        palette.Changed += () => raised++;

        Assert.True(palette.Remove("#abcdef"));
        Assert.False(palette.Remove("#abcdef"));

        Assert.Equal(new[] { "#123456" }, palette.Colors);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Stops_at_the_cap()
    {
        var palette = Create();
        for (var i = 0; i < CustomColorPalette.MaxColors; i++)
        {
            Assert.True(palette.Add(Color.FromRgb(1, 2, (byte)(i + 10))));
        }

        Assert.False(palette.Add(Color.FromRgb(200, 100, 50)));
        Assert.Equal(CustomColorPalette.MaxColors, palette.Colors.Count);
    }

    [Fact]
    public void Cleans_up_a_hand_edited_settings_list()
    {
        var palette = Create("#123456", "not a color", "#123456", PresetPalette.HexColors[0], "", "#FF0A0B0C");

        Assert.Equal(new[] { "#123456", "#0A0B0C" }, palette.Colors);
    }

    [Fact]
    public void HasSwatch_covers_presets_and_saved_colors()
    {
        var palette = Create("#123456");

        Assert.True(PresetPalette.TryParseHex(PresetPalette.HexColors[3], out var preset));
        Assert.True(palette.HasSwatch(preset));
        Assert.True(palette.HasSwatch(Color.FromRgb(0x12, 0x34, 0x56)));
        Assert.False(palette.HasSwatch(Color.FromRgb(0x01, 0x02, 0x03)));
    }
}
