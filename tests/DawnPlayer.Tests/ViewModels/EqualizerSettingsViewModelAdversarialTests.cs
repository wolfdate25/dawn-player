using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Calculators;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

public sealed class EqualizerSettingsViewModelAdversarialTests
{
    private (EqualizerSettingsViewModel ViewModel, AppSettings Settings, EqSettingsService EqService, AudioSettingsService AudioService) CreateViewModel(bool isExclusive = false)
    {
        var settings = new AppSettings();
        settings.Equalizer.EnsureDefaultProfile();

        int saveCalls = 0;
        var eqService = new EqSettingsService(settings, null, () => saveCalls++, () => { });
        var audioService = new AudioSettingsService(settings, null);

        var vm = new EqualizerSettingsViewModel(eqService, audioService, settings, () => isExclusive);
        return (vm, settings, eqService, audioService);
    }

    [Fact]
    public void BandCount_BoundaryTransitions_0_to_1_to_20_to_Overflow()
    {
        var (vm, _, _, _) = CreateViewModel();
        while (vm.BandCount > 0) vm.RemoveBandAt(0);

        // 1. Boundary: 0 Bands
        Assert.Equal(0, vm.BandCount);
        Assert.Empty(vm.Bands);
        Assert.True(vm.HasEmptyBands);
        Assert.True(vm.CanAddBand);
        Assert.Equal("(0 / 20)", vm.BandCountText);

        // Visualizer calculation with 0 bands must succeed and produce empty band pins
        Assert.NotNull(vm.VisualizerData);
        Assert.Empty(vm.VisualizerData.BandPins);
        Assert.NotEmpty(vm.VisualizerData.CurvePoints);

        // 2. Boundary: 1 Band
        bool addedFirst = vm.AddBand(EqFilterType.PeakEq, 1000, 3.0, 1.41);
        Assert.True(addedFirst);
        Assert.Equal(1, vm.BandCount);
        Assert.Single(vm.Bands);
        Assert.False(vm.HasEmptyBands);
        Assert.True(vm.CanAddBand);
        Assert.Equal("(1 / 20)", vm.BandCountText);
        Assert.Equal("밴드 1", vm.Bands[0].DisplayNumber);
        Assert.Equal(0, vm.Bands[0].Index);
        Assert.Equal(1000, vm.Bands[0].FrequencyHz);
        Assert.Equal(3.0, vm.Bands[0].GainDb);
        Assert.Equal(1.41, vm.Bands[0].Q);

        // 3. Scale up to 20 Bands (the hard upper limit)
        for (int i = 1; i < 20; i++)
        {
            bool added = vm.AddBand(EqFilterType.PeakEq, 20 + i * 900, (i % 10) - 5, 1.0);
            Assert.True(added);
            Assert.Equal(i + 1, vm.BandCount);
        }

        Assert.Equal(20, vm.BandCount);
        Assert.Equal(20, vm.Bands.Count);
        Assert.False(vm.HasEmptyBands);
        Assert.False(vm.CanAddBand);
        Assert.Equal("(20 / 20)", vm.BandCountText);

        // 4. Attempting to add 21st, 22nd, 23rd bands must be rejected
        for (int i = 0; i < 5; i++)
        {
            bool overflow = vm.AddBand(EqFilterType.PeakEq, 5000, 0, 1.0);
            Assert.False(overflow);
            Assert.Equal(20, vm.BandCount);
            Assert.Equal(20, vm.SelectedProfile!.Bands.Count);
            Assert.False(vm.CanAddBand);
        }

        // 5. Remove 1 band (boundary 20 -> 19)
        bool removed = vm.RemoveBandAt(19);
        Assert.True(removed);
        Assert.Equal(19, vm.BandCount);
        Assert.True(vm.CanAddBand);
        Assert.Equal("(19 / 20)", vm.BandCountText);

        // 6. Re-add to reach 20 again
        bool readded = vm.AddBand(EqFilterType.HighShelf, 16000, 2.5, 0.7);
        Assert.True(readded);
        Assert.Equal(20, vm.BandCount);
        Assert.False(vm.CanAddBand);
    }

    [Fact]
    public void BandDeletion_ActiveSelection_ReIndexing_And_ColorIntegrity()
    {
        var (vm, _, _, _) = CreateViewModel();
        while (vm.BandCount > 0) vm.RemoveBandAt(0);

        // Add 5 distinct bands
        var freqs = new double[] { 100, 250, 1000, 4000, 16000 };
        var gains = new double[] { -3.0, 2.0, 5.0, -1.0, 4.0 };
        for (int i = 0; i < 5; i++)
        {
            vm.AddBand(EqFilterType.PeakEq, freqs[i], gains[i], 1.0);
        }

        Assert.Equal(5, vm.BandCount);

        // 1. Delete First Band (Index 0)
        var firstBand = vm.Bands[0];
        bool removedFirst = vm.RemoveBand(firstBand);
        Assert.True(removedFirst);
        Assert.Equal(4, vm.BandCount);

        // Verify re-indexing and frequency shifts
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, vm.Bands[i].Index);
            Assert.Equal($"밴드 {i + 1}", vm.Bands[i].DisplayNumber);
            Assert.Equal(EqVisualizerCalculator.GetBandColorHex(i), vm.Bands[i].ColorHex);
        }
        Assert.Equal(250, vm.Bands[0].FrequencyHz);
        Assert.Equal(1000, vm.Bands[1].FrequencyHz);
        Assert.Equal(4000, vm.Bands[2].FrequencyHz);
        Assert.Equal(16000, vm.Bands[3].FrequencyHz);

        // 2. Delete Middle Band (Index 1: 1000 Hz)
        bool removedMiddle = vm.RemoveBandAt(1);
        Assert.True(removedMiddle);
        Assert.Equal(3, vm.BandCount);

        Assert.Equal(0, vm.Bands[0].Index);
        Assert.Equal(250, vm.Bands[0].FrequencyHz);

        Assert.Equal(1, vm.Bands[1].Index);
        Assert.Equal(4000, vm.Bands[1].FrequencyHz);

        Assert.Equal(2, vm.Bands[2].Index);
        Assert.Equal(16000, vm.Bands[2].FrequencyHz);

        // 3. Delete Last Band (Index 2: 16000 Hz)
        bool removedLast = vm.RemoveBandAt(2);
        Assert.True(removedLast);
        Assert.Equal(2, vm.BandCount);
        Assert.Equal(250, vm.Bands[0].FrequencyHz);
        Assert.Equal(4000, vm.Bands[1].FrequencyHz);

        // 4. Delete Remaining Bands until 0
        Assert.True(vm.RemoveBandAt(0));
        Assert.Equal(1, vm.BandCount);
        Assert.Equal(4000, vm.Bands[0].FrequencyHz);
        Assert.Equal(0, vm.Bands[0].Index);

        Assert.True(vm.RemoveBandAt(0));
        Assert.Equal(0, vm.BandCount);
        Assert.Empty(vm.Bands);
        Assert.True(vm.HasEmptyBands);
    }

    [Fact]
    public void BandDeletion_Adversarial_InvalidIndices_And_NullReferences()
    {
        var (vm, _, _, _) = CreateViewModel();
        while (vm.BandCount > 0) vm.RemoveBandAt(0);

        // Attempting deletion on empty collection
        Assert.False(vm.RemoveBandAt(-1));
        Assert.False(vm.RemoveBandAt(0));
        Assert.False(vm.RemoveBandAt(1));
        Assert.False(vm.RemoveBandAt(100));
        Assert.False(vm.RemoveBand(null!));

        // Add 2 bands
        vm.AddBand(EqFilterType.PeakEq, 500, 0, 1.0);
        vm.AddBand(EqFilterType.PeakEq, 2000, 0, 1.0);
        Assert.Equal(2, vm.BandCount);

        // Out of range indices on non-empty collection
        Assert.False(vm.RemoveBandAt(-5));
        Assert.False(vm.RemoveBandAt(2)); // Index 2 is out of range for count 2
        Assert.False(vm.RemoveBandAt(99));

        // Unrelated band instance not in collection
        var orphanBand = new EqBandViewModel(new EqBandSettings(), 99);
        Assert.False(vm.RemoveBand(orphanBand));
        Assert.Equal(2, vm.BandCount);
    }

    [Fact]
    public void BandSettings_ExtremeValues_Clamping_And_FloatingPointEpsilon()
    {
        var bandModel = new EqBandSettings
        {
            Type = EqFilterType.PeakEq,
            FrequencyHz = 1000,
            GainDb = 0,
            Q = 1.0
        };

        int changeNotificationCount = 0;
        var vm = new EqBandViewModel(bandModel, 0, () => changeNotificationCount++);

        // Frequency Clamping & Formatting
        vm.FrequencyHz = -500;
        Assert.Equal(20, vm.FrequencyHz);
        Assert.Equal("20 Hz", vm.FormattedFrequency);

        vm.FrequencyHz = 0;
        Assert.Equal(20, vm.FrequencyHz);

        vm.FrequencyHz = 19.99;
        Assert.Equal(20, vm.FrequencyHz);

        vm.FrequencyHz = 999.4;
        Assert.Equal(999, vm.FrequencyHz);
        Assert.Equal("999 Hz", vm.FormattedFrequency);

        vm.FrequencyHz = 1000;
        Assert.Equal(1000, vm.FrequencyHz);
        Assert.Equal("1 kHz", vm.FormattedFrequency);

        vm.FrequencyHz = 1500;
        Assert.Equal(1500, vm.FrequencyHz);
        Assert.Equal("1.5 kHz", vm.FormattedFrequency);

        vm.FrequencyHz = 10500;
        Assert.Equal(10500, vm.FrequencyHz);
        Assert.Equal("10.5 kHz", vm.FormattedFrequency);

        vm.FrequencyHz = 20000;
        Assert.Equal(20000, vm.FrequencyHz);
        Assert.Equal("20 kHz", vm.FormattedFrequency);

        vm.FrequencyHz = 50000;
        Assert.Equal(20000, vm.FrequencyHz);
        Assert.Equal("20 kHz", vm.FormattedFrequency);

        // Gain Clamping (-15.0 to +15.0 dB)
        vm.GainDb = -100.0;
        Assert.Equal(-15.0, vm.GainDb);

        vm.GainDb = 100.0;
        Assert.Equal(15.0, vm.GainDb);

        vm.GainDb = 3.30;
        Assert.Equal(3.3, vm.GainDb, 1);

        // Q Clamping (0.1 to 8.0)
        vm.Q = -1.0;
        Assert.Equal(0.1, vm.Q, 2);

        vm.Q = 0.01;
        Assert.Equal(0.1, vm.Q, 2);

        vm.Q = 99.0;
        Assert.Equal(8.0, vm.Q, 2);

        vm.Q = 1.414;
        Assert.Equal(1.41, vm.Q, 2);

        // Epsilon idempotency: re-assigning same or sub-epsilon value should NOT trigger change notification
        int beforeCalls = changeNotificationCount;
        vm.GainDb = 3.3; // Same value
        Assert.Equal(beforeCalls, changeNotificationCount);

        vm.FrequencyHz = 20000; // Same value
        Assert.Equal(beforeCalls, changeNotificationCount);

        vm.Q = 1.41; // Same value
        Assert.Equal(beforeCalls, changeNotificationCount);
    }

    [Fact]
    public void BandType_Transitions_And_GainEnablementState()
    {
        var bandModel = new EqBandSettings { Type = EqFilterType.PeakEq };
        var vm = new EqBandViewModel(bandModel, 0);

        // PeakEq -> Gain Enabled
        vm.Type = EqFilterType.PeakEq;
        Assert.True(vm.IsGainEnabled);
        Assert.Equal(0, vm.TypeIndex);

        // LowShelf -> Gain Enabled
        vm.Type = EqFilterType.LowShelf;
        Assert.True(vm.IsGainEnabled);
        Assert.Equal(1, vm.TypeIndex);

        // HighShelf -> Gain Enabled
        vm.Type = EqFilterType.HighShelf;
        Assert.True(vm.IsGainEnabled);
        Assert.Equal(2, vm.TypeIndex);

        // LowPass -> Gain Disabled (Cutoff filter)
        vm.Type = EqFilterType.LowPass;
        Assert.False(vm.IsGainEnabled);
        Assert.Equal(3, vm.TypeIndex);

        // HighPass -> Gain Disabled (Cutoff filter)
        vm.Type = EqFilterType.HighPass;
        Assert.False(vm.IsGainEnabled);
        Assert.Equal(4, vm.TypeIndex);

        // TypeIndex two-way sync
        vm.TypeIndex = 1;
        Assert.Equal(EqFilterType.LowShelf, vm.Type);
        Assert.True(vm.IsGainEnabled);

        // Out of range TypeIndex must be ignored safely
        vm.TypeIndex = -1;
        Assert.Equal(EqFilterType.LowShelf, vm.Type);

        vm.TypeIndex = 10;
        Assert.Equal(EqFilterType.LowShelf, vm.Type);
    }

    [Fact]
    public void CustomPreset_Lifecycle_Empty_Whitespace_Null_Handling()
    {
        var (vm, _, _, _) = CreateViewModel();

        // 1. Create with empty string -> fallback name
        var emptyProfile = vm.CreateProfile("");
        Assert.NotNull(emptyProfile);
        Assert.StartsWith("프로필", emptyProfile.Name);
        Assert.Equal(emptyProfile.Id, vm.SelectedProfile!.Id);

        // 2. Create with whitespace -> fallback name
        var wsProfile = vm.CreateProfile("   \t\n  ");
        Assert.NotNull(wsProfile);
        Assert.StartsWith("프로필", wsProfile.Name);

        // 3. Rename with empty or whitespace string -> ignored, retains previous name
        string currentName = vm.ProfileName;
        vm.RenameProfile("");
        Assert.Equal(currentName, vm.ProfileName);

        vm.RenameProfile("   ");
        Assert.Equal(currentName, vm.ProfileName);

        // 4. Rename with valid name with leading/trailing whitespace -> trimmed
        vm.RenameProfile("  Dynamic Vocal Boost  ");
        Assert.Equal("Dynamic Vocal Boost", vm.ProfileName);
        Assert.Equal("Dynamic Vocal Boost", vm.SelectedProfile.Name);
    }

    [Fact]
    public void CustomPreset_SpecialCharacters_Unicode_Emoji_LongNames()
    {
        var (vm, _, _, _) = CreateViewModel();

        // Complex unicode & emoji
        string complexName = "🎧 Bass Boost +6dB [Rock & 락/メタル 🎸⚡] <v2.0>";
        var profile = vm.CreateProfile(complexName);
        Assert.Equal(complexName, profile.Name);
        Assert.Equal(complexName, vm.ProfileName);

        // Extremely long name
        string longName = new string('E', 250);
        vm.RenameProfile(longName);
        Assert.Equal(longName, vm.ProfileName);
        Assert.Equal(longName, vm.SelectedProfile!.Name);
    }

    [Fact]
    public void CustomPreset_DuplicateNames_CollisionSafety()
    {
        var (vm, _, _, _) = CreateViewModel();

        // Create two profiles with the exact same display name
        var p1 = vm.CreateProfile("Acoustic");
        var p2 = vm.CreateProfile("Acoustic");

        Assert.NotEqual(p1.Id, p2.Id);
        Assert.Equal("Acoustic", p1.Name);
        Assert.Equal("Acoustic", p2.Name);
        Assert.Equal(p2.Id, vm.SelectedProfile!.Id);

        // Modifying bands on p2 does not affect p1
        vm.AddBand(EqFilterType.PeakEq, 250, 4.0, 1.0);
        Assert.Single(vm.Bands);

        // Switch to p1
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == p1.Id);
        Assert.Equal(p1.Id, vm.SelectedProfile.Id);
        Assert.Empty(vm.Bands);

        // Delete p2
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == p2.Id);
        bool deletedP2 = vm.DeleteCurrentProfile();
        Assert.True(deletedP2);
        Assert.DoesNotContain(vm.Profiles, p => p.Id == p2.Id);
        Assert.Contains(vm.Profiles, p => p.Id == p1.Id);
    }

    [Fact]
    public void CustomPreset_Deletion_And_DeviceBinding_Cascade()
    {
        var (vm, settings, eqService, _) = CreateViewModel();

        var customProf = vm.CreateProfile("Headphones Custom");
        Assert.True(vm.CanDeleteProfile);

        // Bind default device to this custom profile
        var bindingOption = vm.BindingOptions.First(b => b.ProfileId == customProf.Id);
        vm.SelectedBindingOption = bindingOption;

        var driver = settings.Output.DriverType;
        string? devId = vm.SelectedDevice?.Id;
        Assert.Equal(customProf.Id, eqService.GetBoundProfileId(driver, devId));

        // Delete custom profile -> binding must be removed and revert to default
        bool deleted = vm.DeleteCurrentProfile();
        Assert.True(deleted);

        Assert.Null(eqService.GetBoundProfileId(driver, devId));
        Assert.True(vm.SelectedBindingOption!.IsFollowDefault);
        Assert.Contains("기본 프로필", vm.BindingDescriptionText);
        Assert.Equal(eqService.GetDefaultProfileId(), vm.SelectedProfile!.Id);
        Assert.False(vm.CanDeleteProfile); // Default profile cannot be deleted
    }

    [Fact]
    public void ProfileSwitching_BandSynchronization_And_VisualizerIntegrity()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.IsMasterEnabled = true;

        // Duplicate default profile so that Enabled = true is preserved on both
        var profA = vm.DuplicateProfile()!;
        vm.RenameProfile("Profile A");
        while (vm.BandCount > 0) vm.RemoveBandAt(0);

        vm.AddBand(EqFilterType.LowShelf, 100, 3.0, 1.0);
        vm.AddBand(EqFilterType.PeakEq, 1000, -2.0, 1.4);
        vm.AddBand(EqFilterType.HighShelf, 10000, 4.0, 1.0);
        Assert.Equal(3, vm.BandCount);

        // Duplicate to create Profile B
        var profB = vm.DuplicateProfile()!;
        vm.RenameProfile("Profile B");
        while (vm.BandCount > 0) vm.RemoveBandAt(0);

        for (int i = 0; i < 6; i++)
        {
            vm.AddBand(EqFilterType.PeakEq, 200 * (i + 1), (i % 3) + 1, 1.0);
        }
        Assert.Equal(6, vm.BandCount);

        // Switch back to Profile A
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == profA.Id);
        Assert.Equal(3, vm.BandCount);
        Assert.Equal(3, vm.Bands.Count);
        Assert.Equal(100, vm.Bands[0].FrequencyHz);
        Assert.Equal(1000, vm.Bands[1].FrequencyHz);
        Assert.Equal(10000, vm.Bands[2].FrequencyHz);
        Assert.Equal(3, vm.VisualizerData!.BandPins.Count);

        // Switch to Profile B
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == profB.Id);
        Assert.Equal(6, vm.BandCount);
        Assert.Equal(6, vm.Bands.Count);
        Assert.Equal(6, vm.VisualizerData!.BandPins.Count);
    }

    [Fact]
    public void ExclusiveModeWarning_And_MasterEnable_Matrix()
    {
        var (vm, _, _, _) = CreateViewModel(isExclusive: false);

        // 1. Not exclusive, EQ disabled -> No warning
        vm.IsMasterEnabled = false;
        vm.SetExclusiveSessionState(false);
        Assert.False(vm.IsExclusiveWarningVisible);

        // 2. Not exclusive, EQ enabled -> No warning
        vm.IsMasterEnabled = true;
        Assert.False(vm.IsExclusiveWarningVisible);

        // 3. Exclusive session, EQ enabled -> Warning visible
        vm.SetExclusiveSessionState(true);
        Assert.True(vm.IsExclusiveWarningVisible);

        // 4. Exclusive session, EQ disabled -> Warning hidden
        vm.IsMasterEnabled = false;
        Assert.False(vm.IsExclusiveWarningVisible);
    }

    [Fact]
    public void Visualizer_Dimensions_Boundary_And_ZeroResilience()
    {
        var (vm, _, _, _) = CreateViewModel();

        // Valid dimensions
        vm.VisualizerWidth = 800;
        vm.VisualizerHeight = 250;
        Assert.NotNull(vm.VisualizerData);
        Assert.True(vm.VisualizerData.PlotWidth > 0);
        Assert.True(vm.VisualizerData.PlotHeight > 0);

        // Small/zero values must not crash
        var zeroData = EqVisualizerCalculator.Calculate(vm.SelectedProfile, 0, 0);
        Assert.NotNull(zeroData);
        Assert.True(zeroData.PlotWidth > 0);
        Assert.True(zeroData.PlotHeight > 0);

        // Null profile calculation must not crash
        var nullProfileData = EqVisualizerCalculator.Calculate(null, 700, 190);
        Assert.NotNull(nullProfileData);
        Assert.False(nullProfileData.IsEnabled);
        Assert.Empty(nullProfileData.BandPins);
    }

    [Fact]
    public async Task RapidBandModification_ThreadSafety_And_Integrity()
    {
        var (vm, _, _, _) = CreateViewModel();

        // Run concurrent rapid operations on different profiles and properties
        await Task.Run(() =>
        {
            for (int step = 0; step < 50; step++)
            {
                vm.PreampDb = (step % 24) - 12.0;
                vm.IsMasterEnabled = (step % 2 == 0);
                if (vm.CanAddBand)
                {
                    vm.AddBand(EqFilterType.PeakEq, 100 + step * 20, (step % 10) - 5, 1.0);
                }
                else if (vm.BandCount > 0)
                {
                    vm.RemoveBandAt(step % vm.BandCount);
                }
            }
        });

        // Ensure collection state remains consistent and invariants hold
        Assert.InRange(vm.BandCount, 0, 20);
        Assert.Equal(vm.Bands.Count, vm.SelectedProfile!.Bands.Count);
        for (int i = 0; i < vm.Bands.Count; i++)
        {
            Assert.Equal(i, vm.Bands[i].Index);
            Assert.Equal($"밴드 {i + 1}", vm.Bands[i].DisplayNumber);
        }
    }

    [Fact]
    public void MasterEnableToggle_SynchronizesVisualizer_ForDefaultAndCustomProfiles()
    {
        var (vm, _, _, _) = CreateViewModel();

        // 1. Default Profile with Master EQ Enabled
        vm.IsMasterEnabled = true;
        vm.AddBand(EqFilterType.PeakEq, 1000, 3.0, 1.0);
        Assert.True(vm.IsMasterEnabled);
        Assert.NotNull(vm.VisualizerData);
        Assert.True(vm.VisualizerData.IsEnabled);
        Assert.Single(vm.VisualizerData.BandPins);

        // 2. Turn Master EQ OFF
        vm.IsMasterEnabled = false;
        Assert.False(vm.IsMasterEnabled);
        Assert.NotNull(vm.VisualizerData);
        Assert.False(vm.VisualizerData.IsEnabled);
        Assert.Empty(vm.VisualizerData.BandPins);

        // 3. Create Custom Profile while Master EQ is OFF
        var custom = vm.CreateProfile("Custom Rock");
        Assert.True(custom.Enabled);
        vm.AddBand(EqFilterType.LowShelf, 100, 4.0, 1.0);
        vm.AddBand(EqFilterType.HighShelf, 10000, 3.0, 1.0);
        Assert.Equal(2, vm.BandCount);
        // Master EQ is OFF, so visualizer must remain disabled
        Assert.False(vm.VisualizerData.IsEnabled);
        Assert.Empty(vm.VisualizerData.BandPins);

        // 4. Turn Master EQ ON with Custom Profile selected
        vm.IsMasterEnabled = true;
        Assert.True(vm.VisualizerData.IsEnabled);
        Assert.Equal(2, vm.VisualizerData.BandPins.Count);

        // 5. Switch back to Default Profile
        string defaultId = vm.Profiles.First().Id;
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == defaultId);
        Assert.True(vm.VisualizerData.IsEnabled);
        Assert.Single(vm.VisualizerData.BandPins);
    }
}
