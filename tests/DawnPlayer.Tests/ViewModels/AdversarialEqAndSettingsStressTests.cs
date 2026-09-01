using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Calculators;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

public sealed class AdversarialEqAndSettingsStressTests
{
    private static (SettingsViewModel ViewModel, AppSettings Settings, EqSettingsService EqService, AudioSettingsService AudioService) CreateMasterViewModel(bool isExclusive = false)
    {
        var settings = new AppSettings();
        settings.Equalizer.EnsureDefaultProfile();

        int saveCount = 0;
        int scanCount = 0;
        int lyricsNotifyCount = 0;

        var audioService = new AudioSettingsService(settings, null);
        var eqService = new EqSettingsService(settings, null, () => saveCount++, () => { });
        var appService = new AppearanceSettingsService(settings);

        var vm = new SettingsViewModel(
            settings,
            audioService,
            eqService,
            appService,
            scanStarter: () => scanCount++,
            lyricsChangedNotifier: () => lyricsNotifyCount++,
            settingsSaver: s => saveCount++,
            isExclusiveSessionGetter: () => isExclusive);

        return (vm, settings, eqService, audioService);
    }

    #region 1. EqVisualizerCalculator Adversarial Stress & Edge Cases

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(1.0)]
    [InlineData(19.9999)]
    [InlineData(20.0)]
    [InlineData(1000.0)]
    [InlineData(20000.0)]
    [InlineData(20000.001)]
    [InlineData(1000000.0)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EqVisualizerCalculator_XFromFreq_ExtremeFrequencies_ClampedAndFinite(double freq)
    {
        double plotWidth = 600.0;
        double padLeft = 36.0;

        double x = EqVisualizerCalculator.XFromFreq(freq, plotWidth, padLeft);

        Assert.False(double.IsNaN(x), $"X coordinate should not be NaN for freq={freq}");
        Assert.False(double.IsInfinity(x), $"X coordinate should not be Infinity for freq={freq}");
        Assert.True(x >= padLeft - 1e-6, $"X coordinate should be >= padLeft for freq={freq}, actual={x}");
        Assert.True(x <= padLeft + plotWidth + 1e-6, $"X coordinate should be <= padLeft + plotWidth for freq={freq}, actual={x}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(36.0)]
    [InlineData(336.0)]
    [InlineData(636.0)]
    [InlineData(10000.0)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EqVisualizerCalculator_FreqFromX_ExtremeCoordinates_ClampedAndFinite(double x)
    {
        double plotWidth = 600.0;
        double padLeft = 36.0;

        double f = EqVisualizerCalculator.FreqFromX(x, plotWidth, padLeft);

        Assert.False(double.IsNaN(f), $"Frequency should not be NaN for x={x}");
        Assert.False(double.IsInfinity(f), $"Frequency should not be Infinity for x={x}");
        Assert.True(f >= EqVisualizerCalculator.MinFreqHz - 1e-6, $"Frequency should be >= MinFreqHz for x={x}, actual={f}");
        Assert.True(f <= EqVisualizerCalculator.MaxFreqHz + 1e-6, $"Frequency should be <= MaxFreqHz for x={x}, actual={f}");
    }

    [Fact]
    public void EqVisualizerCalculator_FreqAndX_InvertibilityFuzzing()
    {
        double plotWidth = 750.0;
        double padLeft = 40.0;
        var rng = new Random(42);

        for (int i = 0; i < 2000; i++)
        {
            // Log-uniform random frequency between 20Hz and 20000Hz
            double logF = Math.Log10(20.0) + rng.NextDouble() * (Math.Log10(20000.0) - Math.Log10(20.0));
            double origFreq = Math.Pow(10.0, logF);

            double x = EqVisualizerCalculator.XFromFreq(origFreq, plotWidth, padLeft);
            double restoredFreq = EqVisualizerCalculator.FreqFromX(x, plotWidth, padLeft);

            double relError = Math.Abs(origFreq - restoredFreq) / origFreq;
            Assert.True(relError < 1e-5, $"Invertibility failed for freq={origFreq}, restored={restoredFreq}, relError={relError}");
        }
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-100.0, -100.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(50.0, 50.0)]
    [InlineData(50.0001, 50.0001)]
    [InlineData(10000.0, 5000.0)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    public void EqVisualizerCalculator_Calculate_AdversarialDimensions_NeverThrowsAndYieldsValidData(double width, double height)
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = -2.5,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 80, GainDb = 4.0, Q = 1.0 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = -3.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 12000, GainDb = 3.0, Q = 1.0 }
            }
        };

        var data = EqVisualizerCalculator.Calculate(profile, width, height);

        Assert.NotNull(data);
        Assert.True(data.PlotWidth >= 10.0);
        Assert.True(data.PlotHeight >= 10.0);
        Assert.NotEmpty(data.HorizontalDbLines);
        Assert.NotEmpty(data.VerticalFreqLines);
        Assert.NotEmpty(data.CurvePoints);
        Assert.NotEmpty(data.FillPoints);
        Assert.Equal(3, data.BandPins.Count);

        foreach (var pt in data.CurvePoints)
        {
            Assert.False(double.IsNaN(pt.X));
            Assert.False(double.IsNaN(pt.Y));
            Assert.False(double.IsInfinity(pt.X));
            Assert.False(double.IsInfinity(pt.Y));
        }

        foreach (var pt in data.FillPoints)
        {
            Assert.False(double.IsNaN(pt.X));
            Assert.False(double.IsNaN(pt.Y));
            Assert.False(double.IsInfinity(pt.X));
            Assert.False(double.IsInfinity(pt.Y));
        }
    }

    [Fact]
    public void EqVisualizerCalculator_Calculate_CorruptedProfileData_HandledSafely()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = double.NaN, // Corrupted Preamp
            Bands = new()
            {
                new EqBandSettings { Type = (EqFilterType)999, FrequencyHz = -500, GainDb = 500, Q = -10 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = double.PositiveInfinity, GainDb = double.NaN, Q = double.NegativeInfinity },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 15000, GainDb = 0, Q = 1.0 }
            }
        };

        // Should calculate safely without throwing unhandled exceptions
        var data = EqVisualizerCalculator.Calculate(profile, 700, 190);
        Assert.NotNull(data);
        Assert.Equal(3, data.BandPins.Count);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    [InlineData(384000)]
    public void EqFrequencyResponseCalculator_VariousSampleRatesAndNyquistFrequencies_CalculatesStableResponse(int sampleRate)
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 30, GainDb = 6.0, Q = 0.7 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = sampleRate * 0.45, GainDb = -10.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = Math.Min(18000.0, sampleRate * 0.48), GainDb = 5.0, Q = 0.7 }
            }
        };

        double[] freqs = { 20.0, 100.0, 1000.0, sampleRate * 0.25, sampleRate * 0.499 };
        var response = EqFrequencyResponseCalculator.CalculateResponse(profile, freqs, sampleRate);

        Assert.Equal(freqs.Length, response.Length);
        for (int i = 0; i < response.Length; i++)
        {
            Assert.False(double.IsNaN(response[i]), $"Response at index {i} should not be NaN for sampleRate {sampleRate}");
            Assert.False(double.IsInfinity(response[i]), $"Response at index {i} should not be Infinity for sampleRate {sampleRate}");
        }
    }

    #endregion

    #region 2. ViewModel Re-Entrancy and Redundant Event Suppression (INV-UI-1)

    [Fact]
    public void ViewModels_RedundantPropertyAssignments_SuppressPropertyChangedEvents()
    {
        var (vm, settings, _, _) = CreateMasterViewModel();
        var audio = vm.Audio;
        var playback = vm.Playback;
        var eq = vm.Equalizer;

        // Warm up initial values
        audio.LatencyMs = 150;
        playback.NormalizerTargetDb = -14.0;
        playback.NormalizerMaxBoostDb = 6.0;
        playback.ReplayGainPreampDb = 2.0;
        eq.PreampDb = -3.0;

        int audioPropCount = 0;
        int playbackPropCount = 0;
        int eqPropCount = 0;

        audio.PropertyChanged += (_, _) => audioPropCount++;
        playback.PropertyChanged += (_, _) => playbackPropCount++;
        eq.PropertyChanged += (_, _) => eqPropCount++;

        // Re-assign identical values 100 times
        for (int i = 0; i < 100; i++)
        {
            audio.LatencyMs = 150;
            playback.NormalizerTargetDb = -14.0;
            playback.NormalizerMaxBoostDb = 6.0;
            playback.ReplayGainPreampDb = 2.0;
            eq.PreampDb = -3.0;
        }

        // Sub-panels must produce exactly ZERO PropertyChanged events when values don't change
        Assert.Equal(0, audioPropCount);
        Assert.Equal(0, playbackPropCount);
        Assert.Equal(0, eqPropCount);
    }

    [Fact]
    public void EqBandViewModel_EpsilonFloatingPointJitter_SuppressesRedundantEvents()
    {
        var bandModel = new EqBandSettings
        {
            Type = EqFilterType.PeakEq,
            FrequencyHz = 1000.0,
            GainDb = 3.0,
            Q = 1.41
        };

        int notifyCount = 0;
        var bandVm = new EqBandViewModel(bandModel, 0, () => notifyCount++);

        int propChangeCount = 0;
        bandVm.PropertyChanged += (_, _) => propChangeCount++;

        // Micro-jitter within epsilon (Frequency rounds to int, Gain rounds to 0.1, Q rounds to 0.01)
        bandVm.FrequencyHz = 1000.0001; // rounds to 1000
        bandVm.GainDb = 3.001;          // rounds to 3.0
        bandVm.Q = 1.4101;              // rounds to 1.41

        Assert.Equal(0, notifyCount);
        Assert.Equal(0, propChangeCount);

        // Meaningful change above epsilon
        bandVm.GainDb = 3.5;
        Assert.Equal(1, notifyCount);
        Assert.Equal(1, propChangeCount);
        Assert.Equal(3.5, bandVm.GainDb);
    }

    #endregion

    #region 3. Profile Lifecycle, Cascade Deletion and Dynamic Binding Stress (INV-UI-3)

    [Fact]
    public void EqualizerSettingsViewModel_ProfileCascadeDeletionStress_PreservesInvariants()
    {
        var (vm, settings, eqService, _) = CreateMasterViewModel();
        var eq = vm.Equalizer;

        string defaultId = eqService.GetDefaultProfileId();

        // 1. Create 15 custom profiles
        var createdProfiles = new List<EqProfile>();
        for (int i = 0; i < 15; i++)
        {
            var p = eq.CreateProfile($"Custom Profile {i + 1}");
            createdProfiles.Add(p);
        }

        Assert.Equal(16, eq.Profiles.Count); // 1 default + 15 custom

        // 2. Add bands to each profile
        foreach (var p in createdProfiles)
        {
            eq.SelectedProfile = p;
            for (int b = 0; b < 5; b++)
            {
                eq.AddBand(EqFilterType.PeakEq, 200 * (b + 1), b * 1.5, 1.0);
            }
            Assert.Equal(5, eq.BandCount);
        }

        // 3. Bind a device to custom profile #5
        var targetProfile = createdProfiles[4];
        eq.SelectedProfile = targetProfile;
        var bindingOption = eq.BindingOptions.FirstOrDefault(o => o.ProfileId == targetProfile.Id);
        Assert.NotNull(bindingOption);
        eq.SelectedBindingOption = bindingOption;

        // 4. Delete custom profile #5 (active & bound)
        bool deleted = eq.DeleteCurrentProfile();
        Assert.True(deleted);

        // Verification:
        // - Count decremented
        Assert.Equal(15, eq.Profiles.Count);
        // - Active profile safely fell back to default profile
        Assert.NotNull(eq.SelectedProfile);
        Assert.Equal(defaultId, eq.SelectedProfile.Id);
        // - Deleted profile is no longer in binding options
        Assert.DoesNotContain(eq.BindingOptions, o => o.ProfileId == targetProfile.Id);

        // 5. Delete all remaining custom profiles one by one
        while (eq.Profiles.Count > 1)
        {
            var custom = eq.Profiles.First(p => p.Id != defaultId);
            eq.SelectedProfile = custom;
            bool del = eq.DeleteCurrentProfile();
            Assert.True(del);
        }

        // Final state: Exactly 1 profile (default), CanDeleteProfile must be false
        Assert.Single(eq.Profiles);
        Assert.Equal(defaultId, eq.Profiles[0].Id);
        Assert.False(eq.CanDeleteProfile);

        // Attempting to delete last remaining default profile must be rejected
        Assert.False(eq.DeleteCurrentProfile());
        Assert.Single(eq.Profiles);
    }

    [Fact]
    public void EqualizerSettingsViewModel_BandBoundaryChurn_MaintainsConsistency()
    {
        var (vm, _, _, _) = CreateMasterViewModel();
        var eq = vm.Equalizer;

        eq.SelectedProfile!.Bands.Clear();
        eq.RefreshProfiles(eq.SelectedProfile.Id);
        Assert.Equal(0, eq.BandCount);

        // Add 20 bands
        for (int i = 0; i < 20; i++)
        {
            Assert.True(eq.AddBand(EqFilterType.PeakEq, 100 * (i + 1), 1.0, 1.0));
        }
        Assert.Equal(20, eq.BandCount);
        Assert.False(eq.CanAddBand);

        // Churn: remove first, add new, remove last, add new, remove middle, add new
        for (int round = 0; round < 10; round++)
        {
            // Remove band at 0
            Assert.True(eq.RemoveBandAt(0));
            Assert.Equal(19, eq.BandCount);
            Assert.True(eq.CanAddBand);

            // Add new band
            Assert.True(eq.AddBand(EqFilterType.LowShelf, 60, 2.0, 0.7));
            Assert.Equal(20, eq.BandCount);

            // Remove band at last index (19)
            Assert.True(eq.RemoveBandAt(19));
            Assert.Equal(19, eq.BandCount);

            // Add new band
            Assert.True(eq.AddBand(EqFilterType.HighShelf, 14000, 3.0, 0.7));
            Assert.Equal(20, eq.BandCount);

            // Remove middle band (10)
            Assert.True(eq.RemoveBandAt(10));
            Assert.Equal(19, eq.BandCount);

            // Add new band
            Assert.True(eq.AddBand(EqFilterType.PeakEq, 2500, -1.5, 2.0));
            Assert.Equal(20, eq.BandCount);
        }

        // Verify index integrity: all 20 bands must have Index == 0..19 and matching DisplayNumber
        for (int i = 0; i < eq.Bands.Count; i++)
        {
            Assert.Equal(i, eq.Bands[i].Index);
            Assert.Equal($"밴드 {i + 1}", eq.Bands[i].DisplayNumber);
            Assert.Equal(EqVisualizerCalculator.GetBandColorHex(i), eq.Bands[i].ColorHex);
        }
    }

    #endregion

    #region 4. Concurrent Multithreaded Stress Test

    [Fact]
    public async Task SettingsViewModel_ConcurrentMultiThreadedStress_NoDeadlocksOrExceptions()
    {
        var (vm, settings, _, _) = CreateMasterViewModel(isExclusive: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var token = cts.Token;

        var tasks = new List<Task>();

        // Worker 1: Rapid Audio changes
        tasks.Add(Task.Run(() =>
        {
            int step = 0;
            while (!token.IsCancellationRequested)
            {
                vm.Audio.DriverType = (AudioDriverType)(step % 3);
                vm.Audio.LatencyMs = 30 + (step % 470);
                vm.Audio.UseExclusiveMode = (step % 2 == 0);
                step++;
            }
        }));

        // Worker 2: Rapid Playback Normalizer & ReplayGain changes
        tasks.Add(Task.Run(() =>
        {
            int step = 0;
            while (!token.IsCancellationRequested)
            {
                vm.Playback.NormalizerEnabled = (step % 2 == 0);
                vm.Playback.NormalizerTargetDb = -24.0 + (step % 18);
                vm.Playback.NormalizerMaxBoostDb = step % 18;
                vm.Playback.NormalizerSpeedIndex = step % 3;
                vm.Playback.ReplayGainPreampDb = -12.0 + (step % 24);
                step++;
            }
        }));

        // Worker 3: Rapid Equalizer Preamp & Visualizer recalculation
        tasks.Add(Task.Run(() =>
        {
            int step = 0;
            while (!token.IsCancellationRequested)
            {
                vm.Equalizer.IsMasterEnabled = (step % 2 == 0);
                vm.Equalizer.PreampDb = -12.0 + (step % 24);
                vm.Equalizer.RecalculateVisualizer(600 + (step % 400), 150 + (step % 200));
                step++;
            }
        }));

        // Worker 4: Category Navigation and Session changes
        tasks.Add(Task.Run(() =>
        {
            int step = 0;
            while (!token.IsCancellationRequested)
            {
                vm.SelectedCategoryIndex = step % 8;
                vm.HandleSessionChanged(new SessionInfo("Speakers", step % 2 == 0, "44.1kHz / 24-bit", 100));
                step++;
            }
        }));

        // Wait for stress run to complete
        await Task.WhenAll(tasks);

        // Invariant checks after stress
        Assert.NotNull(vm.Audio);
        Assert.NotNull(vm.Playback);
        Assert.NotNull(vm.Equalizer);
        Assert.NotNull(vm.Equalizer.VisualizerData);
        Assert.True(vm.Equalizer.VisualizerData.PlotWidth > 0);
        Assert.True(vm.Equalizer.VisualizerData.PlotHeight > 0);
    }

    #endregion
}
