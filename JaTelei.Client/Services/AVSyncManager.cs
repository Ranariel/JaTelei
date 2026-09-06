namespace JaTelei.Client.Services;

// =============================================================================
// AVSyncManager — audio/video PTS drift detection and compensation
//
//  Tracks the PTS of the most recent audio and video frames to detect
//  synchronisation drift.  When drift exceeds a threshold the manager
//  calls registered handlers so callers can compensate (e.g. drop/repeat
//  video frames, adjust audio clock).
//
//  PTS units: 100-nanosecond ticks (consistent with Media Foundation / JC_VideoFrame.pts).
//  1 ms = 10 000 ticks.  1 s = 10 000 000 ticks.
//
//  Drift formula:  drift = videoPts − audioPts
//    Positive drift → video is ahead of audio  → slow video / wait
//    Negative drift → audio is ahead of video  → speed video / drop audio
//
//  Correction thresholds:
//    |drift| <  30 ms → ignored (within acceptable sync window)
//    |drift| <  80 ms → logged only
//    |drift| ≥  80 ms → OnDrift event fired
//    |drift| ≥ 300 ms → hard reset (treat as new stream / discontinuity)
// =============================================================================

public sealed class AVSyncManager
{
    // ── Ticks constants ───────────────────────────────────────────────────────

    private const long TicksPerMs = 10_000L;

    private const long ToleranceTicks    =  30L * TicksPerMs;   // 30ms — ignore
    private const long WarnTicks         =  80L * TicksPerMs;   // 80ms — fire event
    private const long ResetTicks        = 300L * TicksPerMs;   // 300ms — discontinuity

    // ── State ─────────────────────────────────────────────────────────────────

    private long _lastVideoPts = long.MinValue;
    private long _lastAudioPts = long.MinValue;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when drift exceeds <see cref="WarnTicks"/> (80ms).
    /// Argument: drift in milliseconds (positive = video ahead, negative = audio ahead).
    /// </summary>
    public event Action<double>? OnDrift;

    /// <summary>
    /// Fired when drift exceeds <see cref="ResetTicks"/> (300ms) — treat as discontinuity.
    /// </summary>
    public event Action? OnDiscontinuity;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Report the PTS of the latest decoded video frame (100-ns ticks).
    /// </summary>
    public void ReportVideo(long pts)
    {
        _lastVideoPts = pts;
        Evaluate();
    }

    /// <summary>
    /// Report the PTS of the latest decoded audio packet (100-ns ticks).
    /// </summary>
    public void ReportAudio(long pts)
    {
        _lastAudioPts = pts;
        Evaluate();
    }

    /// <summary>Current A/V drift in milliseconds (positive = video ahead).</summary>
    public double DriftMs
    {
        get
        {
            if (_lastVideoPts == long.MinValue || _lastAudioPts == long.MinValue)
                return 0.0;
            return (_lastVideoPts - _lastAudioPts) / (double)TicksPerMs;
        }
    }

    public void Reset()
    {
        _lastVideoPts = long.MinValue;
        _lastAudioPts = long.MinValue;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void Evaluate()
    {
        if (_lastVideoPts == long.MinValue || _lastAudioPts == long.MinValue)
            return;

        long drift = Math.Abs(_lastVideoPts - _lastAudioPts);

        if (drift >= ResetTicks)
        {
            OnDiscontinuity?.Invoke();
            Reset();
        }
        else if (drift >= WarnTicks)
        {
            double driftMs = (_lastVideoPts - _lastAudioPts) / (double)TicksPerMs;
            OnDrift?.Invoke(driftMs);
        }
        // Below ToleranceTicks: do nothing
    }
}
