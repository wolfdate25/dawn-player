using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Plays one cue-sheet track — a [start, end) range of a physical audio file — through the same
/// <see cref="ITrackReader"/> contract the sequencer uses. The sequencer treats it like any track:
/// draining the range triggers the normal gapless advance, and two consecutive cue tracks of the
/// same image chain sample-accurately (each has its own decoder of the parent file).
/// </summary>
public sealed class CueTrackReader : ITrackReader
{
    private readonly ITrackReader _inner;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;

    public CueTrackReader(ITrackReader inner, TimeSpan start, TimeSpan end)
    {
        _inner = inner;
        _start = start;
        _end = end >= start ? end : start;
        Samples = new RangeCutSampleProvider(inner, _end);
        // Position before exposing samples: the range provider only cuts at the far end.
        _inner.CurrentTime = _start;
    }

    public ISampleProvider Samples { get; }

    /// <summary>The wrapped reader. DoP detection must see through cue wrappers: a raw-DSD
    /// track inside a range reader still requires raw-passthrough playback.</summary>
    public ITrackReader Inner => _inner;

    public WaveFormat SourceFormat => _inner.SourceFormat;

    public TimeSpan TotalTime => _end - _start;

    /// <summary>Track-relative position; setting it maps onto the parent file's timeline.</summary>
    public TimeSpan CurrentTime
    {
        get
        {
            var rel = _inner.CurrentTime - _start;
            return rel < TimeSpan.Zero ? TimeSpan.Zero
                : rel > TotalTime ? TotalTime
                : rel;
        }
        set
        {
            var clamped = value < TimeSpan.Zero ? TimeSpan.Zero
                : value > TotalTime ? TotalTime
                : value;
            _inner.CurrentTime = _start + clamped;
        }
    }

    /// <summary>The physical file (fragment stripped) — logs and tag writes want the real path.</summary>
    public string Path => _inner.Path;

    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// Wraps the parent file's sample stream and stops it at the cue end. Reads probe the parent
    /// reader's position (an O(1) Position-derived property) once per block and never read past the
    /// boundary, so the sequencer sees a normal end-of-stream exactly at the track end.
    /// </summary>
    private sealed class RangeCutSampleProvider : ISampleProvider
    {
        private readonly ITrackReader _inner;
        private readonly TimeSpan _end;

        public RangeCutSampleProvider(ITrackReader inner, TimeSpan end)
        {
            _inner = inner;
            _end = end;
        }

        public WaveFormat WaveFormat => _inner.Samples.WaveFormat;

        public int Read(Span<float> buffer)
        {
            var remaining = _end - _inner.CurrentTime;
            if (remaining <= TimeSpan.Zero) return 0;

            var fmt = WaveFormat;
            int framesWanted = buffer.Length / fmt.Channels;
            // +1 frame of slack covers block-aligned position rounding at the boundary.
            int framesAvail = (int)Math.Ceiling(remaining.TotalSeconds * fmt.SampleRate) + 1;
            int frames = Math.Min(framesWanted, framesAvail);
            if (frames <= 0) return 0;

            return _inner.Samples.Read(buffer.Slice(0, frames * fmt.Channels));
        }
    }
}
