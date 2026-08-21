using System;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Controls;

/// <summary>
/// The transport toggle arithmetic behind the NowPlayingBar buttons, kept free of WinUI so both the
/// buttons and the keyboard shortcuts drive the same rules (and so it is unit-testable).
/// <para>All volume values are slider percent (0-100), matching <c>VolumeSlider</c>.</para>
/// </summary>
public static class TransportToggleCalculator
{
    /// <summary>How far one volume-up/down shortcut press moves the slider.</summary>
    public const double VolumeStepPercent = 5;

    /// <summary>
    /// Where unmuting lands when there is no earlier non-zero volume to restore — matching the
    /// <see cref="PlaybackSettings.Volume"/> default, so unmute never silently stays silent.
    /// </summary>
    public const double DefaultRestorePercent = 80;

    /// <summary>Off to Tracks to Albums and back.</summary>
    public static ShuffleMode NextShuffleMode(ShuffleMode current) => current switch
    {
        ShuffleMode.Off => ShuffleMode.Tracks,
        ShuffleMode.Tracks => ShuffleMode.Albums,
        _ => ShuffleMode.Off
    };

    /// <summary>Off to All to One and back.</summary>
    public static RepeatMode NextRepeatMode(RepeatMode current) => current switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off
    };

    /// <summary>Moves the volume by <paramref name="deltaPercent"/>, clamped to the slider range.</summary>
    public static double StepVolumePercent(double currentPercent, double deltaPercent)
    {
        if (double.IsNaN(currentPercent)) currentPercent = 0;
        if (double.IsNaN(deltaPercent)) deltaPercent = 0;
        return Math.Clamp(currentPercent + deltaPercent, 0, 100);
    }

    /// <summary>
    /// Mutes to zero while remembering the level, or restores the remembered level.
    /// Returns the new slider percent and the level to remember for the next unmute.
    /// </summary>
    public static (double VolumePercent, double LastNonZeroPercent) ComputeMuteToggle(
        double currentPercent, double lastNonZeroPercent)
    {
        if (double.IsNaN(currentPercent)) currentPercent = 0;
        if (double.IsNaN(lastNonZeroPercent)) lastNonZeroPercent = 0;

        currentPercent = Math.Clamp(currentPercent, 0, 100);
        lastNonZeroPercent = Math.Clamp(lastNonZeroPercent, 0, 100);

        if (currentPercent > 0)
        {
            return (0, currentPercent);
        }

        var restored = lastNonZeroPercent > 0 ? lastNonZeroPercent : DefaultRestorePercent;
        return (restored, restored);
    }
}
