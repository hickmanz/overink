using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverInk.Models;
using OverInk.Services;

namespace OverInk.Remote;

public partial class RemoteStreamSettingsControl : UserControl
{
    private SettingsService? _settings;
    private bool _loading;

    public RemoteStreamSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // The streamed monitor changes when the toolbar is dragged to another one; keep the summary honest.
            StreamTargetResolver.FollowMonitorChanged += OnFollowMonitorChanged;
            RefreshSummary();
        };
        Unloaded += (_, _) => StreamTargetResolver.FollowMonitorChanged -= OnFollowMonitorChanged;
    }

    private void OnFollowMonitorChanged() => Dispatcher.BeginInvoke(RefreshSummary);

    private void TargetFollow_Click(object sender, RoutedEventArgs e)
    {
        _settings?.ApplyStreamTarget(StreamTarget.FollowToolbar);
        RefreshSummary();
    }

    private void TargetAll_Click(object sender, RoutedEventArgs e)
    {
        _settings?.ApplyStreamTarget(StreamTarget.AllMonitors);
        RefreshSummary();
    }

    private void TargetMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        // Start from the monitor already chosen, otherwise the one the toolbar is on.
        var chosen = MonitorHelper.FindByDeviceName(_settings.Current.StreamMonitor)
            ?? MonitorHelper.FindByDeviceName(StreamTargetResolver.FollowMonitorDevice)
            ?? MonitorHelper.GetPrimaryMonitorGeometry();
        _settings.ApplyStreamTarget(StreamTarget.SpecificMonitor, chosen.DeviceName);
        RefreshSummary();
    }

    private void RefreshTargetUi()
    {
        if (_settings is null || TargetFollowButton is null)
        {
            return;
        }

        var s = _settings.Current;
        MarkSelected(TargetFollowButton, s.StreamTarget == StreamTarget.FollowToolbar);
        MarkSelected(TargetMonitorButton, s.StreamTarget == StreamTarget.SpecificMonitor);
        MarkSelected(TargetAllButton, s.StreamTarget == StreamTarget.AllMonitors);

        MonitorListPanel.Children.Clear();
        var monitors = MonitorHelper.GetAllMonitors();
        MonitorListPanel.Visibility = s.StreamTarget == StreamTarget.SpecificMonitor && monitors.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (MonitorListPanel.Visibility == Visibility.Visible)
        {
            var current = MonitorHelper.FindByDeviceName(s.StreamMonitor)?.DeviceName;
            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                var button = new Button
                {
                    Content = $"Screen {i + 1}{(monitor.IsPrimary ? " (main)" : string.Empty)} · " +
                              $"{(int)monitor.Pixels.Width}×{(int)monitor.Pixels.Height}",
                    Style = (Style)FindResource("PresetButton"),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                MarkSelected(button, string.Equals(monitor.DeviceName, current, StringComparison.OrdinalIgnoreCase));
                var device = monitor.DeviceName;
                button.Click += (_, _) =>
                {
                    _settings?.ApplyStreamTarget(StreamTarget.SpecificMonitor, device);
                    RefreshSummary();
                };
                MonitorListPanel.Children.Add(button);
            }
        }

        TargetHintText.Text = s.RemoteRegionEnabled
            ? "A capture region is set, so the tablet sees just that area. Clear the region to use this choice."
            : s.StreamTarget switch
            {
                StreamTarget.AllMonitors => "Everything you can draw on is streamed as one wide picture. A very wide desktop is heavy on Wi‑Fi; lower the max long edge if it stutters.",
                StreamTarget.SpecificMonitor => monitors.Count > 1
                    ? "The tablet always sees the screen picked above. Ink you draw on other screens won't appear there."
                    : "Only one screen is connected.",
                _ => monitors.Count > 1
                    ? "The tablet sees whichever screen the toolbar is on. Drag the toolbar to another screen to switch."
                    : "The tablet sees your screen."
            };
    }

    private void MarkSelected(Button button, bool selected)
    {
        button.Background = selected ? (Brush)FindResource("AccentSoftBrush") : (Brush)new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20));
        button.BorderBrush = selected ? (Brush)FindResource("AccentBrush") : (Brush)new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    }

    public void Bind(SettingsService settings)
    {
        _settings = settings;
        LoadFromSettings();
        RefreshSummary();
    }

    private int MonitorLongEdge =>
        _settings is null ? 1920 : RemoteStreamEstimator.Estimate(_settings.Current).MonitorLongEdge;

    private void LoadFromSettings()
    {
        if (_settings is null)
        {
            return;
        }

        _loading = true;
        var s = _settings.Current;
        var est = RemoteStreamEstimator.Estimate(s);
        // "Auto" shows what is really streamed (for the whole desktop that is capped below native).
        var edgeForSlider = s.RemoteMaxEdge <= 0 ? RemoteStreamEstimator.GetEffectiveMaxEdge(s) : s.RemoteMaxEdge;
        MaxEdgeSlider.Value = Math.Clamp(edgeForSlider, (int)MaxEdgeSlider.Minimum, (int)MaxEdgeSlider.Maximum);
        FpsSlider.Value = Math.Clamp(s.RemoteMaxFps, (int)FpsSlider.Minimum, (int)FpsSlider.Maximum);
        QualitySlider.Value = Math.Clamp(s.RemoteJpegQuality, (int)QualitySlider.Minimum, (int)QualitySlider.Maximum);
        _loading = false;
    }

    private void ApplySliders()
    {
        if (_loading || _settings is null || !IsLoaded)
        {
            return;
        }

        _settings.ApplyRemoteStreamSettings(
            (int)MaxEdgeSlider.Value,
            (int)FpsSlider.Value,
            (int)QualitySlider.Value);
        RefreshSummary();
    }

    public void RefreshSummary()
    {
        if (_settings is null || MonitorText is null)
        {
            return;
        }

        RefreshTargetUi();
        var est = RemoteStreamEstimator.Estimate(_settings.Current);
        MonitorText.Text =
            $"{est.MonitorLabel} · {est.MonitorPixelWidth}×{est.MonitorPixelHeight} px " +
            $"(scale {est.ScalePercent:0}%, {est.MonitorDipWidth}×{est.MonitorDipHeight} dp)";
        StreamText.Text =
            $"Stream {est.StreamWidth}×{est.StreamHeight} · {_settings.Current.RemoteMaxFps} fps · " +
            $"JPEG {_settings.Current.RemoteJpegQuality}";
        GuidanceText.Text = est.Guidance;
        MaxEdgeValue.Text = $"{(int)MaxEdgeSlider.Value} px  ·  monitor {est.MonitorLongEdge} px";
        FpsValue.Text = $"{(int)FpsSlider.Value} fps";
        QualityValue.Text = $"{(int)QualitySlider.Value}";
        BandwidthText.Text = $"≈ {est.EstimatedKbps:0} Kbps LAN (~{est.EstimatedKbps / 8:0} KB/s)";
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplySliders();

    private void ApplyPreset((int MaxEdge, int Fps, int Quality) preset)
    {
        _loading = true;
        MaxEdgeSlider.Value = Math.Clamp(preset.MaxEdge, (int)MaxEdgeSlider.Minimum, (int)MaxEdgeSlider.Maximum);
        FpsSlider.Value = preset.Fps;
        QualitySlider.Value = preset.Quality;
        _loading = false;
        ApplySliders();
    }

    private void PresetEco_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetEco());

    /// <summary>A wide multi-monitor image needs a longer edge for the same sharpness per monitor.</summary>
    private int PresetWidthFactor =>
        _settings is { Current: { StreamTarget: StreamTarget.AllMonitors, RemoteRegionEnabled: false } } ? 2 : 1;

    private void PresetBalanced_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetBalanced(MonitorLongEdge, PresetWidthFactor));

    private void PresetSharp_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetSharp(MonitorLongEdge, PresetWidthFactor));

    private void PresetNative_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetNative(MonitorLongEdge));
}
