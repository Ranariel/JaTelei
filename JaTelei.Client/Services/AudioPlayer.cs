using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JaTelei.Client.Services;

/// <summary>
/// Minimal G711 µ-law (PCMU) → WaveOut player.
/// Decodes PCMU frames received via WebRTC and streams them to the default audio device.
/// Uses only Win32 waveOut P/Invoke — no extra NuGet dependencies.
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

    private const ushort WAVE_FORMAT_MULAW = 7;
    private const uint   WHDR_DONE         = 0x00000001;
    private const uint   WHDR_PREPARED     = 0x00000002;
    private const int    WAVE_MAPPER       = -1;

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

            // Decode µ-law → 16-bit PCM
            var pcm16 = new short[pcmu.Length];
            for (int i = 0; i < pcmu.Length; i++)
                pcm16[i] = MuLawToLinear(pcmu[i]);

            // Pin the PCM buffer and queue it via waveOut
            var gcPin = GCHandle.Alloc(pcm16, GCHandleType.Pinned);
            var hdr   = new WAVEHDR
            {
                lpData        = gcPin.AddrOfPinnedObject(),
                dwBufferLength = (uint)(pcm16.Length * 2),
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
        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = WAVE_FORMAT_MULAW,
            nChannels       = 1,
            nSamplesPerSec  = 8000,
            nAvgBytesPerSec = 8000,
            nBlockAlign     = 1,
            wBitsPerSample  = 8,
            cbSize          = 0,
        };
        int hr = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        _ready = (hr == 0);
        if (!_ready)
            File.AppendAllText(WebRtcService.LogPath,
                $"[AudioPlayer] {DateTime.Now}: waveOutOpen failed hr={hr}\n");
    }

    // Standard ITU-T G.711 µ-law decode table
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

    private static short MuLawToLinear(byte b) => MuLawTable[b];
}
