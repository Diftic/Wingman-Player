namespace wingman_player.Services;

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Settings;

/// <summary>
/// Localhost HTTP command surface for Wingman skills. Bound to 127.0.0.1 only,
/// no auth — same trust model as Accountant's command channel.
///
/// Routes (more added in subsequent steps):
///   GET  /player/state    → 200 {state, ...}
///   POST /player/play     → planned
///   POST /player/pause    → planned
///   POST /player/stop     → planned
///   POST /player/next     → planned
///   POST /player/previous → planned
///   POST /player/seek     → planned
///
/// Responses are short JSON; one connection per request, Connection: close.
/// </summary>
internal sealed class WingmanPlayerHttpServer : IHostedService, IDisposable
{
    private readonly ILogger<WingmanPlayerHttpServer> _logger;
    private readonly PlayerConfig _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;

    public WingmanPlayerHttpServer(ILogger<WingmanPlayerHttpServer> logger, PlayerConfig config)
    {
        _logger = logger;
        _config = config;
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

            var response = Route(request);
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

        // Body starts 4 bytes after headerEnd (past the \r\n\r\n).
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
    // Routing — stub for step 1; real handlers land in subsequent steps
    // -------------------------------------------------------------------------

    private sealed record Response(int Status, string Body);

    private Response Route(Request req)
    {
        return (req.Method, req.Path) switch
        {
            ("GET", "/player/state") => new Response(200, "{\"state\":\"idle\"}"),
            _ => new Response(404, "{\"error\":\"not_found\"}"),
        };
    }

    // -------------------------------------------------------------------------
    // Response writer
    // -------------------------------------------------------------------------

    private static void WriteResponse(NetworkStream stream, int status, string jsonBody)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
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
        stream.Write(bodyBytes);
    }
}
