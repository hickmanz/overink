using System.Windows;
using System.Windows.Controls;

namespace OverInk.Toolbar;

/// <summary>Right-click menu for a saved color swatch (shared by the toolbar and the PC editor).</summary>
public static class CustomSwatchMenu
{
    /// <summary>
    /// Attaches a "Remove color" menu to <paramref name="swatch"/>. <paramref name="onOpenChanged"/> reports when the
    /// menu opens/closes so a host popup that dismisses on outside clicks can stay open meanwhile.
    /// </summary>
    public static void Attach(FrameworkElement swatch, Action remove, Action<bool>? onOpenChanged = null)
    {
        var menu = new ContextMenu { Style = (Style)swatch.FindResource("DarkContextMenu") };
        var item = new MenuItem
        {
            Header = "Remove color",
            Style = (Style)swatch.FindResource("DarkMenuItemDanger")
        };
        item.Click += (_, _) => remove();
        menu.Items.Add(item);

        if (onOpenChanged is not null)
        {
            menu.Opened += (_, _) => onOpenChanged(true);
            menu.Closed += (_, _) => onOpenChanged(false);
        }

        swatch.ContextMenu = menu;
    }
}
