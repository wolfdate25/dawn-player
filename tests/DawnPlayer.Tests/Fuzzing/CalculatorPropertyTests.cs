using System.Globalization;
using DawnPlayer.App.Controls;
using DawnPlayer.App.Helpers;
using DawnPlayer.Core.Audio;
using Xunit;

namespace DawnPlayer.Tests.Fuzzing;

/// <summary>
/// Property-based tests for the pure calculators (potato-pyramid verification layer, plan §15).
/// Instead of a handful of hand-written examples, each property states an invariant that must
/// hold for ANY input, then a seeded Random floods it — the same discipline as the §14 parser
/// fuzz gate, applied to the numeric domain. Invariants were designed from each calculator's
/// documented contract BEFORE the loops were written:
///
///   P-W1 ClampToVirtualScreen keeps a 160×60 sliver visible on both axes (audit PT1-06).
///   P-W2 clamping is idempotent — an already-clamped point returns unchanged.
///   P-Q1 TargetIndex yields null or an in-range index, never itself, and exactly matches the
///        WinUI remove+insert semantics for in-range drops (plan §12 I11).
///   P-A1 FractionToX maps [0,1] onto the thumb-center travel [ThumbWidth/2, w−ThumbWidth/2]
///        monotonically; degenerate tracks center; ±∞ clamp to the ends.
///   P-A2 Compute never emits a band outside the track, an inverted/negative band, or geometry
///        when hidden conditions hold; identical inputs give identical geometry.
///   P-M1 PhysicalSize stays within 0.5 physical px of the logical design at any DPI scale and
///        is monotone in scale (2026-10-08 mini-clipping defect class).
///   P-S1 slider progress is never negative / past duration, and only re-bases Max on real drift.
///   P-S2 restore state always yields max ≥ 1, 0 ≤ value ≤ max, finite even for NaN/∞ inputs.
///   P-S3 drag completion requires an armed drag, lands in [0, duration], and disarms itself.
///
/// Iterations follow the fuzz-gate convention: default 2000, DAWN_PLAYER_FUZZ_ITERATIONS
/// overrides for deep bursts.
/// </summary>
public sealed class CalculatorPropertyTests
{
    private const int Seed = 20261118;

    private static int Iterations()
    {
        var raw = Environment.GetEnvironmentVariable("DAWN_PLAYER_FUZZ_ITERATIONS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : 2000;
    }

    private static double NextDouble(Random rnd, double min, double max) => min + rnd.NextDouble() * (max - min);

    // ------------------------------------------------------------------ WindowPlacementMath

    [Fact]
    public void ClampToVirtualScreen_AlwaysKeepsSliver_AndIsIdempotent()
    {
        var rnd = new Random(Seed);
        for (int i = 0; i < Iterations(); i++)
        {
            int vx = rnd.Next(-5000, 5000), vy = rnd.Next(-5000, 5000);
            int vw = rnd.Next(400, 4000), vh = rnd.Next(200, 2000);
            int w = rnd.Next(50, vw + 1), h = rnd.Next(20, vh + 1);
            int x = rnd.Next(-10000, 10000), y = rnd.Next(-10000, 10000);

            var (cx, cy) = WindowPlacementMath.ClampToVirtualScreen(x, y, w, h, vx, vy, vw, vh);

            // P-W1: a 160×60 sliver stays reachable on both axes (bounds cannot invert because
            // the generator keeps w ≤ vw, h ≤ vh with the screen comfortably larger).
            Assert.InRange(cx, vx - w + 160, vx + vw - 160);
            Assert.InRange(cy, vy - h + 60, vy + vh - 60);

            // P-W2: idempotence.
            var (cx2, cy2) = WindowPlacementMath.ClampToVirtualScreen(cx, cy, w, h, vx, vy, vw, vh);
            Assert.Equal((cx, cy), (cx2, cy2));
        }
    }

    // ------------------------------------------------------------------ QueueReorderCalculator

    [Fact]
    public void TargetIndex_NullOrInRange_NeverSelf_MatchesRemoveInsertSemantics()
    {
        var rnd = new Random(Seed + 1);
        for (int i = 0; i < Iterations(); i++)
        {
            int count = rnd.Next(0, 50);
            int from = rnd.Next(-2, count + 3);
            int drop = rnd.Next(-3, count + 4);

            var target = QueueReorderCalculator.TargetIndex(from, drop, count);

            if (target is int t)
            {
                Assert.InRange(t, 0, count - 1);      // P-Q1: null or in-range
                Assert.NotEqual(from, t);             // P-Q1: a move, never a self-no-op
            }

            // P-Q1 exact contract for the well-formed case: WinUI removes `from` then inserts at
            // `drop`, so the core Move target IS the clamped drop index.
            if (count > 0 && from >= 0 && from < count)
            {
                int clamped = Math.Clamp(drop, 0, count - 1);
                Assert.Equal(clamped == from ? (int?)null : clamped, target);
            }
            else
            {
                Assert.Null(target);                  // out-of-bounds inputs are rejected wholesale
            }
        }
    }

    // ------------------------------------------------------------------ AbRepeatOverlayCalculator

    [Fact]
    public void FractionToX_TracksThumbCenter_Monotonically_AndClampsExtremes()
    {
        var rnd = new Random(Seed + 2);
        for (int i = 0; i < Iterations(); i++)
        {
            double trackWidth = NextDouble(rnd, 1.0, 2000.0);
            double f1 = NextDouble(rnd, -5.0, 5.0);
            double f2 = NextDouble(rnd, -5.0, 5.0);

            var x1 = AbRepeatOverlayCalculator.FractionToX(f1, trackWidth);
            var x2 = AbRepeatOverlayCalculator.FractionToX(f2, trackWidth);

            if (trackWidth <= AbRepeatOverlayCalculator.ThumbWidth)
            {
                Assert.Equal(trackWidth / 2, x1);     // P-A1: degenerate track centers
            }
            else
            {
                Assert.InRange(x1, AbRepeatOverlayCalculator.ThumbWidth / 2, trackWidth - AbRepeatOverlayCalculator.ThumbWidth / 2);
                Assert.InRange(x2, AbRepeatOverlayCalculator.ThumbWidth / 2, trackWidth - AbRepeatOverlayCalculator.ThumbWidth / 2);
                if (f1 <= f2) Assert.True(x1 <= x2, $"monotonicity broken: f1={f1} f2={f2} x1={x1} x2={x2}");
            }
        }

        // P-A1 endpoints: fraction 0/1 land exactly on the thumb-center travel ends.
        for (double trackWidth = 13.0; trackWidth < 500.0; trackWidth += 37.5)
        {
            Assert.Equal(AbRepeatOverlayCalculator.ThumbWidth / 2, AbRepeatOverlayCalculator.FractionToX(0.0, trackWidth), 10);
            Assert.Equal(trackWidth - AbRepeatOverlayCalculator.ThumbWidth / 2, AbRepeatOverlayCalculator.FractionToX(1.0, trackWidth), 10);
            Assert.Equal(AbRepeatOverlayCalculator.ThumbWidth / 2, AbRepeatOverlayCalculator.FractionToX(double.NegativeInfinity, trackWidth), 10);
            Assert.Equal(trackWidth - AbRepeatOverlayCalculator.ThumbWidth / 2, AbRepeatOverlayCalculator.FractionToX(double.PositiveInfinity, trackWidth), 10);
        }
    }

    [Fact]
    public void Compute_BandStaysInsideTrack_NeverInverted_Deterministic()
    {
        var rnd = new Random(Seed + 3);
        var stages = new[] { AbRepeatStage.Off, AbRepeatStage.WaitingForB, AbRepeatStage.Looping };
        for (int i = 0; i < Iterations(); i++)
        {
            var stage = stages[rnd.Next(stages.Length)];
            double durationSec = NextDouble(rnd, 0.0, 3600.0);
            var duration = TimeSpan.FromSeconds(durationSec);
            var start = TimeSpan.FromSeconds(NextDouble(rnd, -10.0, durationSec + 20.0));
            var end = TimeSpan.FromSeconds(NextDouble(rnd, -10.0, durationSec + 20.0));
            var position = TimeSpan.FromSeconds(NextDouble(rnd, -10.0, durationSec + 20.0));
            double trackWidth = NextDouble(rnd, 1.0, 2000.0);

            var g = AbRepeatOverlayCalculator.Compute(stage, start, end, position, duration, trackWidth);

            if (stage == AbRepeatStage.Off || trackWidth <= AbRepeatOverlayCalculator.ThumbWidth || duration <= TimeSpan.Zero)
            {
                Assert.False(g.Visible);              // P-A2: hidden conditions hide everything
                continue;
            }

            Assert.True(g.Visible);
            const double eps = 1e-9;
            double lo = AbRepeatOverlayCalculator.ThumbWidth / 2 - eps;
            double hi = trackWidth - AbRepeatOverlayCalculator.ThumbWidth / 2 + eps;
            Assert.InRange(g.MarkerALeft, lo, hi);
            if (g.ShowBand)
            {
                Assert.True(g.BandWidth >= 0, "P-A2: never a negative band");
                Assert.InRange(g.BandLeft, lo, hi);
                Assert.InRange(g.BandLeft + g.BandWidth, lo, hi);
            }

            // P-A2: determinism.
            var again = AbRepeatOverlayCalculator.Compute(stage, start, end, position, duration, trackWidth);
            Assert.Equal(g.Visible, again.Visible);
            Assert.Equal(g.ShowBand, again.ShowBand);
            Assert.Equal(g.BandIsPreview, again.BandIsPreview);
            Assert.Equal(g.BandLeft, again.BandLeft);
            Assert.Equal(g.BandWidth, again.BandWidth);
            Assert.Equal(g.MarkerALeft, again.MarkerALeft);
            Assert.Equal(g.MarkerBLeft, again.MarkerBLeft);
        }
    }

    // ------------------------------------------------------------------ MiniPlayerPlacement

    [Fact]
    public void PhysicalSize_WithinHalfPixelOfLogicalDesign_MonotoneInScale()
    {
        var rnd = new Random(Seed + 4);
        for (int i = 0; i < Iterations(); i++)
        {
            double scale = NextDouble(rnd, 0.5, 5.0);
            var (w, h) = MiniPlayerPlacement.PhysicalSize(scale);

            Assert.True(w > 0 && h > 0);                                                        // P-M1
            Assert.True(Math.Abs(w - MiniPlayerPlacement.LogicalWidth * scale) <= 0.5, $"P-M1 width drift at scale {scale}");
            Assert.True(Math.Abs(h - MiniPlayerPlacement.LogicalHeight * scale) <= 0.5, $"P-M1 height drift at scale {scale}");

            // P-M1: monotone in scale (a larger DPI never shrinks the window).
            var (w2, h2) = MiniPlayerPlacement.PhysicalSize(scale + 0.01);
            Assert.True(w2 >= w && h2 >= h);
        }

        // The caption gutter + bar never overlap — the height IS their sum at every scale.
        Assert.Equal(MiniPlayerPlacement.CaptionGutterHeight + MiniPlayerPlacement.BarLogicalHeight, MiniPlayerPlacement.LogicalHeight);
    }

    // ------------------------------------------------------------------ SeekbarScrubbingCalculator

    [Fact]
    public void SliderProgress_NeverNegativeOrPastDuration_RebasesOnlyOnDrift()
    {
        var rnd = new Random(Seed + 5);
        for (int i = 0; i < Iterations(); i++)
        {
            double durSec = NextDouble(rnd, 0.0, 7200.0);
            var duration = TimeSpan.FromSeconds(durSec);
            var position = TimeSpan.FromSeconds(NextDouble(rnd, -10.0, durSec + 10.0));
            double max = NextDouble(rnd, 0.0, 8000.0);
            bool dragging = rnd.Next(2) == 0;

            var (updateMax, newMax, newValue) = SeekbarScrubbingCalculator.CalculateSliderProgress(position, duration, max, dragging);

            if (dragging)
            {
                Assert.False(updateMax);              // P-S1: drag freezes the max
                Assert.Equal(max, newMax);
                Assert.Equal(Math.Max(0.0, position.TotalSeconds), newValue);
            }
            else if (duration > TimeSpan.Zero)
            {
                // Compare against duration.TotalSeconds (the tick-truncated value the calculator
                // sees), not the raw generated double — a FromSeconds roundtrip shifts ~1e-7.
                Assert.InRange(newValue, 0.0, duration.TotalSeconds);          // P-S1
                Assert.Equal(Math.Abs(max - duration.TotalSeconds) > 0.5, updateMax); // P-S1
                Assert.Equal(updateMax ? duration.TotalSeconds : max, newMax);
            }
            else
            {
                Assert.Equal(100.0, newMax);
                Assert.Equal(0.0, newValue);
            }
            Assert.True(double.IsFinite(newValue));
        }
    }

    [Fact]
    public void RestoreState_MaxAtLeastOne_ValueInRange_FiniteForHostileInputs()
    {
        var rnd = new Random(Seed + 6);
        var hostile = new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1, 0, 1e9 };
        for (int i = 0; i < Iterations(); i++)
        {
            double seconds = rnd.Next(3) == 0 ? hostile[rnd.Next(hostile.Length)] : NextDouble(rnd, -100.0, 100000.0);
            double maxSeconds = rnd.Next(3) == 0 ? hostile[rnd.Next(hostile.Length)] : NextDouble(rnd, -100.0, 100000.0);

            var (max, value, elapsed, remaining) = SeekbarScrubbingCalculator.CalculateRestoreState(seconds, maxSeconds);

            Assert.True(max >= 1.0);                   // P-S2
            Assert.InRange(value, 0.0, max);
            Assert.True(double.IsFinite(max) && double.IsFinite(value));
            Assert.NotEqual(string.Empty, elapsed);
            Assert.True(remaining == "0:00" || remaining.StartsWith('-'), $"P-S2 remaining format: {remaining}");
        }
    }

    [Fact]
    public void CompleteDrag_RequiresArmedDrag_LandsInRange_AndDisarms()
    {
        var rnd = new Random(Seed + 7);
        var calc = new SeekbarScrubbingCalculator();

        // P-S3: an un-armed drag never seeks.
        Assert.Null(calc.CompleteDrag(NextDouble(rnd, 0, 100), TimeSpan.FromMinutes(3)));

        for (int i = 0; i < Iterations(); i++)
        {
            double durSec = NextDouble(rnd, 0.0, 3600.0);
            var duration = TimeSpan.FromSeconds(durSec);
            double value = NextDouble(rnd, -50.0, durSec + 50.0);

            calc.BeginDrag();
            var result = calc.CompleteDrag(value, duration);

            Assert.False(calc.IsDragging);             // P-S3: completion disarms
            Assert.NotNull(result);
            var t = result!.Value;
            Assert.True(t >= TimeSpan.Zero, $"P-S3 negative result {t}");
            if (duration > TimeSpan.Zero)
                Assert.True(t <= duration + TimeSpan.FromMilliseconds(1), $"P-S3 past duration: {t} > {duration}");

            // A second completion without re-arming stays a no-op.
            Assert.Null(calc.CompleteDrag(value, duration));
        }
    }
}
