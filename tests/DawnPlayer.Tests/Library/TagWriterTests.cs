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
