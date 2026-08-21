using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

public class ComprehensiveSettingsPersistenceTests
{
    [Fact]
    public void TestAllOutputSettingsRoundtripPersistence()
    {
        var settings = new AppSettings();
        settings.Output.DriverType = AudioDriverType.DirectSound;
        settings.Output.DeviceId = "custom-device-guid-1234";
        settings.Output.UseExclusiveMode = false;
        settings.Output.ExclusiveBitDepth = ExclusiveBitDepth.Bits24;
        settings.Output.LatencyMs = 240;
        settings.Output.AllowVolumeInExclusive = true;

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(AudioDriverType.DirectSound, loaded.Output.DriverType);
        Assert.Equal("custom-device-guid-1234", loaded.Output.DeviceId);
        Assert.False(loaded.Output.UseExclusiveMode);
        Assert.Equal(ExclusiveBitDepth.Bits24, loaded.Output.ExclusiveBitDepth);
        Assert.Equal(240, loaded.Output.LatencyMs);
        Assert.True(loaded.Output.AllowVolumeInExclusive);
    }

    [Fact]
    public void TestAllPlaybackAndSessionSettingsRoundtripPersistence()
    {
        var settings = new AppSettings();
        settings.Playback.Volume = 0.65;
        settings.Playback.Shuffle = true;
        settings.Playback.Repeat = RepeatMode.One;
        settings.Playback.ReplayGain = ReplayGainMode.Album;
        settings.Playback.ReplayGainPreampDb = 4.5;
        settings.Playback.ReplayGainPreventClipping = false;
        settings.Playback.ActivePlaylistName = "K-Pop Favorites";
        settings.Playback.LastPlayedTrackPath = @"C:\Music\test.flac";
        settings.Playback.LastPlayedPlaylistName = "K-Pop Favorites";
        settings.Playback.LastPlayedPositionSeconds = 142.8;
        settings.Playback.QueueItems = new List<QueueSavedEntry>
        {
            new() { PlaylistName = "K-Pop Favorites", TrackPath = @"C:\Music\song1.flac" },
            new() { PlaylistName = "Default", TrackPath = @"C:\Music\song2.mp3" }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(0.65, loaded.Playback.Volume, 3);
        Assert.True(loaded.Playback.Shuffle);
        Assert.Equal(RepeatMode.One, loaded.Playback.Repeat);
        Assert.Equal(ReplayGainMode.Album, loaded.Playback.ReplayGain);
        Assert.Equal(4.5, loaded.Playback.ReplayGainPreampDb, 1);
        Assert.False(loaded.Playback.ReplayGainPreventClipping);
        Assert.Equal("K-Pop Favorites", loaded.Playback.ActivePlaylistName);
        Assert.Equal(@"C:\Music\test.flac", loaded.Playback.LastPlayedTrackPath);
        Assert.Equal("K-Pop Favorites", loaded.Playback.LastPlayedPlaylistName);
        Assert.Equal(142.8, loaded.Playback.LastPlayedPositionSeconds, 1);
        Assert.Equal(2, loaded.Playback.QueueItems.Count);
        Assert.Equal(@"C:\Music\song1.flac", loaded.Playback.QueueItems[0].TrackPath);
    }

    [Fact]
    public void TestAllUiAndLayoutSettingsRoundtripPersistence()
    {
        var settings = new AppSettings();
        settings.Ui.Theme = ThemeMode.Light;
        settings.Ui.AccentColor = AccentColorPreset.CrimsonRed;
        settings.Ui.Backdrop = BackdropMode.Acrylic;
        settings.Ui.WindowWidth = 1600;
        settings.Ui.WindowHeight = 900;
        settings.Ui.WindowX = 120;
        settings.Ui.WindowY = 80;
        settings.Ui.WindowMaximized = true;
        settings.Ui.ShowLyricsPane = true;
        settings.Ui.LastNavTab = "Playlists";
        settings.Ui.PlaylistGroupedView = false;
        settings.Ui.LeftSidebarWidth = 320;
        settings.Ui.RightSidebarWidth = 380;
        settings.Ui.LyricsSidebarWidth = 350;
        settings.Ui.AlbumCoverSize = 210;
        settings.Ui.LibraryTreeGroupMode = 6; // Folder
        settings.Ui.LibraryViewMode = 1;      // List
        settings.Ui.LibrarySortColumn = 3;    // Album
        settings.Ui.LibrarySortAscending = false;
        settings.Ui.LibrarySelectedFilterType = "Folder";
        settings.Ui.LibrarySelectedFilterValue = @"H:\Music\OST";
        settings.Ui.LibrarySelectedFilterExtra = "ExtraData";

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(ThemeMode.Light, loaded.Ui.Theme);
        Assert.Equal(AccentColorPreset.CrimsonRed, loaded.Ui.AccentColor);
        Assert.Equal(BackdropMode.Acrylic, loaded.Ui.Backdrop);
        Assert.Equal(1600, loaded.Ui.WindowWidth);
        Assert.Equal(900, loaded.Ui.WindowHeight);
        Assert.Equal(120, loaded.Ui.WindowX);
        Assert.Equal(80, loaded.Ui.WindowY);
        Assert.True(loaded.Ui.WindowMaximized);
        Assert.True(loaded.Ui.ShowLyricsPane);
        Assert.Equal("Playlists", loaded.Ui.LastNavTab);
        Assert.False(loaded.Ui.PlaylistGroupedView);
        Assert.Equal(320, loaded.Ui.LeftSidebarWidth);
        Assert.Equal(380, loaded.Ui.RightSidebarWidth);
        Assert.Equal(350, loaded.Ui.LyricsSidebarWidth);
        Assert.Equal(210, loaded.Ui.AlbumCoverSize);
        Assert.Equal(6, loaded.Ui.LibraryTreeGroupMode);
        Assert.Equal(1, loaded.Ui.LibraryViewMode);
        Assert.Equal(3, loaded.Ui.LibrarySortColumn);
        Assert.False(loaded.Ui.LibrarySortAscending);
        Assert.Equal("Folder", loaded.Ui.LibrarySelectedFilterType);
        Assert.Equal(@"H:\Music\OST", loaded.Ui.LibrarySelectedFilterValue);
        Assert.Equal("ExtraData", loaded.Ui.LibrarySelectedFilterExtra);
    }

    [Fact]
    public void TestBackwardCompatibilityWithMissingOrPartialJson()
    {
        // An old or minimal JSON payload missing new fields
        string partialJson = """
        {
          "Ui": {
            "Theme": 2,
            "AlbumCoverSize": 180
          }
        }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(partialJson);
        Assert.NotNull(loaded);
        Assert.Equal(180, loaded.Ui.AlbumCoverSize);
        Assert.Equal(ThemeMode.Dark, loaded.Ui.Theme);
        // Default values for omitted fields should remain intact
        Assert.Equal(220, loaded.Ui.LeftSidebarWidth);
        Assert.Equal(300, loaded.Ui.RightSidebarWidth);
        Assert.Equal("Library", loaded.Ui.LastNavTab);
        Assert.True(loaded.Ui.PlaylistGroupedView);
        Assert.Equal(AudioDriverType.Wasapi, loaded.Output.DriverType);
    }

    [Fact]
    public void TestCorruptedJsonHandlingFallsBackSafely()
    {
        string corruptedJson = "{ this is invalid json !!! }";
        AppSettings? loaded = null;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(corruptedJson);
        }
        catch
        {
            loaded = AppSettings.CreateDefault();
        }

        Assert.NotNull(loaded);
        Assert.Equal(144, loaded.Ui.AlbumCoverSize);
        Assert.Equal(220, loaded.Ui.LeftSidebarWidth);
    }

    [Fact]
    public void TestAllEqualizerSettingsRoundtripPersistence()
    {
        var settings = new AppSettings();
        settings.Equalizer.DefaultProfileId = "prof-default";
        settings.Equalizer.Profiles["prof-default"] = new EqProfile
        {
            Id = "prof-default",
            Name = "공통 기본",
            Enabled = true,
            PreampDb = -2.5,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 80, GainDb = 3.0, Q = 0.707 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = -1.5, Q = 1.414 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 10000, GainDb = 2.0, Q = 0.707 }
            }
        };

        settings.Equalizer.Profiles["prof-hd600"] = new EqProfile
        {
            Id = "prof-hd600",
            Name = "Sennheiser HD600",
            Enabled = true,
            PreampDb = -4.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 50, GainDb = 4.0, Q = 0.8 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 3500, GainDb = -2.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 12000, GainDb = 1.5, Q = 1.0 }
            }
        };

        settings.Equalizer.DeviceBindings["wasapi:{0.0.0.00000000}.{custom-endpoint}"] = "prof-hd600";

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Equalizer);
        Assert.Equal("prof-default", loaded.Equalizer.DefaultProfileId);
        Assert.Equal(2, loaded.Equalizer.Profiles.Count);

        var def = loaded.Equalizer.Profiles["prof-default"];
        Assert.Equal("공통 기본", def.Name);
        Assert.True(def.Enabled);
        Assert.Equal(-2.5, def.PreampDb, 1);
        Assert.Equal(3, def.Bands.Count);
        Assert.Equal(EqFilterType.LowShelf, def.Bands[0].Type);
        Assert.Equal(80, def.Bands[0].FrequencyHz);
        Assert.Equal(3.0, def.Bands[0].GainDb);
        Assert.Equal(0.707, def.Bands[0].Q, 3);

        var hd600 = loaded.Equalizer.Profiles["prof-hd600"];
        Assert.Equal("Sennheiser HD600", hd600.Name);
        Assert.True(hd600.Enabled);
        Assert.Equal(-4.0, hd600.PreampDb, 1);
        Assert.Equal(3, hd600.Bands.Count);

        Assert.Single(loaded.Equalizer.DeviceBindings);
        Assert.Equal("prof-hd600", loaded.Equalizer.DeviceBindings["wasapi:{0.0.0.00000000}.{custom-endpoint}"]);
    }
}
