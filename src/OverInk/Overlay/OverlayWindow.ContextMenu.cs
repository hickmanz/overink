using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverInk.Models;
using OverInk.Services;

namespace OverInk.Overlay;

/// <summary>The right-click menu shown on the overlay while drawing.</summary>
public partial class OverlayWindow
{
    private static readonly (SketchTool Tool, string Label, string IconKey)[] MenuTools =
    {
        (SketchTool.Pen, "Pen", "IconPen"),
        (SketchTool.Eraser, "Eraser", "IconEraser"),
        (SketchTool.Line, "Line", "IconLine"),
        (SketchTool.Arrow, "Arrow", "IconArrow"),
        (SketchTool.Rectangle, "Rectangle", "IconRectangle"),
        (SketchTool.Ellipse, "Ellipse", "IconEllipse")
    };

    private void InitializeDrawMenu()
    {
        PreviewMouseRightButtonUp += OnOverlayRightClick;

        // A held pen or finger turns into a right-click; that would pop the menu up in the middle of a stroke.
        System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(InkSurface, false);
        System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(ShapeLayer, false);
    }

    private void OnOverlayRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_session.Mode != InteractionMode.Draw || _session.IsOverlayMenuOpen)
        {
            return;
        }

        e.Handled = true;
        ShowDrawMenu();
    }

    private void ShowDrawMenu()
    {
        HidePenCursor();

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };

        foreach (var (tool, label, iconKey) in MenuTools)
        {
            var entry = MenuEntry(label, iconKey, () =>
            {
                _session.SetTool(tool);
                _session.PersistSettings();
            }, active: _session.Tool == tool);
            entry.InputGestureText = DrawShortcutMap.KeyFor(tool) ?? string.Empty;
            menu.Items.Add(entry);
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });
        menu.Items.Add(MenuEntry("Clear all", "IconTrash", ClearAll, danger: true));

        var exit = MenuEntry("Exit draw mode", "IconX", () =>
        {
            _session.SetMode(InteractionMode.PassThrough);
            _session.PersistSettings();
        });
        exit.InputGestureText = "Esc";
        menu.Items.Add(exit);

        menu.Closed += (_, _) => _session.IsOverlayMenuOpen = false;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        // Set before opening: from here on the keyboard hook leaves Esc and the arrows to the menu.
        _session.IsOverlayMenuOpen = true;
        menu.IsOpen = true;
    }

    private MenuItem MenuEntry(string header, string iconKey, Action onClick, bool danger = false, bool active = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource(danger ? "DarkMenuItemDanger" : "DarkMenuItem"),
            Icon = new ContentControl
            {
                Style = (Style)FindResource("LucideIcon"),
                Tag = FindResource(iconKey),
                Width = 16,
                Height = 16
            }
        };

        if (active)
        {
            // The tool in use is lit with the accent colour (icon and label).
            item.Foreground = (Brush)FindResource("AccentBrush");
        }

        item.Click += (_, _) => onClick();
        return item;
    }
}
