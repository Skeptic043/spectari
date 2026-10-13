using System.Net;
using System.Text.Json;

namespace Spectari.Server;

/// <summary>
/// Minimal HttpListener server: serves the viewer page from wwwroot and upgrades
/// /ws to a WebSocket handled by the Broadcaster. No ASP.NET dependency.
/// Media requires the per-session key when enabled. Viewer shells and status
/// stay key-free so players can recover through the separate bootstrap route.
/// A null broadcaster runs the server in idle mode: pages are served ungated
/// (they reveal nothing), /api/stats reports state "idle", and /ws refuses -
/// this is the holding page shown while the app is open but not streaming, so
/// tabs opened early connect themselves once a stream starts.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Broadcaster? _broadcaster;
    private readonly string _wwwroot;
    private readonly string? _viewKey;
    private readonly string _idleName;
    private readonly ViewerAccessPolicy _access;
    public string BoundPrefix { get; }

    /// <summary>True when only the localhost prefix could be bound (no URL ACL for
    /// this port), so this server can answer loopback only. Lets a caller advertise
    /// a reachable link instead of a network address the server would reject.</summary>
    public bool LocalOnly => BoundPrefix.Contains("localhost");

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    public WebServer(int port, Broadcaster? broadcaster, string? viewKey = null, string? idleName = null)
        : this(port, broadcaster, viewKey, idleName, LocalViewerAuthority.Create(port)) { }

    internal WebServer(int port, Broadcaster? broadcaster, string? viewKey, string? idleName,
        ViewerAccessPolicy access, bool loopbackOnly = false)
    {
        _access = access;
        _broadcaster = broadcaster;
        _viewKey = viewKey;
        _idleName = string.IsNullOrWhiteSpace(idleName) ? Environment.MachineName : idleName.Trim();
        _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // A failed Start() poisons the HttpListener, so each attempt gets a fresh one.
        // The wildcard needs a urlacl (or admin); localhost always works for this
        // user - so falling through to localhost means "no remote access yet",
        // while failing BOTH means the port itself is taken by another process.
        (_listener, BoundPrefix) = (loopbackOnly ? null : TryBind($"http://+:{port}/")) ?? TryBind($"http://localhost:{port}/")
            ?? throw new InvalidOperationException(DescribeBindFailure(port));
        if (BoundPrefix.Contains("localhost"))
        {
            Console.WriteLine(_broadcaster is null
                ? $"[http] holding page is localhost-only for now; port {port} opens up via Open port or setup.bat."
                : $"[http] WARNING: bound to localhost only. Run setup.bat {port} as administrator, then restart the stream.");
        }
    }

    private static (HttpListener, string)? TryBind(string prefix)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(prefix);
            listener.Start();
            return (listener, prefix);
        }
        catch (HttpListenerException)
        {
            try { listener.Close(); } catch { }
            return null;
        }
    }

    /// <summary>Turns a total bind failure into an actionable message: name the
    /// likely culprit (another Spectari answers /api/stats) so the user closes
    /// the right thing instead of force-quitting blindly.</summary>
    private static string DescribeBindFailure(int port)
    {
        string culprit = ProbedAnotherSpectari(port)
            ? "another Spectari is already running on this port (check the taskbar or Task Manager and close it, or pick a different port)"
            : "another program is using this port; close it or pick a different port";
        return $"Port {port} is already in use: {culprit}.";
    }

    /// <summary>Best-effort: does something on this port answer like our own
    /// /api/stats? A short timeout keeps the error path snappy.</summary>
    private static bool ProbedAnotherSpectari(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };
            string body = http.GetStringAsync($"http://localhost:{port}/api/stats").GetAwaiter().GetResult();
            return body.Contains("\"state\"");
        }
        catch { return false; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        // Hoisted so the catch can name the failing route. AbsolutePath excludes
        // the query string, so the viewer key never lands in the error log.
        string path = "/";
        try
        {
            path = context.Request.Url?.AbsolutePath ?? "/";
            bool keyOk = ViewerAccessPolicy.KeyMatches(_viewKey, context.Request.QueryString["k"]);
            Uri? target = context.Request.Url;
            if (!_access.AllowsAuthority(target, context.Request.Headers["Host"]))
            {
                await WriteResponseAsync(context, 400, "text/plain", "unrecognized destination");
                return;
            }
            string? origin = context.Request.Headers["Origin"];
            if (path == "/api/viewer-key")
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                int status = context.Request.HttpMethod != "GET" ? 405
                    : ViewerAccessPolicy.AllowsBootstrap(target!, origin,
                        context.Request.Headers[ViewerAccessPolicy.BootstrapHeader],
                        context.Request.RemoteEndPoint?.Address) ? 200 : 403;
                await WriteResponseAsync(context, status, "application/json",
                    status == 200 ? JsonSerializer.Serialize(new { key = _viewKey,
                        state = _broadcaster?.State ?? "idle" }) : "{}");
                return;
            }
            if (context.Request.HttpMethod != "GET")
            {
                await WriteResponseAsync(context, 405, "text/plain", "method not allowed");
                return;
            }

            if (path == "/ws")
            {
                if (!ViewerAccessPolicy.AllowsOrigin(target!, origin))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }
                if (_broadcaster is null) // idle: nothing to stream yet
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }
                if (!keyOk)
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }
                var wsContext = await context.AcceptWebSocketAsync(null);
                await _broadcaster.HandleClientAsync(wsContext.WebSocket, ct);
                return;
            }

            if (path == "/api/stats")
            {
                // CORS so the grid page (served by a friend's host) can probe us
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                var stats = _broadcaster is null
                    ? new Dictionary<string, object?>
                    {
                        ["name"] = _idleName,
                        ["state"] = "idle",
                        ["viewers"] = 0,
                    }
                    : new Dictionary<string, object?>
                    {
                        ["name"] = _broadcaster.StreamName,
                        ["state"] = _broadcaster.State,
                        ["viewers"] = _broadcaster.ViewerCount,
                        ["fragments"] = Interlocked.Read(ref _broadcaster.FragmentsSent),
                        ["sourceFps"] = _broadcaster.SourceFps,
                        ["dupPct"] = _broadcaster.DupPercent,
                        ["audio"] = _broadcaster.HasAudio,
                    };
                await WriteResponseAsync(context, 200, "application/json", JsonSerializer.Serialize(stats));
                return;
            }

            if (path == "/") path = "/index.html";
            if (path == "/grid") path = "/grid.html";
            string file = Path.GetFullPath(Path.Combine(_wwwroot, path.TrimStart('/')));
            // Trailing separator so a sibling like "wwwroot-x" can't pass the prefix check.
            string root = _wwwroot.EndsWith(Path.DirectorySeparatorChar) ? _wwwroot : _wwwroot + Path.DirectorySeparatorChar;
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                await WriteResponseAsync(context, 404, "text/plain", "not found");
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            byte[] bytes = await File.ReadAllBytesAsync(file, ct);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log before responding: a bare 500 with no detail erased exactly the
            // info a public bug report needs. ConsoleMirror tees this to the log.
            Console.Error.WriteLine($"[http] {path} handler error: {ex.GetType().Name}");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
