using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Util;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Cue-sheet support: text parsing (quoted/relative FILE, INDEX 01 with pregap lines present),
/// the #cue= path fragment codec, and the range reader that carves one track out of a parent
/// file (positions relative, drain exactly at the range end).
/// </summary>
public sealed class CueSheetTests
{
    private const string CueText = """
        REM COMMENT "ExactAudioCopy v1.6"
        PERFORMER "들국화"
        TITLE "행진"
        FILE "bilder.wav" WAVE
          TRACK 01 AUDIO
            TITLE "그것만이 내 세상"
            PERFORMER "들국화"
            INDEX 00 00:00:00
            INDEX 01 00:00:33
          TRACK 02 AUDIO
            TITLE "행진"
            INDEX 00 04:30:60
            INDEX 01 04:32:74
          TRACK 03 AUDIO
            TITLE "이젠 없구나"
            INDEX 01 09:15:00
        """;

    private static string WriteCue(string dir, string text = CueText)
    {
        var path = Path.Combine(dir, "album.cue");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Parse_ResolvesRelativeFile_AndReadsTrackFields()
    {
        var dir = NewTempDir();
        try
        {
            var cuePath = WriteCue(dir);
            var cue = CueSheet.TryParseFile(cuePath);

            Assert.NotNull(cue);
            Assert.Equal("행진", cue!.AlbumTitle);
            Assert.Equal(3, cue.Entries.Count);

            var entries = cue.Entries;
            var first = entries[0];
            Assert.Equal(Path.Combine(dir, "bilder.wav"), first.AudioPath);
            Assert.Equal(1, first.TrackNumber);
            Assert.Equal("그것만이 내 세상", first.Title);
            Assert.Equal("들국화", first.Performer);
            // "00:00:33" is mm:ss:ff → 0 min, 0 sec, 33 CD frames (44 ms) — the classic
            // frames-vs-seconds confusion the parser must not have.
            Assert.Equal(TimeSpan.FromTicks(33 * TimeSpan.TicksPerSecond / 75), first.Start);

            var second = entries[1];
            Assert.Equal(2, second.TrackNumber);
            Assert.Equal("행진", second.Title);
            // INDEX 00 (pregap) must be ignored in favor of INDEX 01: 4*60+32 s + 74/75 s.
            Assert.Equal(TimeSpan.FromSeconds(4 * 60 + 32) + TimeSpan.FromTicks((long)Math.Round(74 * TimeSpan.TicksPerSecond / 75.0)), second.Start);

            // Track-level PERFORMER absent → album performer inherited.
            Assert.Equal("들국화", entries[2].Performer);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Parse_ToleratesUnquotedFile_AndUnparseableInput()
    {
        var cue = CueSheet.Parse("FILE track.flac WAVE\r\n  TRACK 01 AUDIO\r\n  INDEX 01 01:02:03\r\n", @"X:\m\a.cue");
        Assert.NotNull(cue);
        Assert.Equal(@"X:\m\track.flac", cue!.Entries[0].AudioPath);
        Assert.Equal(TimeSpan.FromSeconds(62) + TimeSpan.FromTicks(3 * TimeSpan.TicksPerSecond / 75), cue.Entries[0].Start);

        // Nothing playable: no entries, no throw.
        Assert.Null(CueSheet.Parse("REM nothing here", @"X:\m\a.cue"));
        Assert.Null(CueSheet.Parse("", @"X:\m\a.cue"));
        Assert.Null(CueSheet.TryParseFile(@"X:\definitely\missing\album.cue"));
    }

    // ---------------- path fragments ----------------

    [Fact]
    public void CuePathFragment_RoundTrips()
    {
        Assert.False(AppPaths.IsCueFragment(@"X:\m\album.flac"));
        Assert.Equal(@"X:\m\album.flac", AppPaths.PhysicalPath(@"X:\m\album.flac"));

        var virtualPath = AppPaths.MakeCuePath(@"X:\m\album.flac", 33000, 272987);
        Assert.True(AppPaths.IsCueFragment(virtualPath));
        Assert.Equal(@"X:\m\album.flac", AppPaths.PhysicalPath(virtualPath));

        Assert.True(AppPaths.TryDecodeCuePath(virtualPath, out var physical, out var start, out var end));
        Assert.Equal(@"X:\m\album.flac", physical);
        Assert.Equal(33000, start);
        Assert.Equal(272987, end);

        // Extension no longer parses as audio on the virtual path — existence checks must use
        // PhysicalPath, and IsSupportedAudioFile must reject the fragment itself.
        Assert.False(AppPaths.IsSupportedAudioFile(virtualPath));
        Assert.True(AppPaths.IsSupportedAudioFile(AppPaths.PhysicalPath(virtualPath)));

        Assert.False(AppPaths.TryDecodeCuePath(@"X:\m\album.flac#cue=", out _, out _, out _));
        Assert.False(AppPaths.TryDecodeCuePath(@"X:\m\album.flac#cue=abc", out _, out _, out _));
        Assert.False(AppPaths.TryDecodeCuePath(@"X:\m\album.flac#cue=5-", out _, out _, out _));
    }

    // ---------------- range reader ----------------

    [Fact]
    public void CueTrackReader_PlaysOnlyItsRange_WithRelativePositions()
    {
        var dir = NewTempDir();
        try
        {
            // 2 s of a 440 Hz tone, 44100 Hz mono.
            var file = Path.Combine(dir, "tone.wav");
            File.WriteAllBytes(file, MinimalWav(44100, 1, 440.0, 2.0));

            using var parent = AudioFileReaderFactory.Open(file);
            var fmt = parent.SourceFormat;

            using var cueReader = new CueTrackReader(AudioFileReaderFactory.Open(file),
                TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));

            Assert.Equal(fmt.SampleRate, cueReader.SourceFormat.SampleRate);
            Assert.Equal(TimeSpan.FromSeconds(1), cueReader.TotalTime);
            Assert.Equal(TimeSpan.Zero, cueReader.CurrentTime);
            Assert.Equal(file, cueReader.Path); // physical path, no fragment

            // Reading through the range: after ~1 s of frames the provider must hit end-of-stream.
            var buffer = new float[44100 * 2];
            int total = 0, read;
            while ((read = cueReader.Samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                Assert.True(total <= 44100 + 2205, $"range overrun: {total} frames"); // 1 s + 50 ms slack
            }
            Assert.True(total >= 44100 - 2205, $"range short: {total} frames");
            Assert.True(Math.Abs(cueReader.CurrentTime.TotalSeconds - 1.0) < 0.05,
                $"expected ~1.0 s, got {cueReader.CurrentTime}");

            // Seeking maps into the parent's timeline.
            cueReader.CurrentTime = TimeSpan.FromSeconds(0.25);
            Assert.True(Math.Abs(cueReader.CurrentTime.TotalSeconds - 0.25) < 0.02);

            // Clamping: seek past the end lands at the range end, not the file end.
            cueReader.CurrentTime = TimeSpan.FromSeconds(5);
            Assert.True(cueReader.CurrentTime <= cueReader.TotalTime);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Factory_OpensCueFragment_AsRangeReader()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "tone.wav");
            File.WriteAllBytes(file, MinimalWav(44100, 1, 440.0, 1.0));

            using var reader = AudioFileReaderFactory.Open(AppPaths.MakeCuePath(file, 100, 600));
            Assert.IsType<CueTrackReader>(reader);
            Assert.Equal(TimeSpan.FromMilliseconds(500), reader.TotalTime);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Scan_IndexesCueTracks_HidesCoveredImageRow_AndCarriesStats()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "cue.db");
        try
        {
            // A 3 s image with a 3-track cue at 0 s / 1 s / 2 s.
            var image = Path.Combine(dir, "bilder.wav");
            File.WriteAllBytes(image, MinimalWav(44100, 1, 440.0, 3.0));
            WriteCue(dir, """
                PERFORMER "들국화"
                TITLE "행진"
                FILE "bilder.wav" WAVE
                  TRACK 01 AUDIO
                    TITLE "그것만이 내 세상"
                    INDEX 01 00:00:00
                  TRACK 02 AUDIO
                    TITLE "행진"
                    INDEX 01 00:01:00
                  TRACK 03 AUDIO
                    TITLE "이젠 없구나"
                    INDEX 01 00:02:00
                """);

            var settings = DawnPlayer.Core.Persistence.AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { dir };

            using (var library = new MusicLibrary(dbPath))
            {
                await library.ScanAsync(settings);

                // The whole-file row is hidden while the cue covers the image.
                Assert.Null(library.GetTrack(image));

                var expected = new Dictionary<int, string>
                {
                    [1] = AppPaths.MakeCuePath(image, 0, 1000),
                    [2] = AppPaths.MakeCuePath(image, 1000, 2000),
                    [3] = AppPaths.MakeCuePath(image, 2000, 3000),
                };
                foreach (var (no, path) in expected)
                {
                    var track = library.GetTrack(path);
                    Assert.NotNull(track);
                    Assert.Equal(no, track!.TrackNo);
                }

                var first = library.GetTrack(expected[1])!;
                Assert.Equal("그것만이 내 세상", first.Title);
                Assert.Equal("들국화", first.Artist);
                Assert.Equal(1000, first.DurationMs);
                Assert.Equal("행진", first.Album); // inherited from the image tags

                // Stats survive a rescan (the carry-forward path keyed on the fragment path).
                first.PlayCount = 3;
                first.Rating = 4;
                library.UpdateStats(first);
                library.UpdateRating(first);

                await library.ScanAsync(settings);
                var after = library.GetTrack(expected[1])!;
                Assert.Equal(3, after.PlayCount);
                Assert.Equal(4, after.Rating);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_Cue_{Guid.NewGuid():N}");
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
}
