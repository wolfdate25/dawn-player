using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Represents an extracted color palette from album artwork.
/// </summary>
public sealed record ExtractedAlbumPalette(
    string AccentHex,
    string HoverHex,
    string PressedHex,
    string MutedHex,
    string GlowHex,
    bool IsDarkImage
);

/// <summary>
/// High-performance album art color extraction engine.
/// Extracts vibrant accent and ambient glow colors with luminance and contrast normalization,
/// backed by a thread-safe in-memory palette cache.
/// </summary>
public static class AlbumArtColorExtractor
{
    private static readonly ConcurrentDictionary<string, ExtractedAlbumPalette> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the extracted palette cache.
    /// </summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// Tries to get a cached palette for the given cache key.
    /// </summary>
    public static bool TryGetCached(string cacheKey, out ExtractedAlbumPalette? palette)
    {
        return _cache.TryGetValue(cacheKey, out palette);
    }

    /// <summary>
    /// Extracts an optimized accent and ambient glow palette from an image file on disk.
    /// Returns null if the file does not exist, cannot be decoded, or contains only pure monochrome.
    /// </summary>
    public static ExtractedAlbumPalette? ExtractFromFile(string imagePath, string? cacheKey = null, bool isDarkTheme = true)
    {
        var key = cacheKey ?? imagePath;
        if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var original = new Bitmap(stream);
            var palette = ExtractFromBitmap(original, isDarkTheme);
            if (palette != null && !string.IsNullOrEmpty(key))
            {
                _cache[key] = palette;
            }
            return palette;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts an optimized accent and ambient glow palette from raw image bytes.
    /// </summary>
    public static ExtractedAlbumPalette? ExtractFromBytes(byte[] imageBytes, string? cacheKey = null, bool isDarkTheme = true)
    {
        if (!string.IsNullOrEmpty(cacheKey) && _cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (imageBytes == null || imageBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var original = new Bitmap(ms);
            var palette = ExtractFromBitmap(original, isDarkTheme);
            if (palette != null && !string.IsNullOrEmpty(cacheKey))
            {
                _cache[cacheKey] = palette;
            }
            return palette;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts palette from a <see cref="Bitmap"/> by downsampling to a 32x32 thumbnail and analyzing pixel buckets.
    /// </summary>
    public static ExtractedAlbumPalette? ExtractFromBitmap(Bitmap original, bool isDarkTheme = true)
    {
        try
        {
            const int sampleSize = 32;
            using var thumb = new Bitmap(sampleSize, sampleSize, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(thumb))
            {
                gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                gfx.DrawImage(original, 0, 0, sampleSize, sampleSize);
            }

            var bucketScores = new Dictionary<int, (Color Color, double Score, int Count)>();
            long totalR = 0, totalG = 0, totalB = 0;
            int validPixelCount = 0;

            for (int y = 0; y < sampleSize; y++)
            {
                for (int x = 0; x < sampleSize; x++)
                {
                    var p = thumb.GetPixel(x, y);
                    if (p.A < 128) continue;

                    totalR += p.R;
                    totalG += p.G;
                    totalB += p.B;
                    validPixelCount++;

                    RgbToHsl(p.R, p.G, p.B, out double h, out double s, out double l);

                    // Skip very dark or very bright or completely desaturated pixels for accent selection
                    if (l < 0.12 || l > 0.90 || s < 0.15)
                    {
                        continue;
                    }

                    // Quantize HSL to bucket: 24 hue steps, 4 sat steps, 4 lum steps
                    int hBucket = (int)(h / 15.0) % 24;
                    int sBucket = (int)(s * 4.0);
                    int lBucket = (int)(l * 4.0);
                    int bucketKey = (hBucket << 8) | (sBucket << 4) | lBucket;

                    // Score favors higher saturation and moderate luminance
                    double score = s * (1.0 - Math.Abs(l - 0.5) * 1.2);

                    if (bucketScores.TryGetValue(bucketKey, out var entry))
                    {
                        bucketScores[bucketKey] = (entry.Color, entry.Score + score, entry.Count + 1);
                    }
                    else
                    {
                        bucketScores[bucketKey] = (p, score, 1);
                    }
                }
            }

            bool isDarkImage = true;
            if (validPixelCount > 0)
            {
                double avgLum = (totalR * 0.299 + totalG * 0.587 + totalB * 0.114) / (validPixelCount * 255.0);
                isDarkImage = avgLum < 0.5;
            }

            Color baseColor;
            if (bucketScores.Count > 0)
            {
                baseColor = bucketScores.Values.OrderByDescending(b => b.Score).First().Color;
            }
            else if (validPixelCount > 0)
            {
                // Monochromatic image fallback - return null to indicate fallback to default accent
                return null;
            }
            else
            {
                return null;
            }

            // Adjust saturation & luminance for UI legibility
            RgbToHsl(baseColor.R, baseColor.G, baseColor.B, out double baseH, out double baseS, out double baseL);

            // Boost saturation slightly if below 0.5
            double finalS = Math.Clamp(Math.Max(baseS, 0.55), 0.4, 0.95);

            // Clamp luminance for dark/light themes
            double finalL = isDarkTheme
                ? Math.Clamp(baseL, 0.48, 0.68)
                : Math.Clamp(baseL, 0.35, 0.52);

            HslToRgb(baseH, finalS, finalL, out int r, out int g, out int b);

            // Hover: slightly brighter / more saturated
            HslToRgb(baseH, Math.Min(1.0, finalS * 1.05), Math.Clamp(finalL + (isDarkTheme ? 0.08 : -0.06), 0.2, 0.85), out int hr, out int hg, out int hb);

            // Pressed: darker / deeper
            HslToRgb(baseH, finalS, Math.Clamp(finalL - (isDarkTheme ? 0.10 : -0.08), 0.15, 0.75), out int pr, out int pg, out int pb);

            string accentHex = $"#FF{r:X2}{g:X2}{b:X2}";
            string hoverHex = $"#FF{hr:X2}{hg:X2}{hb:X2}";
            string pressedHex = $"#FF{pr:X2}{pg:X2}{pb:X2}";
            string mutedHex = $"#33{r:X2}{g:X2}{b:X2}";
            string glowHex = $"#26{r:X2}{g:X2}{b:X2}";

            return new ExtractedAlbumPalette(accentHex, hoverHex, pressedHex, mutedHex, glowHex, isDarkImage);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts RGB (0..255) to HSL (H: 0..360, S: 0..1, L: 0..1).
    /// </summary>
    public static void RgbToHsl(int r, int g, int b, out double h, out double s, out double l)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        l = (max + min) / 2.0;

        if (delta == 0)
        {
            h = 0;
            s = 0;
        }
        else
        {
            s = l < 0.5 ? (delta / (max + min)) : (delta / (2.0 - max - min));

            if (rd == max)
                h = ((gd - bd) / delta) + (gd < bd ? 6.0 : 0.0);
            else if (gd == max)
                h = ((bd - rd) / delta) + 2.0;
            else
                h = ((rd - gd) / delta) + 4.0;

            h *= 60.0;
        }
    }

    /// <summary>
    /// Converts HSL (H: 0..360, S: 0..1, L: 0..1) to RGB (0..255).
    /// </summary>
    public static void HslToRgb(double h, double s, double l, out int r, out int g, out int b)
    {
        if (s == 0)
        {
            r = g = b = (int)Math.Round(l * 255.0);
            return;
        }

        double q = l < 0.5 ? (l * (1.0 + s)) : (l + s - (l * s));
        double p = (2.0 * l) - q;
        double hk = (h % 360.0) / 360.0;

        r = (int)Math.Round(HueToRgb(p, q, hk + (1.0 / 3.0)) * 255.0);
        g = (int)Math.Round(HueToRgb(p, q, hk) * 255.0);
        b = (int)Math.Round(HueToRgb(p, q, hk - (1.0 / 3.0)) * 255.0);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + ((q - p) * 6.0 * t);
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
        return p;
    }
}
