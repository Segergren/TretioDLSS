using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using TretioDLSS.Core;

namespace TretioDLSS.App;

/// <summary>
/// Single-client WebSocket server. Messages come in over the socket or through the PhotinoX
/// bridge and are both dispatched to <see cref="Received"/>.
/// </summary>
public sealed class MessageService : IDisposable
{
    private readonly int _port;
    private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private HttpListener? _listener;
    private readonly List<WebSocket> _sockets = new();
    private readonly CancellationTokenSource _cts = new();

    public string Token { get; }
    public int Port => _port;
    public event Action<string, JsonElement>? Received;
    public event Action? Connected;

    public MessageService(int port, string? token = null)
    {
        _port = port;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public void AllowOrigin(string origin) => _allowedOrigins.Add(origin.TrimEnd('/'));

    public bool IsConnected
    {
        get { lock (_sockets) return _sockets.Any(x => x.State == WebSocketState.Open); }
    }

    /// <summary>True when a connection request carries the launch token and an allowed origin.</summary>
    public bool Accepts(string? origin, string? token)
    {
        if (!string.Equals(token, Token, StringComparison.Ordinal)) return false;
        if (origin == null) return true;
        return _allowedOrigins.Contains(origin.TrimEnd('/'));
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        Log.Information("WebSocket server on ws://localhost:{Port}", _port);
    }

    private async Task AcceptLoop()
    {
        while (_listener is { IsListening: true } && !_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            if (!ctx.Request.IsWebSocketRequest || !Accepts(ctx.Request.Headers["Origin"], ctx.Request.QueryString["token"]))
            {
                Log.Warning("Refused WebSocket connection from origin {Origin}", ctx.Request.Headers["Origin"]);
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                continue;
            }

            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                lock (_sockets) _sockets.Add(wsCtx.WebSocket);
                _ = Task.Run(() => ReadLoop(wsCtx.WebSocket));
                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebSocket accept failed");
            }
        }
    }

    private async Task ReadLoop(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                var text = sb.ToString();
                sb.Clear();
                if (text == "ping")
                {
                    await SendRaw(socket, "pong");
                    continue;
                }
                Dispatch(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Log.Warning(ex, "WebSocket read failed"); }
        finally
        {
            lock (_sockets) _sockets.Remove(socket);
            socket.Dispose();
        }
    }

    /// <summary>Handles one JSON message { Method, Parameters } from either transport.</summary>
    public void Dispatch(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string method = root.TryGetProperty("Method", out var m) ? m.GetString() ?? "" : "";
            JsonElement parameters = root.TryGetProperty("Parameters", out var p) ? p.Clone() : default;
            Received?.Invoke(method, parameters);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bad frontend message");
        }
    }

    /// <summary>Sends to every connected frontend (the app window, and a browser during UI tests).</summary>
    public Task Send(string method, object? content)
    {
        WebSocket[] targets;
        lock (_sockets) targets = _sockets.Where(x => x.State == WebSocketState.Open).ToArray();
        if (targets.Length == 0) return Task.CompletedTask;
        string json;
        try { json = JsonSerializer.Serialize(new { method, content }, Json.Compact); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not serialize {Method}", method);
            return Task.CompletedTask;
        }
        return Task.WhenAll(targets.Select(t => SendRaw(t, json)));
    }

    private async Task SendRaw(WebSocket socket, string text)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
