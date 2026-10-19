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

    public SettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OverInk");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        Current = Load();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_path, json);
    }

    public void ApplyRemoteStreamSettings(int maxEdge, int maxFps, int jpegQuality, bool persist = true)
    {
        Current.RemoteMaxEdge = Math.Clamp(maxEdge, 320, 7680);
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
