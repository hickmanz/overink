using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OverInk.Models;

public sealed class AppSettings
{
    public double ToolbarLeft { get; set; } = 80;
    public double ToolbarTop { get; set; } = 80;
    public string PenColor { get; set; } = "#E53935";
    public double PenThickness { get; set; } = 4;
    public bool RunAtStartup { get; set; }
    public bool HideToolbarWhenNotDrawing { get; set; }
    public uint HotkeyModifiers { get; set; } = NativeMethodsDefaults.DefaultHotkeyModifiers;
    public uint HotkeyVirtualKey { get; set; } = 0x44; // D
    public SketchTool LastTool { get; set; } = SketchTool.Pen;

    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 8787;
    public int RemoteMaxFps { get; set; } = 10;
    public int RemoteJpegQuality { get; set; } = 78;
    public int RemoteMaxEdge { get; set; } = 2560;
    public int RemoteMaxClients { get; set; } = 8;
    public bool RemoteRequireJoinApproval { get; set; }
    public List<PairedDevice> PairedDevices { get; set; } = new();

    /// <summary>Empty uses Pictures\OverInk.</summary>
    public string LibraryFolder { get; set; } = string.Empty;

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
