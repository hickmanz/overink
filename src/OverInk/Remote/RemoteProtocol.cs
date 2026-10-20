using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverInk.Remote;

public static class RemoteProtocol
{
    public const int Version = 1;
    public const byte BinaryFrameType = 1;
}

public sealed class RemoteJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public sealed record RemoteHello(int V, string? DeviceKey, string? Pair, string? Join, string DeviceName);

public sealed record RemoteWelcome(
    int V,
    string ClientId,
    string? DeviceKey,
    double ScreenW,
    double ScreenH,
    RemoteState State,
    long InkRevision,
    /// <summary>Capability token for the /lib HTTP endpoints. Only sent to the paired owner device.</summary>
    string? LibToken = null);

/// <summary>iPad to PC: take a screenshot into the capture library.</summary>
public sealed record RemoteCapture();

/// <summary>iPad to PC: "live" (receives screen frames) or "doc" (editing a library capture, frames paused).</summary>
public sealed record RemoteViewMode(string Mode);

/// <summary>PC to iPad: the capture was saved to the library.</summary>
public sealed record RemoteCaptured(string Id);

/// <summary>PC to iPad: the capture could not be taken (locked screen, display off, ...).</summary>
public sealed record RemoteCaptureFailed(string Message);

/// <summary>PC to iPad: captures were added, removed or renamed.</summary>
public sealed record RemoteLibraryChanged();

/// <summary>PC to iPad: one capture's image or ink was written (by the PC editor or another save).</summary>
public sealed record RemoteLibraryItemChanged(string Id, string Rev);

public sealed record RemoteState(
    string Tool,
    string Color,
    double Size,
    string Mode,
    double ScreenW,
    double ScreenH,
    bool RegionActive,
    bool PressureSensitive = true,
    double ScreenVeil = 0,
    /// <summary>Colors saved on the PC, shown after the presets. Sent even when empty so clients can clear theirs.</summary>
    IReadOnlyList<string>? CustomColors = null);

public sealed record RemoteSetPressure(bool Enabled);

/// <summary>White veil over the screen (under the ink), 0–0.6. The PC renders it, so it shows in the stream.</summary>
public sealed record RemoteSetVeil(double Value);

public sealed record RemoteAck(Guid Id, long InkRevision);

/// <summary>Strokes (by client stroke id) that no longer exist on the PC; clients drop their vector copies.</summary>
public sealed record RemoteStrokesRemoved(Guid[] Ids);

/// <summary>All ink was cleared on the PC.</summary>
public sealed record RemoteCleared();

public sealed record RemoteError(string Code, string Message);

public sealed record RemoteJoin(string ClientId, string Name, string Color);

public sealed record RemoteLeave(string ClientId);

public sealed record RemoteRosterEntry(string ClientId, string Name, string Color);

public sealed record RemoteRoster(List<RemoteRosterEntry> Participants);

public sealed record RemotePeerStrokeBegin(
    string Author,
    Guid Id,
    string Color,
    double Size,
    double X,
    double Y,
    float P);

public sealed record RemotePeerStrokePoints(string Author, Guid Id, float[] Pts);

public sealed record RemotePeerStrokeEnd(string Author, Guid Id);

public sealed record RemotePeerStrokeCancel(string Author, Guid Id);

public sealed record RemotePeerShapePreview(
    string Author,
    Guid Id,
    string Tool,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string Color,
    double Size);

public sealed record RemotePeerShapeCommit(
    string Author,
    Guid Id,
    string Tool,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string Color,
    double Size);

public sealed record RemoteHover(string ClientId, double X, double Y);

public sealed record RemotePing(long Ms);

public sealed record RemotePong(long Ms);

public sealed record RemoteStrokeBegin(Guid Id, string Color, double Size, double X, double Y, float P);

public sealed record RemoteStrokePoints(Guid Id, float[] Pts);

public sealed record RemoteStrokeEnd(Guid Id);

public sealed record RemoteStrokeCancel(Guid Id);

public sealed record RemoteShapePreview(
    Guid Id,
    string Tool,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string Color,
    double Size);

public sealed record RemoteShapeCommit(
    Guid Id,
    string Tool,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string Color,
    double Size);

public sealed record RemoteErase(float[] Pts, double Size, bool? Whole = null);

public sealed record RemoteStrokeSnapshot(Guid Id, string Color, double Size, float[] Pts, bool IgnorePressure);

/// <summary>Strokes that (re)appeared on the PC with ids the client knows: erase leftovers, undo/redo restores.</summary>
public sealed record RemoteStrokesAdded(List<RemoteStrokeSnapshot> Strokes);

public sealed record RemoteSetTool(string Tool);

public sealed record RemoteSetColor(string Color);

public sealed record RemoteSetSize(double Size);

public sealed record RemoteSimpleCommand(string T);
