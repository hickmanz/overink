using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OverInk.Models;

public sealed class AppSettings
{
    public double ToolbarLeft { get; set; } = 80;
    public double ToolbarTop { get; set; } = 80;
    /// <summary>True when the toolbar is laid out as a vertical dock (double-click the grip to toggle).</summary>
    public bool ToolbarVertical { get; set; }
    public string PenColor { get; set; } = "#E53935";
    /// <summary>Colors the user saved from the custom color wheel (#RRGGBB), shown after the presets everywhere.</summary>
    public List<string> CustomColors { get; set; } = new();
    public double PenThickness { get; set; } = 4;
    /// <summary>When false, strokes use a constant width instead of varying with pen pressure.</summary>
    public bool PressureSensitive { get; set; } = true;
    /// <summary>Opacity (0 – 0.6) of the white veil laid over the screen, under the ink, while drawing. 0 = off.</summary>
    public double ScreenVeilOpacity { get; set; }
    public bool RunAtStartup { get; set; }
    /// <summary>UTC time of the last update check that reached GitHub. Null until the first successful check.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Release version the user last chose "Not now" for. A newer tag starts the count over.</summary>
    public string SkippedUpdateVersion { get; set; } = "";
    /// <summary>How many times "Not now" was chosen for <see cref="SkippedUpdateVersion"/>; the startup prompt stops after a few.</summary>
    public int SkippedUpdateCount { get; set; }
    /// <summary>Also offer GitHub pre-releases (beta tags such as v0.3.0-beta.1) when checking for updates.</summary>
    public bool IncludeBetaUpdates { get; set; }
    public bool HideToolbarWhenNotDrawing { get; set; }
    /// <summary>Folds pen / line / arrow / rectangle / ellipse into one toolbar button with a picker (the eraser stays out).</summary>
    public bool CompactToolbarTools { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = NativeMethodsDefaults.DefaultHotkeyModifiers;
    public uint HotkeyVirtualKey { get; set; } = 0x44; // D
    /// <summary>Global shortcut that starts a region capture. A virtual key of 0 means not bound.</summary>
    public uint CaptureHotkeyModifiers { get; set; }
    public uint CaptureHotkeyVirtualKey { get; set; }
    /// <summary>Copy every capture taken on this PC to the clipboard as soon as it is saved.</summary>
    public bool AutoCopyCaptures { get; set; }
    /// <summary>Turn draw mode off once a capture taken on this PC is saved (the editor opens and needs the mouse).</summary>
    public bool ExitDrawModeOnCapture { get; set; } = true;
    /// <summary>Wipe the on-screen drawing after a capture taken on this PC, ready for the next sketch.</summary>
    public bool ClearDrawingAfterCapture { get; set; } = true;
    /// <summary>Leave the toolbar visible in screen grabs (it is normally hidden for the instant of the grab).</summary>
    public bool CaptureToolbarInGrabs { get; set; }
    public SketchTool LastTool { get; set; } = SketchTool.Pen;

    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 8787;
    /// <summary>Network adapter the pairing QR code uses (<c>NetworkInterface.Id</c>). Empty picks the best one automatically.</summary>
    public string RemoteAdapterId { get; set; } = string.Empty;
    public int RemoteMaxFps { get; set; } = 10;
    /// <summary>JPEG encoder quality (45–95 in UI).</summary>
    public int RemoteJpegQuality { get; set; } = 95;
    /// <summary>Max stream long edge in px; 0 = match primary monitor native long edge.</summary>
    public int RemoteMaxEdge { get; set; }
    public int RemoteMaxClients { get; set; } = 8;
    public bool RemoteRequireJoinApproval { get; set; }
    public List<PairedDevice> PairedDevices { get; set; } = new();
    /// <summary>Lets the paired iPad browse, edit and create captures in the library.</summary>
    public bool RemoteLibraryEnabled { get; set; } = true;

    /// <summary>Empty uses Pictures\OverInk.</summary>
    public string LibraryFolder { get; set; } = string.Empty;

    public CaptureToolbarMode ToolbarCaptureMode { get; set; } = CaptureToolbarMode.Auto;

    public bool RemoteRegionEnabled { get; set; }
    public double RemoteRegionX { get; set; }
    public double RemoteRegionY { get; set; }
    public double RemoteRegionW { get; set; } = 1;
    public double RemoteRegionH { get; set; } = 1;

    public Color GetPenColor() => ColorHex.Parse(PenColor);

    public void SetPenColor(Color color) => PenColor = ColorHex.ToRgbHex(color);

    private static class NativeMethodsDefaults
    {
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint DefaultHotkeyModifiers = ModControl | ModShift;
    }
}
