using NAudio.Wave;

namespace JaTelei.Client.Services;

// =============================================================================
// WaveOutPlayer — 48kHz 16-bit stereo PCM playback via NAudio
// Per-stream software volume applied before buffering (no global waveOut hack).
// =============================================================================

public sealed class WaveOutPlayer : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private readonly object _lock = new();
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _waveOut;
    private bool _disposed;

    // Software volume: 0.0 = mute, 1.0 = full level, clamped in QueueAudio.
    public float Volume { get; set; } = 1.0f;

    public WaveOutPlayer()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 80,
            NumberOfBuffers = 3,
        };
        _waveOut.Init(_buffer);
        _waveOut.Play();
    }

    public void QueueAudio(byte[] pcm16)
    {
        if (pcm16 == null || pcm16.Length == 0) return;

        float vol = Volume;
        byte[] data;

        if (vol <= 0f)
        {
            // Silence — queue zeroed buffer to keep playback position advancing
            data = new byte[pcm16.Length];
        }
        else if (Math.Abs(vol - 1.0f) < 0.001f)
        {
            // Unity gain — no copy needed
            data = pcm16;
        }
        else
        {
            // Apply per-sample gain (int16 little-endian)
            data = new byte[pcm16.Length];
            for (int i = 0; i < pcm16.Length - 1; i += 2)
            {
                short sample  = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                float scaled  = sample * vol;
                short clamped = (short)Math.Clamp((int)scaled, short.MinValue, short.MaxValue);
                data[i]       = (byte)(clamped & 0xFF);
                data[i + 1]   = (byte)((clamped >> 8) & 0xFF);
            }
        }

        lock (_lock)
        {
            if (_disposed) return;
            _buffer.AddSamples(data, 0, data.Length);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
