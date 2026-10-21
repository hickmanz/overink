namespace OverInk.Models;

public enum SketchTool
{
    Pen,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Eraser,
    /// <summary>Capture editor only: pick, move, recolor, resize or delete existing ink.</summary>
    Select
}

public enum InteractionMode
{
    Draw,
    PassThrough
}
