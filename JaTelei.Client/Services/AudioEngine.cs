using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace JaTelei.Client.Services;

// =============================================================================
// AudioEngine — Opus audio pipeline for JaTelei Media Engine v2
//
//  SENDER PATH (NAudio WasapiLoopback):
//    WasapiLoopbackCapture (system audio out) → resample to 48kHz stereo
//    → Concentus encode → RTP via _sendOpus delegate
//
//  RECEIVER PATH:
//    SIPSorcery OnRtpPacketReceived (opus RTP) → Concentus decode
//    → int16 48kHz stereo → waveOut (Windows WINMM)
//
//  Threading:
//    - Sender: NAudio DataAvailable callback (WASAPI thread); lock(_audioLock)
//      for encode queue. No polling thread needed.
//    - Receiver: called on SIPSorcery thread; waveOut queued asynchronously
//
//  Audio format:
//    Opus frame = 20ms at 48kHz stereo (960 frames per channel, 1920 samples total)
// =============================================================================

public sealed class AudioEngine : IAsyncDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int  OpusSampleRate   = 48_000;
    private const int  OpusChannels     = 2;           // stereo
    private const int  OpusFrameMs      = 20;          // 20 ms per packet
    private const int  OpusFrameSamples = OpusSampleRate * OpusFrameMs / 1000; // 960
    private const int  OpusMaxPayload   = 4000;        // bytes

    // ── Concentus encoder / decoder ─────────────────────────────────────────

    private readonly IOpusEncoder  _encoder;
    private readonly IOpusDecoder  _decoder;

    // ── Sender state ─────────────────────────────────────────────────────────

    private readonly Action<byte[]>? _sendOpus;
    private Thread?  _senderThread;
    private volatile bool _senderRunning;

    // PCM accumulation buffer at 48kHz stereo (interleaved)
    private readonly float[] _accumBuf   = new float[OpusFrameSamples * OpusChannels * 8];
    private int              _accumCount = 0;

    // Native format (filled when NAudio loopback starts)
    private int  _nativeSr = 0;
    private int  _nativeCh = 0;

    // Encoded output buffer
    private readonly byte[] _encodedBuf = new byte[OpusMaxPayload];

    // Lock protecting accumulation buffer + encode queue (NAudio callback thread)
    private readonly object _audioLock = new();

    // ── Receiver state ────────────────────────────────────────────────────────

    private readonly WaveOutPlayer? _player; // null = sender-only mode

    // ── Diagnostics ──────────────────────────────────────────────────────────

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jatelei_audio.txt");

    private static void Log(string msg) =>
        System.IO.File.AppendAllText(LogPath, $"[Audio] {DateTime.Now:HH:mm:ss.fff} {msg}\n");

    // =========================================================================
    // Construction
    // =========================================================================

    /// <param name="sendOpus">Delegate to send an encoded Opus payload via RTP.
    /// Pass null when this instance is used only on the receiver side.</param>
    /// <param name="player">Pass a WaveOutPlayer for the RECEIVER side.
    /// Pass null for sender-only.</param>
    public AudioEngine(Action<byte[]>? sendOpus, WaveOutPlayer? player)
    {
        _sendOpus = sendOpus;
        _player   = player;

        _encoder = OpusCodecFactory.CreateEncoder(OpusSampleRate, OpusChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate    = 96_000;
        _encoder.Complexity = 5;

        _decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, OpusChannels);

        Log($"AudioEngine created — sender={_sendOpus != null} receiver={player != null}");
    }

    // =========================================================================
    // SENDER — start/stop NAudio loopback capture thread
    // =========================================================================

    public void StartSender()
    {
        if (_sendOpus is null) return;
        if (_senderRunning) return;

        _senderRunning = true;
        _senderThread  = new Thread(SenderLoopNAudio)
        {
            IsBackground = true,
            Name         = "JaTelei-AudioSender",
        };
        _senderThread.Start();
        Log("Sender started (NAudio loopback)");
    }

    public void StopSender()
    {
        _senderRunning = false;
        _senderThread?.Join(1000);
        _senderThread = null;
        Log("Sender stopped");
    }

    // ── Sender loop (NAudio WasapiLoopbackCapture) ───────────────────────────

    private void SenderLoopNAudio()
    {
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            var fmt = capture.WaveFormat;
            _nativeSr = fmt.SampleRate;
            _nativeCh = fmt.Channels;
            Log($"NAudio loopback: {fmt.SampleRate}Hz {fmt.Channels}ch {fmt.BitsPerSample}bit enc={fmt.Encoding}");

            double srcToOpus = (double)OpusSampleRate / fmt.SampleRate;

            capture.DataAvailable += (s, e) =>
            {
                if (!_senderRunning || e.BytesRecorded == 0) return;
                try
                {
                    var floats      = PcmToFloat(e.Buffer, e.BytesRecorded, fmt);
                    int nativeFrames = floats.Length / _nativeCh;
                    lock (_audioLock)
                    {
                        ResampleAndAccumulate(floats, nativeFrames, srcToOpus);
                        FlushEncodeQueue();
                    }
                }
                catch (Exception ex)
                {
                    Log($"DataAvailable: {ex.GetType().Name}: {ex.Message}");
                }
            };

            capture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    Log($"RecordingStopped exception: {e.Exception.GetType().Name}: {e.Exception.Message}");
            };

            capture.StartRecording();
            Log("WasapiLoopbackCapture started");

            while (_senderRunning)
                Thread.Sleep(50);

            capture.StopRecording();
        }
        catch (Exception ex)
        {
            Log($"SenderLoopNAudio: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { capture?.Dispose(); } catch { }
        }
    }

    // ── PCM format conversion → float32 ──────────────────────────────────────

    private static float[] PcmToFloat(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            int samples = bytesRecorded / sizeof(float);
            var result  = new float[samples];
            Buffer.BlockCopy(buffer, 0, result, 0, samples * sizeof(float));
            return result;
        }
        else if (fmt.BitsPerSample == 16)
        {
            int samples = bytesRecorded / 2;
            var result  = new float[samples];
            for (int i = 0; i < samples; i++)
                result[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return result;
        }
        else if (fmt.BitsPerSample == 32)
        {
            int samples = bytesRecorded / 4;
            var result  = new float[samples];
            for (int i = 0; i < samples; i++)
                result[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
            return result;
        }
        Log($"Unsupported NAudio format: {fmt.BitsPerSample}bit {fmt.Encoding}");
        return Array.Empty<float>();
    }

    // ── Encode all complete Opus frames from accumulation buffer ──────────────

    private void FlushEncodeQueue()
    {
        int opusStereoSamples = OpusFrameSamples * OpusChannels;
        while (_accumCount >= opusStereoSamples)
        {
            int encoded = _encoder.Encode(
                _accumBuf.AsSpan(0, opusStereoSamples),
                OpusFrameSamples,
                _encodedBuf.AsSpan(),
                _encodedBuf.Length);

            if (encoded > 0)
            {
                var payload = new byte[encoded];
                Buffer.BlockCopy(_encodedBuf, 0, payload, 0, encoded);
                _sendOpus!(payload);
            }

            int remaining = _accumCount - opusStereoSamples;
            if (remaining > 0)
                Buffer.BlockCopy(_accumBuf, opusStereoSamples * sizeof(float),
                                 _accumBuf, 0, remaining * sizeof(float));
            _accumCount = remaining;
        }
    }

    // ── Resample (linear interpolation) native → 48kHz stereo ────────────────

    private void ResampleAndAccumulate(float[] nativePcm, int nativeFrames, double srcToOpus)
    {
        int outFrames = (int)Math.Ceiling(nativeFrames * srcToOpus);
        int needed    = outFrames * OpusChannels;

        if (_accumCount + needed > _accumBuf.Length)
        {
            int drop = (_accumCount + needed) - _accumBuf.Length;
            drop = Math.Min(drop, _accumCount);
            if (drop > 0)
            {
                Buffer.BlockCopy(_accumBuf, drop * sizeof(float),
                                 _accumBuf, 0, (_accumCount - drop) * sizeof(float));
                _accumCount -= drop;
            }
        }

        for (int outF = 0; outF < outFrames; outF++)
        {
            double srcPos = outF / srcToOpus;
            int    srcI   = (int)srcPos;
            float  t      = (float)(srcPos - srcI);
            int    srcI1  = Math.Min(srcI + 1, nativeFrames - 1);

            float l0 = nativePcm[srcI  * _nativeCh + 0];
            float l1 = nativePcm[srcI1 * _nativeCh + 0];
            float l  = l0 + t * (l1 - l0);

            float r0 = _nativeCh > 1 ? nativePcm[srcI  * _nativeCh + 1] : l0;
            float r1 = _nativeCh > 1 ? nativePcm[srcI1 * _nativeCh + 1] : l1;
            float r  = r0 + t * (r1 - r0);

            if (_accumCount + 2 <= _accumBuf.Length)
            {
                _accumBuf[_accumCount++] = l;
                _accumBuf[_accumCount++] = r;
            }
        }
    }

    // =========================================================================
    // RECEIVER — decode incoming Opus RTP payload → waveOut
    // =========================================================================

    private readonly short[] _decodeBuf = new short[OpusFrameSamples * OpusChannels * 4];

    /// <summary>
    /// Call this from SIPSorcery's OnRtpPacketReceived event.
    /// Decodes the Opus payload and queues it to the waveOut player.
    /// </summary>
    public void OnOpusReceived(byte[] payload)
    {
        if (_player is null || payload is null || payload.Length == 0) return;

        try
        {
            int frames = _decoder.Decode(payload.AsSpan(), _decodeBuf.AsSpan(), OpusFrameSamples, false);
            if (frames > 0)
            {
                int samples = frames * OpusChannels;
                var pcm16   = new byte[samples * 2];
                Buffer.BlockCopy(_decodeBuf, 0, pcm16, 0, pcm16.Length);
                _player.QueueAudio(pcm16);
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR in OnOpusReceived: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // =========================================================================
    // Dispose
    // =========================================================================

    public ValueTask DisposeAsync()
    {
        StopSender();
        _player?.Dispose();
        return ValueTask.CompletedTask;
    }
}
