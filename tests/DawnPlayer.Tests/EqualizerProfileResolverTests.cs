using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public sealed class EqualizerProfileResolverTests
{
    [Theory]
    [InlineData(AudioDriverType.Wasapi, "{0.0.0.00000000}.{1234}", "wasapi:{0.0.0.00000000}.{1234}")]
    [InlineData(AudioDriverType.DirectSound, "d0865882-e885-48b0-81f1-39578efc2250", "dsound:d0865882-e885-48b0-81f1-39578efc2250")]
    [InlineData(AudioDriverType.WaveOut, "0", "waveout:0")]
    [InlineData(AudioDriverType.Wasapi, null, "wasapi:")]
    [InlineData(AudioDriverType.DirectSound, null, "dsound:")]
    [InlineData(AudioDriverType.WaveOut, null, "waveout:")]
    public void CanonicalKey_FormatsExpectedPrefixAndId(AudioDriverType driver, string? deviceId, string expected)
    {
        string key = EqualizerProfileResolver.CanonicalKey(driver, deviceId);
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Resolve_WhenDeviceIsBoundToProfile_ReturnsClonedBoundProfile()
    {
        var settings = new EqualizerSettings
        {
            Enabled = true,
            DefaultProfileId = "def",
            Profiles = new()
            {
                ["def"] = new EqProfile { Id = "def", Name = "기본", Enabled = false },
                ["vocal"] = new EqProfile
                {
                    Id = "vocal",
                    Name = "보컬 강조",
                    Enabled = true,
                    PreampDb = 3.5,
                    Bands = new()
                    {
                        new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 250, GainDb = 4.0, Q = 1.2 }
                    }
                }
            },
            DeviceBindings = new()
            {
                ["wasapi:dac123"] = "vocal"
            }
        };

        var resolved = EqualizerProfileResolver.Resolve(settings, AudioDriverType.Wasapi, "dac123");

        Assert.Equal("vocal", resolved.Id);
        Assert.Equal("보컬 강조", resolved.Name);
        Assert.True(resolved.Enabled);
        Assert.Equal(3.5, resolved.PreampDb);
        Assert.Single(resolved.Bands);

        // Verification of clone isolation
        resolved.PreampDb = -6.0;
        resolved.Bands.Clear();

        Assert.Equal(3.5, settings.Profiles["vocal"].PreampDb);
        Assert.Single(settings.Profiles["vocal"].Bands);
    }

    [Fact]
    public void Resolve_WhenGlobalMasterDisabled_ResolvedProfileEnabledIsFalse()
    {
        var settings = new EqualizerSettings
        {
            Enabled = false,
            DefaultProfileId = "vocal",
            Profiles = new()
            {
                ["vocal"] = new EqProfile
                {
                    Id = "vocal",
                    Name = "보컬 강조",
                    PreampDb = 3.5,
                    Bands = new()
                    {
                        new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 250, GainDb = 4.0, Q = 1.2 }
                    }
                }
            }
        };

        var resolved = EqualizerProfileResolver.Resolve(settings, AudioDriverType.Wasapi, "dac123");
        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void Resolve_WhenMultipleDevicesBoundToSameProfile_BothResolveToSameProfileData()
    {
        var settings = new EqualizerSettings
        {
            Enabled = true,
            Profiles = new()
            {
                ["headphone"] = new EqProfile
                {
                    Id = "headphone",
                    Name = "헤드폰 HD600",
                    Enabled = true,
                    PreampDb = -2.0,
                    Bands = new()
                    {
                        new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 80, GainDb = 3.0 }
                    }
                }
            },
            DeviceBindings = new()
            {
                ["wasapi:dac-usb"] = "headphone",
                ["dsound:soundcard-pci"] = "headphone"
            }
        };

        var resolvedWasapi = EqualizerProfileResolver.Resolve(settings, AudioDriverType.Wasapi, "dac-usb");
        var resolvedDsound = EqualizerProfileResolver.Resolve(settings, AudioDriverType.DirectSound, "soundcard-pci");

        Assert.Equal("headphone", resolvedWasapi.Id);
        Assert.Equal("headphone", resolvedDsound.Id);
        Assert.Equal(-2.0, resolvedWasapi.PreampDb);
        Assert.Equal(-2.0, resolvedDsound.PreampDb);
        Assert.True(resolvedWasapi.Enabled);
        Assert.True(resolvedDsound.Enabled);
    }

    [Fact]
    public void Resolve_WhenDeviceNotBound_FallsBackToDefaultProfile()
    {
        var settings = new EqualizerSettings
        {
            Enabled = true,
            DefaultProfileId = "prof-default",
            Profiles = new()
            {
                ["prof-default"] = new EqProfile
                {
                    Id = "prof-default",
                    Name = "공통 기본",
                    Enabled = true,
                    PreampDb = -1.5,
                    Bands = new()
                    {
                        new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = 2.0, Q = 0.7 }
                    }
                }
            }
        };

        var resolved = EqualizerProfileResolver.Resolve(settings, AudioDriverType.DirectSound, "unbound-dev");

        Assert.Equal("prof-default", resolved.Id);
        Assert.True(resolved.Enabled);
        Assert.Equal(-1.5, resolved.PreampDb);
    }

    [Fact]
    public void Resolve_WhenBoundProfileDeletedOrMissing_FallsBackToDefaultProfileSafely()
    {
        var settings = new EqualizerSettings
        {
            Enabled = false,
            DefaultProfileId = "safe-default",
            Profiles = new()
            {
                ["safe-default"] = new EqProfile { Id = "safe-default", Name = "안전 기본", Enabled = false }
            },
            DeviceBindings = new()
            {
                ["wasapi:dev-ghost"] = "non-existent-profile"
            }
        };

        var resolved = EqualizerProfileResolver.Resolve(settings, AudioDriverType.Wasapi, "dev-ghost");

        Assert.Equal("safe-default", resolved.Id);
        Assert.Equal("안전 기본", resolved.Name);
        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void Resolve_WhenSettingsNull_ReturnsCleanDefaultProfile()
    {
        var resolved = EqualizerProfileResolver.Resolve(null, AudioDriverType.Wasapi, "dev1");

        Assert.NotNull(resolved);
        Assert.False(resolved.Enabled);
        Assert.Equal(0, resolved.PreampDb);
        Assert.Empty(resolved.Bands);
    }
}
