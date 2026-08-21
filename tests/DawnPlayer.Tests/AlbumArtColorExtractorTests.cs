using System.Drawing;
using System.Drawing.Imaging;
using DawnPlayer.Core.Library;
using Xunit;

namespace DawnPlayer.Tests;

public class AlbumArtColorExtractorTests
{
    [Fact]
    public void RgbToHsl_And_HslToRgb_RoundTrip_PreservesColors()
    {
        // Test primary Red
        AlbumArtColorExtractor.RgbToHsl(255, 0, 0, out double h, out double s, out double l);
        Assert.Equal(0, Math.Round(h));
        Assert.Equal(1.0, Math.Round(s, 2));
        Assert.Equal(0.5, Math.Round(l, 2));

        AlbumArtColorExtractor.HslToRgb(h, s, l, out int r, out int g, out int b);
        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void ExtractFromBitmap_WithVibrantColors_ExtractsLegiblePalette()
    {
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 230, 80, 20)); // Vibrant orange
        }

        var palette = AlbumArtColorExtractor.ExtractFromBitmap(bmp, isDarkTheme: true);

        Assert.NotNull(palette);
        Assert.StartsWith("#FF", palette.AccentHex);
        Assert.StartsWith("#FF", palette.HoverHex);
        Assert.StartsWith("#FF", palette.PressedHex);
        Assert.StartsWith("#33", palette.MutedHex);
        Assert.StartsWith("#26", palette.GlowHex);
    }

    [Fact]
    public void ExtractFromBitmap_WithMonochromeImage_ReturnsNullForSafeFallback()
    {
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 128, 128, 128)); // Pure neutral gray
        }

        var palette = AlbumArtColorExtractor.ExtractFromBitmap(bmp, isDarkTheme: true);

        // Should return null so player cleanly falls back to user preset
        Assert.Null(palette);
    }

    [Fact]
    public void ExtractFromBytes_And_CacheKey_StoresInCache()
    {
        AlbumArtColorExtractor.ClearCache();

        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 30, 140, 240)); // Vibrant blue
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        var cacheKey = "test_album_key_123";
        var palette = AlbumArtColorExtractor.ExtractFromBytes(bytes, cacheKey, isDarkTheme: true);

        Assert.NotNull(palette);
        Assert.True(AlbumArtColorExtractor.TryGetCached(cacheKey, out var cached));
        Assert.Equal(palette.AccentHex, cached?.AccentHex);

        AlbumArtColorExtractor.ClearCache();
        Assert.False(AlbumArtColorExtractor.TryGetCached(cacheKey, out _));
    }

    [Fact]
    public void AlbumArtBlurHelper_GeneratesAndCachesBlurredImage()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), $"dawn_test_art_{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new Bitmap(100, 100, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(255, 200, 50, 80));
                }
                bmp.Save(tempSource, ImageFormat.Png);
            }

            var albumKey = $"test_blur_key_{Guid.NewGuid():N}";
            var blurPath = AlbumArtBlurHelper.GetOrCreateBlurredArtPath(tempSource, albumKey, 12);

            Assert.NotNull(blurPath);
            Assert.True(File.Exists(blurPath));
            Assert.True(new FileInfo(blurPath).Length > 0);

            // Calling again should return cached path immediately
            var cachedBlurPath = AlbumArtBlurHelper.GetOrCreateBlurredArtPath(tempSource, albumKey, 12);
            Assert.Equal(blurPath, cachedBlurPath);

            try { File.Delete(blurPath); } catch { }
        }
        finally
        {
            try { if (File.Exists(tempSource)) File.Delete(tempSource); } catch { }
        }
    }
}
