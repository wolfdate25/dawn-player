using System;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// A-B repeat UX layer: the seekbar overlay geometry and the controller's new surface (cancel
/// path, rejection signals, window snapshot). The invariants under test:
/// - a band edge lands exactly where the seekbar thumb's center sits at that time (the drawn
///   window may never lie about the enforced window — mirrored thumb-travel mapping),
/// - degenerate inputs (zero duration, B ≤ A, narrow slider) collapse gracefully instead of
///   producing NaN or negative widths,
/// - a live/unseekable source can never enter the marking stages (RadioStreamReader no-ops
///   seeks; a "looping" report there would be a false affordance).
/// </summary>
public class AbRepeatTests
{
    // ---------------- geometry: value → pixel mapping ----------------

    [Theory]
    [InlineData(0.0, 6.0)]      // fraction 0 → thumb center at half the thumb width
    [InlineData(0.5, 150.0)]    // midpoint of a 300px track
    [InlineData(1.0, 294.0)]    // fraction 1 → width minus half the thumb width
    public void FractionToX_MatchesThumbTravel(double fraction, double expected)
    {
        var x = AbRepeatOverlayCalculator.FractionToX(fraction, trackWidth: 300);
        Assert.Equal(expected, x, precision: 6);
    }

    [Fact]
    public void FractionToX_TrackNarrowerThanThumb_ReturnsFiniteCenter()
    {
        var x = AbRepeatOverlayCalculator.FractionToX(0.7, trackWidth: 8);
        Assert.Equal(4.0, x, precision: 6); // width/2, no NaN, no negative
    }

    [Fact]
    public void FractionToX_OutOfRangeFraction_ClampsInsideTrack()
    {
        // Defensive: a position racing past the duration must not push the overlay off the slider.
        Assert.Equal(6.0, AbRepeatOverlayCalculator.FractionToX(-2.0, 300), precision: 6);
        Assert.Equal(294.0, AbRepeatOverlayCalculator.FractionToX(2.0, 300), precision: 6);
    }

    // ---------------- geometry: stage → overlay ----------------

    [Fact]
    public void Compute_Off_IsHidden()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.Off,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), 300);
        Assert.False(geo.Visible);
    }

    [Fact]
    public void Compute_ZeroDuration_IsHidden_NoDivisionByZero()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.Looping,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(6), TimeSpan.Zero, 300);
        Assert.False(geo.Visible);
    }

    [Fact]
    public void Compute_NarrowSlider_IsHidden()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.Looping,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(100), 12);
        Assert.False(geo.Visible);
    }

    [Fact]
    public void Compute_Looping_BandEdgesAtThumbPositions()
    {
        // 100s track on a 300px slider: A at 25s, B at 75s.
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.Looping,
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(100), 300);

        Assert.True(geo.Visible);
        Assert.True(geo.ShowBand);
        Assert.False(geo.BandIsPreview);
        Assert.Equal(6.0 + 0.25 * 288, geo.MarkerALeft, precision: 6);
        Assert.Equal(6.0 + 0.75 * 288, geo.MarkerBLeft, precision: 6);
        Assert.Equal(geo.MarkerALeft, geo.BandLeft, precision: 6);
        Assert.Equal(geo.MarkerBLeft - geo.MarkerALeft, geo.BandWidth, precision: 6);
    }

    [Fact]
    public void Compute_WaitingForB_PreviewBandFollowsPlayhead()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.WaitingForB,
            TimeSpan.FromSeconds(20), TimeSpan.Zero,
            TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100), 300);

        Assert.True(geo.Visible);
        Assert.True(geo.ShowBand);
        Assert.True(geo.BandIsPreview);
        Assert.Equal(6.0 + 0.2 * 288, geo.BandLeft, precision: 6);
        Assert.Equal((0.5 - 0.2) * 288, geo.BandWidth, precision: 6);
    }

    [Fact]
    public void Compute_WaitingForB_PlayheadBeforeA_BandCollapsed()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.WaitingForB,
            TimeSpan.FromSeconds(20), TimeSpan.Zero,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), 300);

        Assert.True(geo.Visible);
        Assert.False(geo.ShowBand);
    }

    [Fact]
    public void Compute_WaitingForB_PlayheadPastDuration_ClampsInsideTrack()
    {
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.WaitingForB,
            TimeSpan.FromSeconds(20), TimeSpan.Zero,
            TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(100), 300);

        Assert.True(geo.ShowBand);
        Assert.Equal(294.0, geo.BandLeft + geo.BandWidth, precision: 6);
    }

    [Fact]
    public void Compute_Looping_EndBeforeStart_MarkersOnly_NoNegativeBand()
    {
        // Defensive: the controller refuses B ≤ A, but the overlay must never draw an inverted
        // band even if a degenerate window ever reaches it.
        var geo = AbRepeatOverlayCalculator.Compute(AbRepeatStage.Looping,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(100), 300);

        Assert.True(geo.Visible);
        Assert.False(geo.ShowBand);
        Assert.Equal(6.0 + 0.6 * 288, geo.MarkerALeft, precision: 6);
        Assert.Equal(6.0 + 0.3 * 288, geo.MarkerBLeft, precision: 6);
    }

    // ---------------- tooltip loop length ----------------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(-5, "0")]
    [InlineData(500, "0.5")]
    [InlineData(21600, "21.6")]
    [InlineData(65300, "1:05.3")]
    [InlineData(60000, "1:00.0")]
    public void FormatLoopLength_RoundsAcrossBoundaries(int milliseconds, string expected)
    {
        Assert.Equal(expected, AbRepeatOverlayCalculator.FormatLoopLength(TimeSpan.FromMilliseconds(milliseconds)));
    }

    // ---------------- controller: stopped-state and gate ----------------

    [Fact]
    public void CycleAbRepeat_WhenStopped_StaysOffAndRaisesNothing()
    {
        using var controller = new PlaybackController(new AppSettings(), new PlaylistManager(new MusicLibrary()));
        bool changed = false;
        bool rejected = false;
        controller.AbRepeatChanged += () => changed = true;
        controller.AbRepeatRejected += _ => rejected = true;

        var stage = controller.CycleAbRepeat();

        Assert.Equal(AbRepeatStage.Off, stage);
        Assert.False(changed);
        Assert.False(rejected);
    }

    [Fact]
    public void CancelAbRepeat_WhenOff_IsANoopWithoutEvent()
    {
        using var controller = new PlaybackController(new AppSettings(), new PlaylistManager(new MusicLibrary()));
        bool changed = false;
        controller.AbRepeatChanged += () => changed = true;

        Assert.False(controller.CancelAbRepeat());
        Assert.False(changed);
    }

    [Fact]
    public void AbRepeatWindow_WhenStopped_IsOff()
    {
        using var controller = new PlaybackController(new AppSettings(), new PlaylistManager(new MusicLibrary()));

        var window = controller.AbRepeatWindow;

        Assert.Equal(AbRepeatStage.Off, window.Stage);
        Assert.False(window.HasStart);
        Assert.False(window.HasEnd);
    }

    [Fact]
    public void AbRepeatGate_RefusesLiveSources_AcceptsFiniteTracks()
    {
        // Radio readers report TotalTime zero and no-op seeks; a marked loop there could never
        // be enforced while the UI reported one.
        Assert.False(AbRepeatGate.CanMark(TimeSpan.Zero));
        Assert.False(AbRepeatGate.CanMark(TimeSpan.FromSeconds(-1)));
        Assert.True(AbRepeatGate.CanMark(TimeSpan.FromMilliseconds(1)));
        Assert.True(AbRepeatGate.CanMark(TimeSpan.FromHours(3)));
    }
}
