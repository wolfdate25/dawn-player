namespace DawnPlayer.Core.Audio;

/// <summary>Thread-safe byte queue between a PCM fill task and the render thread, shared by the
/// radio reader and the YouTube pipe reader. Termination is explicit and has two flavors:
/// <see cref="MarkDead"/> (source lost — the sample provider keeps emitting silence so a live
/// station never advances on a network hiccup) and <see cref="MarkEnded(int)"/> (a finite source
/// finished, or its process died — the buffer drains and the sample provider then reports
/// end-of-stream so the sequencer advances to the next track). Public for the generation-gate
/// contract tests.</summary>
public sealed class BufferedPcm
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _headChunkOffset;
    private long _bufferedBytes;
    private bool _dead;
    private bool _ended;
    // Generation of the producer that owns the buffer right now. A superseded producer (a seek
    // restarted the chain) must not add bytes or latch end-of-stream into the new producer's
    // buffer — checked and set under the same lock, so there is no check-act window.
    private int _generation;

    /// <summary>Adopts a new producer generation and drops everything the previous one left:
    /// buffered bytes AND the end-of-stream latch (only <see cref="MarkDead"/> is permanent).</summary>
    public void Reset(int generation)
    {
        lock (_lock)
        {
            _chunks.Clear();
            _headChunkOffset = 0;
            _bufferedBytes = 0;
            _ended = false;
            _generation = generation;
        }
    }

    /// <summary>Radio's enqueue: no generation gate (radio never resets its buffer).</summary>
    public void AddBytes(byte[] bytes, int count)
    {
        var copy = new byte[count];
        Array.Copy(bytes, copy, count);
        lock (_lock)
        {
            if (_dead) return;
            _chunks.Enqueue(copy);
            _bufferedBytes += count;
        }
    }

    /// <summary>Enqueues on behalf of a producer; returns false (dropping the bytes) when that
    /// producer has been superseded.</summary>
    public bool AddBytes(byte[] bytes, int count, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _dead) return false;
            var copy = new byte[count];
            Array.Copy(bytes, copy, count);
            _chunks.Enqueue(copy);
            _bufferedBytes += count;
            return true;
        }
    }

    /// <summary>Latches end-of-stream on behalf of a producer; a superseded producer can never
    /// end the new producer's track.</summary>
    public void MarkEnded(int generation)
    {
        lock (_lock)
        {
            if (generation == _generation) _ended = true;
        }
    }

    public void MarkDead()
    {
        lock (_lock) _dead = true;
    }

    public void MarkEnded()
    {
        lock (_lock) _ended = true;
    }

    /// <summary>True once the source is gone (dead or ended); already-buffered data still reads back.</summary>
    public bool HasEnded
    {
        get { lock (_lock) return _dead || _ended; }
    }

    public long BufferedBytes
    {
        get { lock (_lock) return _bufferedBytes; }
    }

    /// <summary>Discards buffered data and clears the ended latch. Kept for the radio-style
    /// unconditional clear; the YouTube reader uses <see cref="Reset"/> which also re-arms the
    /// generation gate.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
            _headChunkOffset = 0;
            _bufferedBytes = 0;
            _ended = false;
        }
    }

    /// <summary>Blocks until at least <paramref name="seconds"/> of 16-bit stereo-equivalent
    /// audio is buffered, or the timeout elapses. (Radio's historical 44.1 kHz arithmetic.)</summary>
    public void WaitUntilBuffered(double seconds, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_bufferedBytes >= seconds * 44100 * 4 || _dead) return;
            }
            Thread.Sleep(50);
        }
    }

    /// <summary>Format-aware variant: blocks until <paramref name="minBytes"/> are buffered, the
    /// source ends, or the timeout elapses.</summary>
    public void WaitUntilBufferedBytes(long minBytes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_bufferedBytes >= minBytes || _dead || _ended) return;
            }
            Thread.Sleep(50);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int total = 0;
            while (total < count && _chunks.Count > 0)
            {
                var chunk = _chunks.Peek();
                int available = chunk.Length - _headChunkOffset;
                int take = Math.Min(available, count - total);
                Array.Copy(chunk, _headChunkOffset, buffer, offset + total, take);
                _headChunkOffset += take;
                total += take;
                _bufferedBytes -= take;
                if (_headChunkOffset == chunk.Length)
                {
                    _chunks.Dequeue();
                    _headChunkOffset = 0;
                }
            }
            return total;
        }
    }
}
