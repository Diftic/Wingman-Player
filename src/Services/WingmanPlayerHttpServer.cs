namespace wingman_player.Services;

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Settings;

/// <summary>
/// Localhost HTTP command surface for Wingman skills. Bound to 127.0.0.1 only,
/// no auth — same trust model as Accountant's command channel.
///
/// Routes:
///   GET  /player/state    → 200 {state, videoId, title, currentTime, duration} | 503 if not ready
///   POST /player/load     → 204 / 400 / 503    body: {source, videoId?, playlistId?, index?, startSeconds?, endSeconds?}
///   POST /player/play     → 204 / 503          (resume — distinct from /load which starts a new video)
///   POST /player/pause    → 204 / 503
///   POST /player/stop     → 204 / 503
///   POST /player/next     → 204 / 503
///   POST /player/previous → 204 / 503
///   POST /player/seek     → 204 / 400 / 503    body: {seconds}
///
/// /load vs /play: /load is "start playing this new content" (req 1/2/3 — search → load).
/// /play is "resume what's already loaded" (req 4 — transport). Skills pick based on intent.
///
/// Source-agnostic: /player/play takes a {source} discriminator so a future
/// Spotify or local-file skill can drive the same transport. Today only
/// "youtube" is wired; unknown sources return 400.
///
/// One connection at a time, Connection: close per response.
/// </summary>
internal sealed class WingmanPlayerHttpServer : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<WingmanPlayerHttpServer> _logger;
    private readonly PlayerConfig _config;
    private readonly PlayerCommandBridge _bridge;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;

    public WingmanPlayerHttpServer(
        ILogger<WingmanPlayerHttpServer> logger,
        PlayerConfig config,
        PlayerCommandBridge bridge)
    {
        _logger = logger;
        _config = config;
        _bridge = bridge;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _config.CommandServerPort);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex,
                "WingmanPlayerHttpServer could not bind 127.0.0.1:{Port}", _config.CommandServerPort);
            return Task.CompletedTask;
        }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WingmanPlayerHttp" };
        _acceptThread.Start();
        _logger.LogInformation(
            "WingmanPlayerHttpServer listening on http://127.0.0.1:{Port}", _config.CommandServerPort);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _acceptThread?.Join(2000);
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();

    // -------------------------------------------------------------------------
    // Accept loop
    // -------------------------------------------------------------------------

    private void AcceptLoop()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = _listener!.AcceptTcpClient();
                client.NoDelay = true;
                ServeClient(client);
            }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Accept loop error");
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private void ServeClient(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        stream.ReadTimeout = 2000;

        try
        {
            var request = ReadRequest(stream);
            if (request is null)
            {
                WriteResponse(stream, 400, "{\"error\":\"bad_request\"}");
                return;
            }

            // Block the accept thread while we marshal to the UI thread and back.
            // Acceptable here because (a) accept is single-connection at a time,
            // (b) the dispatch target is the WPF Dispatcher which can't deadlock
            // on the accept thread, and (c) commands return in <100ms.
            var response = RouteAsync(request).GetAwaiter().GetResult();
            WriteResponse(stream, response.Status, response.Body);
        }
        catch (IOException) { /* client went away */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ServeClient error");
            try { WriteResponse(stream, 500, "{\"error\":\"internal\"}"); } catch { /* ignore */ }
        }
    }

    // -------------------------------------------------------------------------
    // HTTP/1.1 minimal parser — request line + headers + Content-Length body
    // -------------------------------------------------------------------------

    private sealed record Request(string Method, string Path, string Body);

    private static Request? ReadRequest(NetworkStream stream)
    {
        var headerBuf = new byte[4096];
        int total = 0;
        int headerEnd = -1;

        while (total < headerBuf.Length)
        {
            int n = stream.Read(headerBuf, total, headerBuf.Length - total);
            if (n == 0) break;
            total += n;
            var prefix = Encoding.ASCII.GetString(headerBuf, 0, total);
            int idx = prefix.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0) { headerEnd = idx; break; }
        }

        if (headerEnd < 0) return null;

        var headerText = Encoding.ASCII.GetString(headerBuf, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var method = requestLine[0];
        var path   = requestLine[1];

        // Content-Length is the only framing we support (no chunked).
        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[15..].Trim(), out contentLength);
                break;
            }
        }

        var bodyStart = headerEnd + 4;
        var bodyAlreadyRead = total - bodyStart;
        var body = string.Empty;
        if (contentLength > 0)
        {
            using var ms = new MemoryStream(contentLength);
            if (bodyAlreadyRead > 0)
                ms.Write(headerBuf, bodyStart, Math.Min(bodyAlreadyRead, contentLength));

            int remaining = contentLength - (int)ms.Length;
            var chunk = new byte[2048];
            while (remaining > 0)
            {
                int n = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (n == 0) break;
                ms.Write(chunk, 0, n);
                remaining -= n;
            }
            body = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        return new Request(method, path, body);
    }

    // -------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------

    private sealed record Response(int Status, string Body);

    private static readonly Response NotFound  = new(404, "{\"error\":\"not_found\"}");
    private static readonly Response NotReady  = new(503, "{\"error\":\"player_not_ready\"}");
    private static readonly Response NoContent = new(204, "");
    private static Response BadRequest(string reason) =>
        new(400, $"{{\"error\":\"bad_request\",\"reason\":{JsonSerializer.Serialize(reason)}}}");

    private async Task<Response> RouteAsync(Request req)
    {
        return (req.Method, req.Path) switch
        {
            ("GET",  "/player/state")    => await HandleStateAsync(),
            ("POST", "/player/load")     => await HandleLoadAsync(req.Body),
            ("POST", "/player/play")     => await SimpleCommandAsync("window.__wingmanPlay()",     showOverlay: true),
            ("POST", "/player/pause")    => await SimpleCommandAsync("window.__wingmanPause()",    showOverlay: false),
            ("POST", "/player/stop")     => await SimpleCommandAsync("window.__wingmanStop()",     showOverlay: false),
            ("POST", "/player/next")     => await SimpleCommandAsync("window.__wingmanNext()",     showOverlay: true),
            ("POST", "/player/previous") => await SimpleCommandAsync("window.__wingmanPrevious()", showOverlay: true),
            ("POST", "/player/seek")     => await HandleSeekAsync(req.Body),
            _ => NotFound,
        };
    }

    // -------------------------------------------------------------------------
    // Handlers
    // -------------------------------------------------------------------------

    private async Task<Response> HandleStateAsync()
    {
        var raw = await _bridge.ExecuteWithResultAsync(
            "(window.__wingmanGetState && window.__wingmanGetState()) || {state:'idle'}");
        if (raw is null) return NotReady;

        // Augment the renderer-side state with update info from the bridge so
        // the skill (and Wingman) can mention available updates to the user.
        var update = _bridge.LatestUpdate;
        if (update?.HasUpdate == true)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, JsonOpts);
                if (dict is not null)
                {
                    dict["updateAvailable"] = true;
                    dict["latestVersion"] = update.Version;
                    return new Response(200, JsonSerializer.Serialize(dict, JsonOpts));
                }
            }
            catch (JsonException)
            {
                // Fall through — return the raw renderer state without augmenting.
            }
        }

        // ExecuteScriptAsync already returns a JSON-encoded value; pass through.
        return new Response(200, raw);
    }

    private sealed record LoadCommand(
        string?  Source,
        string?  VideoId,
        string?  PlaylistId,
        int?     Index,
        double?  StartSeconds,
        double?  EndSeconds);

    private async Task<Response> HandleLoadAsync(string body)
    {
        LoadCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<LoadCommand>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            return BadRequest($"invalid_json: {ex.Message}");
        }
        if (cmd is null) return BadRequest("empty_body");

        // Source-agnostic schema; today only "youtube" is wired.
        if (!string.Equals(cmd.Source, "youtube", StringComparison.OrdinalIgnoreCase))
            return BadRequest("unsupported_source");

        if (string.IsNullOrWhiteSpace(cmd.VideoId) && string.IsNullOrWhiteSpace(cmd.PlaylistId))
            return BadRequest("missing_videoId_or_playlistId");

        // JSON-encode every value we splice into JS so a malicious id can't
        // break out of the string literal.
        var optsJson = BuildOptsJson(cmd);
        var script =
            $"window.__wingmanLoad(" +
            $"{JsonSerializer.Serialize(cmd.VideoId)}, " +
            $"{JsonSerializer.Serialize(cmd.PlaylistId)}, " +
            $"{optsJson})";

        var dispatched = await _bridge.ExecuteAsync(script);
        if (!dispatched) return NotReady;
        // /load is a playback-starting command — bring the overlay on-screen
        // so the user can see the video and the WebView2 surface is visible
        // (browser autoplay policy holds back media on hidden surfaces).
        await _bridge.EnsureOverlayVisibleAsync();
        return NoContent;
    }

    private static string BuildOptsJson(LoadCommand cmd)
    {
        var dict = new Dictionary<string, object>();
        if (cmd.Index.HasValue)        dict["index"]        = cmd.Index.Value;
        if (cmd.StartSeconds.HasValue) dict["startSeconds"] = cmd.StartSeconds.Value;
        if (cmd.EndSeconds.HasValue)   dict["endSeconds"]   = cmd.EndSeconds.Value;
        return JsonSerializer.Serialize(dict);
    }

    private sealed record SeekCommand(double? Seconds);

    private async Task<Response> HandleSeekAsync(string body)
    {
        SeekCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<SeekCommand>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            return BadRequest($"invalid_json: {ex.Message}");
        }
        if (cmd?.Seconds is null) return BadRequest("missing_seconds");

        var script = $"window.__wingmanSeek({JsonSerializer.Serialize(cmd.Seconds.Value)})";
        var dispatched = await _bridge.ExecuteAsync(script);
        if (!dispatched) return NotReady;
        await _bridge.EnsureOverlayVisibleAsync();
        return NoContent;
    }

    private async Task<Response> SimpleCommandAsync(string script, bool showOverlay)
    {
        var dispatched = await _bridge.ExecuteAsync(script);
        if (!dispatched) return NotReady;
        if (showOverlay)
            await _bridge.EnsureOverlayVisibleAsync();
        return NoContent;
    }

    // -------------------------------------------------------------------------
    // Response writer
    // -------------------------------------------------------------------------

    private static void WriteResponse(NetworkStream stream, int status, string jsonBody)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _   => "Unknown",
        };

        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        stream.Write(Encoding.ASCII.GetBytes(headers));
        if (bodyBytes.Length > 0)
            stream.Write(bodyBytes);
    }
}
