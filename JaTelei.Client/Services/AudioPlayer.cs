using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JaTelei.Client.Services;

/// <summary>
/// G711 µ-law (PCMU) → WaveOut player using Win32 P/Invoke only.
///
/// Key design:
/// - Decodes µ-law → PCM16 in software (no ACM G.711 codec required).
/// - WAVEHDR and PCM data live in unmanaged heap memory so their addresses
///   remain valid after Play() returns and while waveOut still uses them.
/// - Time-based unprepare: sleeps for the buffer duration + margin instead
///   of polling WHDR_DONE on a stack copy of the header (which is wrong).
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
    private const int    WAVE_MAPPER     = -1;

    // Use IntPtr overloads so we can pass unmanaged memory (not ref to a stack struct)
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, int id, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);

    // ── State ─────────────────────────────────────────────────────────────────

    private static readonly object _lock = new();
    private static IntPtr  _hwo   = IntPtr.Zero;
    private static bool    _ready;
    private static int     _hdrSize = Marshal.SizeOf<WAVEHDR>();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queues one PCMU frame for playback. Thread-safe.
    /// </summary>
    public static void Play(byte[] pcmu)
    {
        if (pcmu == null || pcmu.Length == 0) return;

        // Open device lazily; capture handle under lock
        IntPtr hwo;
        lock (_lock)
        {
            if (!_ready) OpenDevice();
            if (!_ready) return;
            hwo = _hwo;
        }

        // Decode µ-law → PCM16 (no ACM codec needed)
        int samples  = pcmu.Length;
        int pcmBytes = samples * 2;

        // Allocate PCM buffer in unmanaged heap — stable address for waveOut
        IntPtr pcmPtr = Marshal.AllocHGlobal(pcmBytes);
        unsafe
        {
            short* dst = (short*)pcmPtr;
            for (int i = 0; i < samples; i++)
                dst[i] = MuLawTable[pcmu[i]];
        }

        // Allocate WAVEHDR in unmanaged heap — stable address for waveOut
        IntPtr hdrPtr = Marshal.AllocHGlobal(_hdrSize);
        {
            var h = new WAVEHDR { lpData = pcmPtr, dwBufferLength = (uint)pcmBytes };
            Marshal.StructureToPtr(h, hdrPtr, false);
        }

        waveOutPrepareHeader(hwo, hdrPtr, _hdrSize);
        waveOutWrite(hwo, hdrPtr, _hdrSize);

        // Free after playback: sleep for the buffer duration + 80 ms margin.
        // We must NOT free before waveOut finishes with the buffer.
        int waitMs = samples * 1000 / 8000 + 80;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(waitMs);
            waveOutUnprepareHeader(hwo, hdrPtr, _hdrSize);
            Marshal.FreeHGlobal(pcmPtr);
            Marshal.FreeHGlobal(hdrPtr);
        });
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static void OpenDevice()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = WAVE_FORMAT_PCM,
            nChannels       = 1,
            nSamplesPerSec  = 8000,
            nAvgBytesPerSec = 16000,   // 8000 samples/s × 2 bytes
            nBlockAlign     = 2,       // 1 ch × 2 bytes
            wBitsPerSample  = 16,
            cbSize          = 0,
        };
        int hr = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        _ready = (hr == 0);
        File.AppendAllText(WebRtcService.LogPath,
            $"[AudioPlayer] {DateTime.Now}: waveOutOpen hr={hr} ready={_ready}\n");
    }

    // ── ITU-T G.711 µ-law decode table ────────────────────────────────────────
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
