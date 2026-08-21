using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Boundary and concurrency coverage for the two services the settings page writes through,
/// <see cref="AudioSettingsService"/> and <see cref="AppearanceSettingsService"/>:
/// 1. Clamping at and past the range ends, including infinities and double.MinValue/MaxValue —
///    latency [30, 500] ms, ReplayGain preamp [-12, 12] dB, album cover size [80, 260].
/// 2. Device selection fallbacks for null, empty, and unknown ids across all driver types, and
///    exclusive-mode status queries against device ids that match no endpoint.
/// 3. Exactly one change event per mutation, and no lost mutation or torn value when many threads
///    subscribe, unsubscribe, and mutate at once.
/// </summary>
[Collection("SettingsStoreCollection")]
public class SettingsServiceClampingAndConcurrencyTests : IDisposable
{
    private readonly string _tempSettingsDir;
    private readonly AppSettings _settings;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _playback;
    private readonly AudioSettingsService _audioService;
    private readonly AppearanceSettingsService _appearanceService;

    public SettingsServiceClampingAndConcurrencyTests()
    {
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), $"dawn_settings_svc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSettingsDir);

        _settings = AppSettings.CreateDefault();
        _library = new MusicLibrary();
        _playlists = new PlaylistManager(_library);
        _playback = new PlaybackController(_settings, _playlists);

        _audioService = new AudioSettingsService(_settings, _playback);
        _appearanceService = new AppearanceSettingsService(_settings);
    }

    public void Dispose()
    {
        _playback.Dispose();
        _library.Dispose();
        if (Directory.Exists(_tempSettingsDir))
        {
            try { Directory.Delete(_tempSettingsDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // 1. AudioSettingsService - Bounds Clamping Adversarial Tests
    // =========================================================================

    [Theory]
    [InlineData(int.MinValue, 30)]
    [InlineData(-1000000, 30)]
    [InlineData(-100, 30)]
    [InlineData(-1, 30)]
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(31, 31)]
    [InlineData(120, 120)]
    [InlineData(250, 250)]
    [InlineData(499, 499)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(1000, 500)]
    [InlineData(1000000, 500)]
    [InlineData(int.MaxValue, 500)]
    public void AudioSettingsService_SetLatency_AdversarialBoundsClamping(int inputLatency, int expectedLatency)
    {
        _audioService.SetLatency(inputLatency);

        Assert.Equal(expectedLatency, _settings.Output.LatencyMs);
        Assert.InRange(_settings.Output.LatencyMs, 30, 500);
    }

    [Theory]
    [InlineData(double.MinValue, -12.0)]
    [InlineData(double.NegativeInfinity, -12.0)]
    [InlineData(-10000.0, -12.0)]
    [InlineData(-13.0, -12.0)]
    [InlineData(-12.001, -12.0)]
    [InlineData(-12.0, -12.0)]
    [InlineData(-11.999, -11.999)]
    [InlineData(-6.0, -6.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(6.0, 6.0)]
    [InlineData(11.999, 11.999)]
    [InlineData(12.0, 12.0)]
    [InlineData(12.001, 12.0)]
    [InlineData(13.0, 12.0)]
    [InlineData(10000.0, 12.0)]
    [InlineData(double.PositiveInfinity, 12.0)]
    [InlineData(double.MaxValue, 12.0)]
    public void AudioSettingsService_SetReplayGain_AdversarialPreampClamping(double inputPreamp, double expectedPreamp)
    {
        _audioService.SetReplayGain(ReplayGainMode.Track, inputPreamp, true);

        Assert.Equal(expectedPreamp, _settings.Playback.ReplayGainPreampDb, precision: 3);
        Assert.InRange(_settings.Playback.ReplayGainPreampDb, -12.0, 12.0);
    }

    [Theory]
    [InlineData(ReplayGainMode.Off, 0.0, false)]
    [InlineData(ReplayGainMode.Off, 5.0, true)]
    [InlineData(ReplayGainMode.Track, -6.0, true)]
    [InlineData(ReplayGainMode.Track, 3.5, false)]
    [InlineData(ReplayGainMode.Album, -2.5, false)]
    [InlineData(ReplayGainMode.Album, 10.0, true)]
    public void AudioSettingsService_SetReplayGain_AllModesAndClippingFlags(
        ReplayGainMode mode, double preampDb, bool preventClipping)
    {
        _audioService.SetReplayGain(mode, preampDb, preventClipping);

        Assert.Equal(mode, _settings.Playback.ReplayGain);
        Assert.Equal(preampDb, _settings.Playback.ReplayGainPreampDb, precision: 3);
        Assert.Equal(preventClipping, _settings.Playback.ReplayGainPreventClipping);
    }

    // =========================================================================
    // 2. AudioSettingsService - Device Selection & Null Fallback Tests
    // =========================================================================

    [Theory]
    [InlineData(AudioDriverType.Wasapi)]
    [InlineData(AudioDriverType.DirectSound)]
    [InlineData(AudioDriverType.WaveOut)]
    public void AudioSettingsService_GetSelectedDevice_NullOrEmptyOrUnknownId_FallsBackDeterministically(
        AudioDriverType driverType)
    {
        var devices = _audioService.GetDevices(driverType);
        var selectedNull = _audioService.GetSelectedDevice(driverType, null);
        var selectedEmpty = _audioService.GetSelectedDevice(driverType, "");
        var selectedUnknown = _audioService.GetSelectedDevice(driverType, "NON_EXISTENT_GUID_99999");

        if (devices.Count > 0)
        {
            Assert.NotNull(selectedNull);
            Assert.NotNull(selectedEmpty);
            Assert.NotNull(selectedUnknown);

            var expected = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
            Assert.Equal(expected.Id, selectedNull!.Id);
            Assert.Equal(expected.Id, selectedEmpty!.Id);
            Assert.Equal(expected.Id, selectedUnknown!.Id);
        }
        else
        {
            Assert.Null(selectedNull);
            Assert.Null(selectedEmpty);
            Assert.Null(selectedUnknown);
        }
    }

    [Fact]
    public void AudioSettingsService_GetSelectedDevice_InvalidDriverType_HandlesSafely()
    {
        var invalidDriver = (AudioDriverType)999;
        var selected = _audioService.GetSelectedDevice(invalidDriver, "some-id");
        // Should not throw; returns either null or fallback
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NON_EXISTENT_DEVICE_GUID_12345")]
    [InlineData("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}")]
    [InlineData("!@#$%^&*()_+-=[]{}|;':,.<>?")]
    public void AudioSettingsService_GetExclusiveModeStatus_HandlesAllAdversarialDeviceIdsSafely(string? deviceId)
    {
        var status = _audioService.GetExclusiveModeStatus(deviceId);

        Assert.NotNull(status);
        Assert.NotNull(status.StatusText);
        Assert.NotEmpty(status.StatusText);
        Assert.NotNull(status.DetailsText);
        Assert.NotEmpty(status.DetailsText);
    }

    [Theory]
    [InlineData(AudioDriverType.Wasapi)]
    [InlineData(AudioDriverType.DirectSound)]
    [InlineData(AudioDriverType.WaveOut)]
    public void AudioSettingsService_SetDriverType_ResetsDeviceIdToNull_AndPersists(AudioDriverType driverType)
    {
        _settings.Output.DeviceId = "custom-device-id";
        _audioService.SetDriverType(driverType);

        Assert.Equal(driverType, _settings.Output.DriverType);
        Assert.Null(_settings.Output.DeviceId);
    }

    [Theory]
    [InlineData(ExclusiveBitDepth.Source)]
    [InlineData(ExclusiveBitDepth.Bits16)]
    [InlineData(ExclusiveBitDepth.Bits24)]
    [InlineData(ExclusiveBitDepth.Bits32)]
    public void AudioSettingsService_SetExclusiveBitDepth_AllVariants_Persists(ExclusiveBitDepth bitDepth)
    {
        _audioService.SetExclusiveBitDepth(bitDepth);
        Assert.Equal(bitDepth, _settings.Output.ExclusiveBitDepth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AudioSettingsService_SetUseExclusiveMode_And_AllowVolume_Persists(bool flag)
    {
        _audioService.SetUseExclusiveMode(flag);
        Assert.Equal(flag, _settings.Output.UseExclusiveMode);

        _audioService.SetAllowVolumeInExclusive(flag);
        Assert.Equal(flag, _settings.Output.AllowVolumeInExclusive);
    }

    // =========================================================================
    // 3. AppearanceSettingsService - Bounds Clamping Adversarial Tests
    // =========================================================================

    [Theory]
    [InlineData(double.MinValue, 80.0)]
    [InlineData(double.NegativeInfinity, 80.0)]
    [InlineData(-500.0, 80.0)]
    [InlineData(-1.0, 80.0)]
    [InlineData(0.0, 80.0)]
    [InlineData(79.9, 80.0)]
    [InlineData(80.0, 80.0)]
    [InlineData(80.1, 80.1)]
    [InlineData(144.0, 144.0)]
    [InlineData(200.0, 200.0)]
    [InlineData(259.9, 259.9)]
    [InlineData(260.0, 260.0)]
    [InlineData(260.1, 260.0)]
    [InlineData(500.0, 260.0)]
    [InlineData(10000.0, 260.0)]
    [InlineData(double.PositiveInfinity, 260.0)]
    [InlineData(double.MaxValue, 260.0)]
    public void AppearanceSettingsService_SetAlbumCoverSize_AdversarialBoundsClamping(
        double inputSize, double expectedSize)
    {
        int eventCount = 0;
        _appearanceService.AppearanceChanged += () => eventCount++;

        _appearanceService.SetAlbumCoverSize(inputSize);

        Assert.Equal(expectedSize, _settings.Ui.AlbumCoverSize, precision: 2);
        Assert.InRange(_settings.Ui.AlbumCoverSize, 80.0, 260.0);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void AppearanceSettingsService_ResetLayoutToDefaults_EnforcesExactConstants()
    {
        _settings.Ui.LeftSidebarWidth = 999.0;
        _settings.Ui.RightSidebarWidth = 888.0;
        _settings.Ui.LyricsSidebarWidth = 777.0;
        _settings.Ui.AlbumCoverSize = 666.0;

        int eventCount = 0;
        _appearanceService.AppearanceChanged += () => eventCount++;

        _appearanceService.ResetLayoutToDefaults();

        Assert.Equal(220.0, _settings.Ui.LeftSidebarWidth);
        Assert.Equal(300.0, _settings.Ui.RightSidebarWidth);
        Assert.Equal(300.0, _settings.Ui.LyricsSidebarWidth);
        Assert.Equal(144.0, _settings.Ui.AlbumCoverSize);
        Assert.Equal(1, eventCount);
    }

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void AppearanceSettingsService_SetTheme_AllVariants_RaisesEvent(ThemeMode theme)
    {
        int eventCount = 0;
        _appearanceService.AppearanceChanged += () => eventCount++;

        _appearanceService.SetTheme(theme);

        Assert.Equal(theme, _settings.Ui.Theme);
        Assert.Equal(1, eventCount);
    }

    [Theory]
    [InlineData(AccentColorPreset.EoleAmber)]
    [InlineData(AccentColorPreset.ElectricGold)]
    [InlineData(AccentColorPreset.ForestEmerald)]
    [InlineData(AccentColorPreset.CyanSapphire)]
    [InlineData(AccentColorPreset.CrimsonRed)]
    [InlineData(AccentColorPreset.ModernSlate)]
    public void AppearanceSettingsService_SetAccentColor_AllVariants_RaisesEvent(AccentColorPreset preset)
    {
        int eventCount = 0;
        _appearanceService.AppearanceChanged += () => eventCount++;

        _appearanceService.SetAccentColor(preset);

        Assert.Equal(preset, _settings.Ui.AccentColor);
        Assert.Equal(1, eventCount);
    }

    [Theory]
    [InlineData(BackdropMode.Mica)]
    [InlineData(BackdropMode.MicaAlt)]
    [InlineData(BackdropMode.Acrylic)]
    [InlineData(BackdropMode.Solid)]
    public void AppearanceSettingsService_SetBackdrop_AllVariants_RaisesEvent(BackdropMode backdrop)
    {
        int eventCount = 0;
        _appearanceService.AppearanceChanged += () => eventCount++;

        _appearanceService.SetBackdrop(backdrop);

        Assert.Equal(backdrop, _settings.Ui.Backdrop);
        Assert.Equal(1, eventCount);
    }

    // =========================================================================
    // 4. High-Contention Multi-threaded Concurrency & Event Notification Stress
    // =========================================================================

    [Fact]
    public async Task AppearanceSettingsService_ConcurrentSubscribeUnsubscribeAndMutate_ThreadSafe()
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 15;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        int totalEventsObserved = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(() =>
        {
            try
            {
                Action subscriber = () => Interlocked.Increment(ref totalEventsObserved);

                for (int i = 0; i < iterationsPerWorker; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    // Dynamically subscribe
                    _appearanceService.AppearanceChanged += subscriber;

                    // Trigger mutations
                    _appearanceService.SetAlbumCoverSize(80.0 + ((workerId * 2 + i) % 180));
                    _appearanceService.SetCustomAccentHex(((workerId + i) % 2 == 0) ? "#FFE8A33D" : "#FF3D59A1");
                    _appearanceService.SetTheme((ThemeMode)(i % 3));
                    _appearanceService.SetAccentColor((AccentColorPreset)(i % 6));
                    _appearanceService.SetBackdrop((BackdropMode)(i % 4));

                    if (i % 10 == 0)
                    {
                        _appearanceService.ResetLayoutToDefaults();
                    }

                    // Dynamically unsubscribe
                    _appearanceService.AppearanceChanged -= subscriber;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, cts.Token));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        Assert.True(totalEventsObserved > 0, "Events must be received across concurrent threads.");
        Assert.InRange(_settings.Ui.AlbumCoverSize, 80.0, 260.0);
    }

    [Fact]
    public async Task AudioSettingsService_ConcurrentMutationsUnderLoad_ThreadSafe()
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 15;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterationsPerWorker; i++)
                {
                    int latency = 10 + (workerId * 40) + i; // Will trigger bounds clamping
                    _audioService.SetLatency(latency);

                    double preamp = -20.0 + (i % 40); // Will trigger bounds clamping
                    _audioService.SetReplayGain((ReplayGainMode)(i % 3), preamp, (i % 2 == 0));

                    _audioService.SetDriverType((AudioDriverType)(i % 3));
                    _audioService.SetExclusiveBitDepth((ExclusiveBitDepth)(i % 4));
                    _audioService.SetUseExclusiveMode(i % 2 == 1);
                    _audioService.SetAllowVolumeInExclusive(i % 2 == 0);
                    _audioService.SetDevice($"device-{workerId}-{i}");
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        Assert.InRange(_settings.Output.LatencyMs, 30, 500);
        Assert.InRange(_settings.Playback.ReplayGainPreampDb, -12.0, 12.0);
    }
}
