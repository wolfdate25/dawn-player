using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Defensive gates for the 2026-10-24 adversarial review repairs. Each test pins the exact
/// defect the repair closed, so a regression fails here instead of in a user's ears or files.
/// </summary>
public sealed class Review20261024RepairGateTests : IDisposable
{
    private readonly string _tempDir;

    public Review20261024RepairGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_RepairGates_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // CP949 fixtures below need the code-pages provider before any GetEncoding(949) call.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class EmptyLibrary : IMusicLibrary
    {
        public IReadOnlyList<Track> Tracks { get; } = Array.Empty<Track>();
        public int Count => 0;
        public event Action? TracksChanged;
        public event Action<ScanProgress>? ScanProgress;
        public Track? GetTrack(string path) => null;
        public void UpdateStats(Track track) { }
        public void UpdateRating(Track track) { }
        public void UpdateReplayGain(Track track) { }
        public void ReplaceTracks(IReadOnlyCollection<Track> tracks) { }
        public void LoadFromDb() { }
        public Task ScanAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        internal void Unused() { TracksChanged?.Invoke(); ScanProgress?.Invoke(default!); }
    }

    private static Track MakeTrack(string id, string title) => new()
    {
        Path = $@"C:\Music\{id}.mp3",
        Title = title,
        Album = "Album",
        Artist = "Artist",
        DurationMs = 180000,
    };

    // ---------------- P0: rename over an existing playlist's file ----------------

    [Fact]
    public void RenamePlaylist_DuplicateName_IsRefused()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var a = manager.CreatePlaylist("A");
        var b = manager.CreatePlaylist("B");
        manager.AddTracks(b, new[] { MakeTrack("b1", "B's only track") });

        bool ok = manager.RenamePlaylist(a, "B");

        Assert.False(ok, "Renaming onto an existing playlist must be refused — it overwrote B.m3u8.");
        Assert.Equal("A", a.Name);
        Assert.Equal("B's only track", b.Items.Single().Track.Title);
    }

    [Fact]
    public void RenamePlaylist_SanitizationCollision_IsRefused()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var slash = manager.CreatePlaylist("Rock/Pop");
        // AddPlaylist registers under the exact name (LoadAll does the same): "Rock/Pop" and
        // "Rock_Pop" sanitize to the SAME .m3u8, so the rename must be refused at the file level.
        manager.AddPlaylist(new Playlist("Rock_Pop"));

        Assert.False(manager.RenamePlaylist(slash, "Rock_Pop"));
        Assert.Equal("Rock/Pop", slash.Name);
    }

    [Fact]
    public void RenamePlaylist_UniqueName_Succeeds()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var a = manager.CreatePlaylist("A");

        Assert.True(manager.RenamePlaylist(a, "Renamed"));
        Assert.Equal("Renamed", a.Name);
    }

    [Fact]
    public void RenamePlaylist_SystemName_IsRefused()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var a = manager.CreatePlaylist("A");

        // Renaming onto the reserved "Now Playing" name would merge into the system playlist
        // on the next LoadAll.
        Assert.False(manager.RenamePlaylist(a, PlaylistManager.NowPlayingPlaylistName));
    }

    // ---------------- P0-adjacent: query parser depth bomb ----------------

    [Fact]
    public void SmartPlaylistQuery_ParenthesisBomb_ReportsErrorInsteadOfStackOverflow()
    {
        // StackOverflowException cannot be caught — before the depth cap this killed the process
        // straight from the UI thread's TextChanged handler.
        string bomb = new string('(', 5000);

        bool ok = SmartPlaylistQuery.TryParse(bomb, out var query, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Null(query);
    }

    [Fact]
    public void SmartPlaylistQuery_DeepButLegalNesting_StillParses()
    {
        string legal = new string('(', 20) + "%rating% GREATER 3" + new string(')', 20);
        Assert.True(SmartPlaylistQuery.TryParse(legal, out var query, out var error));
        Assert.NotNull(query);
        Assert.Null(error);
    }

    [Fact]
    public void SmartPlaylistQuery_HugeDuringCount_ClampsInsteadOfOverflowing()
    {
        // 100000000 * 30 days overflowed int and silently matched nothing.
        Assert.True(SmartPlaylistQuery.TryParse("%last_played% DURING LAST 100000000 MONTHS", out var query, out _));
        Assert.NotNull(query);

        var track = MakeTrack("t", "T") with { LastPlayedUtcTicks = DateTime.UtcNow.Ticks };
        Assert.Equal(1, query!.Apply(new[] { track }).Count());
    }

    // ---------------- album ReplayGain peak aggregation ----------------

    [Fact]
    public void LoudnessScanner_AppendPeak_FoldsMemberPeakIntoAlbumResult()
    {
        var trackScanner = new LoudnessScanner(44100, 2);
        var loud = new float[44100 * 2];
        for (int i = 0; i < loud.Length; i += 2)
        {
            loud[i] = 0.9f * (float)Math.Sin(i * 0.01);
            loud[i + 1] = loud[i];
        }
        trackScanner.ProcessSamples(loud, 0, loud.Length, 2);
        var trackResult = trackScanner.Finish();

        // The album aggregate never decodes samples — before AppendPeak its Peak stayed 0.0,
        // which disabled anti-clipping AND wrote "0.000000" into album peak tags.
        var albumScanner = new LoudnessScanner(44100, 2);
        albumScanner.AppendBlocks(trackScanner.BlockEnergies);
        Assert.Equal(0.0, albumScanner.Finish().Peak, 6);

        albumScanner.AppendPeak(trackResult.Peak);
        Assert.Equal(trackResult.Peak, albumScanner.Finish().Peak, 6);
        Assert.True(albumScanner.Finish().Peak > 0.5, "Peak must be folded, not zeroed.");
    }

    // ---------------- CUE parsing ----------------

    [Fact]
    public void CueSheet_QuotedFileNameWithSpaces_ResolvesWholePath()
    {
        // "Artist - Album.flac" is the standard EAC form; the old first-space split truncated it
        // at "Artist" and the whole album failed File.Exists.
        string cuePath = Path.Combine(_tempDir, "album.cue");
        File.WriteAllText(cuePath, "FILE \"My Album Image.flac\" WAVE\n" +
                                   "  TRACK 01 AUDIO\n" +
                                   "    TITLE \"Song One\"\n" +
                                   "    INDEX 01 00:00:00\n" +
                                   "  TRACK 02 AUDIO\n" +
                                   "    TITLE \"Song Two\"\n" +
                                   "    INDEX 01 01:30:00\n");
        File.WriteAllBytes(Path.Combine(_tempDir, "My Album Image.flac"), new byte[] { 0xFF, 0xFB, 0x90, 0x00 });

        var doc = CueSheet.TryParseFile(cuePath);

        Assert.NotNull(doc);
        Assert.All(doc!.Entries, e => Assert.StartsWith(Path.Combine(_tempDir, "My Album Image.flac"), e.AudioPath));
        Assert.Equal(2, doc.Entries.Count);
    }

    [Fact]
    public void CueSheet_NonUtf8Bytes_DecodeWithoutReplacementCharacters()
    {
        // CP949-encoded cue (BOM-less, legacy EAC): the old Encoding.Default fallback is UTF-8
        // on .NET Core and turned every Korean title into U+FFFD.
        string cuePath = Path.Combine(_tempDir, "ansi.cue");
        string content = "TITLE \"앨범\"\n" +
                         "PERFORMER \"가수\"\n" +
                         "FILE \"image.flac\" WAVE\n" +
                         "  TRACK 01 AUDIO\n" +
                         "    TITLE \"제목\"\n" +
                         "    INDEX 01 00:00:00\n";
        var cp949 = Encoding.GetEncoding(949);
        File.WriteAllBytes(cuePath, cp949.GetBytes(content));

        var doc = CueSheet.TryParseFile(cuePath);

        Assert.NotNull(doc);
        string all = doc!.AlbumTitle + doc.AlbumPerformer + doc.Entries[0].Title;
        Assert.DoesNotContain('\uFFFD', all);
        Assert.Equal("앨범", doc.AlbumTitle);
        Assert.Equal("제목", doc.Entries[0].Title);
    }

    // ---------------- LRC ANSI detection ----------------

    [Fact]
    public void LrcParser_NonUtf8Bytes_DecodeWithoutReplacementCharacters()
    {
        // Same defect class as the CUE fallback: "UTF-8/ANSI auto detection" never detected ANSI.
        string lrcPath = Path.Combine(_tempDir, "ansi.lrc");
        string content = "[ti:제목]\n[00:01.00]가사 줄\n";
        var cp949 = Encoding.GetEncoding(949);
        File.WriteAllBytes(lrcPath, cp949.GetBytes(content));

        var doc = LrcParser.ParseFile(lrcPath);

        Assert.True(doc.HasLines);
        string all = doc.Title + string.Concat(doc.Lines.Select(l => l.Text));
        Assert.DoesNotContain('\uFFFD', all);
        Assert.Equal("제목", doc.Title);
        Assert.Equal("가사 줄", doc.Lines[0].Text);
    }

    [Fact]
    public void LegacyTextDecoder_ShortMarkupHeavyLine_StillPicksCp949OverSystemAnsi()
    {
        // CI regression (2026-10-29): an absolute Hangul-ratio threshold rejected CP949 for
        // markup-heavy LRC lines (most bytes are ASCII "[00:01.00]" markup) on non-Korean
        // systems. The decision is now the DELTA between candidates — pinned with the exact
        // short line that failed.
        string content = "[ti:제목]
[00:01.00]가사 줄
";
        var bytes = Encoding.GetEncoding(949).GetBytes(content);

        var decoded = DawnPlayer.Core.Util.LegacyTextDecoder.DecodeNonUtf8(bytes);

        Assert.Contains("제목", decoded);
        Assert.Contains("가사 줄", decoded);
    }

    // ---------------- EQ delay-line continuity ----------------

    [Fact]
    public void EqualizerDspEffect_LiveProfileEdit_PreservesDelayLineContinuity()
    {
        const int rate = 44100;
        var eq = new EqualizerDspEffect(new EqProfile
        {
            Enabled = true,
            PreampDb = 0,
            Bands = new List<EqBandSettings>
            {
                new() { Type = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = 12, Q = 0.7 },
            }
        });
        eq.Initialize(rate, 2);

        var buf = new float[rate * 2];
        for (int f = 0; f < rate; f++)
        {
            float s = 0.5f * (float)Math.Sin(2 * Math.PI * 100.0 * f / rate);
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }
        // Let the filter reach steady state, then note the very last sample.
        eq.Process(buf, 0, buf.Length);
        double lastBefore = buf[buf.Length - 2];

        // A live gain tweak (the slider's 0.1 dB ticks) must NOT zero the TDF2 delay lines:
        // z1 of a low shelf at listening level is several times the signal, so zeroing injected
        // a full-scale click on every tick.
        eq.SetProfile(new EqProfile
        {
            Enabled = true,
            PreampDb = 0,
            Bands = new List<EqBandSettings>
            {
                new() { Type = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = 11, Q = 0.7 },
            }
        });

        var next = new float[rate * 2];
        for (int f = 0; f < rate; f++)
        {
            float s = 0.5f * (float)Math.Sin(2 * Math.PI * 100.0 * (rate + f) / rate);
            next[f * 2] = s;
            next[f * 2 + 1] = s;
        }
        eq.Process(next, 0, next.Length);
        double firstAfter = next[0];

        Assert.True(Math.Abs(firstAfter - lastBefore) < 0.25,
            $"Delay lines were reset on a live profile edit: last {lastBefore:F3} -> first {firstAfter:F3} (step of a zeroed state would be ≈1.5)");
    }

    // ---------------- normalizer toggle-off double application ----------------

    [Fact]
    public void DynamicNormalizerDspEffect_DisableWhileRidingReplayGain_SnapsToUnityNotDouble()
    {
        const int rate = 44100;
        var norm = new DynamicNormalizerDspEffect();
        norm.Initialize(rate, 2);
        norm.ApplySettings(new NormalizerSettings { Enabled = true, Mode = NormalizerMode.Hybrid });
        norm.SetReplayGain(2.0f); // converged: the effect multiplies by 2 (node passes unity)

        var buf = new float[rate * 2];
        Array.Fill(buf, 0.25f);
        norm.Process(buf, 0, buf.Length); // converge on the static RG gain
        double convergedRatio = buf[buf.Length - 2] / 0.25; // end of the 1 s block = converged
        Assert.True(convergedRatio > 1.9, $"Expected the RG gain to converge at 2x, measured {convergedRatio:F2}");

        // Toggling OFF moves the same RG to the pre-chain node. The effect must snap to unity
        // (product stays RG); the old fade-out multiplied node×effect = RG² for its duration.
        norm.ApplySettings(new NormalizerSettings { Enabled = false, Mode = NormalizerMode.Hybrid });
        Array.Clear(buf);
        Array.Fill(buf, 0.25f);
        norm.Process(buf, 0, buf.Length);
        double afterToggleRatio = buf[0] / 0.25;

        Assert.True(Math.Abs(afterToggleRatio - 1.0) < 0.15,
            $"Disable while riding RG must snap to unity (node carries the gain), measured {afterToggleRatio:F2} — an RG² boost would read ≈2.0");
    }

    // ---------------- smart refresh identity ----------------

    [Fact]
    public void ReplaceWithTracks_NoChange_SkipsChurnAndKeepsInstances()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var pl = manager.CreatePlaylist("PL");
        var t1 = MakeTrack("1", "One");
        var t2 = MakeTrack("2", "Two");
        manager.AddTracks(pl, new[] { t1, t2 });
        var originalItems = pl.Items.ToList();

        int removedCalls = 0;
        manager.ItemsRemoved += (_, _) => removedCalls++;

        var result = manager.ReplaceWithTracks(pl, new List<Track> { t1, t2 });

        Assert.Same(originalItems[0], result[0]);
        Assert.Same(originalItems[1], result[1]);
        Assert.Equal(0, removedCalls);
    }

    [Fact]
    public void ReplaceWithTracks_PartialChange_ReusesKeptInstancesAndPurgesOnlyGoneOnes()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var pl = manager.CreatePlaylist("PL");
        var t1 = MakeTrack("1", "One");
        var t2 = MakeTrack("2", "Two");
        var t3 = MakeTrack("3", "Three");
        manager.AddTracks(pl, new[] { t1, t2 });

        PlaylistItem? removedReported = null;
        manager.ItemsRemoved += (_, items) => removedReported = items.Single();

        // t1 disappears, t3 appears, t2 survives — the surviving QUEUE ENTRY must survive too
        // (before the fix the whole list was rebuilt and the queue was purged wholesale).
        var result = manager.ReplaceWithTracks(pl, new List<Track> { t2, t3 });

        Assert.Same(pl.Items[0], result[0]);
        Assert.NotNull(removedReported);
        Assert.Equal(t1.Path, removedReported!.Track.Path);
        Assert.Equal(2, pl.Items.Count);
    }

    [Fact]
    public void ReplaceWithTracks_MetadataChange_FlowsThrough()
    {
        var manager = new PlaylistManager(new EmptyLibrary());
        var pl = manager.CreatePlaylist("PL");
        var t1 = MakeTrack("1", "One");
        manager.AddTracks(pl, new[] { t1 });

        // The library re-resolved the same path to a NEW instance with fresh metadata: the
        // no-change fast path must not swallow it.
        var rescanned = MakeTrack("1", "One (retagged)");
        manager.ReplaceWithTracks(pl, new List<Track> { rescanned });

        Assert.Equal("One (retagged)", pl.Items.Single().Track.Title);
    }
}
