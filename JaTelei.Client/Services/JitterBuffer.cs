namespace JaTelei.Client.Services;

// =============================================================================
// JitterBuffer — sequence-number based video frame reorder buffer
//
//  Holds out-of-order encoded video frames and releases them in sequence-
//  number order.  Designed for the sender side where the network may reorder
//  RTP packets — and for the receiver where SIPSorcery delivers frames that
//  may arrive slightly out of order.
//
//  Capacity:     max 8 frames held simultaneously
//  Timeout:      200ms — a frame waited longer than this is released regardless
//                of ordering (prevents head-of-line blocking)
//  Thread-safe:  yes (fine-grained lock per operation)
//
//  Usage (sender):
//    buffer.Push(seq, data, isKey, pts);
//    while (buffer.TryPop(out var f)) SendFrame(f);
//
//  Usage (receiver):
//    buffer.Push(seq, data, isKey, pts);
//    while (buffer.TryPop(out var f)) DecodeFrame(f);
// =============================================================================

public sealed class JitterBuffer
{
    // ── Frame record ─────────────────────────────────────────────────────────

    public sealed class JitterFrame
    {
        public ushort   Seq;
        public byte[]   Data    = Array.Empty<byte>();
        public bool     IsKey;
        public long     Pts;           // 100-ns ticks (MF/JC units)
        public DateTime Inserted;      // wall-clock insertion time
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    private const int     MaxCapacity = 8;
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(200);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly List<JitterFrame> _buf = new(MaxCapacity + 1);
    private readonly object            _lock = new();

    private ushort  _nextSeq    = 0;      // sequence number we expect next
    private bool    _seeded     = false;  // have we received the first frame?

    // ── Push ──────────────────────────────────────────────────────────────────

    /// <summary>Insert an encoded frame into the buffer.</summary>
    public void Push(ushort seq, byte[] data, bool isKey, long pts)
    {
        lock (_lock)
        {
            if (!_seeded)
            {
                _nextSeq = seq;
                _seeded  = true;
            }

            // Drop if already past this sequence
            if (!IsNewerOrEqual(seq, _nextSeq)) return;

            // Drop duplicates
            foreach (var f in _buf)
                if (f.Seq == seq) return;

            // Evict oldest when at capacity
            if (_buf.Count >= MaxCapacity)
                _buf.RemoveAt(0);

            _buf.Add(new JitterFrame
            {
                Seq      = seq,
                Data     = data,
                IsKey    = isKey,
                Pts      = pts,
                Inserted = DateTime.UtcNow,
            });

            // Keep sorted by sequence number
            _buf.Sort((a, b) => SequenceCompare(a.Seq, b.Seq));
        }
    }

    // ── Pop ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Try to pop the next in-order frame.  Returns true if a frame was available.
    /// A frame is returned if:
    ///   (a) it has the expected sequence number, OR
    ///   (b) it has waited longer than <see cref="MaxWait"/> (timeout path).
    /// </summary>
    public bool TryPop(out JitterFrame? frame)
    {
        lock (_lock)
        {
            if (_buf.Count == 0) { frame = null; return false; }

            var candidate = _buf[0];

            bool inOrder  = candidate.Seq == _nextSeq;
            bool timedOut = (DateTime.UtcNow - candidate.Inserted) >= MaxWait;

            if (inOrder || timedOut)
            {
                _buf.RemoveAt(0);
                _nextSeq = (ushort)(_nextSeq + 1);
                frame = candidate;
                return true;
            }

            frame = null;
            return false;
        }
    }

    /// <summary>Drain ALL buffered frames immediately (e.g. on key-frame reset).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            _buf.Clear();
            _seeded = false;
        }
    }

    public int Count { get { lock (_lock) return _buf.Count; } }

    // ── Sequence arithmetic (handles 16-bit wraparound) ───────────────────────

    private static bool IsNewerOrEqual(ushort a, ushort b)
    {
        // RFC 3550 §A.1 — within half the sequence-number space
        const int Half = 32768;
        return a == b || (ushort)(a - b) < Half;
    }

    private static int SequenceCompare(ushort a, ushort b)
    {
        const int Half = 32768;
        int diff = (ushort)(a - b);
        if (diff == 0)  return 0;
        if (diff < Half) return 1;   // a is after b
        return -1;                    // a is before b
    }
}
