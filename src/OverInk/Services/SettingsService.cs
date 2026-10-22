using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OverInk.Models;

namespace OverInk.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public AppSettings Current { get; private set; }

    /// <summary>Fired when remote stream quality knobs change (live update on LAN server).</summary>
    public event Action? RemoteStreamSettingsChanged;

    /// <summary>Fired when what the iPad sees changes (the stream target or the chosen monitor).</summary>
    public event Action? StreamTargetChanged;

    /// <summary>Chooses which part of the desktop is streamed. <paramref name="monitorDeviceName"/> only matters for <see cref="StreamTarget.SpecificMonitor"/>.</summary>
    public void ApplyStreamTarget(StreamTarget target, string? monitorDeviceName = null)
    {
        Current.StreamTarget = target;
        if (target == StreamTarget.SpecificMonitor && !string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            Current.StreamMonitor = monitorDeviceName;
        }

        Save();
        StreamTargetChanged?.Invoke();
        RemoteStreamSettingsChanged?.Invoke();
    }

    public SettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OverInk");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        Current = Load();
        MigrateLegacyRemoteStreamDefaults();
    }

    /// <summary>Previous factory defaults were 2560px / quality 78.</summary>
    private void MigrateLegacyRemoteStreamDefaults()
    {
        if (Current.RemoteMaxEdge != 2560 || Current.RemoteJpegQuality != 78)
        {
            return;
        }

        Current.RemoteMaxEdge = 0;
        Current.RemoteJpegQuality = 95;
        Save();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_path, json);
    }

    public void ApplyRemoteStreamSettings(int maxEdge, int maxFps, int jpegQuality, bool persist = true)
    {
        Current.RemoteMaxEdge = maxEdge <= 0 ? 0 : Math.Clamp(maxEdge, 320, 7680);
        Current.RemoteMaxFps = Math.Clamp(maxFps, 1, 30);
        Current.RemoteJpegQuality = Math.Clamp(jpegQuality, 35, 95);
        if (persist)
        {
            Save();
        }

        RemoteStreamSettingsChanged?.Invoke();
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
