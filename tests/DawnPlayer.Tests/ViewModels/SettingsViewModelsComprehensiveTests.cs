using System;
using System.Collections.Generic;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

[Collection("SettingsStoreCollection")]
public sealed class SettingsViewModelsComprehensiveTests
{
    private (SettingsViewModel Master, AppSettings Settings, AudioSettingsService AudioService, EqSettingsService EqService, AppearanceSettingsService AppearanceService) CreateEnvironment(bool isExclusive = false)
    {
        var settings = AppSettings.CreateDefault();
        var audioService = new AudioSettingsService(settings, null);
        var eqService = new EqSettingsService(settings, null);
        var appearanceService = new AppearanceSettingsService(settings);

        var master = new SettingsViewModel(
            settings,
            audioService,
            eqService,
            appearanceService,
            scanStarter: () => { },
            lyricsChangedNotifier: () => { },
            settingsSaver: s => { },
            isExclusiveSessionGetter: () => isExclusive);

        return (master, settings, audioService, eqService, appearanceService);
    }

    [Fact]
    public void SettingsViewModel_CategorySelection_UpdatesAllSelectionFlags()
    {
        var (master, _, _, _, _) = CreateEnvironment();

        Assert.Equal(0, master.SelectedCategoryIndex);
        Assert.True(master.IsAudioCategorySelected);
        Assert.False(master.IsEqualizerCategorySelected);

        master.SelectedCategoryIndex = 1;
        Assert.False(master.IsAudioCategorySelected);
        Assert.True(master.IsEqualizerCategorySelected);
        Assert.NotNull(master.Equalizer.VisualizerData);

        master.SelectedCategoryIndex = 2;
        Assert.True(master.IsPlaybackCategorySelected);

        master.SelectedCategoryIndex = 3;
        Assert.True(master.IsLibraryCategorySelected);

        master.SelectedCategoryIndex = 4;
        Assert.True(master.IsLyricsCategorySelected);

        master.SelectedCategoryIndex = 5;
        Assert.True(master.IsAppearanceCategorySelected);

        master.SelectedCategoryIndex = 6;
        Assert.True(master.IsLayoutCategorySelected);

        master.SelectedCategoryIndex = 7;
        Assert.True(master.IsShortcutsCategorySelected);

        master.SelectedCategoryIndex = 8;
        Assert.True(master.IsAboutCategorySelected);
    }

    [Fact]
    public void LayoutSettingsViewModel_Scaling_And_ResetToDefaults()
    {
        var (master, settings, _, _, _) = CreateEnvironment();
        var layout = master.Layout;

        layout.AlbumCoverSize = 200;
        Assert.Equal(200, settings.Ui.AlbumCoverSize);
        Assert.Equal("200px", layout.AlbumCoverSizeText);

        layout.ResetLayoutToDefaults();
        Assert.Equal(144.0, layout.AlbumCoverSize);
        Assert.Equal(220.0, settings.Ui.LeftSidebarWidth);
        Assert.Equal(300.0, settings.Ui.RightSidebarWidth);
    }

    [Fact]
    public void AppearanceSettingsViewModel_ThemeAndBackdropAndHexColor()
    {
        var (master, settings, _, _, _) = CreateEnvironment();
        var app = master.Appearance;

        app.ThemeIndex = 1;
        Assert.Equal(ThemeMode.Light, settings.Ui.Theme);

        app.BackdropIndex = 2;
        Assert.Equal(BackdropMode.Acrylic, settings.Ui.Backdrop);

        bool success = app.TrySetCustomAccentHex("#FF112233");
        Assert.True(success);
        Assert.Equal("#FF112233", settings.Ui.CustomAccentHex);

        bool invalid = app.TrySetCustomAccentHex("invalid-color");
        Assert.False(invalid);
    }
}
