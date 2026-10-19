using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverInk.Models;

namespace OverInk.Toolbar;

public partial class ColorPickerControl : UserControl
{
    private const int WheelSize = 176;

    private WriteableBitmap? _wheelBitmap;
    private double _hue;
    private double _sat;
    private double _val = 1;
    private bool _dragging;
    private bool _suppressEvents;

    public ColorPickerControl()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureWheel();
    }

    public event EventHandler<Color>? ColorChanged;

    public Color SelectedColor => ColorSpaces.HsvToRgb(_hue, _sat, _val);

    public void SetColor(Color color)
    {
        (_hue, _sat, _val) = ColorSpaces.RgbToHsv(color);
        _suppressEvents = true;
        BrightnessSlider.Value = _val * 100;
        _suppressEvents = false;
        RefreshWheel();
        UpdatePreview();
        PlaceMarker();
    }

    private void EnsureWheel()
    {
        if (_wheelBitmap != null)
        {
            return;
        }

        _wheelBitmap = new WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        WheelImage.Source = _wheelBitmap;
        RefreshWheel();
        PlaceMarker();
    }

    private void RefreshWheel()
    {
        if (_wheelBitmap == null)
        {
            return;
        }

        var pixels = new byte[WheelSize * WheelSize * 4];
        var cx = WheelSize / 2.0;
        var cy = WheelSize / 2.0;
        var maxR = Math.Min(cx, cy) - 2;

        for (var y = 0; y < WheelSize; y++)
        {
            for (var x = 0; x < WheelSize; x++)
            {
                var i = (y * WheelSize + x) * 4;
                var dx = x - cx + 0.5;
                var dy = y - cy + 0.5;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > maxR)
                {
                    pixels[i + 3] = 0;
                    continue;
                }

                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var sat = dist / maxR;
                var c = ColorSpaces.HsvToRgb(hue, sat, _val);
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 255;
            }
        }

        _wheelBitmap.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, WheelSize * 4, 0);
    }

    private void UpdatePreview()
    {
        var c = SelectedColor;
        PreviewBrush.Color = c;
        HexLabel.Text = ColorHex.ToRgbHex(c);
        if (!_suppressEvents)
        {
            ColorChanged?.Invoke(this, c);
        }
    }

    private void PlaceMarker()
    {
        var cx = WheelSize / 2.0;
        var cy = WheelSize / 2.0;
        var maxR = Math.Min(cx, cy) - 2;
        var rad = _hue * Math.PI / 180;
        var r = _sat * maxR;
        var x = cx + Math.Cos(rad) * r;
        var y = cy + Math.Sin(rad) * r;
        Canvas.SetLeft(PickerMarker, x - 7);
        Canvas.SetTop(PickerMarker, y - 7);
        PickerMarker.Visibility = Visibility.Visible;
    }

    private void PickAt(Point pos)
    {
        var cx = WheelSize / 2.0;
        var cy = WheelSize / 2.0;
        var maxR = Math.Min(cx, cy) - 2;
        var dx = pos.X - cx;
        var dy = pos.Y - cy;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > maxR)
        {
            dist = maxR;
            var angle = Math.Atan2(dy, dx);
            dx = Math.Cos(angle) * maxR;
            dy = Math.Sin(angle) * maxR;
        }

        _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        _sat = dist / maxR;
        PlaceMarker();
        UpdatePreview();
    }

    private void Wheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        WheelImage.CaptureMouse();
        PickAt(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        PickAt(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        WheelImage.ReleaseMouseCapture();
    }

    private void Wheel_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressEvents)
        {
            return;
        }

        _val = BrightnessSlider.Value / 100;
        RefreshWheel();
        UpdatePreview();
    }
}
