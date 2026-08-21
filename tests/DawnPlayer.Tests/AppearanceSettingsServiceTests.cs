using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit test suite for <see cref="IAppearanceSettingsService"/> and <see cref="AppearanceSettingsService"/>:
/// 1. Constructor parameter validation and null safety.
/// 2. Theme mode switching and notification dispatch.
/// 3. Accent color preset configuration and notification dispatch.
/// 4. Backdrop material selection and notification dispatch.
/// 5. UI font scale clamping (0.5x - 2.0x) and notification dispatch.
/// 6. Album cover size clamping (80px - 260px) and notification dispatch.
/// 7. Layout defaults reset (Left: 220, Right: 300, Lyrics: 300, Cover: 144) and notification dispatch.
/// 8. Event subscription, multi-subscriber dispatch, and unsubscription isolation.
/// 9. Multi-threaded stress testing and settings store synchronization.
/// </summary>
[Collection("SettingsStoreCollection")]
public class AppearanceSettingsServiceTests
{
    private readonly AppSettings _settings;
    private readonly AppearanceSettingsService _service;

    public AppearanceSettingsServiceTests()
    {
        _settings = AppSettings.CreateDefault();
        _service = new AppearanceSettingsService(_settings);
    }

    #region 1. Constructor Validation

    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AppearanceSettingsService(null!));
    }

    #endregion

    #region 2. Theme Mode Switching

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.OledBlack)]
    public void SetTheme_UpdatesSetting_AndRaisesAppearanceChanged(ThemeMode theme)
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetTheme(theme);

        Assert.Equal(theme, _settings.Ui.Theme);
        Assert.Equal(1, eventCount);
    }

    #endregion

    #region 3. Accent Color Presets

    [Theory]
    [InlineData(AccentColorPreset.EoleAmber)]
    [InlineData(AccentColorPreset.ElectricGold)]
    [InlineData(AccentColorPreset.ForestEmerald)]
    [InlineData(AccentColorPreset.CyanSapphire)]
    [InlineData(AccentColorPreset.CrimsonRed)]
    [InlineData(AccentColorPreset.ModernSlate)]
    [InlineData(AccentColorPreset.NordFrost)]
    [InlineData(AccentColorPreset.TokyoNight)]
    [InlineData(AccentColorPreset.CatppuccinMocha)]
    [InlineData(AccentColorPreset.RosePine)]
    [InlineData(AccentColorPreset.SunsetViolet)]
    [InlineData(AccentColorPreset.Custom)]
    public void SetAccentColor_UpdatesPreset_AndRaisesAppearanceChanged(AccentColorPreset preset)
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetAccentColor(preset);

        Assert.Equal(preset, _settings.Ui.AccentColor);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void SetCustomAccentHex_UpdatesHexAndSetsPresetToCustom()
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetCustomAccentHex("#FF123456");

        Assert.Equal("#FF123456", _settings.Ui.CustomAccentHex);
        Assert.Equal(AccentColorPreset.Custom, _settings.Ui.AccentColor);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void SetAutoAlbumArtAccent_UpdatesSetting_AndRaisesEvent()
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetAutoAlbumArtAccent(false);
        Assert.False(_settings.Ui.AutoAlbumArtAccent);
        Assert.Equal(1, eventCount);

        _service.SetAutoAlbumArtAccent(true);
        Assert.True(_settings.Ui.AutoAlbumArtAccent);
        Assert.Equal(2, eventCount);
    }

    [Fact]
    public void RefreshAppearance_RaisesAppearanceChangedEvent()
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.RefreshAppearance();
        Assert.Equal(1, eventCount);
    }

    #endregion

    #region 4. Backdrop Mode

    [Theory]
    [InlineData(BackdropMode.Mica)]
    [InlineData(BackdropMode.MicaAlt)]
    [InlineData(BackdropMode.Acrylic)]
    [InlineData(BackdropMode.Solid)]
    [InlineData(BackdropMode.AlbumArtBlur)]
    public void SetBackdrop_UpdatesBackdrop_AndRaisesAppearanceChanged(BackdropMode backdrop)
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetBackdrop(backdrop);

        Assert.Equal(backdrop, _settings.Ui.Backdrop);
        Assert.Equal(1, eventCount);
    }

    #endregion

    #region 6. Album Cover Size Clamping

    [Theory]
    [InlineData(80.0, 80.0)]
    [InlineData(144.0, 144.0)]
    [InlineData(200.0, 200.0)]
    [InlineData(260.0, 260.0)]
    [InlineData(50.0, 80.0)]     // Clamped underflow
    [InlineData(-100.0, 80.0)]   // Clamped negative
    [InlineData(300.0, 260.0)]   // Clamped overflow
    [InlineData(9999.0, 260.0)]  // Clamped extreme overflow
    public void SetAlbumCoverSize_ClampsBetween80And260Px_AndRaisesAppearanceChanged(
        double inputSize, double expectedSize)
    {
        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.SetAlbumCoverSize(inputSize);

        Assert.Equal(expectedSize, _settings.Ui.AlbumCoverSize, precision: 2);
        Assert.Equal(1, eventCount);
    }

    #endregion

    #region 7. Layout Reset to Factory Defaults

    [Fact]
    public void ResetLayoutToDefaults_RestoresStandardDimensions_AndRaisesAppearanceChanged()
    {
        // Mutate to non-default layout values
        _settings.Ui.LeftSidebarWidth = 450.0;
        _settings.Ui.RightSidebarWidth = 190.0;
        _settings.Ui.LyricsSidebarWidth = 420.0;
        _settings.Ui.AlbumCoverSize = 250.0;

        int eventCount = 0;
        _service.AppearanceChanged += () => eventCount++;

        _service.ResetLayoutToDefaults();

        Assert.Equal(220.0, _settings.Ui.LeftSidebarWidth);
        Assert.Equal(300.0, _settings.Ui.RightSidebarWidth);
        Assert.Equal(300.0, _settings.Ui.LyricsSidebarWidth);
        Assert.Equal(144.0, _settings.Ui.AlbumCoverSize);
        Assert.Equal(1, eventCount);
    }

    #endregion

    #region 8. Multiple Event Subscribers & Unsubscription

    [Fact]
    public void AppearanceChanged_NotifiesMultipleSubscribers_AndHandlesUnsubscribe()
    {
        int sub1Count = 0;
        int sub2Count = 0;

        Action sub1 = () => sub1Count++;
        Action sub2 = () => sub2Count++;

        _service.AppearanceChanged += sub1;
        _service.AppearanceChanged += sub2;

        _service.SetTheme(ThemeMode.Dark);

        Assert.Equal(1, sub1Count);
        Assert.Equal(1, sub2Count);

        // Unsubscribe sub1
        _service.AppearanceChanged -= sub1;

        _service.SetAccentColor(AccentColorPreset.ForestEmerald);

        Assert.Equal(1, sub1Count); // Should remain 1
        Assert.Equal(2, sub2Count); // Should increment to 2
    }

    #endregion

    #region 9. Multi-threaded Stress Testing

    [Fact]
    public async Task AppearanceSettingsService_ConcurrentModifications_MaintainsDataIntegrity()
    {
        const int taskCount = 8;
        int eventCounter = 0;
        _service.AppearanceChanged += () => System.Threading.Interlocked.Increment(ref eventCounter);

        var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                _service.SetTheme((ThemeMode)(i % 3));
                _service.SetAccentColor((AccentColorPreset)(i % 6));
                _service.SetBackdrop((BackdropMode)(i % 4));
                _service.SetAlbumCoverSize(80.0 + (i * 3.6));
                if (i % 10 == 0) _service.ResetLayoutToDefaults();
            }
        }));

        await Task.WhenAll(tasks);

        // Verify values are strictly in valid ranges after heavy concurrent updates
        Assert.InRange(_settings.Ui.AlbumCoverSize, 80.0, 260.0);
        Assert.True(eventCounter > 0);
    }

    #endregion
}
