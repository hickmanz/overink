using OverInk.Models;

namespace OverInk.Services;

/// <summary>The single-letter tool keys shared by the editor and draw mode.</summary>
public static class DrawShortcutMap
{
    public static bool TryGetTool(int virtualKey, out SketchTool tool)
    {
        // Letter virtual-key codes are the uppercase ASCII values.
        switch (virtualKey)
        {
            case 'P': tool = SketchTool.Pen; return true;
            case 'E': tool = SketchTool.Eraser; return true;
            case 'L': tool = SketchTool.Line; return true;
            case 'A': tool = SketchTool.Arrow; return true;
            case 'R': tool = SketchTool.Rectangle; return true;
            case 'O': tool = SketchTool.Ellipse; return true;
            default: tool = default; return false;
        }
    }

    /// <summary>The key hint shown next to a tool (toolbar tooltips and the draw-mode menu).</summary>
    public static string? KeyFor(SketchTool tool) => tool switch
    {
        SketchTool.Pen => "P",
        SketchTool.Eraser => "E",
        SketchTool.Line => "L",
        SketchTool.Arrow => "A",
        SketchTool.Rectangle => "R",
        SketchTool.Ellipse => "O",
        _ => null
    };
}
