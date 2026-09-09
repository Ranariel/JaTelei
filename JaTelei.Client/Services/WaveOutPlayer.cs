using NAudio.Wave;

namespace JaTelei.Client.Services;

// =============================================================================
// WaveOutPlayer — 48kHz 16-bit stereo PCM playback via NAudio
// =============================================================================

public sealed class WaveOutPlayer : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private readonly object _lock = new();
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _waveOut;
    private bool _disposed;

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

        lock (_lock)
        {
            if (_disposed) return;
            _buffer.AddSamples(pcm16, 0, pcm16.Length);
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
