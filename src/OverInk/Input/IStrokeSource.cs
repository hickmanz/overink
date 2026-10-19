namespace OverInk.Input;

/// <summary>
/// Remote inputs (e.g. iPad) push live ink through this interface.
/// </summary>
public interface IStrokeSource
{
    event EventHandler<RemoteStrokeBeginEventArgs>? StrokeBegan;
    event EventHandler<RemoteStrokePointsEventArgs>? StrokePointsAdded;
    event EventHandler<RemoteStrokeIdEventArgs>? StrokeEnded;
    event EventHandler<RemoteStrokeIdEventArgs>? StrokeCancelled;
    event EventHandler<RemoteShapeEventArgs>? ShapePreview;
    event EventHandler<RemoteShapeEventArgs>? ShapeCommitted;
    event EventHandler<RemoteEraseEventArgs>? EraseRequested;
}

public sealed class RemoteStrokeSource : IStrokeSource
{
    public event EventHandler<RemoteStrokeBeginEventArgs>? StrokeBegan;
    public event EventHandler<RemoteStrokePointsEventArgs>? StrokePointsAdded;
    public event EventHandler<RemoteStrokeIdEventArgs>? StrokeEnded;
    public event EventHandler<RemoteStrokeIdEventArgs>? StrokeCancelled;
    public event EventHandler<RemoteShapeEventArgs>? ShapePreview;
    public event EventHandler<RemoteShapeEventArgs>? ShapeCommitted;
    public event EventHandler<RemoteEraseEventArgs>? EraseRequested;

    public void RaiseStrokeBegan(RemoteStrokeBeginEventArgs args) => StrokeBegan?.Invoke(this, args);
    public void RaiseStrokePoints(RemoteStrokePointsEventArgs args) => StrokePointsAdded?.Invoke(this, args);
    public void RaiseStrokeEnded(RemoteStrokeIdEventArgs args) => StrokeEnded?.Invoke(this, args);
    public void RaiseStrokeCancelled(RemoteStrokeIdEventArgs args) => StrokeCancelled?.Invoke(this, args);
    public void RaiseShapePreview(RemoteShapeEventArgs args) => ShapePreview?.Invoke(this, args);
    public void RaiseShapeCommitted(RemoteShapeEventArgs args) => ShapeCommitted?.Invoke(this, args);
    public void RaiseErase(RemoteEraseEventArgs args) => EraseRequested?.Invoke(this, args);
}
