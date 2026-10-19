namespace OverInk.Models;

/// <summary>What the main Capture button captures (one-off region pick is separate).</summary>
public enum CaptureToolbarMode
{
    /// <summary>Active stream region when set; otherwise full primary monitor.</summary>
    Auto = 0,
    FullScreen = 1
}
