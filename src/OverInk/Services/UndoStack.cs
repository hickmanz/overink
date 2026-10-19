namespace OverInk.Services;

public interface IUndoableAction
{
    string AuthorId { get; }
    void Undo();
    void Redo();
}

public sealed class UndoStack
{
    private readonly Stack<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IUndoableAction action)
    {
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo(string? authorId = null)
    {
        if (_undo.Count == 0)
        {
            return;
        }

        if (authorId is null)
        {
            var action = _undo.Pop();
            action.Undo();
            _redo.Push(action);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var buffer = new Stack<IUndoableAction>();
        IUndoableAction? target = null;
        while (_undo.Count > 0)
        {
            var candidate = _undo.Pop();
            buffer.Push(candidate);
            if (candidate.AuthorId == authorId)
            {
                target = candidate;
                break;
            }
        }

        while (buffer.Count > 0)
        {
            var item = buffer.Pop();
            if (!ReferenceEquals(item, target))
            {
                _undo.Push(item);
            }
        }

        if (target is null)
        {
            return;
        }

        target.Undo();
        _redo.Push(target);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo(string? authorId = null)
    {
        if (_redo.Count == 0)
        {
            return;
        }

        if (authorId is null)
        {
            var action = _redo.Pop();
            action.Redo();
            _undo.Push(action);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var buffer = new Stack<IUndoableAction>();
        IUndoableAction? target = null;
        while (_redo.Count > 0)
        {
            var candidate = _redo.Pop();
            buffer.Push(candidate);
            if (candidate.AuthorId == authorId)
            {
                target = candidate;
                break;
            }
        }

        while (buffer.Count > 0)
        {
            var item = buffer.Pop();
            if (!ReferenceEquals(item, target))
            {
                _redo.Push(item);
            }
        }

        if (target is null)
        {
            return;
        }

        target.Redo();
        _undo.Push(target);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
