using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

public sealed class EqualizerSettingsViewModelTests
{
    private static (EqualizerSettingsViewModel ViewModel, AppSettings Settings, EqSettingsService EqService, AudioSettingsService AudioService) CreateViewModel(bool isExclusive = false)
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
    public void MasterEnabled_TogglesAndUpdatesState()
    {
        var (vm, settings, _, _) = CreateViewModel(isExclusive: true);

        var propChanges = new List<string>();
        vm.PropertyChanged += (_, e) => propChanges.Add(e.PropertyName!);

        // Default is disabled
        Assert.False(vm.IsMasterEnabled);
        Assert.False(vm.IsExclusiveWarningVisible);

        // Enable master EQ
        vm.IsMasterEnabled = true;
        Assert.True(vm.IsMasterEnabled);
        Assert.True(settings.Equalizer.Enabled);
        Assert.True(vm.IsExclusiveWarningVisible); // isExclusive is true and master is enabled
        Assert.Contains(nameof(vm.IsMasterEnabled), propChanges);
        Assert.Contains(nameof(vm.IsExclusiveWarningVisible), propChanges);

        // Disable master EQ
        propChanges.Clear();
        vm.IsMasterEnabled = false;
        Assert.False(vm.IsMasterEnabled);
        Assert.False(settings.Equalizer.Enabled);
        Assert.False(vm.IsExclusiveWarningVisible);
    }

    [Fact]
    public void ProfileCRUD_CreateDuplicateRenameDelete_WorksCorrectly()
    {
        var (vm, settings, eqService, _) = CreateViewModel();

        // Initially default profile is selected
        Assert.NotNull(vm.SelectedProfile);
        string defaultId = eqService.GetDefaultProfileId();
        Assert.Equal(defaultId, vm.SelectedProfile.Id);
        Assert.False(vm.CanDeleteProfile); // Default profile cannot be deleted

        // 1. Create new profile
        var created = vm.CreateProfile("Acoustic Boost");
        Assert.NotNull(created);
        Assert.Equal("Acoustic Boost", created.Name);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(created.Id, vm.SelectedProfile.Id);
        Assert.True(vm.CanDeleteProfile); // Custom profile can be deleted

        // 2. Duplicate profile
        var dup = vm.DuplicateProfile();
        Assert.NotNull(dup);
        Assert.Contains("복사본", dup.Name);
        Assert.Equal(3, vm.Profiles.Count);
        Assert.Equal(dup.Id, vm.SelectedProfile.Id);

        // 3. Rename profile
        vm.RenameProfile("Renamed Acoustic");
        Assert.Equal("Renamed Acoustic", vm.ProfileName);
        Assert.Equal("Renamed Acoustic", vm.SelectedProfile.Name);

        // 4. Delete profile
        bool deleted = vm.DeleteCurrentProfile();
        Assert.True(deleted);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(defaultId, vm.SelectedProfile.Id);
        Assert.False(vm.CanDeleteProfile);

        // Attempting to delete default profile must return false
        bool deleteDefault = vm.DeleteCurrentProfile();
        Assert.False(deleteDefault);
        Assert.Equal(2, vm.Profiles.Count);
    }

    [Fact]
    public void Preamp_ClampingAndVisualizerRecalculation()
    {
        var (vm, _, _, _) = CreateViewModel();
        Assert.NotNull(vm.SelectedProfile);

        var propChanges = new List<string>();
        vm.PropertyChanged += (_, e) => propChanges.Add(e.PropertyName!);

        // Normal range
        vm.PreampDb = 4.5;
        Assert.Equal(4.5, vm.PreampDb, 1);
        Assert.Equal(4.5, vm.SelectedProfile.PreampDb, 1);
        Assert.Contains(nameof(vm.PreampDb), propChanges);

        // Out of range clamping
        vm.PreampDb = 25.0;
        Assert.Equal(12.0, vm.PreampDb, 1);

        vm.PreampDb = -30.0;
        Assert.Equal(-12.0, vm.PreampDb, 1);
    }

    [Fact]
    public void BandCollection_AddBand_EnforcesMax20Limit()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.SelectedProfile!.Bands.Clear();
        vm.RefreshProfiles(vm.SelectedProfile.Id);

        Assert.Equal(0, vm.BandCount);
        Assert.True(vm.HasEmptyBands);
        Assert.True(vm.CanAddBand);

        // Add 20 bands
        for (int i = 0; i < 20; i++)
        {
            bool added = vm.AddBand(EqFilterType.PeakEq, 100 * (i + 1), 2.0, 1.0);
            Assert.True(added);
            Assert.Equal(i + 1, vm.BandCount);
            Assert.False(vm.HasEmptyBands);
        }

        Assert.Equal(20, vm.BandCount);
        Assert.False(vm.CanAddBand);
        Assert.Equal("(20 / 20)", vm.BandCountText);

        // 21st band must be rejected
        bool overflow = vm.AddBand(EqFilterType.PeakEq, 5000, 0, 1.0);
        Assert.False(overflow);
        Assert.Equal(20, vm.BandCount);
    }

    [Fact]
    public void BandCollection_RemoveBand_ReIndexesRemainingBands()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.SelectedProfile!.Bands.Clear();
        vm.RefreshProfiles(vm.SelectedProfile.Id);

        vm.AddBand(EqFilterType.LowShelf, 100, 3.0, 1.0); // Index 0
        vm.AddBand(EqFilterType.PeakEq, 1000, -2.0, 1.4);  // Index 1
        vm.AddBand(EqFilterType.HighShelf, 10000, 4.0, 1.0); // Index 2

        Assert.Equal(3, vm.BandCount);
        Assert.Equal("밴드 1", vm.Bands[0].DisplayNumber);
        Assert.Equal("밴드 2", vm.Bands[1].DisplayNumber);
        Assert.Equal("밴드 3", vm.Bands[2].DisplayNumber);

        // Remove middle band (Index 1)
        var middleBand = vm.Bands[1];
        bool removed = vm.RemoveBand(middleBand);
        Assert.True(removed);
        Assert.Equal(2, vm.BandCount);

        // Remaining bands must be re-indexed: band 0 remains 0, band 2 becomes 1
        Assert.Equal(0, vm.Bands[0].Index);
        Assert.Equal("밴드 1", vm.Bands[0].DisplayNumber);
        Assert.Equal(100, vm.Bands[0].FrequencyHz);

        Assert.Equal(1, vm.Bands[1].Index);
        Assert.Equal("밴드 2", vm.Bands[1].DisplayNumber);
        Assert.Equal(10000, vm.Bands[1].FrequencyHz);
    }

    [Fact]
    public void EqBandViewModel_PropertyClampingAndNotifications()
    {
        var model = new EqBandSettings
        {
            Type = EqFilterType.PeakEq,
            FrequencyHz = 1000,
            GainDb = 0,
            Q = 1.0
        };

        int changeCount = 0;
        var vm = new EqBandViewModel(model, 0, () => changeCount++);

        var propChanges = new List<string>();
        vm.PropertyChanged += (_, e) => propChanges.Add(e.PropertyName!);

        // Frequency clamping
        vm.FrequencyHz = 10;
        Assert.Equal(20, vm.FrequencyHz);
        Assert.Equal("20 Hz", vm.FormattedFrequency);
        Assert.Equal(1, changeCount);

        vm.FrequencyHz = 50000;
        Assert.Equal(20000, vm.FrequencyHz);
        Assert.Equal("20 kHz", vm.FormattedFrequency);

        // Gain clamping
        vm.GainDb = -25;
        Assert.Equal(-15.0, vm.GainDb);

        vm.GainDb = 30;
        Assert.Equal(15.0, vm.GainDb);

        // Q factor clamping
        vm.Q = 0.01;
        Assert.Equal(0.1, vm.Q, 2);

        vm.Q = 15.0;
        Assert.Equal(8.0, vm.Q, 2);

        // Type transitions and IsGainEnabled
        Assert.True(vm.IsGainEnabled);
        vm.Type = EqFilterType.LowPass;
        Assert.False(vm.IsGainEnabled);
        Assert.Equal(3, vm.TypeIndex);

        vm.TypeIndex = 0; // PeakEq
        Assert.True(vm.IsGainEnabled);
        Assert.Equal(EqFilterType.PeakEq, vm.Type);

        vm.Type = EqFilterType.HighPass;
        Assert.False(vm.IsGainEnabled);
    }

    [Fact]
    public void DeviceBinding_UpdatesDescriptionAndSelection()
    {
        var (vm, settings, _, _) = CreateViewModel();

        Assert.NotEmpty(vm.BindingOptions);
        var defaultOption = vm.BindingOptions.First(o => o.IsFollowDefault);
        Assert.Equal(defaultOption, vm.SelectedBindingOption);
        Assert.Contains("기본 프로필", vm.BindingDescriptionText);

        // Create a custom profile
        var custom = vm.CreateProfile("Studio Monitors");
        Assert.Contains(vm.BindingOptions, o => o.ProfileId == custom.Id);

        var customOption = vm.BindingOptions.First(o => o.ProfileId == custom.Id);
        vm.SelectedBindingOption = customOption;
        Assert.Contains("바인딩", vm.BindingDescriptionText);
    }
}
