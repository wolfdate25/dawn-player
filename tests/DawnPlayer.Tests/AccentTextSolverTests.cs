using DawnPlayer.App.Helpers;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Failure-scenario gates for ContrastMath.SolveTextVariant (2026-09-30 audit PT1-01/PT5-05).
/// The solver feeds DawnAccentTextBrush — every accent preset (including custom album-derived
/// colors) passes through it, so the invariants here are what keep accent text readable:
///   * an already-passing accent is returned byte-identical (dark theme must not drift);
///   * a failing accent comes back at/above the target ratio with hue order preserved;
///   * the shift is idempotent and monotonic away from the background's luminance.
/// </summary>
public class AccentTextSolverTests
{
    private static double Ratio((byte r, byte g, byte b) c, byte bgR, byte bgG, byte bgB)
    {
        var la = ContrastMath.RelativeLuminance(c.r, c.g, c.b);
        var lb = ContrastMath.RelativeLuminance(bgR, bgG, bgB);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void PassingAccent_IsReturnedUnchanged()
    {
        // Dark amber on the dark panel already clears 4.5:1 — the solver must not touch it,
        // or every dark-theme accent would silently shift.
        var result = ContrastMath.SolveTextVariant(0xE8, 0xA3, 0x3D, 0x1F, 0x1F, 0x25);
        Assert.Equal((0xE8, 0xA3, 0x3D), result);
    }

    [Fact]
    public void FailingLightAccent_DarkensToTarget_PreservingHue()
    {
        // Light amber #C77F1B sits at ~2.6:1 on the light panel — the exact PT1-01 defect.
        var result = ContrastMath.SolveTextVariant(0xC7, 0x7F, 0x1B, 0xF0, 0xEF, 0xEB);
        Assert.True(Ratio(result, 0xF0, 0xEF, 0xEB) >= 4.5,
            $"solved light accent ratio {Ratio(result, 0xF0, 0xEF, 0xEB):F2} < 4.5");
        // Hue preserved: amber keeps its r > g > b channel ordering after darkening.
        Assert.True(result.r > result.g && result.g > result.b, "amber hue order lost");
        // It darkened, not lightened.
        Assert.True(result.r < 0xC7 && result.g < 0x7F && result.b < 0x1B, "variant moved the wrong way");
    }

    [Fact]
    public void FailingDarkAccent_LightensToTarget()
    {
        // PlayGreen on the OLED panel is below 4.5:1 as text; the solver must lighten it.
        var result = ContrastMath.SolveTextVariant(0x15, 0x80, 0x3D, 0x00, 0x00, 0x00);
        Assert.True(Ratio(result, 0x00, 0x00, 0x00) >= 4.5,
            $"solved green-on-black ratio {Ratio(result, 0x00, 0x00, 0x00):F2} < 4.5");
        Assert.True(result.r >= 0x15 && result.g >= 0x80 && result.b >= 0x3D, "variant moved the wrong way");
    }

    [Fact]
    public void Solver_IsIdempotent()
    {
        var once = ContrastMath.SolveTextVariant(0xC7, 0x7F, 0x1B, 0xF0, 0xEF, 0xEB);
        var twice = ContrastMath.SolveTextVariant(once.r, once.g, once.b, 0xF0, 0xEF, 0xEB);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void WhiteOnWhite_ConvergesToBlack()
    {
        // Unreachable target in the "natural" direction: the extreme (t=1) is the documented
        // best effort and here it is black — maximum contrast.
        var result = ContrastMath.SolveTextVariant(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Assert.True(Ratio(result, 0xFF, 0xFF, 0xFF) >= 4.5, "white-on-white did not reach the target");
    }

    [Fact]
    public void GrayAccent_OnBothExtremes_MeetsTarget()
    {
        // Neutral gray has no hue to preserve but must still resolve on both surface classes.
        var onLight = ContrastMath.SolveTextVariant(0x95, 0x95, 0x95, 0xF0, 0xEF, 0xEB);
        Assert.True(Ratio(onLight, 0xF0, 0xEF, 0xEB) >= 4.5);
        var onDark = ContrastMath.SolveTextVariant(0x30, 0x30, 0x30, 0x1F, 0x1F, 0x25);
        Assert.True(Ratio(onDark, 0x1F, 0x1F, 0x25) >= 4.5);
    }

    [Fact]
    public void CustomAccent_QuantileSweep_AlwaysMeetsTarget()
    {
        // Every preset hex ThemeService ships plus a spread of arbitrary hues — none may emit
        // a variant below the AA text bar on its theme's panel.
        string[] presetAccents =
        {
            "#FFFFD13B", "#FF2ECC71", "#FF00B4D8", "#FFFF4757", "#FF95A5A6",
            "#FF88C0D0", "#FF7AA2F7", "#FFCBA6F7", "#FFEBBCBA", "#FFA78BFA",
            "#FF22C55E", "#FFE8A33D",
        };
        string[] panels = { "#FF1F1F25", "#FFF0EFEB", "#FF000000", "#FF111114", "#FFE6E4DE" };
        foreach (var hex in presetAccents)
        {
            var r = Convert.ToByte(hex[3..5], 16);
            var g = Convert.ToByte(hex[5..7], 16);
            var b = Convert.ToByte(hex[7..9], 16);
            foreach (var panel in panels)
            {
                var pr = Convert.ToByte(panel[3..5], 16);
                var pg = Convert.ToByte(panel[5..7], 16);
                var pb = Convert.ToByte(panel[7..9], 16);
                var solved = ContrastMath.SolveTextVariant(r, g, b, pr, pg, pb);
                Assert.True(Ratio(solved, pr, pg, pb) >= 4.5,
                    $"{hex} on {panel} solved to ratio {Ratio(solved, pr, pg, pb):F2}");
            }
        }
    }
}
