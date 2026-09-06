using System.Runtime.InteropServices;

namespace JaTelei.Client.Services;

// =============================================================================
// WaveOutPlayer — 48kHz 16-bit stereo PCM playback via Win32 waveOut
//
//  Designed to play Opus-decoded audio (Concentus output) with low latency.
//  Uses a double-buffer scheme: while one buffer plays, the next is prepared.
//  Thread-safe: QueueAudio can be called from any thread.
// =============================================================================

public sealed class WaveOutPlayer : IDisposable
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    private const uint  WAVE_FORMAT_PCM   = 1;
    private const uint  WHDR_DONE         = 0x00000001;
    private const uint  CALLBACK_FUNCTION = 0x00030000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
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
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint   dwBufferLength;
        public uint   dwBytesRecorded;
        public IntPtr dwUser;
        public uint   dwFlags;
        public uint   dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance,
                                      IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")] private static extern int  waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, WaveOutProc? dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int  waveOutClose(IntPtr hWaveOut);
    [DllImport("winmm.dll")] private static extern int  waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int  waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int  waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int  waveOutReset(IntPtr hWaveOut);

    // ── Constants ─────────────────────────────────────────────────────────────

    private const int SampleRate    = 48_000;
    private const int Channels      = 2;
    private const int BitsPerSample = 16;

    // 20ms @ 48kHz stereo 16-bit = 3840 bytes
    private const int FrameBytes = SampleRate * Channels * (BitsPerSample / 8) * 20 / 1000;

    // Number of pre-allocated waveOut headers (≥ 3 to stay ahead of driver)
    private const int BufferCount = 4;

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr    _hWaveOut = IntPtr.Zero;
    private readonly WaveOutProc _callback; // keep delegate alive
    private readonly object      _lock = new();

    // Queued PCM waiting to be written
    private readonly Queue<byte[]> _queue = new();

    // waveOut header pool
    private readonly WAVEHDR[]  _headers  = new WAVEHDR[BufferCount];
    private readonly IntPtr[]   _dataPtrs = new IntPtr[BufferCount];
    private readonly bool[]     _busy     = new bool[BufferCount];
    private          bool       _disposed;

    // ── Construction ─────────────────────────────────────────────────────────

    public WaveOutPlayer()
    {
        _callback = WaveOutCallback;

        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = (ushort)WAVE_FORMAT_PCM,
            nChannels       = Channels,
            nSamplesPerSec  = SampleRate,
            nAvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
            nBlockAlign     = (ushort)(Channels * BitsPerSample / 8),
            wBitsPerSample  = BitsPerSample,
            cbSize          = 0,
        };

        int mm = waveOutOpen(out _hWaveOut, uint.MaxValue /* WAVE_MAPPER */, ref fmt,
                             _callback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (mm != 0)
            throw new InvalidOperationException($"waveOutOpen failed: {mm}");

        // Pre-allocate pinned native buffers for each header slot
        for (int i = 0; i < BufferCount; i++)
        {
            _dataPtrs[i] = Marshal.AllocHGlobal(FrameBytes * 8); // 8× frame = 160ms max
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queue raw PCM16 stereo 48kHz audio for playback.
    /// Thread-safe; non-blocking.
    /// </summary>
    public void QueueAudio(byte[] pcm16)
    {
        if (_disposed || pcm16 == null || pcm16.Length == 0) return;

        lock (_lock)
        {
            _queue.Enqueue(pcm16);
            DrainQueue();
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void DrainQueue()
    {
        // Called under _lock
        for (int i = 0; i < BufferCount; i++)
        {
            if (_busy[i]) continue;
            if (_queue.Count == 0) break;
            WriteBuffer(i, _queue.Dequeue());
        }
    }

    private void WriteBuffer(int idx, byte[] data)
    {
        // Copy data to pinned buffer
        int copyLen = Math.Min(data.Length, FrameBytes * 8);
        Marshal.Copy(data, 0, _dataPtrs[idx], copyLen);

        _headers[idx] = new WAVEHDR
        {
            lpData        = _dataPtrs[idx],
            dwBufferLength = (uint)copyLen,
        };

        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        waveOutPrepareHeader(_hWaveOut, ref _headers[idx], hdrSize);
        _busy[idx] = true;
        waveOutWrite(_hWaveOut, ref _headers[idx], hdrSize);
    }

    // ── waveOut callback (called from Win32 on buffer completion) ────────────

    private void WaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance,
                                 IntPtr dwParam1, IntPtr dwParam2)
    {
        const uint WOM_DONE = 0x3BD;
        if (uMsg != WOM_DONE) return;

        lock (_lock)
        {
            // Find which header completed
            for (int i = 0; i < BufferCount; i++)
            {
                if (!_busy[i]) continue;
                if ((_headers[i].dwFlags & WHDR_DONE) != 0)
                {
                    waveOutUnprepareHeader(_hWaveOut, ref _headers[i],
                                           (uint)Marshal.SizeOf<WAVEHDR>());
                    _busy[i] = false;
                }
            }
            DrainQueue();
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hWaveOut != IntPtr.Zero)
        {
            waveOutReset(_hWaveOut);
            // Brief wait for callbacks to drain
            Thread.Sleep(50);
            for (int i = 0; i < BufferCount; i++)
                if (_busy[i])
                    waveOutUnprepareHeader(_hWaveOut, ref _headers[i],
                                           (uint)Marshal.SizeOf<WAVEHDR>());
            waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }

        for (int i = 0; i < BufferCount; i++)
            if (_dataPtrs[i] != IntPtr.Zero)
                Marshal.FreeHGlobal(_dataPtrs[i]);
    }
}
