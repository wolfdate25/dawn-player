using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Helper service for generating and caching high-quality blurred album artwork
/// for the Eole Blurred Artwork Wallpaper mode.
/// </summary>
public static class AlbumArtBlurHelper
{
    private static readonly string BlurCacheDir = Path.Combine(Util.AppPaths.ArtCacheDir, "blur");

    /// <summary>
    /// Gets or creates a blurred version of the specified album art file.
    /// Returns the absolute path to the cached blurred image file, or null if the input is invalid.
    /// </summary>
    public static string? GetOrCreateBlurredArtPath(string? originalArtPath, string? albumKey, int blurRadius = 24)
    {
        if (string.IsNullOrWhiteSpace(originalArtPath) || !File.Exists(originalArtPath))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(BlurCacheDir))
            {
                Directory.CreateDirectory(BlurCacheDir);
            }

            var key = !string.IsNullOrWhiteSpace(albumKey)
                ? albumKey
                : originalArtPath.ToLowerInvariant();

            var hash = TagReader.Sha1Hex(key + $"_blur_{blurRadius}");
            var targetFile = Path.Combine(BlurCacheDir, hash + ".jpg");

            if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
            {
                return targetFile;
            }

            using var stream = new FileStream(originalArtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var original = new Bitmap(stream);

            // Downscale for performance and smooth soft blur appearance (300x300)
            const int targetW = 300;
            const int targetH = 300;
            using var downscaled = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(downscaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, targetW, targetH);
            }

            // Apply multi-pass box blur to simulate Gaussian blur
            ApplyFastBlur(downscaled, blurRadius);

            var tempFile = Path.Combine(BlurCacheDir, $"{hash}_{Guid.NewGuid():N}.tmp");
            try
            {
                var encoderParams = new EncoderParameters(1)
                {
                    Param = { [0] = new EncoderParameter(Encoder.Quality, 88L) }
                };
                var jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                    ?? ImageCodecInfo.GetImageEncoders()[0];

                downscaled.Save(tempFile, jpegCodec, encoderParams);
                File.Move(tempFile, targetFile, overwrite: true);
                return targetFile;
            }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
                {
                    return targetFile;
                }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies a 3-pass fast horizontal & vertical box blur approximation of Gaussian blur.
    /// </summary>
    public static void ApplyFastBlur(Bitmap image, int radius)
    {
        if (radius < 1) return;

        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int w = image.Width;
            int h = image.Height;
            int stride = Math.Abs(data.Stride);
            byte[] buffer = new byte[stride * h];
            byte[] target = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            // 3-pass box blur
            BoxBlurPass(buffer, target, w, h, stride, radius);
            BoxBlurPass(target, buffer, w, h, stride, radius);
            BoxBlurPass(buffer, target, w, h, stride, radius);

            System.Runtime.InteropServices.Marshal.Copy(target, 0, data.Scan0, target.Length);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    private static void BoxBlurPass(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        // Horizontal pass
        byte[] temp = new byte[src.Length];
        int div = 2 * r + 1;

        for (int y = 0; y < h; y++)
        {
            int yOffset = y * stride;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (int i = -r; i <= r; i++)
            {
                int x = Math.Clamp(i, 0, w - 1);
                int idx = yOffset + (x * 4);
                sumB += src[idx];
                sumG += src[idx + 1];
                sumR += src[idx + 2];
                sumA += src[idx + 3];
            }

            for (int x = 0; x < w; x++)
            {
                int idx = yOffset + (x * 4);
                temp[idx] = (byte)(sumB / div);
                temp[idx + 1] = (byte)(sumG / div);
                temp[idx + 2] = (byte)(sumR / div);
                temp[idx + 3] = (byte)(sumA / div);

                int xLeft = Math.Clamp(x - r, 0, w - 1);
                int xRight = Math.Clamp(x + r + 1, 0, w - 1);
                int idxLeft = yOffset + (xLeft * 4);
                int idxRight = yOffset + (xRight * 4);

                sumB += src[idxRight] - src[idxLeft];
                sumG += src[idxRight + 1] - src[idxLeft + 1];
                sumR += src[idxRight + 2] - src[idxLeft + 2];
                sumA += src[idxRight + 3] - src[idxLeft + 3];
            }
        }

        // Vertical pass
        for (int x = 0; x < w; x++)
        {
            int xOffset = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            for (int i = -r; i <= r; i++)
            {
                int y = Math.Clamp(i, 0, h - 1);
                int idx = (y * stride) + xOffset;
                sumB += temp[idx];
                sumG += temp[idx + 1];
                sumR += temp[idx + 2];
                sumA += temp[idx + 3];
            }

            for (int y = 0; y < h; y++)
            {
                int idx = (y * stride) + xOffset;
                dst[idx] = (byte)(sumB / div);
                dst[idx + 1] = (byte)(sumG / div);
                dst[idx + 2] = (byte)(sumR / div);
                dst[idx + 3] = (byte)(sumA / div);

                int yTop = Math.Clamp(y - r, 0, h - 1);
                int yBottom = Math.Clamp(y + r + 1, 0, h - 1);
                int idxTop = (yTop * stride) + xOffset;
                int idxBottom = (yBottom * stride) + xOffset;

                sumB += temp[idxBottom] - temp[idxTop];
                sumG += temp[idxBottom + 1] - temp[idxTop + 1];
                sumR += temp[idxBottom + 2] - temp[idxTop + 2];
                sumA += temp[idxBottom + 3] - temp[idxTop + 3];
            }
        }
    }
}
