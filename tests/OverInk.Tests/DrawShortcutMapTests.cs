using OverInk.Models;
using OverInk.Services;
using Xunit;

namespace OverInk.Tests;

public class DrawShortcutMapTests
{
    [Theory]
    [InlineData(0x50, SketchTool.Pen)]
    [InlineData(0x45, SketchTool.Eraser)]
    [InlineData(0x4C, SketchTool.Line)]
    [InlineData(0x41, SketchTool.Arrow)]
    [InlineData(0x52, SketchTool.Rectangle)]
    [InlineData(0x4F, SketchTool.Ellipse)]
    public void Maps_tool_letters(int vk, SketchTool expected)
    {
        Assert.True(DrawShortcutMap.TryGetTool(vk, out var tool));
        Assert.Equal(expected, tool);
    }

    [Theory]
    [InlineData(0x56)] // V: select exists only in the editor
    [InlineData(0x5A)] // Z
    [InlineData(0x1B)] // Escape
    [InlineData(0x20)] // Space
    public void Ignores_other_keys(int vk)
    {
        Assert.False(DrawShortcutMap.TryGetTool(vk, out _));
    }

    [Fact]
    public void Every_mapped_key_round_trips_through_KeyFor()
    {
        foreach (var vk in new[] { 'P', 'E', 'L', 'A', 'R', 'O' })
        {
            Assert.True(DrawShortcutMap.TryGetTool(vk, out var tool));
            Assert.Equal(((char)vk).ToString(), DrawShortcutMap.KeyFor(tool));
        }
    }
}
