namespace wingman_player.PInvoke;

using System.Runtime.InteropServices;

/// <summary>
/// WASAPI process-loopback interop. The Windows 10 v2004+ ActivateAudioInterfaceAsync
/// path lets us capture audio from a specific process tree and re-emit it from our
/// own process so OBS Window Capture's "Capture Audio (BETA)" picks it up
/// (audio sessions otherwise live in WebView2 helper PIDs invisible to that capture).
/// </summary>
internal static class AudioBridgeInterop
{
    // --- Constants ----------------------------------------------------------

    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

    public const uint AUDCLNT_SHAREMODE_SHARED        = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK    = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST   = 0x00080000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT             = 0x2;
    public const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR    = 0x4;

    public const ushort VT_BLOB  = 0x41;
    public const ushort VT_EMPTY = 0x00;

    public const ushort WAVE_FORMAT_PCM        = 0x0001;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    /// <summary>100-nanosecond units, 1 second.</summary>
    public const long REFTIMES_PER_SEC = 10_000_000;

    public enum AUDIOCLIENT_ACTIVATION_TYPE : uint
    {
        Default         = 0,
        ProcessLoopback = 1,
    }

    public enum PROCESS_LOOPBACK_MODE : uint
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    /// <summary>
    /// Audio stream category — declared via IAudioClient2.SetClientProperties so
    /// app-aware routers (SteelSeries Sonar, Wavelink, Voicemeeter) classify the
    /// stream into the right channel. Without this, Sonar guesses Game by default
    /// and refuses to let the user re-route the session ("didn't allow Sonar to
    /// change the audio settings").
    /// </summary>
    public enum AudioStreamCategory
    {
        Other                  = 0,
        ForegroundOnlyMedia    = 1,
        BackgroundCapableMedia = 2,
        Communications         = 3,
        Alerts                 = 4,
        SoundEffects           = 5,
        GameEffects            = 6,
        GameMedia              = 7,
        GameChat               = 8,
        Speech                 = 9,
        Movie                  = 10,
        Media                  = 11,
    }

    public enum AudioStreamOptions : uint
    {
        None        = 0,
        Raw         = 0x1,
        MatchFormat = 0x2,
        Ambisonics  = 0x4,
    }

    // --- Structs ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLOB
    {
        public uint cbSize;
        public IntPtr pBlobData;
    }

    /// <summary>
    /// Minimal PROPVARIANT just big enough for VT_BLOB activation params.
    /// The real PROPVARIANT is a tagged union; we only ever set vt=VT_BLOB
    /// and put the activation params blob in the BLOB slot at offset 8.
    /// Total size on 64-bit must be 24 bytes to match the COM ABI.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public BLOB blob;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientProperties
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.Bool)] public bool bIsOffload;
        public AudioStreamCategory eCategory;
        public AudioStreamOptions Options;
    }

    // --- COM interfaces -----------------------------------------------------

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(
            uint shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            IntPtr pFormat,
            IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    /// <summary>
    /// Extends IAudioClient with SetClientProperties so we can declare the
    /// stream's AudioCategory before Initialize. Inheritance order in COM
    /// vtable matters — every IAudioClient method is repeated first, then the
    /// IAudioClient2 additions.
    /// </summary>
    [ComImport]
    [Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient2
    {
        // --- IAudioClient (vtable order) ---
        [PreserveSig] int Initialize(
            uint shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            IntPtr pFormat,
            IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        // --- IAudioClient2 additions ---
        [PreserveSig] int IsOffloadCapable(AudioStreamCategory category, [MarshalAs(UnmanagedType.Bool)] out bool pbOffloadCapable);
        [PreserveSig] int SetClientProperties(ref AudioClientProperties pProperties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr pFormat, [MarshalAs(UnmanagedType.Bool)] bool bEventDriven, out long phnsMinBufferDuration, out long phnsMaxBufferDuration);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr ppData,
            out uint numFramesToRead,
            out uint dwFlags,
            out ulong devicePosition,
            out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    public static readonly Guid IID_IAudioClient        = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient  = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    // --- DllImports ---------------------------------------------------------

    [DllImport("mmdevapi.dll", PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool manualReset, bool initialState, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT  = 0x00000102;
    public const uint WAIT_FAILED   = 0xFFFFFFFF;
    public const uint INFINITE      = 0xFFFFFFFF;
}
