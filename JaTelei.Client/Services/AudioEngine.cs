using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;

namespace JaTelei.Client.Services;

// =============================================================================
// AudioEngine — Opus audio pipeline for JaTelei Media Engine v2
//
//  SENDER PATH:
//    JC_GetPcmAudio (float32 native-rate) → resample to 48kHz → Concentus encode
//    → RTP via SIPSorcery AudioExtrasSource.SendAudioFrame(opus)
//
//  RECEIVER PATH:
//    SIPSorcery OnAudioFrameReceived (opus RTP) → Concentus decode
//    → float32 48kHz → waveOut (Windows WINMM)
//
//  Threading:
//    - Sender: one dedicated thread polling JC_GetPcmAudio at ~10ms intervals
//    - Receiver: called on SIPSorcery thread; waveOut queued asynchronously
//
//  Audio format:
//    Opus frame = 20ms at 48kHz stereo (960 frames per channel, 1920 samples total)
//    Concentus supports 8/12/16/24/48 kHz; we always work at 48kHz internally.
// =============================================================================

public sealed class AudioEngine : IAsyncDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int  OpusSampleRate   = 48_000;
    private const int  OpusChannels     = 2;           // stereo
    private const int  OpusFrameMs      = 20;          // 20 ms per packet
    private const int  OpusFrameSamples = OpusSampleRate * OpusFrameMs / 1000; // 960
    private const int  OpusMaxPayload   = 4000;        // bytes — plenty for 20ms stereo
    private const int  PollIntervalMs   = 10;

    // ── Concentus encoder / decoder ─────────────────────────────────────────

    private readonly IOpusEncoder  _encoder;
    private readonly IOpusDecoder  _decoder;

    // ── Sender state ─────────────────────────────────────────────────────────

    // Callback: caller provides a delegate to actually send the Opus RTP payload.
    // (WebRtcService passes: payload => _pc.SendAudio(rtpDuration, payload))
    private readonly Action<byte[]>? _sendOpus;
    private Thread?  _senderThread;
    private volatile bool _senderRunning;

    // PCM accumulation buffer at 48kHz stereo (interleaved)
    private readonly float[] _accumBuf   = new float[OpusFrameSamples * OpusChannels * 8]; // 8 frames headroom
    private int              _accumCount = 0; // samples (not frames) accumulated so far

    // Resampler state (simple linear for now; only kicks in when native != 48kHz)
    private int  _nativeSr   = 0;
    private int  _nativeCh   = 0;

    // PCM poll buffer (one large block per poll)
    private readonly float[] _pollBuf = new float[OpusSampleRate]; // 1 second of mono at 48kHz

    // Encoded output buffer
    private readonly byte[]  _encodedBuf = new byte[OpusMaxPayload];

    // ── Receiver state ────────────────────────────────────────────────────────

    private readonly WaveOutPlayer? _player; // null = sender-only mode

    // ── Diagnostics ──────────────────────────────────────────────────────────

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jaclipei_audio.txt");

    private static void Log(string msg) =>
        System.IO.File.AppendAllText(LogPath, $"[Audio] {DateTime.Now:HH:mm:ss.fff} {msg}\n");

    // =========================================================================
    // Construction
    // =========================================================================

    /// <param name="sendOpus">Delegate to send an encoded Opus payload via RTP.
    /// Typically: <c>payload => _pc.SendAudio(rtpDurationUnits, payload)</c>.
    /// Pass null when this instance is used only on the receiver side.</param>
    /// <param name="player">Pass a WaveOutPlayer when this is the RECEIVER.
    /// Pass null when this instance is used only on the sender side.</param>
    public AudioEngine(Action<byte[]>? sendOpus, WaveOutPlayer? player)
    {
        _sendOpus = sendOpus;
        _player   = player;

        _encoder = OpusCodecFactory.CreateEncoder(OpusSampleRate, OpusChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate    = 96_000;   // 96 kbps stereo — good quality for screen-share audio
        _encoder.Complexity = 5;        // 0-10; 5 is a solid balance

        _decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, OpusChannels);

        Log($"AudioEngine created — sender={_sendOpus != null} receiver={player != null}");
    }

    // =========================================================================
    // SENDER — start/stop the PCM poll-and-encode thread
    // =========================================================================

    public void StartSender()
    {
        if (_sendOpus is null) return;
        if (_senderRunning) return;

        _senderRunning = true;
        _senderThread  = new Thread(SenderLoop)
        {
            IsBackground = true,
            Name         = "JaTelei-AudioSender",
        };
        _senderThread.Start();
        Log("Sender started");
    }

    public void StopSender()
    {
        _senderRunning = false;
        _senderThread?.Join(500);
        _senderThread = null;
        Log("Sender stopped");
    }

    // ── Sender loop ──────────────────────────────────────────────────────────

    private void SenderLoop()
    {
        // Warm-up: wait until DLL reports audio format (WASAPI started)
        for (int i = 0; i < 100 && _senderRunning; i++)
        {
            var (sr, ch) = ScreenCaptureService.GetAudioFormat();
            if (sr > 0 && ch > 0)
            {
                _nativeSr = sr;
                _nativeCh = ch;
                break;
            }
            Thread.Sleep(50);
        }

        if (_nativeSr <= 0)
        {
            Log("WARN: DLL reported no audio format — audio sender idle");
            return;
        }

        Log($"Sender loop: native {_nativeSr}Hz {_nativeCh}ch → Opus {OpusSampleRate}Hz {OpusChannels}ch");

        int maxNativeFrames = _nativeSr / (1000 / PollIntervalMs) * 4; // 4x poll interval headroom
        // Shared resampler state (linear interpolation)
        double srcToOpus = (double)OpusSampleRate / _nativeSr;

        while (_senderRunning)
        {
            try
            {
                // 1. Drain raw PCM from DLL
                int frames = ScreenCaptureService.GetPcmAudio(_pollBuf, maxNativeFrames);
                if (frames > 0)
                {
                    // 2. Resample to 48kHz stereo and accumulate
                    ResampleAndAccumulate(_pollBuf, frames, srcToOpus);
                }

                // 3. Encode and send complete Opus frames
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

                    // Shift accumulation buffer
                    int remaining = _accumCount - opusStereoSamples;
                    if (remaining > 0)
                        Buffer.BlockCopy(_accumBuf, opusStereoSamples * sizeof(float),
                                         _accumBuf, 0, remaining * sizeof(float));
                    _accumCount = remaining;
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR in SenderLoop: {ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    // ── Resample (linear interpolation) native → 48kHz stereo ──────────────

    private void ResampleAndAccumulate(float[] nativePcm, int nativeFrames, double srcToOpus)
    {
        // nativePcm is interleaved with _nativeCh channels
        // We need to produce stereo 48kHz output
        int outFrames = (int)Math.Ceiling(nativeFrames * srcToOpus);
        int needed    = outFrames * OpusChannels;

        // Ensure accumulation buffer has space
        if (_accumCount + needed > _accumBuf.Length)
        {
            // Drop oldest samples if we're overflowing (shouldn't happen in steady state)
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

            // Left / mono channel
            float l0 = nativePcm[srcI  * _nativeCh + 0];
            float l1 = nativePcm[srcI1 * _nativeCh + 0];
            float l  = l0 + t * (l1 - l0);

            // Right channel (mirror left if mono source)
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
    /// Call this from SIPSorcery's OnAudioFrameReceived event.
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
                // Convert int16 stereo to byte[] and pass to waveOut
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
