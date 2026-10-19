using System.Windows;
using System.Windows.Controls;
using OverInk.Services;

namespace OverInk.Remote;

public partial class RemoteStreamSettingsControl : UserControl
{
    private SettingsService? _settings;
    private bool _loading;

    public RemoteStreamSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshSummary();
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
        var edgeForSlider = s.RemoteMaxEdge <= 0 ? est.MonitorLongEdge : s.RemoteMaxEdge;
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

    private void PresetBalanced_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetBalanced(MonitorLongEdge));

    private void PresetSharp_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetSharp(MonitorLongEdge));

    private void PresetNative_Click(object sender, RoutedEventArgs e) =>
        ApplyPreset(RemoteStreamEstimator.PresetNative(MonitorLongEdge));
}
