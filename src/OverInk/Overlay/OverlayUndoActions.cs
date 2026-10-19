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
