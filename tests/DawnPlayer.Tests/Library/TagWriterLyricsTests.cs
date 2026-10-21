using System;
using System.IO;
using DawnPlayer.Core.Library;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Adversarial and roundtrip tests for TagWriter embedded lyrics support (T3-1).
/// Verifies roundtripping through TagReader.ReadEmbeddedLyrics, empty-string deletion,
/// null-preservation invariant, and unicode/synced lyrics integrity.
/// </summary>
public sealed class TagWriterLyricsTests
{
    [Fact]
    public void ApplyAtomic_WritesLyrics_AndReadEmbeddedLyricsReadsThem()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "lyrics_test.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            string expectedLyrics = "[00:01.00]가사 테스트 첫 줄\n[00:05.50]Hello world line 2\n[00:10.00]日本語の歌詞テスト";

            var result = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "가사 테스트 곡",
                Artist: "테스트 아티스트",
                Lyrics: expectedLyrics));

            Assert.Equal(TagWriteResult.Ok, result);

            var readLyrics = TagReader.ReadEmbeddedLyrics(file);
            Assert.NotNull(readLyrics);
            Assert.Equal(expectedLyrics.Trim(), readLyrics);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ApplyAtomic_EmptyLyrics_RemovesExistingLyrics()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "lyrics_remove.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            // 1. Initial write with lyrics
            var result1 = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "초기 곡",
                Lyrics: "임베디드 가사 내용"));
            Assert.Equal(TagWriteResult.Ok, result1);
            Assert.Equal("임베디드 가사 내용", TagReader.ReadEmbeddedLyrics(file));

            // 2. Clear lyrics with empty string
            var result2 = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "초기 곡",
                Lyrics: string.Empty));
            Assert.Equal(TagWriteResult.Ok, result2);

            // 3. Verify lyrics removed (null)
            var readLyrics = TagReader.ReadEmbeddedLyrics(file);
            Assert.Null(readLyrics);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ApplyAtomic_NullLyrics_PreservesExistingLyrics()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "lyrics_preserve.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            // 1. Initial write with lyrics
            var result1 = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "초기 제목",
                Lyrics: "[00:01.00]보존되어야 할 가사"));
            Assert.Equal(TagWriteResult.Ok, result1);

            // 2. Modify other fields with Lyrics = null
            var result2 = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "수정된 제목",
                Artist: "새 아티스트",
                Lyrics: null));
            Assert.Equal(TagWriteResult.Ok, result2);

            // 3. Verify existing lyrics are preserved
            var readLyrics = TagReader.ReadEmbeddedLyrics(file);
            Assert.Equal("[00:01.00]보존되어야 할 가사", readLyrics);

            var readTrack = TagReader.TryRead(file, out _);
            Assert.NotNull(readTrack);
            Assert.Equal("수정된 제목", readTrack!.Title);
            Assert.Equal("새 아티스트", readTrack.Artist);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ApplyAtomic_UnicodeAndSpecialCharsLyrics_PreservedWithoutCorruption()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "lyrics_unicode.wav");
        File.WriteAllBytes(file, MinimalWav(44100, 2, 440.0, 0.2));
        try
        {
            string complexLyrics = "🎵 Emoji & Special: <>&\"'\r\n한글: 가나다라마바사\r\n日本語: 桜の花びらたち\r\nAccents: café résumé über";

            var result = TagWriter.TryApplyAtomic(file, new TagEdit(
                Title: "유니코드 가사",
                Lyrics: complexLyrics));

            Assert.Equal(TagWriteResult.Ok, result);

            var readLyrics = TagReader.ReadEmbeddedLyrics(file);
            Assert.NotNull(readLyrics);
            // Normalize CRLF to LF or compare trimmed content
            Assert.Equal(complexLyrics.Trim().Replace("\r\n", "\n"), readLyrics.Replace("\r\n", "\n"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DawnPlayerTests_Lyrics_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static byte[] MinimalWav(int sampleRate, int channels, double freqHz, double durationSec)
    {
        int totalSamples = (int)(sampleRate * durationSec);
        int bytesPerSample = 2;
        int dataSize = totalSamples * channels * bytesPerSample;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);
        w.Write((short)(channels * bytesPerSample));
        w.Write((short)(bytesPerSample * 8));
        w.Write("data"u8.ToArray());
        w.Write(dataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            short sample = (short)(Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate) * 16384);
            for (int c = 0; c < channels; c++) w.Write(sample);
        }
        return ms.ToArray();
    }
}
