using System;

namespace DawnPlayer.Core.Audio;

/// <summary>Why a CycleAbRepeat press did not advance the A-B state machine.</summary>
public enum AbRepeatRejectionReason
{
    /// <summary>B was marked before (or at) A; the window stays as it was.</summary>
    BBeforeA,

    /// <summary>The current source is live/unseekable (TotalTime zero), so a loop the audio
    /// thread could never enforce would be reported as active.</summary>
    UnseekableSource
}

/// <summary>
/// Pure admission gate for marking A-B points, factored out so the live-source refusal is unit
/// testable without an audio session. Radio readers report <see cref="ITrackReader.TotalTime"/>
/// zero and no-op their <c>CurrentTime</c> setter — marking A there would leave the UI showing a
/// loop while the render thread silently kept playing straight through.
/// </summary>
public static class AbRepeatGate
{
    public static bool CanMark(TimeSpan trackDuration) => trackDuration > TimeSpan.Zero;
}

/// <summary>
/// Immutable time-domain snapshot of the A-B window for UI affordances (seekbar overlay, tooltip).
/// Bytes are converted with the sequencer's current WaveFormat on read, so what the UI draws and
/// states is exactly the region the audio thread enforces — the reported window may never drift
/// from the enforced one.
/// </summary>
/// <param name="Stage">Current cycle stage.</param>
/// <param name="Start">Point A. Meaningful whenever <see cref="HasStart"/>.</param>
/// <param name="End">Point B. Meaningful only when <see cref="HasEnd"/>.</param>
public readonly record struct AbRepeatWindow(AbRepeatStage Stage, TimeSpan Start, TimeSpan End)
{
    public static AbRepeatWindow Off => new(AbRepeatStage.Off, TimeSpan.Zero, TimeSpan.Zero);

    public bool HasStart => Stage != AbRepeatStage.Off;
    public bool HasEnd => Stage == AbRepeatStage.Looping;
}
