using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Serilog;

namespace TretioDLSS.App;

/// <summary>Serves the embedded frontend build (Resources/wwwroot) to the webview.</summary>
public static class FrontendServer
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    private static IFileProvider? _files;

    public static string Start(int startPort)
    {
        _files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "Resources/wwwroot");
        for (int port = startPort; port < startPort + 100; port++)
        {
            if (!IsPortFree(port)) continue;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => Accept(listener));
            Log.Information("Frontend served at http://localhost:{Port}", port);
            return $"http://localhost:{port}";
        }
        throw new IOException("No free port for the frontend server");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task Accept(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Serve(ctx);
        }
    }

    private static async Task Serve(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            string path = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            if (path.EndsWith('/')) path += "index.html";
            var file = _files!.GetFileInfo(path);
            if (!file.Exists || file.IsDirectory || !ContentTypes.TryGetValue(Path.GetExtension(path), out var type))
            {
                res.StatusCode = 404;
                return;
            }
            res.ContentType = type;
            res.Headers["Cache-Control"] = path.EndsWith(".html") ? "no-cache" : "max-age=31536000, immutable";
            res.ContentLength64 = file.Length;
            await using var stream = file.CreateReadStream();
            await stream.CopyToAsync(res.OutputStream);
        }
        catch (HttpListenerException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Frontend request failed");
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }
}
