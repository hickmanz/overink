using System.Windows.Media;
using OverInk.Models;
using OverInk.Services;

namespace OverInk;

public sealed class SketchSession
{
    public SketchSession(SettingsService settings)
    {
        Settings = settings;
        Tool = settings.Current.LastTool;
        PenColor = settings.Current.GetPenColor();
        PenThickness = settings.Current.PenThickness;
        PressureSensitive = settings.Current.PressureSensitive;
    }

    public SettingsService Settings { get; }
    public UndoStack UndoStack { get; } = new();

    public SketchTool Tool { get; private set; }
    public InteractionMode Mode { get; private set; } = InteractionMode.PassThrough;
    public bool IsHotkeyCaptureActive { get; set; }
    public Color PenColor { get; private set; }
    public double PenThickness { get; private set; }

    /// <summary>Whether pen pressure changes stroke width (off = constant width).</summary>
    public bool PressureSensitive { get; private set; }

    public event Action? Changed;

    public void SetPressureSensitive(bool enabled)
    {
        PressureSensitive = enabled;
        Settings.Current.PressureSensitive = enabled;
        Notify();
    }

    public void SetTool(SketchTool tool)
    {
        Tool = tool;
        Settings.Current.LastTool = tool;
        Notify();
    }

    public void SetColor(Color color)
    {
        PenColor = color;
        Settings.Current.SetPenColor(color);
        Notify();
    }

    public void SetThickness(double thickness)
    {
        PenThickness = Math.Clamp(thickness, 1, 24);
        Settings.Current.PenThickness = PenThickness;
        Notify();
    }

    public void ToggleMode()
    {
        Mode = Mode == InteractionMode.Draw ? InteractionMode.PassThrough : InteractionMode.Draw;
        Notify();
    }

    public void SetMode(InteractionMode mode)
    {
        Mode = mode;
        Notify();
    }

    public void PersistSettings()
    {
        Settings.Save();
    }

    private void Notify() => Changed?.Invoke();
}
