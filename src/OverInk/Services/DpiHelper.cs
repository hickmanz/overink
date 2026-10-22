using System.Windows;

namespace OverInk.Services;

public static class DpiHelper
{
    /// <summary>
    /// The window's rectangle in physical screen pixels, grown by <paramref name="paddingDips"/> on every side.
    /// <c>PointToScreen</c> already returns device pixels (it applies the window's DPI scale), so no further
    /// scaling is needed; scaling again put the rectangle in the wrong place on displays above 100%.
    /// </summary>
    public static Rect GetPhysicalScreenRect(Window window, double paddingDips = 16)
    {
        if (!window.IsLoaded || window.ActualWidth <= 0)
        {
            return Rect.Empty;
        }

        var topLeft = window.PointToScreen(new Point(-paddingDips, -paddingDips));
        var bottomRight = window.PointToScreen(
            new Point(window.ActualWidth + paddingDips, window.ActualHeight + paddingDips));

        return new Rect(topLeft, bottomRight);
    }

    public static bool PhysicalScreenRectContains(Rect physicalRect, int screenX, int screenY) =>
        !physicalRect.IsEmpty &&
        screenX >= physicalRect.Left &&
        screenX < physicalRect.Right &&
        screenY >= physicalRect.Top &&
        screenY < physicalRect.Bottom;
}
