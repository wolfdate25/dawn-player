using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Regression tests for defects found in the full-codebase audit. Each test names the behaviour
/// that was wrong, so a reappearance is unambiguous rather than showing up as a vague symptom.
/// </summary>
[Collection("SettingsStoreCollection")]
public class RegressionAuditFixTests
{
    private static Track TrackAt(string path, string artist = "", string album = "", string title = "T") => new()
    {
        Path = path,
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        DurationMs = 1000,
    };

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_Regr_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- playback queue ----------------

    [Fact]
    public void Queue_Consume_RemovesTheEntryWhereverItSits_NotJustTheHead()
    {
        // The controller consumed the queue by comparing against the head only. When the track that
        // actually started was not the head — an unreadable head gets skipped, and the queue can be
        // reordered while the next track is prefetched — the entry was left behind, and a dead file
        // at the head pinned playback to a single track forever.
        var queue = new PlaybackQueue();
        var a = new PlaylistItem(TrackAt(@"C:\m\a.flac"));
        var b = new PlaylistItem(TrackAt(@"C:\m\b.flac"));
        var c = new PlaylistItem(TrackAt(@"C:\m\c.flac"));
        queue.Enqueue(null, new[] { a, b, c });

        Assert.True(queue.Consume(b), "Consume should report that it removed an entry.");

        Assert.Equal(2, queue.Count);
        Assert.DoesNotContain(b, queue.Entries.Select(e => e.Item));
        Assert.Same(a, queue.Peek()!.Item);

        Assert.False(queue.Consume(b), "Consuming an entry that is no longer queued should report false.");
        Assert.False(queue.Consume(null));
    }

    [Fact]
    public void Queue_Consume_RenumbersRemainingEntries()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 4).Select(i => new PlaylistItem(TrackAt($@"C:\m\{i}.flac"))).ToList();
        queue.Enqueue(null, items);

        queue.Consume(items[0]);

        // Queue indices are 1-based for display.
        Assert.Equal(1, items[1].QueueIndex);
        Assert.Equal(2, items[2].QueueIndex);
        Assert.Equal(3, items[3].QueueIndex);
    }

    // ---------------- audio session teardown ----------------

    [Fact]
    public void SequencerStream_Cancel_DisposesTheReaderItOwns()
    {
        // This is the precondition behind the WASAPI exclusive→shared fallback fix. When exclusive
        // mode failed, the controller called Cancel() on the failed sequencer and then handed the
        // *same* PendingTrack to the shared-mode retry. Cancel() disposes the reader, so the retry
        // ran on a dead decoder: the session started and then produced silence. The controller now
        // re-opens the file; this test pins the disposal behaviour that makes that necessary.
        var reader = new TrackingReader();
        var playlist = new Playlist("SequencerCancel");
        var item = new PlaylistItem(TrackAt(@"C:\m.wav"));
        var pending = new PendingTrack { Playlist = playlist, Item = item, Reader = reader };

        var seq = new SequencerStream(
            NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            applyVolume: true,
            gainProvider: _ => 1f,
            latencyMs: 50);

        seq.SwitchTo(pending);
        Assert.False(reader.Disposed);
        Assert.Same(item, seq.CurrentItem);

        seq.Cancel();

        Assert.True(reader.Disposed, "Cancel() must dispose the reader the sequencer took ownership of.");
        Assert.Null(seq.CurrentItem);

        // And a read after cancellation must be silent rather than throwing on the render thread.
        var buffer = new byte[1024];
        Assert.Equal(0, seq.Read(buffer, 0, buffer.Length));
    }

    private sealed class TrackingReader : ITrackReader
    {
        public bool Disposed { get; private set; }
        public NAudio.Wave.WaveFormat SourceFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public NAudio.Wave.ISampleProvider Samples { get; }
        public TimeSpan TotalTime => TimeSpan.FromSeconds(5);
        public TimeSpan CurrentTime { get; set; }
        public string Path => @"C:\m.wav";

        public TrackingReader() => Samples = new SilentSamples(SourceFormat);

        public void Dispose() => Disposed = true;

        private sealed class SilentSamples : NAudio.Wave.ISampleProvider
        {
            public SilentSamples(NAudio.Wave.WaveFormat format) => WaveFormat = format;
            public NAudio.Wave.WaveFormat WaveFormat { get; }
            public int Read(float[] buffer, int offset, int count)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
    }

    // ---------------- settings writer ----------------

    [Fact]
    public void SettingsWriter_CoalescesManyRequestsIntoOneWrite()
    {
        // Sliders changed settings on every pointer move and each change wrote the whole JSON
        // document synchronously on the UI thread.
        var previousSink = SettingsWriter.WriteSink;
        var previousDelay = SettingsWriter.Delay;
        int writes = 0;
        try
        {
            SettingsWriter.DiscardPending();
            SettingsWriter.WriteSink = _ => Interlocked.Increment(ref writes);
            SettingsWriter.Delay = TimeSpan.FromMilliseconds(50);

            var settings = AppSettings.CreateDefault();
            for (int i = 0; i < 200; i++)
            {
                settings.Playback.Volume = i / 200.0;
                SettingsWriter.Schedule(settings);
            }

            Assert.True(SettingsWriter.HasPendingWrite, "200 rapid changes should leave exactly one pending write.");
            Assert.Equal(0, Volatile.Read(ref writes));

            SettingsWriter.Flush();
            Assert.Equal(1, Volatile.Read(ref writes));
            Assert.False(SettingsWriter.HasPendingWrite);
        }
        finally
        {
            SettingsWriter.DiscardPending();
            SettingsWriter.WriteSink = previousSink;
            SettingsWriter.Delay = previousDelay;
        }
    }

    [Fact]
    public void SettingsWriter_FlushNow_WritesTheLatestSnapshot()
    {
        var previousSink = SettingsWriter.WriteSink;
        try
        {
            string? captured = null;
            SettingsWriter.DiscardPending();
            SettingsWriter.WriteSink = json => captured = json;

            var settings = AppSettings.CreateDefault();
            settings.Playback.Volume = 0.25;
            SettingsWriter.Schedule(settings);

            settings.Playback.Volume = 0.75;
            SettingsWriter.FlushNow(settings);

            Assert.NotNull(captured);
            Assert.Contains("0.75", captured);
            Assert.False(SettingsWriter.HasPendingWrite);
        }
        finally
        {
            SettingsWriter.DiscardPending();
            SettingsWriter.WriteSink = previousSink;
        }
    }

    // ---------------- playlist persistence ----------------

    [Fact]
    public void PlaylistSort_Random_ShufflesWithoutThrowing_OnListsPastTheInsertionSortCutoff()
    {
        // Sort was handed a comparer returning a random sign, which is not a valid ordering:
        // List.Sort could walk off its span and throw, and when it completed the result was a
        // biased permutation rather than a shuffle.
        var library = new StubLibrary();
        var manager = new PlaylistManager(library);
        var pl = manager.CreatePlaylist("RandomSortRegression");
        var tracks = Enumerable.Range(0, 200).Select(i => TrackAt($@"C:\m\{i:D3}.flac", title: i.ToString("D3"))).ToList();
        manager.AddTracks(pl, tracks);

        // A single item should land in many different positions across repeated shuffles. The old
        // random-sign comparer barely moved anything, so this catches the bias as well as the
        // ArgumentException that List.Sort could throw on an inconsistent comparison.
        var positionsOfFirstTrack = new HashSet<int>();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            manager.Sort(pl, PlaylistSort.Random);

            var titles = pl.Items.Select(i => i.Track.Title).ToList();
            Assert.Equal(200, titles.Count);
            Assert.Equal(200, titles.Distinct().Count());

            positionsOfFirstTrack.Add(titles.IndexOf("000"));
        }

        Assert.True(positionsOfFirstTrack.Count >= 15,
            $"Track 000 only ever reached {positionsOfFirstTrack.Count} distinct positions over 30 shuffles; " +
            "the ordering is barely being randomised.");
    }

    [Fact]
    public void CreatePlaylist_WithATakenName_DoesNotProduceASecondPlaylistForTheSameFile()
    {
        // Two playlists with the same name resolve to the same .m3u8; the second one's debounced
        // save overwrote the first, and the loss only became visible after a restart.
        var library = new StubLibrary();
        var manager = new PlaylistManager(library);

        var first = manager.CreatePlaylist("대기열 저장");
        var second = manager.CreatePlaylist("대기열 저장");

        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal(2, manager.Playlists.Count(p => !p.IsSystem));
    }

    [Fact]
    public void CreatePlaylist_TreatsNamesThatSanitizeToTheSameFileNameAsTaken()
    {
        var library = new StubLibrary();
        var manager = new PlaylistManager(library);

        var first = manager.CreatePlaylist("Rock/Pop");
        var second = manager.CreatePlaylist("Rock_Pop");

        // "Rock/Pop" sanitizes to "Rock_Pop", so the second request must not reuse that file name.
        Assert.NotEqual("Rock_Pop", second.Name);
        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public void LoadAll_KeepsEntriesItCouldNotResolve_AndDoesNotRewriteTheFileWhileLoading()
    {
        // Loading is a read operation. Entries whose files were missing used to be dropped, and the
        // collection-changed handler then armed the debounced save, which rewrote the .m3u8 with
        // only the survivors — permanent loss for anyone who launched with a drive unplugged.
        var dir = NewTempDir("LoadAll");
        var previousBase = AppPaths.BaseDir;
        try
        {
            lock (AppPaths.BaseDirGate)
            {
                AppPaths.SetCustomBaseDir(dir);
                try
                {
                    var presentFile = Path.Combine(dir, "present.wav");
                    File.WriteAllBytes(presentFile, new byte[64]);
                    var missingPath = Path.Combine(dir, "offline", "gone.flac");

                    var playlistPath = Path.Combine(AppPaths.PlaylistsDir, "Offline Volume.m3u8");
                    Directory.CreateDirectory(AppPaths.PlaylistsDir);
                    File.WriteAllText(playlistPath,
                        "#EXTM3U" + Environment.NewLine +
                        missingPath + Environment.NewLine,
                        new UTF8Encoding(false));

                    var library = new StubLibrary();
                    var manager = new PlaylistManager(library) { DebounceDelayMs = 10 };
                    manager.LoadAll();

                    var loaded = manager.Playlists.First(p => p.Name == "Offline Volume");
                    Assert.Contains(missingPath, loaded.UnresolvedPaths);

                    // Saving must put the unresolved entry back rather than dropping it.
                    manager.SavePlaylist(loaded);
                    var written = File.ReadAllText(playlistPath);
                    Assert.Contains(missingPath, written);
                }
                finally
                {
                    AppPaths.SetCustomBaseDir(previousBase);
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void M3u_Write_RoundTripsUnresolvedPaths()
    {
        var dir = NewTempDir("M3uUnresolved");
        try
        {
            var file = Path.Combine(dir, "list.m3u8");
            var present = new PlaylistItem(TrackAt(Path.Combine(dir, "here.flac"), "A", "B"));
            var offline = @"Z:\not-mounted\track.flac";

            M3u.Write(file, new[] { present }, "list", new[] { offline });

            var entries = M3u.Read(file);
            Assert.Equal(2, entries.Count);
            Assert.Contains(offline, entries.Select(e => e.Path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void M3u_Read_KeepsPlainUtf8PathsIntact()
    {
        var dir = NewTempDir("M3uUtf8");
        try
        {
            var trackPath = Path.Combine(dir, "한글 노래.mp3");
            var file = Path.Combine(dir, "modern.m3u");
            File.WriteAllText(file, "#EXTM3U" + Environment.NewLine + trackPath + Environment.NewLine,
                new UTF8Encoding(false));

            var entries = M3u.Read(file);

            Assert.Single(entries);
            Assert.Equal(trackPath, entries[0].Path);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void M3u_Read_DoesNotDestroyPathsThatAreNotUtf8()
    {
        // A .m3u written by an older player is not UTF-8. Decoding it as UTF-8 replaced every
        // offending byte with U+FFFD, so File.Exists rejected every entry and the import produced
        // an empty playlist with no error at all.
        var dir = NewTempDir("M3uLegacy");
        try
        {
            var file = Path.Combine(dir, "legacy.m3u");

            // 0x8C 0xF6 is a valid CP949 sequence and invalid UTF-8.
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("#EXTM3U\r\n"));
            bytes.AddRange(Encoding.ASCII.GetBytes(@"C:\Music\"));
            bytes.AddRange(new byte[] { 0x8C, 0xF6 });
            bytes.AddRange(Encoding.ASCII.GetBytes(".mp3\r\n"));
            File.WriteAllBytes(file, bytes.ToArray());

            var entries = M3u.Read(file);

            Assert.Single(entries);
            Assert.DoesNotContain('�', entries[0].Path);
            Assert.EndsWith(".mp3", entries[0].Path, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------- library scanning ----------------

    [Fact]
    public async System.Threading.Tasks.Task Scan_DoesNotDeleteTracksWhoseRootIsCurrentlyUnavailable()
    {
        // An offline root was treated as "every file under it was deleted", so a NAS or USB library
        // was wiped from the database on the automatic startup scan.
        var onlineRoot = NewTempDir("ScanOnline");
        var offlineRoot = NewTempDir("ScanOffline");
        var dbPath = Path.Combine(onlineRoot, "regr_library.db");
        try
        {
            File.WriteAllBytes(Path.Combine(onlineRoot, "a.wav"), MinimalWav());
            File.WriteAllBytes(Path.Combine(offlineRoot, "b.wav"), MinimalWav());

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { onlineRoot, offlineRoot };

            using var library = new MusicLibrary(dbPath);
            await library.ScanAsync(settings);
            int afterFullScan = library.Count;
            Assert.True(afterFullScan >= 2, $"Expected both roots indexed, got {afterFullScan}.");

            // Take the second root away, exactly as an unplugged drive would.
            Directory.Delete(offlineRoot, true);
            await library.ScanAsync(settings);

            Assert.Equal(afterFullScan, library.Count);
            Assert.NotNull(library.GetTrack(Path.Combine(offlineRoot, "b.wav")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(onlineRoot, true); } catch { }
            try { if (Directory.Exists(offlineRoot)) Directory.Delete(offlineRoot, true); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_StillPrunesTracksThatReallyVanishedFromAScannedRoot()
    {
        var root = NewTempDir("ScanPrune");
        var dbPath = Path.Combine(root, "prune_library.db");
        try
        {
            var keep = Path.Combine(root, "keep.wav");
            var drop = Path.Combine(root, "drop.wav");
            File.WriteAllBytes(keep, MinimalWav());
            File.WriteAllBytes(drop, MinimalWav());

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using var library = new MusicLibrary(dbPath);
            await library.ScanAsync(settings);
            Assert.NotNull(library.GetTrack(drop));

            File.Delete(drop);
            await library.ScanAsync(settings);

            Assert.NotNull(library.GetTrack(keep));
            Assert.Null(library.GetTrack(drop));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AlbumKey_ForUntaggedTracks_IsPerFile_NotOneSharedKey()
    {
        // The scanner computed its own album key inline, which collapsed to the same empty value for
        // every untagged file — so they all resolved to the first track's cached cover image.
        var a = TrackAt(@"C:\rips\disc1\01.flac");
        var b = TrackAt(@"C:\rips\disc2\01.flac");

        var keyA = AlbumArtService.ComputeAlbumKey(a);
        var keyB = AlbumArtService.ComputeAlbumKey(b);

        Assert.NotEqual(keyA, keyB);
        Assert.NotEqual("\u0001", keyA);
    }

    private static byte[] MinimalWav()
    {
        const int sampleRate = 44100;
        const short channels = 2;
        const short bits = 16;
        const int frames = 256;
        int dataBytes = frames * channels * (bits / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8)));
        w.Write(bits);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        w.Flush();
        return ms.ToArray();
    }

    private sealed class StubLibrary : IMusicLibrary
    {
        public IReadOnlyList<Track> Tracks => Array.Empty<Track>();
        public int Count => 0;
        // Explicit no-op accessors: a stub never raises these, and a field-backed event would
        // warn about being assigned but never used.
        public event Action? TracksChanged { add { } remove { } }
        public event Action<ScanProgress>? ScanProgress { add { } remove { } }
        public Track? GetTrack(string path) => null;
        public void UpdateStats(Track track) { }
        public void UpdateReplayGain(Track track) { }
        public void ReplaceTracks(IReadOnlyCollection<Track> tracks) { }
        public void LoadFromDb() { }
        public static System.Threading.Tasks.Task ScanAsync(CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task ScanAsync(AppSettings settings, CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
        public void Dispose() { }
    }
}
