using System.Runtime.InteropServices;

namespace JaTelei.Client.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Enums mirroring capture.h
// ─────────────────────────────────────────────────────────────────────────────

public enum JcCodec : int
{
    H264 = 0,
    AV1  = 1,
}

public enum JcEncoderVendor : int
{
    Auto     = 0,
    Nvenc    = 1,
    Amf      = 2,
    Qsv      = 3,
    Software = 4,
}

public enum JcCaptureMode : int
{
    Auto = 0,
    Wgc  = 1,   // Windows Graphics Capture
    Dxgi = 2,   // DXGI Desktop Duplication
}

public enum JcTargetKind : int
{
    Monitor = 0,
    Window  = 1,
}

// ─────────────────────────────────────────────────────────────────────────────
// Structs mirroring capture.h
// ─────────────────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public struct JcInitParams
{
    // capture
    public JcCaptureMode  CaptureMode;
    public JcTargetKind   TargetKind;
    public int            AdapterIndex;
    public int            OutputIndex;
    public IntPtr         WindowHandle;   // HWND (JcTargetKind.Window)

    // output
    public int            DstWidth;
    public int            DstHeight;
    public int            Fps;
    public int            BitrateKbps;

    // codec / encoder
    public JcCodec        Codec;
    public JcEncoderVendor EncoderVendor;

    // audio
    public int            EnableAudio;    // 0 or 1
    public int            AudioBitrate;   // kbps (0=default 128)
}

[StructLayout(LayoutKind.Sequential)]
public struct JcEncoderInfo
{
    public JcEncoderVendor Vendor;
    public JcCodec         Codec;
    public int             IsHardware;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string          Name;
}

[StructLayout(LayoutKind.Sequential)]
public struct JcDisplayInfo
{
    public int  Index;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string FriendlyName;
    public int  Width;
    public int  Height;
    public int  IsPrimary;
}

[StructLayout(LayoutKind.Sequential)]
public struct JcWindowInfo
{
    public IntPtr Hwnd;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string ProcessName;
}

// ─────────────────────────────────────────────────────────────────────────────
// ScreenCaptureService — C# bridge to JaTelei.Capture.dll
// Pipeline: WGC/DXGI → D3D11VP → NVENC/AMF/QSV → H.264/AV1 → WebRTC
// ─────────────────────────────────────────────────────────────────────────────

public static class ScreenCaptureService
{
    private const string DllName = "JaTelei.Capture.dll";

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JC_Init(ref JcInitParams p);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JC_CaptureAndEncode(
        IntPtr outVideoBuffer,  int videoBufferSize,  out int outVideoBytes, out int outIsKeyFrame,
        IntPtr outAudioBuffer,  int audioBufferSize,  out int outAudioBytes);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_ForceKeyframe();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_SetBitrate(int bitrateKbps);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_GetOutputSize(out int width, out int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JC_Release();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JC_EnumEncoders(
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        JcEncoderInfo[]? outInfo, int maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JC_EnumDisplays(
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        JcDisplayInfo[]? outInfo, int maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JC_EnumWindows(
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        JcWindowInfo[]? outInfo, int maxCount);

    // ── State ─────────────────────────────────────────────────────────────────

    private static bool   _initialized;
    private static IntPtr _videoBuf = IntPtr.Zero;
    private static IntPtr _audioBuf = IntPtr.Zero;

    // 50 MB video, 512 KB audio per frame
    private const int VideoBufSize = 50 * 1024 * 1024;
    private const int AudioBufSize =  512 * 1024;

    public static int  OutputWidth   { get; private set; }
    public static int  OutputHeight  { get; private set; }
    public static bool AudioEnabled  { get; private set; }

    // ── Enumeration (call before Initialize) ─────────────────────────────────

    public static JcEncoderInfo[] EnumEncoders()
    {
        int n = JC_EnumEncoders(null, 0);
        if (n <= 0) return Array.Empty<JcEncoderInfo>();
        var arr = new JcEncoderInfo[n];
        JC_EnumEncoders(arr, n);
        return arr;
    }

    public static JcDisplayInfo[] EnumDisplays()
    {
        int n = JC_EnumDisplays(null, 0);
        if (n <= 0) return Array.Empty<JcDisplayInfo>();
        var arr = new JcDisplayInfo[n];
        JC_EnumDisplays(arr, n);
        return arr;
    }

    public static JcWindowInfo[] EnumWindows()
    {
        int n = JC_EnumWindows(null, 0);
        if (n <= 0) return Array.Empty<JcWindowInfo>();
        var arr = new JcWindowInfo[n];
        JC_EnumWindows(arr, n);
        return arr;
    }

    // ── Initialize ────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialize the full capture+encode pipeline via JaTelei.Capture.dll.
    /// Automatically selects best capture method (WGC → DXGI DDup) and
    /// best hardware encoder (NVENC → AMF → QSV → Software).
    /// </summary>
    public static bool Initialize(
        int    dstWidth      = 0,
        int    dstHeight     = 0,
        int    fps           = 30,
        int    bitrateKbps   = 8000,
        int    adapterIndex  = 0,
        int    outputIndex   = 0,
        IntPtr windowHandle  = default,
        JcCaptureMode  captureMode   = JcCaptureMode.Auto,
        JcCodec        codec         = JcCodec.H264,
        JcEncoderVendor encoderVendor = JcEncoderVendor.Auto,
        bool   enableAudio   = false,
        int    audioBitrate  = 128)
    {
        if (_initialized) return true;

        var p = new JcInitParams
        {
            CaptureMode   = captureMode,
            TargetKind    = windowHandle != IntPtr.Zero ? JcTargetKind.Window : JcTargetKind.Monitor,
            AdapterIndex  = adapterIndex,
            OutputIndex   = outputIndex,
            WindowHandle  = windowHandle,
            DstWidth      = dstWidth,
            DstHeight     = dstHeight,
            Fps           = fps,
            BitrateKbps   = bitrateKbps,
            Codec         = codec,
            EncoderVendor = encoderVendor,
            EnableAudio   = enableAudio ? 1 : 0,
            AudioBitrate  = audioBitrate,
        };

        int hr = JC_Init(ref p);
        if (hr != 0) return false;

        _initialized = true;
        _videoBuf    = Marshal.AllocHGlobal(VideoBufSize);
        _audioBuf    = Marshal.AllocHGlobal(AudioBufSize);
        AudioEnabled = enableAudio;

        JC_GetOutputSize(out int w, out int h);
        OutputWidth  = w;
        OutputHeight = h;
        return true;
    }

    // ── Capture + Encode ──────────────────────────────────────────────────────

    public readonly struct CaptureResult
    {
        public readonly byte[]? Video;
        public readonly byte[]? Audio;
        public readonly bool    IsKeyFrame;

        public CaptureResult(byte[]? video, bool isKeyFrame, byte[]? audio)
        {
            Video      = video;
            Audio      = audio;
            IsKeyFrame = isKeyFrame;
        }
    }

    /// <summary>
    /// Capture one frame. Returns encoded H.264/AV1 video (Annex B) and
    /// optionally encoded AAC audio. Returns default if no new frame ready.
    /// Non-blocking.
    /// </summary>
    public static CaptureResult CaptureFrame()
    {
        if (!_initialized || _videoBuf == IntPtr.Zero)
            return default;

        int hr = JC_CaptureAndEncode(
            _videoBuf, VideoBufSize, out int videoBytes, out int isKey,
            _audioBuf, AudioBufSize, out int audioBytes);

        if (hr < 0 || videoBytes <= 0) return default;

        var video = new byte[videoBytes];
        Marshal.Copy(_videoBuf, video, 0, videoBytes);

        byte[]? audio = null;
        if (audioBytes > 0)
        {
            audio = new byte[audioBytes];
            Marshal.Copy(_audioBuf, audio, 0, audioBytes);
        }

        return new CaptureResult(video, isKey != 0, audio);
    }

    /// <summary>
    /// Backward-compatible: returns only video bytes, null if no frame.
    /// </summary>
    public static byte[]? CaptureAndEncode()
    {
        var r = CaptureFrame();
        return r.Video;
    }

    // ── Control ───────────────────────────────────────────────────────────────

    public static void ForceKeyframe()
    {
        if (_initialized) JC_ForceKeyframe();
    }

    public static void SetBitrate(int kbps)
    {
        if (_initialized && kbps > 0) JC_SetBitrate(kbps);
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        JC_Release();
        if (_videoBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_videoBuf); _videoBuf = IntPtr.Zero; }
        if (_audioBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_audioBuf); _audioBuf = IntPtr.Zero; }
        _initialized = false;
        AudioEnabled = false;
        OutputWidth = OutputHeight = 0;
    }
}
