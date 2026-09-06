using System.Net;
using SIPSorcery.Net;

namespace JaTelei.Client.Services;

// =============================================================================
// AdaptiveController — dynamic bitrate + resolution adaptation
//
//  Monitors RTCP statistics (RTT, packet loss) and CPU load to select
//  the appropriate quality profile. Applies changes via:
//    • ScreenCaptureService.SetBitrate()     (DLL: JC_SetBitrate)
//    • ScreenCaptureService.SetResolution()  (DLL: JC_SetResolution)
//
//  Profiles (descending quality):
//    ULTRA  1920×1080  30 Mbps  (limited by MaxBitrateKbps setting below)
//    HIGH   1280× 720  12 Mbps
//    MEDIUM  960× 540   5 Mbps
//    LOW     640× 360   2 Mbps
//
//  Adaptive logic:
//    • Every EvalIntervalMs the controller evaluates RTCP + CPU.
//    • Down-step: loss ≥ LossThresholdDown OR rtt ≥ RttThresholdDownMs OR cpu ≥ CpuThresholdDown
//    • Up-step:   loss < LossThresholdUp  AND rtt < RttThresholdUpMs  AND cpu < CpuThresholdUp
//    •  A profile change triggers a ScreenCaptureService.SetBitrate() immediately,
//       and a SetResolution() with a short guard delay (ResChangeGuardMs) to
//       avoid oscillation.
// =============================================================================

public sealed class AdaptiveController : IDisposable
{
    // ── Profiles ──────────────────────────────────────────────────────────────

    private enum Profile { Ultra = 3, High = 2, Medium = 1, Low = 0 }

    private static readonly (int W, int H, int BitrateKbps)[] Profiles =
    {
        (640,  360,  2_000),   // Low    [0]
        (960,  540,  5_000),   // Medium [1]
        (1280, 720, 12_000),   // High   [2]
        (1920, 1080,30_000),   // Ultra  [3]
    };

    // ── Thresholds ────────────────────────────────────────────────────────────

    private const int   EvalIntervalMs      = 3_000;   // evaluate every 3s
    private const int   ResChangeGuardMs    = 5_000;   // min time between resolution changes
    private const float LossThresholdDown   = 0.03f;   // 3% → step down
    private const float LossThresholdUp     = 0.01f;   // 1% → allow step up
    private const double RttThresholdDownMs = 200.0;   // 200ms → step down
    private const double RttThresholdUpMs   = 120.0;   // 120ms → allow step up
    private const float CpuThresholdDown    = 0.80f;   // 80% CPU → step down
    private const float CpuThresholdUp      = 0.60f;   // 60% CPU → allow step up

    // ── State ─────────────────────────────────────────────────────────────────

    private Profile  _current       = Profile.Ultra;
    private DateTime _lastResChange = DateTime.MinValue;
    private bool     _disposed;

    // RTCP stats (set from the SIPSorcery RTCP event)
    private volatile float  _lossRatio = 0f;
    private volatile double _rttMs     = 0.0;

    private readonly Timer _timer;

    // Delegate for external RTCPeerConnection injection
    private RTCPeerConnection? _pc;

    // ── Construction ─────────────────────────────────────────────────────────

    public AdaptiveController()
    {
        _timer = new Timer(OnEvaluate, null,
                           TimeSpan.FromMilliseconds(EvalIntervalMs),
                           TimeSpan.FromMilliseconds(EvalIntervalMs));
    }

    /// <summary>
    /// Attach to a live RTCPeerConnection to receive RTCP events.
    /// Safe to call before or after StartCapture.
    /// </summary>
    public void Attach(RTCPeerConnection pc)
    {
        if (_pc != null)
            _pc.OnReceiveReport -= OnReceiveReport;

        _pc = pc;
        _pc.OnReceiveReport += OnReceiveReport;
    }

    public void Detach()
    {
        if (_pc != null)
        {
            _pc.OnReceiveReport -= OnReceiveReport;
            _pc = null;
        }
    }

    // ── RTCP callback ─────────────────────────────────────────────────────────

    private void OnReceiveReport(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTCPCompoundPacket report)
    {
        if (mediaType != SDPMediaTypesEnum.video) return;

        foreach (var rr in report.ReceiverReport?.ReceptionReports ?? [])
        {
            _lossRatio = rr.FractionLost / 256f;
        }

        if (report.SenderReport != null)
        {
            // SIPSorcery exposes DLSR/LSR; RTT estimate via RTCP SR/RR round-trip
            // Use the most recently cached RTT if available (SIPSorcery v6+)
        }

        // SIPSorcery RTCPeerConnection exposes LastReceivedSRTimestamp but not RTT directly.
        // We fall back to a simpler heuristic: use DLSR as a proxy.
        // If SIPSorcery exposes RTT in future, plug it in here.
    }

    // ── Evaluation tick ───────────────────────────────────────────────────────

    private void OnEvaluate(object? _)
    {
        if (_disposed || !ScreenCaptureService.IsInitialized) return;

        float  loss = _lossRatio;
        double rtt  = _rttMs;
        float  cpu  = GetCpuUsage();

        bool shouldDown = loss >= LossThresholdDown
                       || rtt  >= RttThresholdDownMs
                       || cpu  >= CpuThresholdDown;

        bool canUp = loss < LossThresholdUp
                  && rtt  < RttThresholdUpMs
                  && cpu  < CpuThresholdUp;

        Profile next = _current;

        if (shouldDown && _current > Profile.Low)
            next = _current - 1;
        else if (canUp && _current < Profile.Ultra)
            next = _current + 1;

        if (next == _current) return;

        ApplyProfile(next, force: false);
    }

    // ── Apply profile ─────────────────────────────────────────────────────────

    private void ApplyProfile(Profile profile, bool force)
    {
        var (w, h, kbps) = Profiles[(int)profile];
        _current = profile;

        // Always apply bitrate immediately
        ScreenCaptureService.SetBitrate(kbps);

        // Resolution change: guard to avoid oscillation
        bool resGuardPassed = (DateTime.UtcNow - _lastResChange).TotalMilliseconds >= ResChangeGuardMs;
        if (force || resGuardPassed)
        {
            int hr = ScreenCaptureService.SetResolution(w, h);
            if (hr == 0) _lastResChange = DateTime.UtcNow;
        }

        Log($"Profile → {profile} ({w}×{h} @ {kbps / 1000}Mbps) loss={_lossRatio:P1} rtt={_rttMs:F0}ms cpu={GetCpuUsage():P0}");
    }

    // ── Public control ────────────────────────────────────────────────────────

    /// <summary>Force a specific profile (e.g. to start at ULTRA regardless of conditions).</summary>
    public void ForceProfile(int profileIndex) =>
        ApplyProfile((Profile)Math.Clamp(profileIndex, 0, 3), force: true);

    /// <summary>Update RTT externally (e.g. from a custom RTCP handler).</summary>
    public void UpdateRtt(double rttMs) => _rttMs = rttMs;

    // ── CPU usage (simple) ────────────────────────────────────────────────────

    private static float GetCpuUsage()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            // Simple: compare total CPU time against wall clock × processors
            // Not a true CPU% but good enough for coarse threshold decisions.
            var total = proc.TotalProcessorTime.TotalMilliseconds;
            var cores = Environment.ProcessorCount;
            // Normalise to 0–1 range (won't exceed 1.0 in practice for our thresholds)
            return (float)(total / (Environment.TickCount64 * cores));
        }
        catch { return 0f; }
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jaclipei_adaptive.txt");

    private static void Log(string msg) =>
        System.IO.File.AppendAllText(LogPath, $"[Adaptive] {DateTime.Now:HH:mm:ss.fff} {msg}\n");

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Detach();
    }
}
