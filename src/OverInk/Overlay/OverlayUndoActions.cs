using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using OverInk.Services;

namespace OverInk.Overlay;

internal sealed class InkStrokeAction : IUndoableAction
{
    private readonly InkCanvas _canvas;
    private readonly StrokeCollection _strokes;

    public InkStrokeAction(InkCanvas canvas, StrokeCollection strokes, bool added, string authorId = UndoAuthors.Host)
    {
        _canvas = canvas;
        _strokes = strokes;
        Added = added;
        AuthorId = authorId;
    }

    public string AuthorId { get; }
    public bool Added { get; }

    public void Undo()
    {
        if (Added)
        {
            _canvas.Strokes.Remove(_strokes);
        }
        else
        {
            _canvas.Strokes.Add(_strokes);
        }
    }

    public void Redo()
    {
        if (Added)
        {
            _canvas.Strokes.Add(_strokes);
        }
        else
        {
            _canvas.Strokes.Remove(_strokes);
        }
    }
}

/// <summary>One undo step that swaps a set of strokes for another (e.g. a partial erase: original -> pieces).</summary>
internal sealed class InkReplaceAction : IUndoableAction
{
    private readonly InkCanvas _canvas;
    private readonly StrokeCollection _removed;
    private readonly StrokeCollection _added;

    public InkReplaceAction(InkCanvas canvas, StrokeCollection removed, StrokeCollection added, string authorId)
    {
        _canvas = canvas;
        _removed = removed;
        _added = added;
        AuthorId = authorId;
    }

    public string AuthorId { get; }

    public void Undo()
    {
        _canvas.Strokes.Remove(_added);
        _canvas.Strokes.Add(_removed);
    }

    public void Redo()
    {
        _canvas.Strokes.Remove(_removed);
        _canvas.Strokes.Add(_added);
    }
}

internal sealed class ShapeAction : IUndoableAction
{
    private readonly Panel _layer;
    private readonly UIElement _element;

    public ShapeAction(Panel layer, UIElement element, bool added, string authorId = UndoAuthors.Host)
    {
        _layer = layer;
        _element = element;
        Added = added;
        AuthorId = authorId;
    }

    public string AuthorId { get; }
    public bool Added { get; }

    public void Undo()
    {
        if (Added)
        {
            _layer.Children.Remove(_element);
        }
        else
        {
            _layer.Children.Add(_element);
        }
    }

    public void Redo()
    {
        if (Added)
        {
            _layer.Children.Add(_element);
        }
        else
        {
            _layer.Children.Remove(_element);
        }
    }
}
