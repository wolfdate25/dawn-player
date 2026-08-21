using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private (SettingsViewModel ViewModel, AppSettings Settings) CreateMasterViewModel()
    {
        var settings = new AppSettings();
        settings.Equalizer.EnsureDefaultProfile();

        int saveCount = 0;
        int scanCount = 0;
        int lyricsNotifyCount = 0;

        var audioService = new AudioSettingsService(settings, null);
        var eqService = new EqSettingsService(settings, null, () => saveCount++, () => { });
        var appService = new AppearanceSettingsService(settings);
        appService.AppearanceChanged += () => saveCount++;

        var vm = new SettingsViewModel(
            settings,
            audioService,
            eqService,
            appService,
            scanStarter: () => scanCount++,
            lyricsChangedNotifier: () => lyricsNotifyCount++,
            settingsSaver: s => saveCount++,
            isExclusiveSessionGetter: () => false);

        return (vm, settings);
    }

    [Fact]
    public void MasterViewModel_CategoryNavigation_SwitchesFlagsCorrectly()
    {
        var (vm, _) = CreateMasterViewModel();

        Assert.Equal(0, vm.SelectedCategoryIndex);
        Assert.True(vm.IsAudioCategorySelected);
        Assert.False(vm.IsEqualizerCategorySelected);

        vm.SelectedCategoryIndex = 1;
        Assert.True(vm.IsEqualizerCategorySelected);
        Assert.False(vm.IsAudioCategorySelected);

        vm.SelectedCategoryIndex = 2;
        Assert.True(vm.IsPlaybackCategorySelected);

        vm.SelectedCategoryIndex = 3;
        Assert.True(vm.IsLibraryCategorySelected);

        vm.SelectedCategoryIndex = 4;
        Assert.True(vm.IsLyricsCategorySelected);

        vm.SelectedCategoryIndex = 5;
        Assert.True(vm.IsAppearanceCategorySelected);

        vm.SelectedCategoryIndex = 6;
        Assert.True(vm.IsLayoutCategorySelected);

        vm.SelectedCategoryIndex = 7;
        Assert.True(vm.IsShortcutsCategorySelected);

        vm.SelectedCategoryIndex = 8;
        Assert.True(vm.IsAboutCategorySelected);
    }

    [Fact]
    public void AudioSettingsViewModel_DriverAndLatency_ClampsAndNotifies()
    {
        var (vm, settings) = CreateMasterViewModel();
        var audio = vm.Audio;

        // Driver selection
        audio.DriverType = AudioDriverType.DirectSound;
        Assert.Equal(AudioDriverType.DirectSound, settings.Output.DriverType);
        Assert.Equal(1, audio.DriverTypeIndex);
        Assert.False(audio.IsWasapiDriver);
        Assert.Contains("DirectSound", audio.DriverDescriptionText);

        audio.DriverTypeIndex = 0; // WASAPI
        Assert.Equal(AudioDriverType.Wasapi, settings.Output.DriverType);
        Assert.True(audio.IsWasapiDriver);

        // Latency bounds clamping (30..500 ms)
        audio.LatencyMs = 200;
        Assert.Equal(200, settings.Output.LatencyMs);
        Assert.Equal("200ms", audio.LatencyText);

        audio.LatencyMs = 10;
        Assert.Equal(30, settings.Output.LatencyMs);

        audio.LatencyMs = 1000;
        Assert.Equal(500, settings.Output.LatencyMs);

        // Exclusive Bit Depth
        audio.ExclusiveBitDepthIndex = 2; // 24-bit
        Assert.Equal(ExclusiveBitDepth.Bits24, settings.Output.ExclusiveBitDepth);

        audio.ExclusiveBitDepthIndex = 0; // Source
        Assert.Equal(ExclusiveBitDepth.Source, settings.Output.ExclusiveBitDepth);
    }

    [Fact]
    public void PlaybackSettingsViewModel_NormalizerAndReplayGain_ClampsAndPersists()
    {
        var (vm, settings) = CreateMasterViewModel();
        var playback = vm.Playback;

        // Normalizer Toggle & Modes
        playback.NormalizerEnabled = true;
        Assert.True(settings.Normalizer.Enabled);

        playback.NormalizerModeIndex = 1; // AlwaysDynamic
        Assert.Equal(NormalizerMode.AlwaysDynamic, settings.Normalizer.Mode);

        playback.NormalizerSpeedIndex = 0; // Fast
        Assert.Equal(NormalizerSpeed.Fast, settings.Normalizer.Speed);

        // Normalizer Target Clamping [-24.0, -6.0]
        playback.NormalizerTargetDb = -15.5;
        Assert.Equal(-15.5, settings.Normalizer.TargetLevelDb, 1);

        playback.NormalizerTargetDb = -40.0;
        Assert.Equal(-24.0, settings.Normalizer.TargetLevelDb, 1);

        playback.NormalizerTargetDb = 0.0;
        Assert.Equal(-6.0, settings.Normalizer.TargetLevelDb, 1);

        // Normalizer Max Boost Clamping [0.0, 18.0]
        playback.NormalizerMaxBoostDb = 8.0;
        Assert.Equal(8.0, settings.Normalizer.MaxBoostDb, 1);

        playback.NormalizerMaxBoostDb = -5.0;
        Assert.Equal(0.0, settings.Normalizer.MaxBoostDb, 1);

        playback.NormalizerMaxBoostDb = 35.0;
        Assert.Equal(18.0, settings.Normalizer.MaxBoostDb, 1);

        // ReplayGain Mode and Preamp Clamping [-12.0, 12.0]
        playback.ReplayGainModeIndex = 2; // Album
        Assert.Equal(ReplayGainMode.Album, settings.Playback.ReplayGain);

        playback.ReplayGainPreampDb = 3.5;
        Assert.Equal(3.5, settings.Playback.ReplayGainPreampDb, 1);

        playback.ReplayGainPreampDb = 20.0;
        Assert.Equal(12.0, settings.Playback.ReplayGainPreampDb, 1);

        playback.ReplayGainPreventClipping = false;
        Assert.False(settings.Playback.ReplayGainPreventClipping);
    }

    [Fact]
    public void LibrarySettingsViewModel_FolderManagement_HandlesDuplicatesAndRemoval()
    {
        var (vm, settings) = CreateMasterViewModel();
        var lib = vm.Library;

        settings.Library.Folders.Clear();
        lib.Folders.Clear();
        Assert.False(lib.HasFolders);

        // Add valid folder
        bool added1 = lib.AddFolder("D:\\Music\\FLAC");
        Assert.True(added1);
        Assert.Single(lib.Folders);
        Assert.True(lib.HasFolders);
        Assert.Contains("D:\\Music\\FLAC", settings.Library.Folders);

        // Duplicate folder addition must be rejected
        bool dup = lib.AddFolder("D:\\Music\\FLAC");
        Assert.False(dup);
        Assert.Single(lib.Folders);

        // Case-insensitive duplicate rejection
        bool dupCase = lib.AddFolder("d:\\music\\flac");
        Assert.False(dupCase);

        // Add second folder
        bool added2 = lib.AddFolder("E:\\Audio\\Lossless");
        Assert.True(added2);
        Assert.Equal(2, lib.Folders.Count);

        // Remove folder
        lib.SelectedFolder = "D:\\Music\\FLAC";
        bool removed = lib.RemoveFolder();
        Assert.True(removed);
        Assert.Single(lib.Folders);
        Assert.Null(lib.SelectedFolder);
        Assert.DoesNotContain("D:\\Music\\FLAC", settings.Library.Folders);

        // Scan on startup toggle
        lib.ScanOnStartup = false;
        Assert.False(settings.Library.ScanOnStartup);
    }

    [Fact]
    public void LyricsSettingsViewModel_TypographyAndPatternParser_WorksCorrectly()
    {
        var (vm, settings) = CreateMasterViewModel();
        var lyr = vm.Lyrics;

        // Font Family Presets
        lyr.FontFamilyIndex = 1; // Pretendard
        Assert.Contains("Pretendard", lyr.EffectiveFontFamily);
        Assert.False(lyr.IsCustomFontVisible);

        lyr.FontFamilyIndex = 4; // Custom
        Assert.True(lyr.IsCustomFontVisible);
        lyr.CustomFontFamily = "D2Coding";
        Assert.Equal("D2Coding", lyr.EffectiveFontFamily);

        // Clamping font sizes, character spacing, and line height
        lyr.FontSize = 15.5;
        Assert.Equal(15.5, settings.Lyrics.FontSize, 1);
        Assert.Equal("15.5px", lyr.FontSizeLabel);

        lyr.FontSize = 5.0; // Under min 10.0
        Assert.Equal(10.0, settings.Lyrics.FontSize, 1);

        lyr.ActiveFontSize = 22.0;
        Assert.Equal(22.0, settings.Lyrics.ActiveFontSize, 1);
        Assert.Equal("22px", lyr.ActiveFontSizeLabel);

        lyr.CharacterSpacing = 50;
        Assert.Equal(50, settings.Lyrics.CharacterSpacing);
        Assert.Equal("50", lyr.CharacterSpacingLabel);

        lyr.LineHeight = 32.0;
        Assert.Equal(32.0, settings.Lyrics.LineHeight, 1);
        Assert.Equal("32px", lyr.LineHeightLabel);

        // Alignment
        lyr.AlignmentIndex = 1; // Left
        Assert.Equal("Left", lyr.Alignment);
        Assert.Equal("Left", settings.Lyrics.Alignment);

        // LRC Patterns parsing and validation
        string testPatterns = @"%filename%.lrc
            invalid_pattern_without_extension
            %artist% - %title%.lrc

            %title%.lrc";

        lyr.SaveLrcPatterns(testPatterns);
        Assert.Equal(3, settings.Lyrics.FilePatterns.Count);
        Assert.DoesNotContain("invalid_pattern_without_extension", settings.Lyrics.FilePatterns);

        // Reset to default patterns
        lyr.ResetLrcPatternsToDefault();
        Assert.Equal(3, settings.Lyrics.FilePatterns.Count);
        Assert.Contains("%filename%.lrc", settings.Lyrics.FilePatterns);
    }

    [Fact]
    public void AppearanceAndLayoutSettingsViewModel_ThemeAndLayout_ValidatesAndClamps()
    {
        var (vm, settings) = CreateMasterViewModel();
        var app = vm.Appearance;
        var layout = vm.Layout;

        // Theme and Backdrop
        app.ThemeIndex = 3; // OledBlack
        Assert.Equal(ThemeMode.OledBlack, settings.Ui.Theme);

        app.BackdropIndex = 1; // MicaAlt
        Assert.Equal(BackdropMode.MicaAlt, settings.Ui.Backdrop);

        // Custom Hex Color Validation
        app.AccentIndex = 11; // Custom
        Assert.True(app.IsCustomColorVisible);

        bool validHex = app.TrySetCustomAccentHex("#3399FF");
        Assert.True(validHex);
        Assert.Equal("#3399FF", settings.Ui.CustomAccentHex);

        bool invalidHex = app.TrySetCustomAccentHex("#ZZZZZZ");
        Assert.False(invalidHex);
        Assert.Equal("#3399FF", settings.Ui.CustomAccentHex); // Preserves previous valid color

        bool hexWithoutHash = app.TrySetCustomAccentHex("FF8800");
        Assert.True(hexWithoutHash);
        Assert.Equal("#FF8800", settings.Ui.CustomAccentHex);

        // Layout AlbumCoverSize Clamping [80..260]
        layout.AlbumCoverSize = 160;
        Assert.Equal(160, settings.Ui.AlbumCoverSize);
        Assert.Equal("160px", layout.AlbumCoverSizeText);

        layout.AlbumCoverSize = 40; // Clamped to 80
        Assert.Equal(80, settings.Ui.AlbumCoverSize);

        layout.AlbumCoverSize = 400; // Clamped to 260
        Assert.Equal(260, settings.Ui.AlbumCoverSize);

        // Reset layout
        layout.ResetLayoutToDefaults();
        Assert.Equal(144, settings.Ui.AlbumCoverSize);
    }
}
