using System.Threading;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// A reader whose transport can stall mid-play (network radio, YouTube pipe): while the
/// source is alive but underrun, the sample provider serves silence instead of ending the
/// track, and this flag is the only way the controller can tell "playing silence" from
/// "playing audio". The controller exposes it through <see cref="PlaybackController.IsBuffering"/>
/// so the UI can show buffering feedback. Read-side sets, fill-side clears — both are single
/// volatile writes, safe from the audio and fill threads without locks.
/// </summary>
public interface IStreamStallSource
{
    /// <summary>True while the reader is alive but serving (or waiting for) data it does not
    /// have; false once the buffer holds at least the resume threshold again.</summary>
    bool IsStalled { get; }
}

/// <summary>
/// Pure stall bookkeeping behind <see cref="IStreamStallSource"/>: the sample provider raises
/// <see cref="NotifyServedSilence"/> whenever it had to pad with silence while the source was
/// alive, and the fill loop raises <see cref="NotifyBufferFilled"/> after each enqueue. Hysteresis
/// is deliberate — one refilled chunk must not clear a stall that the next read would immediately
/// re-assert, so the stall clears only when the buffer holds a whole resume threshold.
/// </summary>
public sealed class StreamStallTracker : IStreamStallSource
{
    private const double ResumeSeconds = 0.25;

    private volatile int _stalled;

    public bool IsStalled => _stalled != 0;

    /// <summary>Called from the read path when silence had to be served while the source is alive.
    /// Idempotent; never clears.</summary>
    public void NotifyServedSilence() => _stalled = 1;

    /// <summary>Called from the fill path after enqueuing bytes. Clears the stall only when the
    /// buffer holds at least <see cref="ResumeSeconds"/> of audio.</summary>
    public void NotifyBufferFilled(long bufferedBytes, long bytesPerSecond)
    {
        if (bufferedBytes >= (long)(ResumeSeconds * bytesPerSecond)) _stalled = 0;
    }
}
