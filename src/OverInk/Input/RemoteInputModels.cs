using System.Windows;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using OverInk.Models;

namespace OverInk.Input;

public sealed class RemoteStrokeBeginEventArgs : EventArgs
{
    public required Guid Id { get; init; }
    public required string AuthorId { get; init; }
    public required DrawingAttributes Attributes { get; init; }
    public required Point FirstPointDips { get; init; }
    public float Pressure { get; init; }
}

public sealed class RemoteStrokePointsEventArgs : EventArgs
{
    public required Guid Id { get; init; }
    public required StylusPointCollection Points { get; init; }
}

public sealed class RemoteShapeEventArgs : EventArgs
{
    public required Guid Id { get; init; }
    public required string AuthorId { get; init; }
    public required SketchTool Tool { get; init; }
    public required Point StartDips { get; init; }
    public required Point EndDips { get; init; }
    public required Color Color { get; init; }
    public required double Thickness { get; init; }
    public bool IsPreview { get; init; }
}

public sealed class RemoteEraseEventArgs : EventArgs
{
    public required string AuthorId { get; init; }
    public required Point[] PointsDips { get; init; }
    public required double EraserSize { get; init; }
}

public sealed class RemoteStrokeIdEventArgs : EventArgs
{
    public required Guid Id { get; init; }
    public required string AuthorId { get; init; }
}
