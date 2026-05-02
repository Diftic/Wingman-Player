namespace wingman_player.Services;

using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// EXPERIMENTAL (experiment/local-audio-stream branch).
///
/// Serves the AudioBridge's captured PCM as an endless WAV over loopback HTTP.
/// OBS Media Source pulls the URL and gets a dedicated audio channel that mirrors
/// what the in-app WebView2 player is producing, with control retained by Wingman Player
/// (pause/skip in Wingman Player => corresponding silence/change in the OBS feed).
///
/// Single connection at a time. Format is whatever the bridge sets via
/// <see cref="SetFormat"/> (always 16-bit PCM stereo; sample rate inherited from
/// the system mix format so we don't resample). The data chunk in the WAV header
/// is sized to int.MaxValue so ffmpeg-backed players (OBS Media Source) treat the
/// stream as effectively endless.
/// </summary>
internal sealed class LocalAudioStreamServer : IHostedService, IDisposable
{
    public const int Port = 17329;
    public const int OutputChannels = 2;
    public const int OutputBitsPerSample = 16;

    private readonly ILogger<LocalAudioStreamServer> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;

    private readonly object _connLock = new();
    private NetworkStream? _activeStream;
    private TcpClient? _activeClient;

    private int _sampleRate;
    private volatile bool _formatReady;

    public LocalAudioStreamServer(ILogger<LocalAudioStreamServer> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "LocalAudioStreamServer could not bind 127.0.0.1:{Port}", Port);
            return Task.CompletedTask;
        }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LocalAudioStream" };
        _acceptThread.Start();
        _logger.LogInformation(
            "LocalAudioStreamServer listening on http://127.0.0.1:{Port}/stream.wav", Port);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        DropActive();
        _acceptThread?.Join(2000);
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();

    /// <summary>
    /// Called by AudioBridge once the WebView2 mix format is known. Until this
    /// is set, incoming HTTP GETs are answered with 503 so OBS doesn't latch
    /// onto a header with the wrong sample rate.
    /// </summary>
    public void SetFormat(int sampleRate)
    {
        _sampleRate = sampleRate;
        _formatReady = true;
    }

    /// <summary>
    /// Push a 16-bit PCM stereo frame buffer to the active connection (if any).
    /// Bridge calls this from the audio pump thread; failure here just drops the
    /// connection and the next OBS reconnect will get a fresh WAV header.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_activeStream is null) return;
        try
        {
            lock (_connLock)
            {
                _activeStream?.Write(data);
            }
        }
        catch
        {
            DropActive();
        }
    }

    public bool HasClient => _activeStream is not null;

    // -------------------------------------------------------------------------
    // Connection handling
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
        var stream = client.GetStream();
        stream.ReadTimeout = 2000;

        var requestBuf = new byte[2048];
        int total = 0;
        try
        {
            while (total < requestBuf.Length)
            {
                int n = stream.Read(requestBuf, total, requestBuf.Length - total);
                if (n == 0) break;
                total += n;
                if (Encoding.ASCII.GetString(requestBuf, 0, total).Contains("\r\n\r\n")) break;
            }
        }
        catch (IOException) { client.Dispose(); return; }

        var request = Encoding.ASCII.GetString(requestBuf, 0, total);
        if (!request.StartsWith("GET", StringComparison.Ordinal))
        {
            WriteRaw(stream, "HTTP/1.0 400 Bad Request\r\nConnection: close\r\n\r\n");
            client.Dispose();
            return;
        }

        if (!_formatReady)
        {
            const string body = "Audio bridge not yet capturing audio.\r\n";
            WriteRaw(stream,
                "HTTP/1.0 503 Service Unavailable\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n" +
                body);
            client.Dispose();
            return;
        }

        var responseHeaders =
            "HTTP/1.0 200 OK\r\n" +
            "Content-Type: audio/wav\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n";
        try
        {
            stream.Write(Encoding.ASCII.GetBytes(responseHeaders));
            stream.Write(BuildWavHeader(_sampleRate, OutputChannels, OutputBitsPerSample));
        }
        catch
        {
            client.Dispose();
            return;
        }

        DropActive();
        lock (_connLock)
        {
            _activeStream = stream;
            _activeClient = client;
        }
        _logger.LogInformation(
            "Audio stream client connected from {Ep}", client.Client.RemoteEndPoint);
    }

    private static void WriteRaw(NetworkStream stream, string text)
    {
        try { stream.Write(Encoding.ASCII.GetBytes(text)); } catch { /* ignore */ }
    }

    private void DropActive()
    {
        lock (_connLock)
        {
            try { _activeStream?.Dispose(); } catch { /* ignore */ }
            try { _activeClient?.Dispose(); } catch { /* ignore */ }
            _activeStream = null;
            _activeClient = null;
        }
    }

    // -------------------------------------------------------------------------
    // WAV header
    // -------------------------------------------------------------------------

    private static byte[] BuildWavHeader(int sampleRate, int channels, int bitsPerSample)
    {
        // Fake-huge data chunk so ffmpeg-backed clients keep consuming indefinitely.
        const uint hugeData = 0x7FFFFFFFu;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        ushort blockAlign = (ushort)(channels * bitsPerSample / 8);

        var hdr = new byte[44];
        hdr[0] = (byte)'R'; hdr[1] = (byte)'I'; hdr[2] = (byte)'F'; hdr[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), hugeData - 8);
        hdr[8] = (byte)'W'; hdr[9] = (byte)'A'; hdr[10] = (byte)'V'; hdr[11] = (byte)'E';
        hdr[12] = (byte)'f'; hdr[13] = (byte)'m'; hdr[14] = (byte)'t'; hdr[15] = (byte)' ';
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(28), (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(34), (ushort)bitsPerSample);
        hdr[36] = (byte)'d'; hdr[37] = (byte)'a'; hdr[38] = (byte)'t'; hdr[39] = (byte)'a';
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(40), hugeData);
        return hdr;
    }
}
