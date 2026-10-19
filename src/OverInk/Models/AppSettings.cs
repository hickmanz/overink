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
    public double PenThickness { get; set; } = 4;
    /// <summary>When false, strokes use a constant width instead of varying with pen pressure.</summary>
    public bool PressureSensitive { get; set; } = true;
    /// <summary>Opacity (0 – 0.6) of the white veil laid over the screen, under the ink, while drawing. 0 = off.</summary>
    public double ScreenVeilOpacity { get; set; }
    public bool RunAtStartup { get; set; }
    public bool HideToolbarWhenNotDrawing { get; set; }
    public uint HotkeyModifiers { get; set; } = NativeMethodsDefaults.DefaultHotkeyModifiers;
    public uint HotkeyVirtualKey { get; set; } = 0x44; // D
    public SketchTool LastTool { get; set; } = SketchTool.Pen;

    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 8787;
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
