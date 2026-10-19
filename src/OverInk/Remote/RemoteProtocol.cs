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
    long InkRevision);

public sealed record RemoteState(
    string Tool,
    string Color,
    double Size,
    string Mode,
    double ScreenW,
    double ScreenH,
    bool RegionActive,
    bool PressureSensitive = true);

public sealed record RemoteSetPressure(bool Enabled);

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
