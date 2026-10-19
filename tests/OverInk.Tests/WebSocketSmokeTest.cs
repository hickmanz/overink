using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OverInk.Remote;
using Xunit;

namespace OverInk.Tests;

public class WebSocketSmokeTest
{
    [Fact]
    public async Task WebSocket_middleware_accepts_upgrade_on_ws_path()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (text.Contains("\"t\":\"hello\"", StringComparison.Ordinal))
            {
                var reply = "{\"t\":\"welcome\",\"clientId\":\"test\",\"screenW\":100,\"screenH\":100,\"inkRevision\":0}";
                var bytes = Encoding.UTF8.GetBytes(reply);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
        });

        await app.StartAsync();
        var address = app.Urls.First();
        var uri = new Uri($"{address.Replace("http://", "ws://")}/ws");

        using var client = new ClientWebSocket();
        await client.ConnectAsync(uri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, client.State);

        var hello = Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"v\":1,\"deviceName\":\"test\"}");
        await client.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        var received = await client.ReceiveAsync(buffer, CancellationToken.None);
        var json = Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Contains("welcome", json, StringComparison.Ordinal);

        await app.StopAsync();
        await app.DisposeAsync();
    }
}
