namespace OverInk.Toolbar;

public enum CaptureRequestKind
{
    /// <summary>Use <see cref="Models.CaptureToolbarMode"/> from settings.</summary>
    Default,
    /// <summary>Drag a rectangle once; does not change stream region or capture mode.</summary>
    PickRegion
}

public sealed class CaptureRequestedEventArgs : EventArgs
{
    public CaptureRequestKind Kind { get; init; }
}
