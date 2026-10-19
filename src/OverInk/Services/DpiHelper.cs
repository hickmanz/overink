using System.Windows;
using System.Windows.Media;

namespace OverInk.Services;

public static class DpiHelper
{
    public static Rect GetPhysicalScreenRect(Window window, double paddingDips = 16)
    {
        if (!window.IsLoaded || window.ActualWidth <= 0)
        {
            return Rect.Empty;
        }

        var topLeft = window.PointToScreen(new Point(-paddingDips, -paddingDips));
        var bottomRight = window.PointToScreen(
            new Point(window.ActualWidth + paddingDips, window.ActualHeight + paddingDips));

        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget == null)
        {
            return new Rect(topLeft, bottomRight);
        }

        var transform = source.CompositionTarget.TransformToDevice;
        var physicalTopLeft = transform.Transform(topLeft);
        var physicalBottomRight = transform.Transform(bottomRight);

        return new Rect(
            physicalTopLeft.X,
            physicalTopLeft.Y,
            physicalBottomRight.X - physicalTopLeft.X,
            physicalBottomRight.Y - physicalTopLeft.Y);
    }

    public static bool PhysicalScreenRectContains(Rect physicalRect, int screenX, int screenY) =>
        !physicalRect.IsEmpty &&
        screenX >= physicalRect.Left &&
        screenX < physicalRect.Right &&
        screenY >= physicalRect.Top &&
        screenY < physicalRect.Bottom;
}
