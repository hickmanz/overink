using System.Windows;

namespace OverInk.Services;

/// <summary>
/// Primary monitor bounds in WPF DIPs (for windows) and physical pixels (for GDI capture).
/// </summary>
public readonly record struct MonitorGeometry(Rect Dips, Rect Pixels, IntPtr MonitorHandle);
