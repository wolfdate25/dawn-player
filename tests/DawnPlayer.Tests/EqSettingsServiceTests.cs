using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public sealed class EqSettingsServiceTests
{
    [Fact]
    public void GetProfiles_ReturnsAllProfilesAndEnsuresDefault()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var profiles = service.GetProfiles();

        Assert.NotEmpty(profiles);
        Assert.Contains(profiles, p => p.Id == service.GetDefaultProfileId());
    }

    [Fact]
    public void CreateProfile_AddsNewNamedProfile()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var created = service.CreateProfile("보컬 튜닝");

        Assert.NotNull(created);
        Assert.Equal("보컬 튜닝", created.Name);
        Assert.NotEmpty(created.Id);
        Assert.True(created.Enabled);

        var found = service.GetProfileById(created.Id);
        Assert.NotNull(found);
        Assert.Equal("보컬 튜닝", found.Name);
        Assert.True(found.Enabled);
    }

    [Fact]
    public void CreateProfile_WithTemplate_DuplicatesParameters()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var template = new EqProfile
        {
            Name = "템플릿",
            Enabled = true,
            PreampDb = -4.5,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 80, GainDb = 5.0, Q = 1.0 }
            }
        };

        var duplicated = service.CreateProfile("복사본", template);

        Assert.NotEqual(template.Id, duplicated.Id);
        Assert.Equal("복사본", duplicated.Name);
        Assert.True(duplicated.Enabled);
        Assert.Equal(-4.5, duplicated.PreampDb);
        Assert.Single(duplicated.Bands);
        Assert.Equal(80, duplicated.Bands[0].FrequencyHz);
    }

    [Fact]
    public void RenameProfile_UpdatesNameSuccessfully()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var profile = service.CreateProfile("구 이름");
        service.RenameProfile(profile.Id, "신규 이름");

        var updated = service.GetProfileById(profile.Id);
        Assert.NotNull(updated);
        Assert.Equal("신규 이름", updated.Name);
    }

    [Fact]
    public void DeleteProfile_RemovesProfileAndCleansUpDeviceBindings()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var custom = service.CreateProfile("삭제할 프로필");
        service.BindDeviceToProfile(AudioDriverType.Wasapi, "device-1", custom.Id);
        service.BindDeviceToProfile(AudioDriverType.DirectSound, "device-2", custom.Id);

        Assert.Equal(custom.Id, service.GetBoundProfileId(AudioDriverType.Wasapi, "device-1"));
        Assert.Equal(custom.Id, service.GetBoundProfileId(AudioDriverType.DirectSound, "device-2"));

        bool deleted = service.DeleteProfile(custom.Id);
        Assert.True(deleted);

        Assert.Null(service.GetProfileById(custom.Id));
        // Device bindings pointing to deleted profile must be automatically purged
        Assert.Null(service.GetBoundProfileId(AudioDriverType.Wasapi, "device-1"));
        Assert.Null(service.GetBoundProfileId(AudioDriverType.DirectSound, "device-2"));
    }

    [Fact]
    public void DeleteProfile_WhenDefaultProfile_ReturnsFalseAndPreventsDeletion()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        string defaultId = service.GetDefaultProfileId();
        bool deleted = service.DeleteProfile(defaultId);

        Assert.False(deleted);
        Assert.NotNull(service.GetProfileById(defaultId));
    }

    [Fact]
    public void SaveProfile_ClampsParametersAndSupportsUpTo20Bands()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);

        var profile = service.CreateProfile("20밴드 테스트");
        profile.PreampDb = 30.0; // Out of bounds

        // Add 25 bands
        profile.Bands = Enumerable.Range(1, 25).Select(i => new EqBandSettings
        {
            FrequencyHz = i * 500,
            GainDb = i * 2.0, // Some will exceed +15dB
            Q = 15.0          // Exceeds 8.0
        }).ToList();

        service.SaveProfile(profile);

        var saved = service.GetProfileById(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal(12.0, saved.PreampDb);
        // Max 20 bands enforced
        Assert.Equal(20, saved.Bands.Count);
        Assert.All(saved.Bands, b =>
        {
            Assert.InRange(b.FrequencyHz, 20.0, 20000.0);
            Assert.InRange(b.GainDb, -15.0, 15.0);
            Assert.InRange(b.Q, 0.1, 8.0);
        });
    }

    [Fact]
    public void BindDeviceToProfile_And_GetResolvedProfileForDevice_WorksSeamlessly()
    {
        var settings = new AppSettings();
        var service = new EqSettingsService(settings);
        service.SetEnabled(true);

        var custom = service.CreateProfile("헤드폰 전용");
        custom.PreampDb = -3.0;
        service.SaveProfile(custom);

        service.BindDeviceToProfile(AudioDriverType.Wasapi, "dac-usb", custom.Id);

        var resolved = service.GetResolvedProfileForDevice(AudioDriverType.Wasapi, "dac-usb");
        Assert.Equal(custom.Id, resolved.Id);
        Assert.True(resolved.Enabled);
        Assert.Equal(-3.0, resolved.PreampDb);

        // Unbind (pass null to follow default)
        service.BindDeviceToProfile(AudioDriverType.Wasapi, "dac-usb", null);
        var resolvedDefault = service.GetResolvedProfileForDevice(AudioDriverType.Wasapi, "dac-usb");
        Assert.Equal(service.GetDefaultProfileId(), resolvedDefault.Id);
    }

    [Theory]
    [InlineData(-20.0, -12.0)]
    [InlineData(-12.0, -12.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(12.0, 12.0)]
    [InlineData(25.0, 12.0)]
    public void ClampProfile_PreampDb_ClampsProperly(double input, double expected)
    {
        var profile = new EqProfile { PreampDb = input };
        var clamped = EqSettingsService.ClampProfile(profile);
        Assert.Equal(expected, clamped.PreampDb);
    }

    [Theory]
    [InlineData(5.0, 20.0)]
    [InlineData(20.0, 20.0)]
    [InlineData(1000.0, 1000.0)]
    [InlineData(20000.0, 20000.0)]
    [InlineData(40000.0, 20000.0)]
    public void ClampProfile_FrequencyHz_ClampsProperly(double input, double expected)
    {
        var profile = new EqProfile
        {
            Bands = new() { new EqBandSettings { FrequencyHz = input } }
        };
        var clamped = EqSettingsService.ClampProfile(profile);
        Assert.Equal(expected, clamped.Bands[0].FrequencyHz);
    }

    [Theory]
    [InlineData(-25.0, -15.0)]
    [InlineData(-15.0, -15.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(15.0, 15.0)]
    [InlineData(30.0, 15.0)]
    public void ClampProfile_GainDb_ClampsProperly(double input, double expected)
    {
        var profile = new EqProfile
        {
            Bands = new() { new EqBandSettings { GainDb = input } }
        };
        var clamped = EqSettingsService.ClampProfile(profile);
        Assert.Equal(expected, clamped.Bands[0].GainDb);
    }

    [Theory]
    [InlineData(0.01, 0.1)]
    [InlineData(0.1, 0.1)]
    [InlineData(1.414, 1.414)]
    [InlineData(8.0, 8.0)]
    [InlineData(15.0, 8.0)]
    public void ClampProfile_Q_ClampsProperly(double input, double expected)
    {
        var profile = new EqProfile
        {
            Bands = new() { new EqBandSettings { Q = input } }
        };
        var clamped = EqSettingsService.ClampProfile(profile);
        Assert.Equal(expected, clamped.Bands[0].Q);
    }

    [Fact]
    public void IsEnabledAndSetEnabled_UpdatesGlobalSettingAndTriggersCallbacks()
    {
        var settings = new AppSettings();
        bool saved = false;
        bool applied = false;

        var service = new EqSettingsService(settings, () => saved = true, () => applied = true);

        Assert.False(service.IsEnabled());

        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.True(settings.Equalizer.Enabled);
        Assert.True(saved);
        Assert.True(applied);

        service.SetEnabled(false);
        Assert.False(service.IsEnabled());
        Assert.False(settings.Equalizer.Enabled);
    }
}
