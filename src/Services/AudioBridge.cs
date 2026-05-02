namespace wingman_player.Services;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PInvoke;
using Settings;
using static PInvoke.AudioBridgeInterop;
using static PInvoke.AudioSessionInterop;

/// <summary>
/// Captures audio from our WebView2 child processes via WASAPI process-loopback
/// (Windows 10 v2004+) and forwards the PCM to <see cref="LocalAudioStreamServer"/>
/// for OBS Media Source consumption.
///
/// The local listening path is unaffected — the user hears WebView2's audio
/// through Windows' default endpoint as normal. The bridge is purely a
/// capture-and-forward stage; it doesn't render anywhere audible itself, so
/// running it imposes no per-machine audio-routing requirement on listeners.
///
/// Lifecycle: starts at app startup, polls until WebView2 has spawned, then
/// runs the capture pump until cancellation. On any error the pump returns
/// and the outer loop retries with a fresh PID lookup.
/// </summary>
internal sealed class AudioBridge : IHostedService, IDisposable
{
    private readonly ILogger<AudioBridge> _logger;
    private readonly SettingsManager _settings;
    private readonly LocalAudioStreamServer _streamServer;
    private readonly uint _ownPid;
    private CancellationTokenSource? _cts;
    private Thread? _pumpThread;

    public AudioBridge(
        SettingsManager settings,
        LocalAudioStreamServer streamServer,
        ILogger<AudioBridge> logger)
    {
        _settings = settings;
        _streamServer = streamServer;
        _logger = logger;
        _ownPid = (uint)Environment.ProcessId;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pumpThread = new Thread(RunPump) { IsBackground = true, Name = "AudioBridge" };
        _pumpThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _pumpThread?.Join(2000);
        _pumpThread = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();

    // -------------------------------------------------------------------------
    // Pump
    // -------------------------------------------------------------------------

    private void RunPump()
    {
        var token = _cts!.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!TryFindWebView2RootPid(out var webView2Pid))
                {
                    Thread.Sleep(500);
                    continue;
                }

                _logger.LogInformation("AudioBridge: targeting WebView2 root pid {Pid}", webView2Pid);
                if (!RunOnce(webView2Pid, token) && !token.IsCancellationRequested)
                {
                    Thread.Sleep(1000);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioBridge pump crashed");
        }
    }

    /// <summary>
    /// One full run: open capture, push packets to the local stream server until
    /// error or cancellation. Returns true on graceful exit, false on error so
    /// the outer loop retries with a fresh PID lookup.
    /// </summary>
    private bool RunOnce(uint webView2Pid, CancellationToken token)
    {
        IntPtr captureEvent = IntPtr.Zero;
        IntPtr mmcssHandle = IntPtr.Zero;
        IntPtr mixFormat = IntPtr.Zero;
        IAudioClient? captureClient = null;
        IAudioClient? formatProbe = null;
        IAudioCaptureClient? cap = null;

        try
        {
            // Read the default render endpoint's mix format. Process-loopback
            // delivers samples in whatever format we pass to Initialize; using
            // the system mix format means Windows' audio engine does no
            // resampling on our side. We Activate but never Initialize/Start
            // this client — it's only used to read GetMixFormat.
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var renderDevice) != 0
                || renderDevice is null)
            {
                _logger.LogWarning("No default render endpoint");
                return false;
            }

            var iidAudioClient = IID_IAudioClient;
            if (renderDevice.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out var probeObj) != 0
                || probeObj is null)
            {
                _logger.LogWarning("Failed to activate IAudioClient on render endpoint");
                return false;
            }
            formatProbe = (IAudioClient)probeObj;

            if (formatProbe.GetMixFormat(out mixFormat) != 0 || mixFormat == IntPtr.Zero)
            {
                _logger.LogWarning("Failed to get render mix format");
                return false;
            }

            var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormat);
            int srcChannels = wfx.nChannels;
            // Mix-format detection. WebView2 typically lands at 32-bit IEEE float
            // (either WAVE_FORMAT_IEEE_FLOAT directly or WAVE_FORMAT_EXTENSIBLE
            // with KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, distinguishable by 32-bit
            // sample size since Windows mixers don't use 32-bit integer PCM).
            bool srcIsFloat = wfx.wBitsPerSample == 32;
            _logger.LogInformation(
                "AudioBridge mix format: tag=0x{Tag:X4} channels={Ch} rate={Rate} bits={Bits} blockAlign={BA}",
                wfx.wFormatTag, wfx.nChannels, wfx.nSamplesPerSec, wfx.wBitsPerSample, wfx.nBlockAlign);

            // Tell the local stream server what sample rate to advertise in its
            // WAV header. Output is fixed at 16-bit PCM stereo regardless of the
            // capture-side bit depth or channel count — see PushToStream below.
            _streamServer.SetFormat((int)wfx.nSamplesPerSec);

            // Reusable conversion buffer sized for one mix-format period (~10ms).
            // 10ms * 48000hz * 2ch * 2bytes = 1920 bytes typically, 8x that for
            // 96kHz 8ch sources. 16KB covers both with headroom.
            byte[] streamBuf = new byte[16 * 1024];

            // Activate process-loopback capture targeting WebView2 + descendants.
            captureClient = ActivateProcessLoopback(webView2Pid);
            if (captureClient is null) return false;

            // Declare Media category before Initialize. Process-loopback capture
            // doesn't itself reach the user's speakers, but app-aware routers
            // sometimes inspect the capture session for classification — leaving
            // it as Other has caused AUX-channel misclassification in the past.
            if (captureClient is IAudioClient2 captureClient2)
            {
                var capProps = new AudioClientProperties
                {
                    cbSize     = (uint)Marshal.SizeOf<AudioClientProperties>(),
                    bIsOffload = false,
                    eCategory  = AudioStreamCategory.Media,
                    Options    = AudioStreamOptions.None,
                };
                var hrCapProps = captureClient2.SetClientProperties(ref capProps);
                if (hrCapProps != 0)
                    _logger.LogDebug("Capture SetClientProperties returned 0x{Hr:X8}", hrCapProps);
            }

            captureEvent = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            var hr = captureClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                0,
                0,
                mixFormat,
                IntPtr.Zero);
            if (hr != 0) { _logger.LogWarning("Capture init failed: 0x{Hr:X8}", hr); return false; }
            captureClient.SetEventHandle(captureEvent);

            var iidCapture = IID_IAudioCaptureClient;
            captureClient.GetService(ref iidCapture, out var captureSvc);
            cap = (IAudioCaptureClient)captureSvc;

            uint mmcssIndex = 0;
            mmcssHandle = AvSetMmThreadCharacteristicsW("Pro Audio", ref mmcssIndex);

            captureClient.Start();
            _logger.LogInformation("AudioBridge running (capture-only, streaming to localhost server)");

            // Pump loop: block on capture event, drain capture, push packets to
            // the local stream server. No render side — listeners hear WebView2
            // directly through Windows' default endpoint as normal, OBS pulls
            // the same audio over http://127.0.0.1:17329/stream.wav.
            while (!token.IsCancellationRequested)
            {
                var waitResult = WaitForSingleObject(captureEvent, 200);
                if (waitResult == WAIT_TIMEOUT) continue;
                if (waitResult != WAIT_OBJECT_0)
                {
                    _logger.LogDebug("Capture event wait failed: 0x{R:X}", waitResult);
                    return false;
                }

                while (cap.GetNextPacketSize(out var packetFrames) == 0 && packetFrames > 0)
                {
                    if (cap.GetBuffer(out var capPtr, out var numFrames, out var capFlags, out _, out _) != 0) break;
                    if (numFrames == 0) { cap.ReleaseBuffer(0); continue; }

                    bool silent = (capFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    PushToStream(capPtr, numFrames, silent, srcIsFloat, srcChannels, streamBuf);
                    cap.ReleaseBuffer(numFrames);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioBridge run error");
            return false;
        }
        finally
        {
            try { captureClient?.Stop(); } catch { /* ignore */ }
            if (cap is not null)            Marshal.ReleaseComObject(cap);
            if (captureClient is not null)  Marshal.ReleaseComObject(captureClient);
            if (formatProbe is not null)    Marshal.ReleaseComObject(formatProbe);
            if (mixFormat != IntPtr.Zero)   CoTaskMemFree(mixFormat);
            if (captureEvent != IntPtr.Zero) CloseHandle(captureEvent);
            if (mmcssHandle != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcssHandle);
        }
    }

    /// <summary>
    /// Convert one captured packet into 16-bit signed PCM stereo and push it to
    /// the local stream server. Source layout depends on the WebView2 mix format
    /// (typically 32-bit float, 2-8 channels); we downmix anything beyond stereo
    /// to L/R by taking the first two channels. Silent packets become zero bytes
    /// so OBS keeps consuming a continuous stream.
    /// </summary>
    private void PushToStream(
        IntPtr capPtr,
        uint numFrames,
        bool silent,
        bool srcIsFloat,
        int srcChannels,
        byte[] scratch)
    {
        int outBytes = (int)numFrames * 2 * sizeof(short);
        if (outBytes <= 0 || outBytes > scratch.Length) return;

        if (silent || capPtr == IntPtr.Zero)
        {
            Array.Clear(scratch, 0, outBytes);
            _streamServer.Write(scratch.AsSpan(0, outBytes));
            return;
        }

        var dst = MemoryMarshal.Cast<byte, short>(scratch.AsSpan(0, outBytes));

        if (srcIsFloat)
        {
            unsafe
            {
                var src = new ReadOnlySpan<float>((void*)capPtr, (int)numFrames * srcChannels);
                for (int f = 0; f < numFrames; f++)
                {
                    float l = src[f * srcChannels];
                    float r = srcChannels >= 2 ? src[f * srcChannels + 1] : l;
                    dst[f * 2]     = ClampToS16(l);
                    dst[f * 2 + 1] = ClampToS16(r);
                }
            }
        }
        else
        {
            // 16-bit integer source. Pass through L/R, downmix-by-truncation.
            unsafe
            {
                var src = new ReadOnlySpan<short>((void*)capPtr, (int)numFrames * srcChannels);
                for (int f = 0; f < numFrames; f++)
                {
                    dst[f * 2]     = src[f * srcChannels];
                    dst[f * 2 + 1] = srcChannels >= 2 ? src[f * srcChannels + 1] : src[f * srcChannels];
                }
            }
        }

        _streamServer.Write(scratch.AsSpan(0, outBytes));
    }

    private static short ClampToS16(float f)
    {
        int v = (int)(f * 32767f);
        if (v >  short.MaxValue) return short.MaxValue;
        if (v <  short.MinValue) return short.MinValue;
        return (short)v;
    }

    private IAudioClient? ActivateProcessLoopback(uint targetPid)
    {
        var actParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.ProcessLoopback,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = targetPid,
                ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.IncludeTargetProcessTree,
            },
        };

        var blobSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blobPtr = Marshal.AllocHGlobal(blobSize);
        try
        {
            Marshal.StructureToPtr(actParams, blobPtr, false);
            var prop = new PROPVARIANT
            {
                vt = VT_BLOB,
                blob = new BLOB
                {
                    cbSize = (uint)blobSize,
                    pBlobData = blobPtr,
                },
            };

            var iidAudioClient = IID_IAudioClient;
            var handler = new ActivationCompletionHandler();
            ActivateAudioInterfaceAsync(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                ref iidAudioClient,
                ref prop,
                handler,
                out var op);

            if (!handler.Done.WaitOne(5000))
            {
                _logger.LogWarning("ActivateAudioInterfaceAsync timeout");
                return null;
            }

            int hr = op.GetActivateResult(out var actHr, out var unk);
            if (hr != 0 || actHr != 0)
            {
                _logger.LogWarning("Process-loopback activate failed: hr=0x{Hr:X8} actHr=0x{ActHr:X8}", hr, actHr);
                return null;
            }
            return (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private bool TryFindWebView2RootPid(out uint pid)
    {
        pid = 0;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return false;
            do
            {
                // Direct child of our process whose image is msedgewebview2.exe →
                // the WebView2 browser process. INCLUDE_TARGET_PROCESS_TREE then
                // sweeps in all of its helper renderer/audio processes.
                if (entry.th32ParentProcessID == _ownPid &&
                    string.Equals(entry.szExeFile, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
                {
                    pid = entry.th32ProcessID;
                    return true;
                }
            } while (Process32NextW(snapshot, ref entry));
            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEvent Done { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Done.Set();
            return 0;
        }
    }
}
