using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OverInk.Services;

/// <summary>
/// Makes a <see cref="Slider"/> behave like modern sliders: pressing anywhere on it puts the knob under the pointer,
/// and the knob then follows the pointer until the button is released (WPF's default only pages toward the click, so
/// you have to hit the knob). The knob is placed from the pointer position every time rather than by dragging the
/// stock thumb, which only follows pointer *movement* and so stays offset when the first jump snaps to a tick.
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
        slider.PreviewMouseMove -= Slider_PreviewMouseMove;
        slider.PreviewMouseLeftButtonUp -= Slider_PreviewMouseLeftButtonUp;
        if ((bool)e.NewValue)
        {
            slider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
            slider.PreviewMouseMove += Slider_PreviewMouseMove;
            slider.PreviewMouseLeftButtonUp += Slider_PreviewMouseLeftButtonUp;
        }
    }

    private static void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || slider.Template.FindName("PART_Track", slider) is not Track track)
        {
            return;
        }

        JumpTo(slider, e.GetPosition(track));
        slider.Focus();
        // Keep receiving the pointer (even outside the slider) until release, and place the knob from it.
        slider.CaptureMouse();
        e.Handled = true;
    }

    private static void Slider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed ||
            slider.Template.FindName("PART_Track", slider) is not Track track)
        {
            return;
        }

        JumpTo(slider, e.GetPosition(track));
        e.Handled = true;
    }

    private static void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>Moves the slider to the value under <paramref name="positionInTrack"/> (honouring tick snapping).</summary>
    public static bool JumpTo(Slider slider, Point positionInTrack)
    {
        if (slider.Template.FindName("PART_Track", slider) is not Track track)
        {
            return false;
        }

        double value;
        if (slider.Orientation == Orientation.Horizontal && track.Thumb is { } thumb)
        {
            // Straight from the geometry: the knob's centre sits half a knob inside each end of the track.
            // (Track.ValueFromPoint is relative to the knob's last *laid out* position, which lags while dragging.)
            var usable = track.ActualWidth - thumb.ActualWidth;
            if (usable <= 0)
            {
                return false;
            }

            var ratio = Math.Clamp((positionInTrack.X - thumb.ActualWidth / 2) / usable, 0, 1);
            if (slider.IsDirectionReversed)
            {
                ratio = 1 - ratio;
            }

            value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }
        else
        {
            value = track.ValueFromPoint(positionInTrack);
        }

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
}
