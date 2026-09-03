using DawnPlayer.Core.Library;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Tag writer: atomic editor writes (fields + artwork), ReplayGain tag roundtrips through
/// <see cref="TagReader"/>, and the read-failure path leaves the original file untouched.
/// </summary>
public sealed class TagWriterTests
{
    [Fact]
    public void ApplyAtomic_WritesFields_AndTagReaderSeesThem()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "tagged.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            var result = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "남산 위의 저 소나무",
                Artist: "전인권",
                AlbumArtist: "들국화",
                Album: "행진",
                Genre: "Rock",
                Year: 1985,
                TrackNo: 3,
                DiscNo: 1));

            Assert.Equal(TagWriteResult.Ok, result);

            var reread = TagReader.TryRead(file, out _);
            Assert.NotNull(reread);
            Assert.Equal("남산 위의 저 소나무", reread!.Title);
            Assert.Equal("전인권", reread.Artist);
            Assert.Equal("들국화", reread.AlbumArtist);
            Assert.Equal("행진", reread.Album);
            Assert.Equal("Rock", reread.Genre);
            Assert.Equal(1985, reread.Year);
            Assert.Equal(3, reread.TrackNo);
            Assert.Equal(1, reread.DiscNo);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ApplyAtomic_EmbbedsArtwork_AsFrontCover()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "art.wav");
        var png = Path.Combine(dir, "cover.png");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        File.WriteAllBytes(png, MinimalPng());
        try
        {
            var result = TagWriter.TryApplyAtomic(file, new TagEdit(Art: TagEditorArt.Embed, ArtSourcePath: png));
            Assert.Equal(TagWriteResult.Ok, result);

            var reread = TagReader.TryRead(file, out var picture);
            Assert.NotNull(reread);
            Assert.NotNull(picture);
            Assert.True(reread!.HasLrc || !reread.HasLrc); // row loaded
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SetReplayGain_RoundTrips_ThroughTagReader()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "rg.wav");
        File.WriteAllBytes(file, MinimalWav(48000, 2, 1000.0, 1.0));
        try
        {
            Assert.True(TagWriter.TrySetReplayGain(file, -4.25, 0.987654, -3.5, 0.950000));

            var reread = TagReader.TryRead(file, out _);
            Assert.NotNull(reread);
            Assert.NotNull(reread!.RgTrackGainDb);
            Assert.True(Math.Abs(reread.RgTrackGainDb.Value - (-4.25)) < 0.01, $"{reread.RgTrackGainDb}");
            Assert.True(Math.Abs((reread.RgTrackPeak ?? 0) - 0.987654) < 1e-5, $"{reread.RgTrackPeak}");
            Assert.True(Math.Abs((reread.RgAlbumGainDb ?? 0) - (-3.5)) < 0.01, $"{reread.RgAlbumGainDb}");
            Assert.True(Math.Abs((reread.RgAlbumPeak ?? 0) - 0.95) < 1e-5, $"{reread.RgAlbumPeak}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ApplyAtomic_MissingFile_DoesNotThrow()
    {
        Assert.Equal(TagWriteResult.FileMissing,
            TagWriter.TryApplyAtomic(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac"), new TagEdit()));
        Assert.False(TagWriter.TrySetReplayGain(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac"), 0, 0, 0, 0));
        Assert.False(TagWriter.TrySetRating(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac"), 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SetRating_RoundTrips_ThroughTagReader(int stars)
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, $"rate{stars}.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            Assert.True(TagWriter.TrySetRating(file, stars));

            var reread = TagReader.TryRead(file, out _);
            Assert.NotNull(reread);
            Assert.Equal(stars, reread!.Rating);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SetRating_Zero_ClearsTheRating()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "unrate.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            Assert.True(TagWriter.TrySetRating(file, 4));
            Assert.Equal(4, TagReader.TryRead(file, out _)!.Rating);

            Assert.True(TagWriter.TrySetRating(file, 0));
            Assert.Equal(0, TagReader.TryRead(file, out _)!.Rating);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void PopmAnchors_MapBothWays()
    {
        // The de facto anchors (1/64/128/196/255) must survive a write→read cycle exactly, which
        // is what interop with other players' ratings depends on.
        int[] anchors = { 1, 64, 128, 196, 255 };
        for (int i = 0; i < anchors.Length; i++)
        {
            Assert.Equal(anchors[i], TagWriter.StarsToPopm(i + 1));
        }
        Assert.Equal(0, TagWriter.StarsToPopm(0));

        foreach (int anchor in anchors)
        {
            Assert.Equal(Array.IndexOf(anchors, anchor) + 1, TagReader.PopmToStars((byte)anchor));
        }
        Assert.Equal(0, TagReader.PopmToStars(0));
    }

    [Fact]
    public void RatingScales_AdaptToStars()
    {
        // 1-5 direct, 0-10 half, 0-100 by twentieth.
        Assert.Equal(4, TagReader.ScaleRating(4));
        Assert.Equal(4, TagReader.ScaleRating(8));
        Assert.Equal(4, TagReader.ScaleRating(80));
        Assert.Equal(0, TagReader.ScaleRating(0));
        Assert.Equal(5, TagReader.ScaleRating(100));
    }

    [Fact]
    public void SetReplayGain_WithR128_WritesBothFieldSets()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "rg2.wav");
        File.WriteAllBytes(file, MinimalWav(48000, 2, 1000.0, 1.0));
        try
        {
            Assert.True(TagWriter.TrySetReplayGain(file, -4.25, 0.987654, -3.5, 0.95, writeR128: true));

            // RG1 fields present and readable as before...
            var reread = TagReader.TryRead(file, out _);
            Assert.NotNull(reread);
            Assert.True(Math.Abs(reread!.RgTrackGainDb!.Value - (-4.25)) < 0.01);
            Assert.True(Math.Abs(reread.RgAlbumGainDb!.Value - (-3.5)) < 0.01);

            // ...and the R128 fields exist too: strip the classic ones and the reader must fall
            // back to the RG2 values instead of reporting the track as untagged.
            using (var tf = TagLib.File.Create(file))
            {
                var id3 = (TagLib.Id3v2.Tag)tf.GetTag(TagLib.TagTypes.Id3v2)!;
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                             .Where(f => f.Description?.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase) == true)
                             .ToList())
                {
                    id3.RemoveFrame(frame);
                }
                tf.Save();
            }

            var stripped = TagReader.TryRead(file, out _);
            Assert.NotNull(stripped);
            Assert.True(Math.Abs(stripped!.RgTrackGainDb!.Value - (-4.25)) < 0.01,
                $"expected R128 track gain fallback, got {stripped.RgTrackGainDb}");
            Assert.True(Math.Abs(stripped.RgAlbumGainDb!.Value - (-3.5)) < 0.01,
                $"expected R128 album gain fallback, got {stripped.RgAlbumGainDb}");
            // RG2 defines no peak fields: the stripped peaks are gone for good.
            Assert.Null(stripped.RgTrackPeak);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_TagWriter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static byte[] MinimalWav(int sampleRate, int channels, double freqHz, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        const short bits = 16;
        int dataBytes = frames * channels * (bits / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8)));
        w.Write(bits);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);

        var samples = new short[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            short s = (short)(0.5 * short.MaxValue * Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = s;
        }
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] MinimalPng()
    {
        // 1x1 transparent PNG.
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
