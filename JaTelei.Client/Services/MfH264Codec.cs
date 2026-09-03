// MfH264Codec.cs
// Windows Media Foundation H264 encoder (BGRA→H264 Annex B)
// and decoder (H264 Annex B→BGRA) using COM vtable P/Invoke.
// No third-party dependencies — relies on inbox Windows MFTs.

using System.Runtime.InteropServices;

namespace JaTelei.Client.Services;

// ── Native declarations ───────────────────────────────────────────────────────

internal static class MfNative
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint dwFlags = 0);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    // ── GUIDs ────────────────────────────────────────────────────────────────

    public static readonly Guid CLSID_MSH264EncoderMFT  = new("6CA50344-051A-4DED-9779-A43305165E35");
    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid IID_IMFTransform         = new("bf94c121-5b05-4e6f-9000-ba5ac3c30e28");

    public static readonly Guid MF_MT_MAJOR_TYPE     = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE        = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE     = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE     = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_AVG_BITRATE    = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    public static readonly Guid MFMediaType_Video  = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    // ── Constants ────────────────────────────────────────────────────────────

    public const uint MF_VERSION                         = 0x00020070; // MF 2.7 (Win10+)
    public const uint CLSCTX_INPROC_SERVER               = 1;
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const int  MF_E_TRANSFORM_NEED_MORE_INPUT     = unchecked((int)0xC00D6D72);
    public const int  MF_E_TRANSFORM_STREAM_CHANGE       = unchecked((int)0xC00D6D61);
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x20000000;

    // ── Delegate types for vtable calls ──────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint Fn_Release(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetUINT32(IntPtr self, ref Guid key, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetUINT64(IntPtr self, ref Guid key, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetUINT64(IntPtr self, ref Guid key, out ulong pValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetGUID(IntPtr self, ref Guid key, ref Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetOutputStreamInfo(IntPtr self, uint streamId,
                                                out MFT_OUTPUT_STREAM_INFO info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetInputType(IntPtr self, uint streamId, IntPtr type, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetOutputType(IntPtr self, uint streamId, IntPtr type, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetCurrentType(IntPtr self, uint streamId, out IntPtr ppType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_ProcessMessage(IntPtr self, uint msg, UIntPtr param);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_ProcessInput(IntPtr self, uint streamId, IntPtr sample, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_ProcessOutput(IntPtr self, uint flags, uint count,
                                          ref MFT_OUTPUT_DATA_BUFFER outBuf, out uint status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_Lock(IntPtr self, out IntPtr data, IntPtr maxLen, out uint curLen);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_Unlock(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetCurLen(IntPtr self, out uint len);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetCurLen(IntPtr self, uint len);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetSampleTime(IntPtr self, long time100ns);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_SetSampleDur(IntPtr self, long dur100ns);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetBufByIndex(IntPtr self, uint idx, out IntPtr buf);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_AddBuffer(IntPtr self, IntPtr buf);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int Fn_GetTotalLen(IntPtr self, out uint len);

    // ── Structures ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    // x64 layout (pointer fields at 8-byte-aligned offsets): total 32 bytes
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        [FieldOffset( 0)] public uint   dwStreamID;
        [FieldOffset( 8)] public IntPtr pSample;
        [FieldOffset(16)] public uint   dwStatus;
        [FieldOffset(24)] public IntPtr pEvents;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Reads a function pointer from a COM object's vtable at the given slot.</summary>
    public static T Vtbl<T>(IntPtr obj, int slot) where T : Delegate
    {
        var vt = Marshal.ReadIntPtr(obj);
        var fn = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    public static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Vtbl<Fn_Release>(p, 2)(p);   // IUnknown::Release is slot 2
        p = IntPtr.Zero;
    }

    // IMFAttributes method shortcuts (vtable slots after IUnknown 0-2):
    // GetItem=3 … DeleteAllItems=20, SetUINT32=21, SetUINT64=22, SetDouble=23, SetGUID=24
    public static int SetUINT32(IntPtr attr, Guid key, uint val)
        => Vtbl<Fn_SetUINT32>(attr, 21)(attr, ref key, val);

    public static int SetUINT64(IntPtr attr, Guid key, ulong val)
        => Vtbl<Fn_SetUINT64>(attr, 22)(attr, ref key, val);

    public static int SetGUID(IntPtr attr, Guid key, Guid val)
        => Vtbl<Fn_SetGUID>(attr, 24)(attr, ref key, ref val);

    // IMFAttributes::GetUINT64 = slot 8
    public static int GetUINT64(IntPtr attr, Guid key, out ulong val)
        => Vtbl<Fn_GetUINT64>(attr, 8)(attr, ref key, out val);

    /// <summary>Pack (width, height) into UINT64 for MF_MT_FRAME_SIZE.</summary>
    public static ulong PackWH(uint w, uint h) => ((ulong)w << 32) | h;

    /// <summary>Pack (num, den) into UINT64 for MF_MT_FRAME_RATE.</summary>
    public static ulong PackFrac(uint n, uint d) => ((ulong)n << 32) | d;

    /// <summary>
    /// Reads all bytes from the first buffer in an IMFSample.
    /// IMFSample vtable (inherits IMFAttributes 33 slots):
    ///   GetTotalLength=45, GetBufferByIndex=40.
    /// IMFMediaBuffer vtable (inherits IUnknown 3 slots):
    ///   Lock=3, Unlock=4, GetCurrentLength=5.
    /// </summary>
    public static byte[] ReadSampleBytes(IntPtr sample)
    {
        Vtbl<Fn_GetTotalLen>(sample, 45)(sample, out var total);
        if (total == 0) return Array.Empty<byte>();

        Vtbl<Fn_GetBufByIndex>(sample, 40)(sample, 0, out var buf);
        try
        {
            Vtbl<Fn_GetCurLen>(buf, 5)(buf, out var len);
            if (len == 0) return Array.Empty<byte>();
            Vtbl<Fn_Lock>(buf, 3)(buf, out var ptr, IntPtr.Zero, out _);
            var data = new byte[len];
            Marshal.Copy(ptr, data, 0, (int)len);
            Vtbl<Fn_Unlock>(buf, 4)(buf);
            return data;
        }
        finally { MfNative.Release(ref buf); }
    }
}

// ── H264 Encoder ─────────────────────────────────────────────────────────────

/// <summary>
/// Encodes BGRA frames to H264 Annex B using the Windows inbox software MFT
/// (CLSID_MSH264EncoderMFT). Thread-hostile — call Encode() from one thread.
/// </summary>
public sealed class MfH264Encoder : IDisposable
{
    private IntPtr _mft;
    private readonly int _fps;
    private long _sampleTime;
    private bool _streaming;

    public MfH264Encoder(int width, int height, int fps = 30, int bitrateBps = 2_500_000)
    {
        _fps = fps;
        MfNative.MFStartup(MfNative.MF_VERSION);

        var clsid = MfNative.CLSID_MSH264EncoderMFT;
        var iid   = MfNative.IID_IMFTransform;
        Throw(MfNative.CoCreateInstance(ref clsid, IntPtr.Zero,
              MfNative.CLSCTX_INPROC_SERVER, ref iid, out _mft), "CoCreateInstance encoder");

        // Output type (H264) must be configured before input type
        MfNative.MFCreateMediaType(out var ot);
        try
        {
            MfNative.SetGUID  (ot, MfNative.MF_MT_MAJOR_TYPE,     MfNative.MFMediaType_Video);
            MfNative.SetGUID  (ot, MfNative.MF_MT_SUBTYPE,        MfNative.MFVideoFormat_H264);
            MfNative.SetUINT32(ot, MfNative.MF_MT_AVG_BITRATE,    (uint)bitrateBps);
            MfNative.SetUINT32(ot, MfNative.MF_MT_INTERLACE_MODE, 2);   // progressive
            MfNative.SetUINT64(ot, MfNative.MF_MT_FRAME_SIZE,
                                   MfNative.PackWH((uint)width, (uint)height));
            MfNative.SetUINT64(ot, MfNative.MF_MT_FRAME_RATE,
                                   MfNative.PackFrac((uint)fps, 1));
            // IMFTransform: SetOutputType = slot 16
            Throw(MfNative.Vtbl<MfNative.Fn_SetOutputType>(_mft, 16)(_mft, 0, ot, 0),
                  "SetOutputType H264");
        }
        finally { MfNative.Release(ref ot); }

        // Input type (NV12)
        MfNative.MFCreateMediaType(out var it);
        try
        {
            MfNative.SetGUID  (it, MfNative.MF_MT_MAJOR_TYPE,     MfNative.MFMediaType_Video);
            MfNative.SetGUID  (it, MfNative.MF_MT_SUBTYPE,        MfNative.MFVideoFormat_NV12);
            MfNative.SetUINT32(it, MfNative.MF_MT_INTERLACE_MODE, 2);
            MfNative.SetUINT64(it, MfNative.MF_MT_FRAME_SIZE,
                                   MfNative.PackWH((uint)width, (uint)height));
            MfNative.SetUINT64(it, MfNative.MF_MT_FRAME_RATE,
                                   MfNative.PackFrac((uint)fps, 1));
            // IMFTransform: SetInputType = slot 15
            Throw(MfNative.Vtbl<MfNative.Fn_SetInputType>(_mft, 15)(_mft, 0, it, 0),
                  "SetInputType NV12");
        }
        finally { MfNative.Release(ref it); }
    }

    /// <summary>Encode one BGRA frame. Returns H264 Annex B bytes or null if the encoder
    /// needs more input frames before producing output (B-frames / GOP start).</summary>
    public byte[]? Encode(byte[] bgra, int width, int height)
    {
        if (_mft == IntPtr.Zero) return null;

        // Start streaming once before first input
        if (!_streaming)
        {
            _streaming = true;
            var pm = MfNative.Vtbl<MfNative.Fn_ProcessMessage>(_mft, 23);
            pm(_mft, MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, UIntPtr.Zero);
            pm(_mft, MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, UIntPtr.Zero);
        }

        var  nv12    = BgraToNv12(bgra, width, height);
        uint nv12Len = (uint)nv12.Length;

        MfNative.MFCreateMemoryBuffer(nv12Len, out var buf);
        MfNative.MFCreateSample(out var sample);
        try
        {
            // Fill the buffer with NV12 data
            MfNative.Vtbl<MfNative.Fn_Lock>   (buf, 3)(buf, out var ptr, IntPtr.Zero, out _);
            Marshal.Copy(nv12, 0, ptr, (int)nv12Len);
            MfNative.Vtbl<MfNative.Fn_Unlock> (buf, 4)(buf);
            MfNative.Vtbl<MfNative.Fn_SetCurLen>(buf, 6)(buf, nv12Len);

            // Attach buffer to sample and set timestamps
            // IMFSample: AddBuffer=42, SetSampleTime=36, SetSampleDuration=38
            MfNative.Vtbl<MfNative.Fn_AddBuffer>   (sample, 42)(sample, buf);
            long dur = 10_000_000L / _fps;
            MfNative.Vtbl<MfNative.Fn_SetSampleTime>(sample, 36)(sample, _sampleTime);
            MfNative.Vtbl<MfNative.Fn_SetSampleDur> (sample, 38)(sample, dur);
            _sampleTime += dur;

            // IMFTransform: ProcessInput = slot 24
            int hr = MfNative.Vtbl<MfNative.Fn_ProcessInput>(_mft, 24)(_mft, 0, sample, 0);
            if (hr < 0) return null;

            return DrainOutput();
        }
        finally
        {
            MfNative.Release(ref sample);
            MfNative.Release(ref buf);
        }
    }

    private byte[]? DrainOutput()
    {
        // IMFTransform: GetOutputStreamInfo = slot 7
        MfNative.Vtbl<MfNative.Fn_GetOutputStreamInfo>(_mft, 7)(_mft, 0, out var si);
        bool mftOwns = (si.dwFlags & MfNative.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        IntPtr outBuf    = IntPtr.Zero;
        IntPtr outSample = IntPtr.Zero;
        try
        {
            if (!mftOwns)
            {
                uint sz = si.cbSize > 0 ? si.cbSize : 1_048_576u;
                MfNative.MFCreateMemoryBuffer(sz, out outBuf);
                MfNative.MFCreateSample(out outSample);
                MfNative.Vtbl<MfNative.Fn_AddBuffer>(outSample, 42)(outSample, outBuf);
            }

            var db = new MfNative.MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample    = outSample   // IntPtr.Zero when mftOwns
            };

            // IMFTransform: ProcessOutput = slot 25
            int hr = MfNative.Vtbl<MfNative.Fn_ProcessOutput>(_mft, 25)
                              (_mft, 0, 1, ref db, out _);

            if (hr == MfNative.MF_E_TRANSFORM_NEED_MORE_INPUT) return null;
            if (hr < 0) return null;

            IntPtr smp = mftOwns ? db.pSample : outSample;
            if (smp == IntPtr.Zero) return null;

            var data = MfNative.ReadSampleBytes(smp);
            if (mftOwns && db.pSample != IntPtr.Zero)
                MfNative.Release(ref db.pSample);

            return data.Length > 0 ? data : null;
        }
        finally
        {
            if (!mftOwns)
            {
                MfNative.Release(ref outSample);
                MfNative.Release(ref outBuf);
            }
        }
    }

    /// <summary>BGRA → NV12 (planar Y then interleaved UV), BT.601 integer math.</summary>
    private static byte[] BgraToNv12(byte[] bgra, int w, int h)
    {
        var  nv12  = new byte[w * h * 3 / 2];
        int  uvOff = w * h;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                int b  = bgra[si], g = bgra[si + 1], r = bgra[si + 2];

                // Y plane
                nv12[y * w + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                // UV plane — one sample per 2×2 block
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    int ui = uvOff + (y / 2) * w + x;
                    nv12[ui]     = (byte)(((-38 * r -  74 * g + 112 * b + 128) >> 8) + 128); // Cb
                    nv12[ui + 1] = (byte)((( 112 * r - 94 * g -  18 * b + 128) >> 8) + 128); // Cr
                }
            }
        }
        return nv12;
    }

    public void Dispose() => MfNative.Release(ref _mft);

    private static void Throw(int hr, string ctx)
    {
        if (hr < 0) throw new InvalidOperationException($"{ctx} failed: 0x{(uint)hr:X8}");
    }
}

// ── H264 Decoder ─────────────────────────────────────────────────────────────

/// <summary>
/// Decodes H264 Annex B frames to BGRA using the Windows inbox software MFT
/// (CLSID_CMSH264DecoderMFT). Thread-hostile — call Decode() from one thread.
///
/// Resolution is auto-detected from the H264 SPS NAL unit — no need to provide
/// width/height upfront. On first keyframe the MFT fires MF_E_TRANSFORM_STREAM_CHANGE
/// which we handle to learn the actual decoded dimensions.
/// </summary>
public sealed class MfH264Decoder : IDisposable
{
    private IntPtr _mft;
    private int    _width, _height;
    private long   _sampleTime;
    private bool   _streaming;
    private bool   _outputTypeSet;

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jaclipei_error.txt");

    public MfH264Decoder()
    {
        MfNative.MFStartup(MfNative.MF_VERSION);
        var clsid = MfNative.CLSID_CMSH264DecoderMFT;
        var iid   = MfNative.IID_IMFTransform;
        int hr    = MfNative.CoCreateInstance(ref clsid, IntPtr.Zero,
                        MfNative.CLSCTX_INPROC_SERVER, ref iid, out _mft);
        if (hr < 0)
            throw new InvalidOperationException($"CoCreateInstance decoder failed: 0x{(uint)hr:X8}");
    }

    /// <summary>
    /// Sets up the H264 input type. Resolution is NOT specified — the MFT reads it
    /// from the bitstream SPS. Output type is configured later via HandleStreamChange().
    /// </summary>
    private void Initialize()
    {
        // Input type: H264 — major type + subtype only; let MFT read resolution from SPS
        MfNative.MFCreateMediaType(out var it);
        try
        {
            MfNative.SetGUID(it, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
            MfNative.SetGUID(it, MfNative.MF_MT_SUBTYPE,    MfNative.MFVideoFormat_H264);
            // Deliberately NOT setting MF_MT_FRAME_SIZE — decoder reads it from SPS NAL
            int hr = MfNative.Vtbl<MfNative.Fn_SetInputType>(_mft, 15)(_mft, 0, it, 0);
            if (hr < 0)
                throw new InvalidOperationException($"Decoder SetInputType H264: 0x{(uint)hr:X8}");
        }
        finally { MfNative.Release(ref it); }

        var pm = MfNative.Vtbl<MfNative.Fn_ProcessMessage>(_mft, 23);
        pm(_mft, MfNative.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, UIntPtr.Zero);
        pm(_mft, MfNative.MFT_MESSAGE_NOTIFY_START_OF_STREAM, UIntPtr.Zero);
        _streaming     = true;
        _outputTypeSet = false;
    }

    /// <summary>
    /// Called after MF_E_TRANSFORM_STREAM_CHANGE — queries the MFT for the actual
    /// output format (width x height) and sets the NV12 output type accordingly.
    /// IMFTransform::GetOutputCurrentType = slot 18
    /// IMFAttributes::GetUINT64           = slot 8
    /// </summary>
    private void HandleStreamChange()
    {
        // Query what the MFT decided the output format is
        int hr = MfNative.Vtbl<MfNative.Fn_GetCurrentType>(_mft, 18)(_mft, 0, out IntPtr mt);
        if (hr >= 0 && mt != IntPtr.Zero)
        {
            try
            {
                hr = MfNative.GetUINT64(mt, MfNative.MF_MT_FRAME_SIZE, out ulong wh);
                if (hr >= 0 && wh != 0)
                {
                    _width  = (int)(wh >> 32);
                    _height = (int)(wh & 0xFFFF_FFFFUL);
                }
            }
            finally { MfNative.Release(ref mt); }
        }

        // Fallback to 1920x1080 if query failed
        if (_width  <= 0) _width  = 1920;
        if (_height <= 0) _height = 1080;

        // Now configure the output type: NV12 at the actual decoded resolution
        MfNative.MFCreateMediaType(out var ot);
        try
        {
            MfNative.SetGUID  (ot, MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Video);
            MfNative.SetGUID  (ot, MfNative.MF_MT_SUBTYPE,    MfNative.MFVideoFormat_NV12);
            MfNative.SetUINT64(ot, MfNative.MF_MT_FRAME_SIZE, MfNative.PackWH((uint)_width, (uint)_height));
            hr = MfNative.Vtbl<MfNative.Fn_SetOutputType>(_mft, 16)(_mft, 0, ot, 0);
            if (hr >= 0) _outputTypeSet = true;
        }
        finally { MfNative.Release(ref ot); }
    }

    /// <summary>
    /// Decode one H264 Annex B frame. Returns (null,0,0) when the decoder needs
    /// more input (e.g. waiting for IDR / B-frame reorder) or is still initialising.
    /// Width/height are ignored — resolution is auto-detected from the bitstream.
    /// </summary>
    public (byte[]? bgra, int width, int height) Decode(byte[] h264, int width = 0, int height = 0)
    {
        if (_mft == IntPtr.Zero) return (null, 0, 0);

        if (!_streaming) Initialize();

        uint h264Len = (uint)h264.Length;
        MfNative.MFCreateMemoryBuffer(h264Len, out var buf);
        MfNative.MFCreateSample(out var sample);
        try
        {
            MfNative.Vtbl<MfNative.Fn_Lock>     (buf, 3)(buf, out var ptr, IntPtr.Zero, out _);
            Marshal.Copy(h264, 0, ptr, (int)h264Len);
            MfNative.Vtbl<MfNative.Fn_Unlock>   (buf, 4)(buf);
            MfNative.Vtbl<MfNative.Fn_SetCurLen>(buf, 6)(buf, h264Len);

            MfNative.Vtbl<MfNative.Fn_AddBuffer>   (sample, 42)(sample, buf);
            long dur = 10_000_000L / 30;
            MfNative.Vtbl<MfNative.Fn_SetSampleTime>(sample, 36)(sample, _sampleTime);
            MfNative.Vtbl<MfNative.Fn_SetSampleDur> (sample, 38)(sample, dur);
            _sampleTime += dur;

            MfNative.Vtbl<MfNative.Fn_ProcessInput>(_mft, 24)(_mft, 0, sample, 0);
        }
        finally { MfNative.Release(ref sample); MfNative.Release(ref buf); }

        var nv12 = DrainDecoder();
        if (nv12 == null || nv12.Length == 0) return (null, 0, 0);
        if (_width <= 0 || _height <= 0)      return (null, 0, 0);

        return (Nv12ToBgra(nv12, _width, _height), _width, _height);
    }

    private byte[]? DrainDecoder()
    {
        // CMSH264DecoderMFT provides its own samples (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES).
        // Always pass pSample = IntPtr.Zero; the MFT fills db.pSample on success.
        // On MF_E_TRANSFORM_STREAM_CHANGE the output format changed — query it and retry once.

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var db = new MfNative.MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample    = IntPtr.Zero
            };

            int hr = MfNative.Vtbl<MfNative.Fn_ProcessOutput>(_mft, 25)
                              (_mft, 0, 1, ref db, out _);

            // Release any event object the MFT may have returned
            if (db.pEvents != IntPtr.Zero)
            {
                var ev = db.pEvents;
                MfNative.Release(ref ev);
            }

            if (hr == MfNative.MF_E_TRANSFORM_NEED_MORE_INPUT)
                return null;

            if (hr == MfNative.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // Output format changed (first SPS decoded, or resolution change).
                // Query actual output type and configure NV12 output.
                HandleStreamChange();
                continue; // retry ProcessOutput with the new output type
            }

            if (hr < 0)
            {
                System.IO.File.AppendAllText(LogPath,
                    $"[Decoder] {DateTime.Now}: ProcessOutput hr=0x{(uint)hr:X8} w={_width} h={_height}\n");
                return null;
            }

            // Success — read bytes from MFT-owned sample
            IntPtr smp = db.pSample;
            if (smp == IntPtr.Zero) return null;

            var data = MfNative.ReadSampleBytes(smp);
            MfNative.Release(ref smp);
            return data.Length > 0 ? data : null;
        }

        return null;
    }

    /// <summary>NV12 → BGRA, BT.601 integer math.</summary>
    private static byte[] Nv12ToBgra(byte[] nv12, int w, int h)
    {
        var bgra  = new byte[w * h * 4];
        int uvOff = w * h;

        for (int y = 0; y < h; y++)
        {
            int uvRow = (y / 2) * w;
            for (int x = 0; x < w; x++)
            {
                int yVal = nv12[y * w + x]             - 16;
                int cb   = nv12[uvOff + uvRow + (x & ~1)]     - 128;
                int cr   = nv12[uvOff + uvRow + (x & ~1) + 1] - 128;

                int r = (298 * yVal + 409 * cr          + 128) >> 8;
                int g = (298 * yVal - 100 * cb - 208 * cr + 128) >> 8;
                int b = (298 * yVal + 516 * cb          + 128) >> 8;

                int di = (y * w + x) * 4;
                bgra[di]     = (byte)Math.Clamp(b, 0, 255);
                bgra[di + 1] = (byte)Math.Clamp(g, 0, 255);
                bgra[di + 2] = (byte)Math.Clamp(r, 0, 255);
                bgra[di + 3] = 255;
            }
        }
        return bgra;
    }

    public void Dispose() => MfNative.Release(ref _mft);
}
