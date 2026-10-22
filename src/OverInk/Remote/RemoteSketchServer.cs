using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OverInk.Editor;
using OverInk.Input;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Services;

namespace OverInk.Remote;

public sealed class RemoteSketchServer : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PairingService _pairing;
    private readonly RemoteStrokeSource _strokeSource;
    private readonly SketchSession _session;
    private readonly OverlayWindow _overlay;
    private readonly FrameStreamer _frameStreamer;
    private readonly RemoteLibraryService _library;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, RemoteConnectedClient> _clients = new();
    private readonly string[] _participantColors = ["#E53935", "#1E88E5", "#43A047", "#FB8C00", "#8E24AA", "#00ACC1", "#FDD835", "#6D4C41"];

    public RemoteSketchServer(
        SettingsService settings,
        PairingService pairing,
        RemoteStrokeSource strokeSource,
        SketchSession session,
        OverlayWindow overlay,
        CaptureLibrary captureLibrary)
    {
        _settings = settings;
        _pairing = pairing;
        _strokeSource = strokeSource;
        _session = session;
        _overlay = overlay;
        _library = new RemoteLibraryService(captureLibrary, () => _settings.Current.RemoteLibraryEnabled);
        _library.Changed += OnLibraryContentChanged;
        _frameStreamer = new FrameStreamer(overlay, () => _settings.Current);
        _frameStreamer.FrameReady += OnFrameReady;
        _settings.RemoteStreamSettingsChanged += () => _frameStreamer.RequestImmediateFrame();
        _overlay.RemoteStrokesRemoved += ids =>
            BroadcastControl(Wrap("strokesRemoved", new RemoteStrokesRemoved(ids.ToArray())), null);
        _overlay.InkCleared += () => BroadcastControl(Wrap("cleared", new RemoteCleared()), null);
        _overlay.RemoteStrokesAdded += OnOverlayStrokesAdded;
    }

    private void OnOverlayStrokesAdded(IReadOnlyList<(Guid Id, System.Windows.Ink.Stroke Stroke)> strokes)
    {
        var streamPixels = StreamTargetResolver.GetStreamPixels(_settings.Current);
        var snapshots = new List<RemoteStrokeSnapshot>(strokes.Count);
        foreach (var (id, stroke) in strokes)
        {
            var points = stroke.StylusPoints;
            var pts = new float[points.Count * 3];
            for (var i = 0; i < points.Count; i++)
            {
                // Stroke points are overlay DIPs; the iPad wants 0-1 fractions of the streamed rectangle.
                var pixel = _overlay.LocalDipsToDesktopPixels(new Point(points[i].X, points[i].Y));
                var (nx, ny) = DesktopLayout.PixelsToNormalized(pixel, streamPixels);
                pts[i * 3] = (float)nx;
                pts[i * 3 + 1] = (float)ny;
                pts[i * 3 + 2] = points[i].PressureFactor;
            }

            snapshots.Add(new RemoteStrokeSnapshot(
                id,
                ColorHex.ToRgbHex(stroke.DrawingAttributes.Color),
                stroke.DrawingAttributes.Width,
                pts,
                stroke.DrawingAttributes.IgnorePressure));
        }

        BroadcastControl(Wrap("strokesAdded", new RemoteStrokesAdded(snapshots)), null);
    }

    public event Action<int>? ClientCountChanged;
    public event Func<string, string, Task<bool>>? JoinApprovalRequired;

    /// <summary>
    /// Takes a screenshot into the library (called on the UI thread). Returns the new capture's id, or an error
    /// message for the iPad when nothing usable was captured.
    /// </summary>
    public Func<(string? Id, string? Error)>? CaptureToLibraryHandler { get; set; }

    private void OnLibraryContentChanged(bool listChanged, IReadOnlyList<(string Id, string Rev)> changed)
    {
        if (listChanged)
        {
            BroadcastToLibraryClients(Wrap("libraryChanged", new RemoteLibraryChanged()));
        }

        foreach (var (id, rev) in changed)
        {
            BroadcastToLibraryClients(Wrap("libraryItemChanged", new RemoteLibraryItemChanged(id, rev)));
        }
    }

    private void BroadcastToLibraryClients(string json)
    {
        foreach (var client in _clients.Values)
        {
            if (client.LibToken is not null)
            {
                client.TryEnqueueControl(json);
            }
        }
    }

    public int ConnectedClients => _clients.Count;
    public bool IsRunning => _app is not null;

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var port = _settings.Current.RemotePort;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}", $"http://0.0.0.0:{port}");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            options.Limits.MaxResponseBufferSize = 32 * 1024 * 1024;
        });

        var embedded = new EmbeddedWwwRootFileProvider();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // WebSocket before static files — Map("/ws") branches can miss upgrade handling with Slim/hosted WPF.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket request expected.").ConfigureAwait(false);
                return;
            }

            if (!RemoteNetworkHelper.IsAllowedRemoteAddress(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await HandleWebSocketAsync(socket, context.Connection.RemoteIpAddress).ConfigureAwait(false);
        });

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = embedded,
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            }
        });

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        _library.Map(app);

        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        _frameStreamer.Start();
    }

    public async Task StopAsync()
    {
        _frameStreamer.Stop();
        foreach (var client in _clients.Values)
        {
            client.Cancel();
        }

        _clients.Clear();
        _library.RevokeAll();
        NotifyClientCountChanged();

        if (_app is null)
        {
            return;
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    public void BroadcastState()
    {
        var state = BuildState();
        var json = Wrap("state", state);
        BroadcastControl(json, excludeClientId: null);
    }

    public void NotifyCaptureRegionChanged()
    {
        _frameStreamer.RequestImmediateFrame();
        BroadcastState();
    }

    public void BroadcastRoster()
    {
        var roster = new RemoteRoster(_clients.Values
            .Select(c => new RemoteRosterEntry(c.ClientId, c.Name, c.Color))
            .ToList());
        var json = Wrap("roster", roster);
        BroadcastControl(json, excludeClientId: null);
    }

    public void Kick(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            client.Cancel();
            NotifyClientCountChanged();
            BroadcastRoster();
        }
    }

    private long _framesCaptured;

    private void OnFrameReady(byte[] frame)
    {
        var n = Interlocked.Increment(ref _framesCaptured);
        if (n == 1 || n % 30 == 0)
        {
            RemoteDebugLog.Write($"frame captured #{n} bytes={frame.Length}");
        }

        foreach (var client in _clients.Values)
        {
            if (client.StreamActive)
            {
                client.TryEnqueueFrame(frame);
            }
        }
    }

    private void NotifyClientCountChanged()
    {
        var count = _clients.Count;
        var handler = ClientCountChanged;
        if (handler is null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => handler(count));
            return;
        }

        handler(count);
    }

    private async Task HandleWebSocketAsync(WebSocket socket, IPAddress? remoteIp)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var client = new RemoteConnectedClient(clientId, socket);
        string? deviceKeyToIssue = null;
        var admitted = false;
        RemoteDebugLog.Write($"WS connect clientId={clientId} remote={remoteIp}");

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                string? message;
                try
                {
                    message = await ReceiveMessageAsync(socket).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RemoteDebugLog.Write($"WS receive error clientId={clientId}: {ex.Message}");
                    break;
                }

                if (message is null)
                {
                    RemoteDebugLog.Write($"WS receive closed clientId={clientId} admitted={admitted}");
                    break;
                }

                if (message.Length == 0)
                {
                    continue;
                }

                if (!admitted)
                {
                    if (!TryParseType(message, out var type) || type != "hello")
                    {
                        await client.SendTextAsync(Wrap("error", new RemoteError("badHello", "First message must be hello."))).ConfigureAwait(false);
                        break;
                    }

                    var hello = RemoteJson.Deserialize<RemoteHello>(message);
                    if (hello is null || hello.V != RemoteProtocol.Version)
                    {
                        await client.SendTextAsync(Wrap("error", new RemoteError("versionMismatch", "Unsupported protocol version."))).ConfigureAwait(false);
                        break;
                    }

                    var auth = await AuthenticateHelloAsync(hello, clientId, remoteIp).ConfigureAwait(false);
                    if (!auth.ok)
                    {
                        RemoteDebugLog.Write($"WS auth failed clientId={clientId} code={auth.code} msg={auth.message}");
                        await client.SendTextAsync(Wrap("error", new RemoteError(auth.code, auth.message))).ConfigureAwait(false);
                        break;
                    }

                    deviceKeyToIssue = auth.deviceKey;
                    client.Name = string.IsNullOrWhiteSpace(hello.DeviceName) ? "Guest" : hello.DeviceName.Trim();
                    client.Color = PickColor();
                    admitted = true;

                    if (_clients.Count >= _settings.Current.RemoteMaxClients)
                    {
                        await client.SendTextAsync(Wrap("error", new RemoteError("sessionFull", "Session is full."))).ConfigureAwait(false);
                        break;
                    }

                    // Only the paired owner device gets library access; session-link participants never do.
                    string? libToken = null;
                    if (auth.libraryDeviceId is not null && _settings.Current.RemoteLibraryEnabled)
                    {
                        libToken = _library.IssueToken(auth.libraryDeviceId, clientId);
                        client.LibToken = libToken;
                    }

                    var welcome = await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var stream = StreamSizeDips();
                        return new RemoteWelcome(
                            RemoteProtocol.Version,
                            clientId,
                            deviceKeyToIssue,
                            stream.Width,
                            stream.Height,
                            BuildState(),
                            _overlay.InkRevision,
                            libToken);
                    }).Task.ConfigureAwait(false);

                    await client.SendTextAsync(Wrap("welcome", welcome)).ConfigureAwait(false);
                    RemoteDebugLog.Write($"WS welcome sent clientId={clientId} name={client.Name}");

                    _clients[clientId] = client;
                    NotifyClientCountChanged();
                    _ = Task.Run(() => client.SendLoopAsync());
                    client.EnableStreaming();
                    BroadcastRoster();
                    continue;
                }

                client.EnableStreaming();

                try
                {
                    await HandleClientMessageAsync(client, message).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RemoteDebugLog.Write($"WS handler error clientId={clientId}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"WS fatal clientId={clientId}: {ex}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _library.RevokeClient(clientId);
            NotifyClientCountChanged();
            BroadcastRoster();
            client.Cancel();
            RemoteDebugLog.Write($"WS cleanup clientId={clientId} state={socket.State}");
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates a hello. <c>libraryDeviceId</c> is non-null only for devices allowed to use the capture library
    /// (paired by QR code, or the PC itself); devices that joined through a session link never get one.
    /// </summary>
    private async Task<(bool ok, string code, string message, string? deviceKey, string? libraryDeviceId)> AuthenticateHelloAsync(
        RemoteHello hello,
        string clientId,
        IPAddress? remoteIp)
    {
        if (_pairing.TryConsumePairToken(hello.Pair))
        {
            var key = _pairing.IssueDeviceKey(hello.DeviceName, canUseLibrary: true);
            return (true, string.Empty, string.Empty, key, _pairing.FindByDeviceKey(key)?.Id);
        }

        var known = _pairing.FindByDeviceKey(hello.DeviceKey);
        if (known is not null)
        {
            return (true, string.Empty, string.Empty, null, known.CanUseLibrary ? known.Id : null);
        }

        var joinOk = _pairing.TryValidateJoinToken(hello.Join);
        if (!joinOk)
        {
            if (remoteIp is not null && IPAddress.IsLoopback(remoteIp))
            {
                var key = _pairing.IssueDeviceKey(hello.DeviceName, canUseLibrary: true);
                return (true, string.Empty, string.Empty, key, _pairing.FindByDeviceKey(key)?.Id);
            }

            return (false, "badToken", "Invalid join or pair token.", null, null);
        }

        if (_settings.Current.RemoteRequireJoinApproval)
        {
            var approver = JoinApprovalRequired;
            if (approver is null)
            {
                return (false, "approvalRequired", "Host approval is required.", null, null);
            }

            var approved = await approver(clientId, hello.DeviceName).ConfigureAwait(false);
            if (!approved)
            {
                return (false, "denied", "Join denied by host.", null, null);
            }
        }

        return (true, string.Empty, string.Empty, _pairing.IssueDeviceKey(hello.DeviceName, canUseLibrary: false), null);
    }

    private async Task HandleClientMessageAsync(RemoteConnectedClient client, string json)
    {
        if (!TryParseType(json, out var type))
        {
            return;
        }

        switch (type)
        {
            case "strokeBegin":
                await DispatchStrokeBeginAsync(client, json).ConfigureAwait(false);
                break;
            case "strokePoints":
                await DispatchStrokePointsAsync(client, json).ConfigureAwait(false);
                break;
            case "strokeEnd":
                await DispatchStrokeEndAsync(client, json).ConfigureAwait(false);
                break;
            case "strokeCancel":
                await DispatchStrokeCancelAsync(client, json).ConfigureAwait(false);
                break;
            case "shapePreview":
                await DispatchShapePreviewAsync(client, json).ConfigureAwait(false);
                break;
            case "shapeCommit":
                await DispatchShapeCommitAsync(client, json).ConfigureAwait(false);
                break;
            case "erase":
                await DispatchEraseAsync(client, json).ConfigureAwait(false);
                break;
            case "setTool":
                ApplySetTool(json);
                break;
            case "setColor":
                ApplySetColor(json);
                break;
            case "saveColor":
                ApplySavedColorChange(json, add: true);
                break;
            case "removeColor":
                ApplySavedColorChange(json, add: false);
                break;
            case "setSize":
                ApplySetSize(json);
                break;
            case "setPressure":
                ApplySetPressure(json);
                break;
            case "setVeil":
                ApplySetVeil(json);
                break;
            case "undo":
                await Application.Current.Dispatcher.InvokeAsync(() => _overlay.Undo(client.ClientId)).Task.ConfigureAwait(false);
                break;
            case "redo":
                await Application.Current.Dispatcher.InvokeAsync(() => _overlay.Redo(client.ClientId)).Task.ConfigureAwait(false);
                break;
            case "clear":
                await Application.Current.Dispatcher.InvokeAsync(_overlay.ClearAll).Task.ConfigureAwait(false);
                break;
            case "ping":
                if (RemoteJson.Deserialize<RemotePing>(json) is { } ping)
                {
                    await SendJsonAsync(client, Wrap("pong", new RemotePong(ping.Ms))).ConfigureAwait(false);
                }

                break;
            case "hover":
                BroadcastControl(json, client.ClientId);
                break;
            case "viewMode":
                ApplyViewMode(client, json);
                break;
            case "capture":
                await HandleCaptureAsync(client).ConfigureAwait(false);
                break;
        }
    }

    private void ApplyViewMode(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteViewMode>(json);
        if (msg is null)
        {
            return;
        }

        var doc = string.Equals(msg.Mode, "doc", StringComparison.OrdinalIgnoreCase);
        // Editing a library capture needs no live frames; pausing them saves Wi-Fi and PC CPU.
        client.DocMode = doc;
        if (!doc)
        {
            _frameStreamer.RequestImmediateFrame();
        }
    }

    private async Task HandleCaptureAsync(RemoteConnectedClient client)
    {
        var handler = CaptureToLibraryHandler;
        if (client.LibToken is null || !_settings.Current.RemoteLibraryEnabled || handler is null)
        {
            await SendJsonAsync(client, Wrap("captureFailed", new RemoteCaptureFailed("Capture is not available for this device."))).ConfigureAwait(false);
            return;
        }

        (string? Id, string? Error) result;
        try
        {
            result = await Application.Current.Dispatcher.InvokeAsync(handler).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"WS capture failed clientId={client.ClientId}: {ex.Message}");
            result = (null, "Capture failed.");
        }

        if (result.Id is not null)
        {
            await SendJsonAsync(client, Wrap("captured", new RemoteCaptured(result.Id))).ConfigureAwait(false);
        }
        else
        {
            await SendJsonAsync(client, Wrap("captureFailed", new RemoteCaptureFailed(result.Error ?? "Capture failed."))).ConfigureAwait(false);
        }
    }

    private void ApplySetTool(string json)
    {
        var msg = RemoteJson.Deserialize<RemoteSetTool>(json);
        if (msg is null || !Enum.TryParse<SketchTool>(msg.Tool, true, out var tool) || tool == SketchTool.Select)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _session.SetTool(tool);
            _session.PersistSettings();
            BroadcastState();
        });
    }

    private void ApplySetColor(string json)
    {
        var msg = RemoteJson.Deserialize<RemoteSetColor>(json);
        if (msg is null)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _session.SetColor(ColorHex.Parse(msg.Color));
            _session.PersistSettings();
            BroadcastState();
        });
    }

    /// <summary>Adds/removes a saved custom color. The palette's Changed event saves settings and rebroadcasts state to every client.</summary>
    private void ApplySavedColorChange(string json, bool add)
    {
        var msg = RemoteJson.Deserialize<RemoteSetColor>(json);
        if (msg is null || !ColorHex.TryParse(msg.Color, out var color))
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (add)
            {
                _session.CustomColors.Add(color);
            }
            else
            {
                _session.CustomColors.Remove(ColorHex.ToRgbHex(color));
            }
        });
    }

    private void ApplySetSize(string json)
    {
        var msg = RemoteJson.Deserialize<RemoteSetSize>(json);
        if (msg is null)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _session.SetThickness(msg.Size);
            _session.PersistSettings();
            BroadcastState();
        });
    }

    private void ApplySetPressure(string json)
    {
        var msg = RemoteJson.Deserialize<RemoteSetPressure>(json);
        if (msg is null)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _session.SetPressureSensitive(msg.Enabled);
            _session.PersistSettings();
            BroadcastState();
        });
    }

    private void ApplySetVeil(string json)
    {
        var msg = RemoteJson.Deserialize<RemoteSetVeil>(json);
        if (msg is null || double.IsNaN(msg.Value))
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _session.SetScreenVeil(msg.Value);
            _session.PersistSettings();
            BroadcastState();
        });
        _frameStreamer.RequestImmediateFrame();
    }

    private async Task DispatchStrokeBeginAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteStrokeBegin>(json);
        if (msg is null)
        {
            RemoteDebugLog.Write(
                $"WS strokeBegin parse failed clientId={client.ClientId} json={json[..Math.Min(json.Length, 160)]}");
            return;
        }

        RemoteDebugLog.Write($"WS strokeBegin clientId={client.ClientId} id={msg.Id}");

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dips = ToDips(msg.X, msg.Y);
            var color = ColorHex.Parse(msg.Color);
            var thickness = Math.Max(1, msg.Size > 0 ? msg.Size : _session.PenThickness);
            var attrs = new DrawingAttributes
            {
                Color = color,
                Width = thickness,
                Height = thickness,
                FitToCurve = false,
                IgnorePressure = !_session.PressureSensitive
            };
            _strokeSource.RaiseStrokeBegan(new RemoteStrokeBeginEventArgs
            {
                Id = msg.Id,
                AuthorId = client.ClientId,
                Attributes = attrs,
                FirstPointDips = dips,
                Pressure = msg.P
            });
        }, DispatcherPriority.Input);

        BroadcastPeer(client.ClientId, "peerStrokeBegin", new RemotePeerStrokeBegin(
            client.ClientId, msg.Id, msg.Color, msg.Size, msg.X, msg.Y, msg.P));
    }

    private async Task DispatchStrokePointsAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteStrokePoints>(json);
        if (msg is null || msg.Pts.Length < 3)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var points = new StylusPointCollection();
            for (var i = 0; i + 2 < msg.Pts.Length; i += 3)
            {
                var dip = ToDips(msg.Pts[i], msg.Pts[i + 1]);
                points.Add(new StylusPoint(dip.X, dip.Y, msg.Pts[i + 2]));
            }

            _strokeSource.RaiseStrokePoints(new RemoteStrokePointsEventArgs
            {
                Id = msg.Id,
                Points = points
            });
        }, DispatcherPriority.Input);

        BroadcastPeer(client.ClientId, "peerStrokePoints", new RemotePeerStrokePoints(client.ClientId, msg.Id, msg.Pts));
    }

    private async Task DispatchStrokeEndAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteStrokeEnd>(json);
        if (msg is null)
        {
            RemoteDebugLog.Write($"WS strokeEnd parse failed clientId={client.ClientId} json={json[..Math.Min(json.Length, 120)]}");
            return;
        }

        long revision = 0;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _strokeSource.RaiseStrokeEnded(new RemoteStrokeIdEventArgs { Id = msg.Id, AuthorId = client.ClientId });
            revision = _overlay.InkRevision;
        }, DispatcherPriority.Input);

        await SendJsonAsync(client, Wrap("ack", new RemoteAck(msg.Id, revision))).ConfigureAwait(false);
        BroadcastPeer(client.ClientId, "peerStrokeEnd", new RemotePeerStrokeEnd(client.ClientId, msg.Id));
    }

    private async Task DispatchStrokeCancelAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteStrokeCancel>(json);
        if (msg is null)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _strokeSource.RaiseStrokeCancelled(new RemoteStrokeIdEventArgs { Id = msg.Id, AuthorId = client.ClientId });
        }, DispatcherPriority.Input);

        BroadcastPeer(client.ClientId, "peerStrokeCancel", new RemotePeerStrokeCancel(client.ClientId, msg.Id));
    }

    private async Task DispatchShapePreviewAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteShapePreview>(json);
        if (msg is null || !Enum.TryParse<SketchTool>(msg.Tool, true, out var tool) || tool == SketchTool.Select)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _strokeSource.RaiseShapePreview(BuildShapeArgs(client.ClientId, msg, tool, preview: true));
        }, DispatcherPriority.Input);

        BroadcastPeer(client.ClientId, "peerShapePreview", new RemotePeerShapePreview(
            client.ClientId, msg.Id, msg.Tool, msg.X1, msg.Y1, msg.X2, msg.Y2, msg.Color, msg.Size));
    }

    private async Task DispatchShapeCommitAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteShapeCommit>(json);
        if (msg is null || !Enum.TryParse<SketchTool>(msg.Tool, true, out var tool) || tool == SketchTool.Select)
        {
            return;
        }

        long revision = 0;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _strokeSource.RaiseShapeCommitted(BuildShapeArgs(client.ClientId, msg, tool, preview: false));
            revision = _overlay.InkRevision;
        }, DispatcherPriority.Input);

        await SendJsonAsync(client, Wrap("ack", new RemoteAck(msg.Id, revision))).ConfigureAwait(false);
        BroadcastPeer(client.ClientId, "peerShapeCommit", new RemotePeerShapeCommit(
            client.ClientId, msg.Id, msg.Tool, msg.X1, msg.Y1, msg.X2, msg.Y2, msg.Color, msg.Size));
    }

    private async Task DispatchEraseAsync(RemoteConnectedClient client, string json)
    {
        var msg = RemoteJson.Deserialize<RemoteErase>(json);
        if (msg is null || msg.Pts.Length < 2)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var points = new List<Point>();
            for (var i = 0; i + 1 < msg.Pts.Length; i += 2)
            {
                points.Add(ToDips(msg.Pts[i], msg.Pts[i + 1]));
            }

            _strokeSource.RaiseErase(new RemoteEraseEventArgs
            {
                AuthorId = client.ClientId,
                PointsDips = points.ToArray(),
                EraserSize = msg.Size,
                Whole = msg.Whole == true
            });
        }, DispatcherPriority.Input);

    }

    private RemoteShapeEventArgs BuildShapeArgs(string authorId, RemoteShapePreview msg, SketchTool tool, bool preview)
    {
        return new RemoteShapeEventArgs
        {
            Id = msg.Id,
            AuthorId = authorId,
            Tool = tool,
            StartDips = ToDips(msg.X1, msg.Y1),
            EndDips = ToDips(msg.X2, msg.Y2),
            Color = ColorHex.Parse(msg.Color),
            Thickness = msg.Size,
            IsPreview = preview
        };
    }

    private RemoteShapeEventArgs BuildShapeArgs(string authorId, RemoteShapeCommit msg, SketchTool tool, bool preview)
    {
        return new RemoteShapeEventArgs
        {
            Id = msg.Id,
            AuthorId = authorId,
            Tool = tool,
            StartDips = ToDips(msg.X1, msg.Y1),
            EndDips = ToDips(msg.X2, msg.Y2),
            Color = ColorHex.Parse(msg.Color),
            Thickness = msg.Size,
            IsPreview = preview
        };
    }

    /// <summary>iPad 0-1 position to overlay DIPs: through the streamed rectangle (desktop pixels) and the overlay's origin and scale.</summary>
    private Point ToDips(double nx, double ny)
    {
        var pixel = CoordinateMapper.NormalizedToPixels(nx, ny, StreamTargetResolver.GetStreamPixels(_settings.Current));
        return _overlay.DesktopPixelsToLocalDips(pixel);
    }

    /// <summary>
    /// Size of the streamed rectangle in overlay DIPs. The iPad uses it as the PC's logical screen size, so pen
    /// widths (DIPs) keep the same proportion to the frame whatever the monitor's scaling.
    /// </summary>
    private Size StreamSizeDips()
    {
        var pixels = StreamTargetResolver.GetStreamPixels(_settings.Current);
        var scale = _overlay.DpiScale;
        return new Size(pixels.Width / scale, pixels.Height / scale);
    }

    private RemoteState BuildState()
    {
        var stream = StreamSizeDips();
        var streamPixels = StreamTargetResolver.GetStreamPixels(_settings.Current);
        var monitors = StreamTargetResolver
            .GetStreamMonitorRects(streamPixels, MonitorHelper.GetAllMonitors())
            .Select(m => new RemoteMonitorRect(m.Normalized.X, m.Normalized.Y, m.Normalized.Width, m.Normalized.Height, m.IsPrimary))
            .ToArray();
        return new RemoteState(
            _session.Tool.ToString(),
            ColorHex.ToRgbHex(_session.PenColor),
            _session.PenThickness,
            _session.Mode.ToString(),
            stream.Width,
            stream.Height,
            _settings.Current.RemoteRegionEnabled,
            _session.PressureSensitive,
            _session.ScreenVeil,
            _session.CustomColors.Colors.ToArray(),
            StreamTargetResolver.GetStreamEpoch(streamPixels),
            monitors);
    }

    private string PickColor()
    {
        var used = _clients.Values.Select(c => c.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var color in _participantColors)
        {
            if (!used.Contains(color))
            {
                return color;
            }
        }

        return _participantColors[Random.Shared.Next(_participantColors.Length)];
    }

    private void BroadcastPeer<T>(string excludeClientId, string type, T payload)
    {
        var json = Wrap(type, payload);
        BroadcastControl(json, excludeClientId);
    }

    private void BroadcastControl(string json, string? excludeClientId)
    {
        foreach (var client in _clients.Values)
        {
            if (excludeClientId is not null && client.ClientId == excludeClientId)
            {
                continue;
            }

            client.TryEnqueueControl(json);
        }
    }

    private static async Task SendJsonAsync(RemoteConnectedClient client, string json)
    {
        client.TryEnqueueControl(json);
        await Task.CompletedTask;
    }

    private static string Wrap<T>(string type, T payload)
    {
        using var doc = JsonSerializer.SerializeToDocument(payload, RemoteJson.Options);
        var obj = doc.RootElement.Clone();
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("t", type);
            foreach (var prop in obj.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool TryParseType(string json, out string type)
    {
        type = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("t", out var tProp))
            {
                type = tProp.GetString() ?? string.Empty;
                return !string.IsNullOrEmpty(type);
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static async Task<string?> ReceiveMessageAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        if (result.MessageType == WebSocketMessageType.Binary)
        {
            RemoteDebugLog.Write("WS ignored unexpected binary message from client");
            return string.Empty;
        }

        return null;
    }

    public void Dispose()
    {
        _library.Dispose();
        _frameStreamer.Dispose();
        _ = StopAsync();
    }

    private sealed class RemoteConnectedClient
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Threading.Channels.Channel<byte[]> _frames =
            System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });

        private readonly System.Threading.Channels.Channel<string> _control =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        public RemoteConnectedClient(string clientId, WebSocket socket)
        {
            ClientId = clientId;
            Socket = socket;
            Name = "Guest";
            Color = "#FFFFFF";
        }

        public string ClientId { get; }
        public WebSocket Socket { get; }
        public string Name { get; set; }
        public string Color { get; set; }

        public bool StreamEnabled { get; private set; }

        /// <summary>Capability token for /lib endpoints; null unless this is the paired owner device.</summary>
        public string? LibToken { get; set; }

        /// <summary>True while the iPad is editing a library capture; live frames are not sent.</summary>
        public volatile bool DocMode;

        public bool StreamActive => StreamEnabled && !DocMode;

        public void EnableStreaming()
        {
            if (StreamEnabled)
            {
                return;
            }

            StreamEnabled = true;
            RemoteDebugLog.Write($"WS stream enabled clientId={ClientId}");
        }

        public void ScheduleStreamEnable()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                EnableStreaming();
            });
        }

        public void TryEnqueueFrame(byte[] frame) => _frames.Writer.TryWrite(frame);
        public void TryEnqueueControl(string json) => _control.Writer.TryWrite(json);

        public void Cancel() => _cts.Cancel();

        public async Task SendTextAsync(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await SendPayloadAsync(bytes, WebSocketMessageType.Text, _cts.Token).ConfigureAwait(false);
        }

        private async Task SendPayloadAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, CancellationToken token)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            await _sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Socket.SendAsync(payload, type, endOfMessage: true, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RemoteDebugLog.Write(
                    $"WS send failed clientId={ClientId} type={type} bytes={payload.Length}: {ex.Message}");
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public async Task SendLoopAsync()
        {
            var token = _cts.Token;
            var framesSent = 0L;
            try
            {
                while (!token.IsCancellationRequested && Socket.State == WebSocketState.Open)
                {
                    var sent = false;

                    if (StreamActive && _frames.Reader.TryRead(out var frame))
                    {
                        await SendPayloadAsync(frame, WebSocketMessageType.Binary, token).ConfigureAwait(false);
                        framesSent++;
                        if (framesSent == 1 || framesSent % 30 == 0)
                        {
                            RemoteDebugLog.Write($"WS frame sent clientId={ClientId} #{framesSent} bytes={frame.Length}");
                        }

                        sent = true;
                    }

                    if (_control.Reader.TryRead(out var json))
                    {
                        await SendPayloadAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, token)
                            .ConfigureAwait(false);
                        sent = true;
                    }

                    if (!StreamActive)
                    {
                        while (_frames.Reader.TryRead(out _))
                        {
                            // Drop frames until the client session is ready for video (or is back in live mode).
                        }
                    }

                    if (sent)
                    {
                        continue;
                    }

                    var controlWait = _control.Reader.WaitToReadAsync(token).AsTask();
                    var frameWait = _frames.Reader.WaitToReadAsync(token).AsTask();
                    await Task.WhenAny(controlWait, frameWait).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RemoteDebugLog.Write($"WS send loop ended clientId={ClientId}: {ex.Message}");
            }
        }
    }
}
