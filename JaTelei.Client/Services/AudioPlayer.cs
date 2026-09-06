using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JaTelei.Client.Services;

/// <summary>
/// Minimal G711 µ-law (PCMU) → WaveOut player.
/// Decodes PCMU frames received via WebRTC and streams them to the default audio device.
/// Uses only Win32 waveOut P/Invoke — no extra NuGet dependencies.
///
/// Deliberately uses WAVE_FORMAT_PCM (not WAVE_FORMAT_MULAW) because the G.711 ACM
/// codec required by MULAW waveOut is not guaranteed to be present on all Windows
/// installations. We decode µ-law → PCM16 in software instead.
/// </summary>
internal static class AudioPlayer
{
    // ── Win32 waveOut ────────────────────────────────────────────────────────

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

    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint   WHDR_DONE       = 0x00000001;
    private const int    WAVE_MAPPER     = -1;

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, int id, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);

    // ── State ─────────────────────────────────────────────────────────────────

    private static readonly object _lock = new();
    private static IntPtr  _hwo   = IntPtr.Zero;
    private static bool    _ready;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a raw PCMU (G711 µ-law) frame for playback.
    /// Thread-safe; safe to call from the WebRTC receive callback.
    /// </summary>
    public static void Play(byte[] pcmu)
    {
        if (pcmu == null || pcmu.Length == 0) return;

        lock (_lock)
        {
            if (!_ready) OpenDevice();
            if (!_ready) return;

            // Decode µ-law → 16-bit PCM in software (no ACM codec needed)
            var pcm16 = new short[pcmu.Length];
            for (int i = 0; i < pcmu.Length; i++)
                pcm16[i] = MuLawTable[pcmu[i]];

            // Pin the PCM buffer and queue it via waveOut
            var gcPin = GCHandle.Alloc(pcm16, GCHandleType.Pinned);
            var hdr   = new WAVEHDR
            {
                lpData         = gcPin.AddrOfPinnedObject(),
                dwBufferLength = (uint)(pcm16.Length * 2),  // bytes = samples × 2 (16-bit)
            };
            int sz = Marshal.SizeOf<WAVEHDR>();
            waveOutPrepareHeader(_hwo, ref hdr, sz);
            waveOutWrite(_hwo, ref hdr, sz);

            // Unprepare asynchronously once WaveOut is done with the buffer
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Poll until done (WHDR_DONE) — typical latency < 10 ms at 8 kHz
                int spins = 0;
                while ((hdr.dwFlags & WHDR_DONE) == 0 && spins++ < 500)
                    Thread.Sleep(2);
                waveOutUnprepareHeader(_hwo, ref hdr, sz);
                gcPin.Free();
            });
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static void OpenDevice()
    {
        // PCM, 8 kHz, mono, 16-bit — supported by every Windows audio driver
        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = WAVE_FORMAT_PCM,
            nChannels       = 1,
            nSamplesPerSec  = 8000,
            nAvgBytesPerSec = 8000 * 2,    // 2 bytes per sample
            nBlockAlign     = 2,            // 1 channel × 2 bytes
            wBitsPerSample  = 16,
            cbSize          = 0,
        };
        int hr = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        _ready = (hr == 0);
        if (!_ready)
            File.AppendAllText(WebRtcService.LogPath,
                $"[AudioPlayer] {DateTime.Now}: waveOutOpen WAVE_FORMAT_PCM failed hr={hr}\n");
        else
            File.AppendAllText(WebRtcService.LogPath,
                $"[AudioPlayer] {DateTime.Now}: waveOutOpen OK (PCM 8kHz mono 16-bit)\n");
    }

    // ── ITU-T G.711 µ-law decode table ────────────────────────────────────────
    // Built once at startup. Formula verified against the ITU-T reference:
    //   v  = ~byte & 0xFF  (invert all bits)
    //   x  = ((mantissa << 3) + 0x84) << exponent
    //   out = positive if sign bit set, negative otherwise (offset by 0x84 bias)
    private static readonly short[] MuLawTable = BuildMuLawTable();
    private static short[] BuildMuLawTable()
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = (~i) & 0xFF;
            int x = (((v & 0x0F) << 3) + 0x84) << ((v & 0x70) >> 4);
            t[i] = (short)((v & 0x80) != 0 ? (0x84 - x) : (x - 0x84));
        }
        return t;
    }
}
