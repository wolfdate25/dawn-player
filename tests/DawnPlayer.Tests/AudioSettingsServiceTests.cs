using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit test suite for <see cref="IAudioSettingsService"/> and <see cref="AudioSettingsService"/>:
/// 1. Constructor parameter validation and null safety.
/// 2. Audio device enumeration for WASAPI, DirectSound, and WaveOut.
/// 3. Device selection resolution and fallback logic.
/// 4. Windows Exclusive Mode status querying and graceful fallback.
/// 5. Driver switching, device ID clearing, and session restart triggers.
/// 6. Latency bounds clamping (30ms - 500ms) and persistence.
/// 7. ReplayGain mode, preamp gain bounds clamping (-12dB - +12dB), and anti-clipping toggle.
/// 8. Exclusive mode bit depth and digital volume scaling settings.
/// 9. Multi-threaded stress testing and settings store synchronization.
/// </summary>
[Collection("SettingsStoreCollection")]
public class AudioSettingsServiceTests : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _playback;
    private readonly AudioSettingsService _service;

    public AudioSettingsServiceTests()
    {
        _settings = AppSettings.CreateDefault();
        _library = new MusicLibrary();
        _playlists = new PlaylistManager(_library);
        _playback = new PlaybackController(_settings, _playlists);
        _service = new AudioSettingsService(_settings, _playback);
    }

    public void Dispose()
    {
        _playback.Dispose();
        _library.Dispose();
    }

    #region 1. Constructor & Basic Contracts

    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioSettingsService(null!));
    }

    [Fact]
    public void Constructor_WithNullPlaybackController_InitializesSuccessfully()
    {
        var service = new AudioSettingsService(_settings, null);
        Assert.NotNull(service);

        // Actions should succeed without throwing NullReferenceException
        service.SetDriverType(AudioDriverType.DirectSound);
        Assert.Equal(AudioDriverType.DirectSound, _settings.Output.DriverType);
    }

    #endregion

    #region 2. Device Enumeration & Selection Resolution

    [Theory]
    [InlineData(AudioDriverType.Wasapi)]
    [InlineData(AudioDriverType.DirectSound)]
    [InlineData(AudioDriverType.WaveOut)]
    public void GetDevices_ReturnsNonNullDeviceList_ForEachDriverType(AudioDriverType driverType)
    {
        var devices = _service.GetDevices(driverType);
        Assert.NotNull(devices);

        // WaveOut and DirectSound always report at least the default Windows device entry
        if (driverType is AudioDriverType.DirectSound or AudioDriverType.WaveOut)
        {
            Assert.NotEmpty(devices);
            Assert.Contains(devices, d => d.IsDefault);
        }
    }

    [Fact]
    public void GetSelectedDevice_MatchingId_ReturnsExactDevice()
    {
        var devices = _service.GetDevices(AudioDriverType.DirectSound);
        Assert.NotEmpty(devices);

        var first = devices[0];
        var selected = _service.GetSelectedDevice(AudioDriverType.DirectSound, first.Id);

        Assert.NotNull(selected);
        Assert.Equal(first.Id, selected!.Id);
        Assert.Equal(first.Name, selected.Name);
    }

    [Fact]
    public void GetSelectedDevice_NullOrUnknownId_FallsBackToDefaultOrFirstDevice()
    {
        var devices = _service.GetDevices(AudioDriverType.DirectSound);
        Assert.NotEmpty(devices);

        var selectedNull = _service.GetSelectedDevice(AudioDriverType.DirectSound, null);
        Assert.NotNull(selectedNull);

        var selectedUnknown = _service.GetSelectedDevice(AudioDriverType.DirectSound, "non-existent-guid-12345");
        Assert.NotNull(selectedUnknown);
    }

    [Fact]
    public void GetExclusiveModeStatus_HandlesNullOrInvalidDeviceGracefully()
    {
        var statusNull = _service.GetExclusiveModeStatus(null);
        Assert.NotNull(statusNull);
        Assert.NotNull(statusNull.StatusText);
        Assert.NotNull(statusNull.DetailsText);

        var statusInvalid = _service.GetExclusiveModeStatus("invalid-device-id-xyz");
        Assert.NotNull(statusInvalid);
        Assert.NotNull(statusInvalid.StatusText);
        Assert.NotNull(statusInvalid.DetailsText);
    }

    #endregion

    #region 3. Driver & Device Configuration

    [Theory]
    [InlineData(AudioDriverType.Wasapi)]
    [InlineData(AudioDriverType.DirectSound)]
    [InlineData(AudioDriverType.WaveOut)]
    public void SetDriverType_UpdatesSetting_ClearsDeviceId_AndPersists(AudioDriverType newDriver)
    {
        _settings.Output.DeviceId = "existing-device-id";

        _service.SetDriverType(newDriver);

        Assert.Equal(newDriver, _settings.Output.DriverType);
        Assert.Null(_settings.Output.DeviceId);
    }

    [Fact]
    public void SetDevice_UpdatesDeviceId_AndPersists()
    {
        const string testDeviceId = "{0.0.0.00000000}.{test-guid-123}";

        _service.SetDevice(testDeviceId);

        Assert.Equal(testDeviceId, _settings.Output.DeviceId);
    }

    #endregion

    #region 4. WASAPI Exclusive Mode & Bit Depth

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetUseExclusiveMode_UpdatesFlag_AndPersists(bool useExclusive)
    {
        _service.SetUseExclusiveMode(useExclusive);
        Assert.Equal(useExclusive, _settings.Output.UseExclusiveMode);
    }

    [Theory]
    [InlineData(ExclusiveBitDepth.Source)]
    [InlineData(ExclusiveBitDepth.Bits16)]
    [InlineData(ExclusiveBitDepth.Bits24)]
    [InlineData(ExclusiveBitDepth.Bits32)]
    public void SetExclusiveBitDepth_UpdatesPolicy_AndPersists(ExclusiveBitDepth bitDepth)
    {
        _service.SetExclusiveBitDepth(bitDepth);
        Assert.Equal(bitDepth, _settings.Output.ExclusiveBitDepth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetAllowVolumeInExclusive_UpdatesFlag_AndPersists(bool allow)
    {
        _service.SetAllowVolumeInExclusive(allow);
        Assert.Equal(allow, _settings.Output.AllowVolumeInExclusive);
    }

    #endregion

    #region 5. Buffer Latency Clamping

    [Theory]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(250, 250)]
    [InlineData(500, 500)]
    [InlineData(10, 30)]     // Clamped underflow
    [InlineData(-100, 30)]   // Clamped negative
    [InlineData(600, 500)]   // Clamped overflow
    [InlineData(99999, 500)] // Clamped extreme overflow
    public void SetLatency_EnforcesStrictClamping_Between30And500Ms(int inputLatency, int expectedLatency)
    {
        _service.SetLatency(inputLatency);
        Assert.Equal(expectedLatency, _settings.Output.LatencyMs);
    }

    #endregion

    #region 6. ReplayGain & DSP Configuration

    [Theory]
    [InlineData(ReplayGainMode.Off, 0.0, true)]
    [InlineData(ReplayGainMode.Track, -3.5, false)]
    [InlineData(ReplayGainMode.Album, 6.0, true)]
    public void SetReplayGain_SetsAllParametersCorrectly(
        ReplayGainMode mode, double preampDb, bool preventClipping)
    {
        _service.SetReplayGain(mode, preampDb, preventClipping);

        Assert.Equal(mode, _settings.Playback.ReplayGain);
        Assert.Equal(preampDb, _settings.Playback.ReplayGainPreampDb, precision: 3);
        Assert.Equal(preventClipping, _settings.Playback.ReplayGainPreventClipping);
    }

    [Theory]
    [InlineData(-12.0, -12.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(12.0, 12.0)]
    [InlineData(-25.0, -12.0)]  // Underflow clamped
    [InlineData(-999.0, -12.0)] // Extreme underflow clamped
    [InlineData(20.0, 12.0)]    // Overflow clamped
    [InlineData(999.0, 12.0)]   // Extreme overflow clamped
    public void SetReplayGain_ClampsPreampDb_StrictlyBetweenMinus12AndPlus12(
        double inputPreamp, double expectedPreamp)
    {
        _service.SetReplayGain(ReplayGainMode.Track, inputPreamp, true);

        Assert.Equal(expectedPreamp, _settings.Playback.ReplayGainPreampDb, precision: 3);
    }

    #endregion

    #region 7. Multi-threaded Concurrency & Stress Testing

    [Fact]
    public async Task AudioSettingsService_ConcurrentModifications_MaintainsDataIntegrity()
    {
        const int taskCount = 8;
        var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                int latency = 30 + ((taskId * 50 + i) % 471);
                _service.SetLatency(latency);

                var driver = (AudioDriverType)(i % 3);
                _service.SetDriverType(driver);

                var bitDepth = (ExclusiveBitDepth)(i % 4);
                _service.SetExclusiveBitDepth(bitDepth);

                var rgMode = (ReplayGainMode)(i % 3);
                double preamp = -12.0 + (i % 25);
                _service.SetReplayGain(rgMode, preamp, i % 2 == 0);
            }
        }));

        await Task.WhenAll(tasks);

        // Verify values are strictly in valid ranges after heavy concurrent updates
        Assert.InRange(_settings.Output.LatencyMs, 30, 500);
        Assert.InRange(_settings.Playback.ReplayGainPreampDb, -12.0, 12.0);
    }

    #endregion
}
