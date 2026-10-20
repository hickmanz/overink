using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OverInk.Services;

/// <summary>
/// Makes a <see cref="Slider"/> behave like modern sliders: pressing anywhere on the track jumps the knob to the
/// pointer and keeps dragging from there (WPF's default only pages toward the click, so you have to hit the knob).
/// </summary>
public static class SliderBehavior
{
    public static readonly DependencyProperty JumpToClickProperty = DependencyProperty.RegisterAttached(
        "JumpToClick", typeof(bool), typeof(SliderBehavior), new PropertyMetadata(false, OnJumpToClickChanged));

    public static bool GetJumpToClick(DependencyObject element) => (bool)element.GetValue(JumpToClickProperty);

    public static void SetJumpToClick(DependencyObject element, bool value) => element.SetValue(JumpToClickProperty, value);

    private static void OnJumpToClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
        {
            return;
        }

        slider.PreviewMouseLeftButtonDown -= Slider_PreviewMouseLeftButtonDown;
        if ((bool)e.NewValue)
        {
            slider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
        }
    }

    private static void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || slider.Template.FindName("PART_Track", slider) is not Track track ||
            track.Thumb is not { } thumb)
        {
            return;
        }

        // Grabbing the knob itself: the normal drag already does the right thing.
        if (e.OriginalSource is DependencyObject source && IsWithin(source, thumb))
        {
            return;
        }

        if (!JumpTo(slider, e.GetPosition(track)))
        {
            return;
        }

        slider.Focus();

        // Hand the press to the knob (now under the pointer) so holding the button keeps dragging.
        track.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb
        });
        e.Handled = true;
    }

    /// <summary>Moves the slider to the value under <paramref name="positionInTrack"/> (honouring tick snapping).</summary>
    public static bool JumpTo(Slider slider, Point positionInTrack)
    {
        if (slider.Template.FindName("PART_Track", slider) is not Track track)
        {
            return false;
        }

        var value = track.ValueFromPoint(positionInTrack);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
        {
            value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        }

        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        return true;
    }
    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }
}
