using DawnPlayer.App.Calculators;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// U1 motion gates: the effective-motion decision (app toggle AND OS animations), the
/// spectrum attack/release envelope, and the new persisted UI settings' defaults. The Windows
/// <c>UISettings</c> probe itself is behind <see cref="IMotionPreferenceSource"/>, so all of
/// this runs headless.
/// </summary>
public class MotionAndSpectrumTests
{
    private sealed class FixedSource(bool enabled) : IMotionPreferenceSource
    {
        public bool AnimationsEnabled { get; set; } = enabled;
    }

    // ---------- MotionService ----------

    [Fact]
    public void Motion_On_OnlyWhenUserAndOsBothAllow()
    {
        var os = new FixedSource(true);
        bool userSetting = true;
        var motion = new MotionService(os, () => userSetting);

        Assert.True(motion.MotionEnabled);

        os.AnimationsEnabled = false;
        Assert.False(motion.MotionEnabled); // OS reduced-motion wins

        os.AnimationsEnabled = true;
        userSetting = false;
        Assert.False(motion.MotionEnabled); // app toggle wins
    }

    [Fact]
    public void Motion_UserSetting_ReevaluatedPerQuery()
    {
        var motion = new MotionService(new FixedSource(true), () => MotionAndSpectrumTestsStatic.Flag);
        MotionAndSpectrumTestsStatic.Flag = false;
        Assert.False(motion.MotionEnabled);
        MotionAndSpectrumTestsStatic.Flag = true;
        Assert.True(motion.MotionEnabled);
    }

    [Fact]
    public void Motion_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new MotionService(null!, () => true));
        Assert.Throws<ArgumentNullException>(() => new MotionService(new FixedSource(true), null!));
    }

    // ---------- SpectrumSmoother ----------

    [Fact]
    public void Smoother_AttackIsInstant_OnRise()
    {
        var smoother = new SpectrumSmoother(4);
        var frame = new double[] { 0.0, 0.0, 0.0, 0.0 };
        smoother.Apply(frame, releasePerSecond: 2.0, dtSeconds: 0.1);

        frame[1] = 1.0;
        smoother.Apply(frame, 2.0, 0.1);
        var rendered = smoother.CopyTo(new double[4]);
        Assert.Equal(1.0, rendered[1]); // no lag on attack
    }

    [Fact]
    public void Smoother_ReleasesLinearly_AndNeverBelowCurrentLevel()
    {
        var smoother = new SpectrumSmoother(2);
        smoother.Apply(new double[] { 1.0, 0.0 }, 2.0, 0.0);

        // Sustained level 0.5 for one second at release 2.0: the envelope decays 2.0 (clamped
        // to 0.5), i.e. it stops exactly at the current level instead of undershooting.
        smoother.Apply(new double[] { 0.5, 0.5 }, 2.0, 1.0);
        var rendered = smoother.CopyTo(new double[2]);
        Assert.Equal(0.5, rendered[0], 12);
        Assert.Equal(0.5, rendered[1], 12);
    }

    [Fact]
    public void Smoother_NeverExceedsCurrentFrame()
    {
        var smoother = new SpectrumSmoother(2);
        smoother.Apply(new double[] { 0.9, 0.0 }, 2.0, 0.0);
        smoother.Apply(new double[] { 0.2, 0.2 }, 10.0, 5.0);
        var rendered = smoother.CopyTo(new double[2]);
        Assert.True(rendered[0] <= 0.2 + 1e-12, $"envelope exceeded current frame: {rendered[0]}");
        Assert.Equal(0.2, rendered[0], 12);
    }

    [Fact]
    public void Smoother_ZeroDt_KeepsEnvelope()
    {
        var smoother = new SpectrumSmoother(1);
        smoother.Apply(new double[] { 0.8 }, 3.0, 0.0);
        smoother.Apply(new double[] { 0.1 }, 3.0, 0.0);
        var rendered = smoother.CopyTo(new double[1]);
        Assert.Equal(0.8, rendered[0], 12);
    }

    [Fact]
    public void Smoother_Reset_SeedsEnvelopeWithoutAnimation()
    {
        var smoother = new SpectrumSmoother(2);
        smoother.Reset(new double[] { 0.7, 0.3 });
        smoother.Apply(new double[] { 0.6, 0.3 }, 2.0, 0.0);
        var rendered = smoother.CopyTo(new double[2]);
        // dt=0 ⇒ no decay: the envelope holds its seeded 0.7 above the 0.6 input (see
        // Smoother_ZeroDt_KeepsEnvelope) and equals the input where already at/below it.
        Assert.Equal(0.7, rendered[0], 12);
        Assert.Equal(0.3, rendered[1], 12);
    }

    [Fact]
    public void Smoother_WrongBinCount_Throws()
    {
        var smoother = new SpectrumSmoother(4);
        Assert.Throws<ArgumentException>(() => smoother.Apply(new double[3], 1.0, 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumSmoother(0));
    }

    // ---------- persisted settings ----------

    [Fact]
    public void UiSettings_MotionAndDensity_Defaults_AreMotionOnCozyAcrylicOff()
    {
        var ui = new UiSettings();
        Assert.True(ui.MotionEnabled);
        Assert.Equal(DensityModes.Cozy, ui.DensityMode);
        Assert.False(ui.FullscreenAcrylic);
    }

    [Fact]
    public void DensityModes_Normalize_FoldsUnknownValuesToCozy()
    {
        Assert.Equal(DensityModes.Cozy, DensityModes.Normalize("Cozy"));
        Assert.Equal(DensityModes.Compact, DensityModes.Normalize("Compact"));
        Assert.Equal(DensityModes.Comfortable, DensityModes.Normalize("Comfortable"));
        Assert.Equal(DensityModes.Cozy, DensityModes.Normalize(null));
        Assert.Equal(DensityModes.Cozy, DensityModes.Normalize("this-string-is-not-settled-denser"));
        Assert.True(DensityModes.IsValid("Compact"));
        Assert.False(DensityModes.IsValid("cozy")); // case-sensitive, normalize instead
    }

    [Fact]
    public void AppearanceSettings_SetMotionAndDensity_PersistAndNotify()
    {
        var settings = new AppSettings();
        var service = new DawnPlayer.App.Services.AppearanceSettingsService(settings);
        var notifications = 0;
        service.AppearanceChanged += () => notifications++;

        service.SetMotionEnabled(false);
        Assert.False(settings.Ui.MotionEnabled);
        service.SetDensityMode("Compact");
        Assert.Equal(DensityModes.Compact, settings.Ui.DensityMode);
        service.SetDensityMode("bogus-mode");
        Assert.Equal(DensityModes.Cozy, settings.Ui.DensityMode); // normalized, not stored raw

        Assert.Equal(3, notifications);
    }
}

internal static class MotionAndSpectrumTestsStatic
{
    public static bool Flag = true;
}
