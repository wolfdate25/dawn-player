using System;
using System.IO;
using System.Text.Json;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

public class AlbumCoverSizePersistenceTests
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData(80.0)]
    [InlineData(100.0)]
    [InlineData(144.0)]
    [InlineData(184.0)]
    [InlineData(220.0)]
    [InlineData(260.0)]
    public void TestAlbumCoverSizeRoundtripPersistence(double coverSize)
    {
        var settings = new AppSettings();
        settings.Ui.AlbumCoverSize = coverSize;

        var json = JsonSerializer.Serialize(settings, IndentedJsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(coverSize, restored.Ui.AlbumCoverSize);
    }

    [Fact]
    public void TestCoverSizeSliderRangeCoverage()
    {
        // Sliders are configured for 80..260
        for (double size = 80; size <= 260; size += 4)
        {
            var settings = new AppSettings();
            settings.Ui.AlbumCoverSize = size;
            var json = JsonSerializer.Serialize(settings);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            Assert.NotNull(loaded);
            Assert.Equal(size, loaded.Ui.AlbumCoverSize);
        }
    }

    [Fact]
    public void TestSettingsStoreIntegrityWithCustomCoverSize()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dawn_settings_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings();
            settings.Ui.AlbumCoverSize = 196.0;
            settings.Ui.LeftSidebarWidth = 280.0;
            settings.Ui.RightSidebarWidth = 340.0;

            File.WriteAllText(tempFile, JsonSerializer.Serialize(settings));
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(tempFile));

            Assert.NotNull(loaded);
            Assert.Equal(196.0, loaded.Ui.AlbumCoverSize);
            Assert.Equal(280.0, loaded.Ui.LeftSidebarWidth);
            Assert.Equal(340.0, loaded.Ui.RightSidebarWidth);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
