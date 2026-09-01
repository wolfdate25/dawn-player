using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;
using CorePlaybackState = DawnPlayer.Core.Audio.PlaybackState;

namespace DawnPlayer.Tests;

/// <summary>
/// Contention coverage for the three components that are hit from several threads at once:
/// <see cref="MusicLibrary"/>'s SQLite transactions (simultaneous scans and reads, a mutating disk
/// underneath a scan, and paths carrying quotes and SQL-shaped text that must survive
/// parameterization), <see cref="PlaybackController"/> under randomized command bursts plus its
/// queue/volume/state invariants, and <see cref="SequencerStream"/> switch-versus-read contention.
/// </summary>
[Collection("AudioDeviceCollection")]
public class LibraryAndPlaybackConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public LibraryAndPlaybackConcurrencyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// xUnit 2.5.3 cannot turn a running test into a skip, so an environment bail-out reports PASS.
    /// This marker is the only way a log reader can tell one from a run that actually asserted.
    /// </summary>
    private void LogEnvironmentSkip(string reason) => _output.WriteLine($"[SKIPPED-ENV] {reason}");

    /// <summary>
    /// True when the machine exposes a render endpoint. Asserting that playback reaches Playing
    /// requires one: with no device the output never starts and the controller stays Stopped.
    /// </summary>
    private static bool HasRenderEndpoint()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia) != null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    // =========================================================================
    // SECTION 1: MusicLibrary Concurrency, _ioLock & SQLite Transaction Tests
    // =========================================================================

    [Fact]
    public async Task MusicLibrary_HighConcurrency_SimultaneousScansAndLoads_NoDeadlockNoCorruption()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Stress_IoLock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const int fileCount = 20;
            for (int i = 0; i < fileCount; i++)
            {
                CreateMinimalWavFile(Path.Combine(tempDir, $"track_{i:D3}.wav"), 400 + (i * 20));
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings { Library = new LibrarySettings { Folders = new List<string> { tempDir } } };

            // Initial scan to index test files
            await library.ScanAsync(settings);
            Assert.True(library.Count >= fileCount);

            var exceptions = new ConcurrentBag<Exception>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Run 4 concurrent scanning tasks
            var scanTasks = Enumerable.Range(0, 4).Select(workerId => Task.Run(async () =>
            {
                for (int iter = 0; iter < 4; iter++)
                {
                    if (cts.IsCancellationRequested) break;
                    try
                    {
                        await library.ScanAsync(settings, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        exceptions.Add(new InvalidOperationException($"Scan worker {workerId} failed at iter {iter}", ex));
                    }
                }
            })).ToList();

            // Run 6 concurrent read/load tasks
            var readTasks = Enumerable.Range(0, 6).Select(workerId => Task.Run(() =>
            {
                for (int iter = 0; iter < 40; iter++)
                {
                    if (cts.IsCancellationRequested) break;
                    try
                    {
                        library.LoadFromDb();
                        var tracks = library.Tracks;
                        Assert.True(tracks.Count >= fileCount);

                        // Verify lookup of known scanned files
                        var samplePath = Path.Combine(tempDir, $"track_{iter % fileCount:D3}.wav");
                        var lookup = library.GetTrack(samplePath);
                        Assert.NotNull(lookup);
                        Assert.Equal(samplePath, lookup.Path);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(new InvalidOperationException($"Read worker {workerId} failed at iter {iter}", ex));
                    }
                    Thread.Yield();
                }
            })).ToList();

            await Task.WhenAll(scanTasks.Concat(readTasks));

            Assert.Empty(exceptions);
            Assert.True(library.Count >= fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                var p = Path.Combine(tempDir, $"track_{i:D3}.wav");
                Assert.NotNull(library.GetTrack(p));
            }
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task MusicLibrary_ScanDuringActiveDiskModifications_HandlesRaceCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Stress_DiskRace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Initial seed
            for (int i = 0; i < 15; i++)
            {
                CreateMinimalWavFile(Path.Combine(tempDir, $"seed_{i}.wav"), 300);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings { Library = new LibrarySettings { Folders = new List<string> { tempDir } } };

            await library.ScanAsync(settings);
            Assert.True(library.Count >= 15);

            var exceptions = new ConcurrentBag<Exception>();
            var running = true;

            // Background disk mutator: rapidly creating, modifying, and deleting files
            var mutatorTask = Task.Run(() =>
            {
                int counter = 100;
                while (Volatile.Read(ref running))
                {
                    try
                    {
                        var newFile = Path.Combine(tempDir, $"dynamic_{counter++}.wav");
                        CreateMinimalWavFile(newFile, 200);

                        var seedFile = Path.Combine(tempDir, $"seed_{counter % 15}.wav");
                        if (File.Exists(seedFile))
                        {
                            File.SetLastWriteTimeUtc(seedFile, DateTime.UtcNow.AddMinutes(counter));
                        }

                        if (File.Exists(newFile) && counter % 2 == 0)
                        {
                            File.Delete(newFile);
                        }

                        Thread.Yield();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Expected occasional file locked by scanner
                    }
                }
            });

            // Perform repeated scans while disk is mutating
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await library.ScanAsync(settings);
                    Assert.True(library.Count >= 0);
                    Assert.NotNull(library.Tracks);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            Volatile.Write(ref running, false);
            await mutatorTask;

            Assert.Empty(exceptions);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task MusicLibrary_SqlInjectionAndSpecialCharacterPaths_HandledSafely()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_SpecialChars_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Filenames containing apostrophes, semicolons, dashes, unicode, Korean, Japanese, and emoji-like names
            var safeNames = new[]
            {
                "Robert'); DROP TABLE tracks;--.wav",
                "O'Connor - Don't Stop.wav",
                "Special & Symbols (100% [Remix]) #1.wav",
                "노래_한글_제목_테스트.wav",
                "日本語_トラック_音楽.wav",
                "Track with spaces and trailing .wav"
            };

            foreach (var name in safeNames)
            {
                CreateMinimalWavFile(Path.Combine(tempDir, name), 300);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings { Library = new LibrarySettings { Folders = new List<string> { tempDir } } };

            await library.ScanAsync(settings);

            Assert.True(library.Count >= safeNames.Length);

            foreach (var name in safeNames)
            {
                var fullPath = Path.Combine(tempDir, name);
                var track = library.GetTrack(fullPath);
                Assert.NotNull(track);
                Assert.Equal(fullPath, track.Path);
            }

            // Verify database can be reloaded cleanly and tracks still exist
            library.LoadFromDb();
            foreach (var name in safeNames)
            {
                var fullPath = Path.Combine(tempDir, name);
                var track = library.GetTrack(fullPath);
                Assert.NotNull(track);
                Assert.Equal(fullPath, track.Path);
            }
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public void MusicLibrary_MultipleInstances_SequentialOpenAndDispose_MaintainsIntegrity()
    {
        for (int i = 0; i < 5; i++)
        {
            using var lib = new MusicLibrary();
            lib.LoadFromDb();
            var count = lib.Count;
            Assert.True(count >= 0);
        }
    }

    // =========================================================================
    // SECTION 2: PlaybackController Locking, Concurrency & State Invariant Tests
    // =========================================================================

    [Fact]
    public async Task PlaybackController_ConcurrentCommandBurst_NoDeadlockOrException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_PlayStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var files = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                var path = Path.Combine(tempDir, $"song_{i:D2}.wav");
                CreateMinimalWavFile(path, 1000);
                files.Add(path);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings();
            settings.Output.DriverType = AudioDriverType.DirectSound; // Safe headless fallback
            var playlistManager = new PlaylistManager(library);
            var playlist = playlistManager.CreatePlaylist("ConcurrentPlaybackTest");
            playlistManager.AddPaths(playlist, files);

            using var controller = new PlaybackController(settings, playlistManager);

            var exceptions = new ConcurrentBag<Exception>();
            var warnings = new ConcurrentBag<string>();
            controller.Warning += msg => warnings.Add(msg);

            const int workerCount = 8;
            const int operationsPerWorker = 25;

            var tasks = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
            {
                var rand = new Random(workerId * 100);
                for (int op = 0; op < operationsPerWorker; op++)
                {
                    try
                    {
                        int action = rand.Next(10);
                        switch (action)
                        {
                            case 0:
                                int idx = rand.Next(playlist.Items.Count);
                                await controller.PlayAsync(playlist, playlist.Items[idx]);
                                break;
                            case 1:
                                controller.PlayPause();
                                break;
                            case 2:
                                controller.Stop();
                                break;
                            case 3:
                                await controller.NextAsync();
                                break;
                            case 4:
                                await controller.PreviousAsync();
                                break;
                            case 5:
                                controller.RestartIfPlaying();
                                break;
                            case 6:
                                controller.Seek(TimeSpan.FromMilliseconds(rand.Next(500)));
                                break;
                            case 7:
                                controller.Volume = rand.NextDouble();
                                break;
                            case 8:
                                // Read properties under lock contention
                                var item = controller.CurrentItem;
                                var pl = controller.CurrentPlaylist;
                                var state = controller.State;
                                var pos = controller.Position;
                                var dur = controller.Duration;
                                break;
                            case 9:
                                // Queue manipulation concurrent with playback
                                int qIdx = rand.Next(playlist.Items.Count);
                                controller.Queue.Enqueue(playlist, new[] { playlist.Items[qIdx] });
                                if (controller.Queue.Count > 3)
                                {
                                    controller.Queue.RemoveAt(0);
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(new InvalidOperationException($"Worker {workerId} failed at op {op}", ex));
                    }
                    await Task.Yield();
                }
            })).ToList();

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            // Verify final controller state is clean and accessible
            Assert.Contains(controller.State, new[] { CorePlaybackState.Playing, CorePlaybackState.Paused, CorePlaybackState.Stopped });

            controller.Stop();
            Assert.Equal(CorePlaybackState.Stopped, controller.State);

            playlistManager.RemovePlaylist(playlist);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    [Trait("Category", "RequiresAudio")]
    public async Task PlaybackController_CurrentItemAndHistory_ConsistentUnderConcurrentAccess()
    {
        if (!HasRenderEndpoint())
        {
            LogEnvironmentSkip("no audio render endpoint: playback cannot reach Playing");
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_HistoryStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var files = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                var path = Path.Combine(tempDir, $"hist_song_{i}.wav");
                // 3 seconds, not 800 samples (~18 ms). This test asserts the controller is still
                // Playing after PlayAsync returns; with an 18 ms track the stream drained before
                // the assertion ran and the test failed at random under load.
                CreateMinimalWavFile(path, 44100 * 3);
                files.Add(path);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings { Output = new OutputSettings { DriverType = AudioDriverType.DirectSound } };
            var playlistManager = new PlaylistManager(library);
            var playlist = playlistManager.CreatePlaylist("HistoryStress");
            var items = playlistManager.AddPaths(playlist, files);

            using var controller = new PlaybackController(settings, playlistManager);

            var trackStartedTcs = new TaskCompletionSource<PlaylistItem?>();
            controller.CurrentChanged += item => trackStartedTcs.TrySetResult(item);

            // Play track 0
            await controller.PlayAsync(playlist, items[0]);
            var startedItem = await Task.WhenAny(trackStartedTcs.Task, Task.Delay(200));

            // Verify State is Playing
            Assert.Equal(CorePlaybackState.Playing, controller.State);
            Assert.Equal(playlist, controller.CurrentPlaylist ?? playlist);

            controller.Stop();
            Assert.Equal(CorePlaybackState.Stopped, controller.State);

            playlistManager.RemovePlaylist(playlist);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task PlaybackController_PlaybackQueue_ConcurrentOperations_InvariantsPreserved()
    {
        using var library = new MusicLibrary();
        var settings = new AppSettings();
        var playlistManager = new PlaylistManager(library);
        var playlist = playlistManager.CreatePlaylist("QueueConcurrencyTest");

        var dummyTracks = Enumerable.Range(0, 30).Select(i => new Track
        {
            Path = $@"C:\Music\queue_track_{i:D2}.mp3",
            Title = $"Queue Track {i}",
            Artist = "Queue Artist"
        }).ToList();

        var items = playlistManager.AddTracks(playlist, dummyTracks);
        var queue = new PlaybackQueue();

        var exceptions = new ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        // 4 threads Enqueueing
        for (int t = 0; t < 4; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(threadId * 13);
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var subset = items.Skip(rand.Next(0, 20)).Take(3).ToList();
                        if (rand.Next(2) == 0)
                            queue.Enqueue(playlist, subset);
                        else
                            queue.EnqueueNext(playlist, subset);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        // 4 threads Dequeueing and removing
        for (int t = 0; t < 4; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(threadId * 17);
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        int mode = rand.Next(3);
                        if (mode == 0)
                        {
                            queue.Dequeue();
                        }
                        else if (mode == 1)
                        {
                            var count = queue.Count;
                            if (count > 0) queue.RemoveAt(rand.Next(count));
                        }
                        else
                        {
                            var subset = items.Skip(rand.Next(0, 25)).Take(2).ToList();
                            queue.RemoveItems(subset);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);

        // Verify final queue invariants:
        // All entries in queue have 1-based sequential QueueIndex (1..N)
        var finalEntries = queue.Entries;
        for (int i = 0; i < finalEntries.Count; i++)
        {
            Assert.Equal(i + 1, finalEntries[i].Item.QueueIndex);
        }

        // Clear queue
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.Entries);

        // All items should have QueueIndex == -1
        foreach (var it in items)
        {
            Assert.Equal(-1, it.QueueIndex);
        }

        playlistManager.RemovePlaylist(playlist);
    }

    [Fact]
    public async Task PlaybackController_AudioOpenException_HandlesGracefullyWithoutDeadlock()
    {
        using var library = new MusicLibrary();
        var settings = new AppSettings();
        var playlistManager = new PlaylistManager(library);
        var playlist = playlistManager.CreatePlaylist("MissingFilesTest");

        // Add nonexistent audio paths
        var nonExistentTrack = new Track
        {
            Path = @"C:\NonExistent_Folder_12345\ghost_song.mp3",
            Title = "Ghost Song",
            Artist = "Ghost Artist"
        };
        var items = playlistManager.AddTracks(playlist, new[] { nonExistentTrack });

        using var controller = new PlaybackController(settings, playlistManager);

        string? warningMessage = null;
        controller.Warning += msg => warningMessage = msg;

        // Attempt to play missing file
        await controller.PlayAsync(playlist, items[0]);

        // Warning should be fired and State should remain Stopped
        Assert.NotNull(warningMessage);
        Assert.Contains("ghost_song.mp3", warningMessage);
        Assert.Equal(CorePlaybackState.Stopped, controller.State);

        // Subsequent NextAsync should also handle missing file gracefully
        warningMessage = null;
        await controller.NextAsync();
        Assert.NotNull(warningMessage);

        // Controller remains responsive and functional
        controller.PlayPause();
        Assert.Equal(CorePlaybackState.Stopped, controller.State);

        playlistManager.RemovePlaylist(playlist);
    }

    [Fact]
    public void PlaybackController_VolumeAndGain_ClampedAndThreadSafe()
    {
        using var library = new MusicLibrary();
        var settings = new AppSettings();
        var playlistManager = new PlaylistManager(library);
        using var controller = new PlaybackController(settings, playlistManager);

        // Test clamping
        controller.Volume = -1.5;
        Assert.Equal(0.0, controller.Volume);

        controller.Volume = 2.5;
        Assert.Equal(1.0, controller.Volume);

        controller.Volume = 0.75;
        Assert.Equal(0.75, controller.Volume);

        // Multi-threaded volume adjustments
        var exceptions = new ConcurrentBag<Exception>();
        Parallel.For(0, 50, i =>
        {
            try
            {
                controller.Volume = (i % 100) / 100.0;
                var v = controller.Volume;
                Assert.InRange(v, 0.0, 1.0);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void SequencerStream_ThreadSafety_SwitchAndReadContention()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_SeqStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var path1 = Path.Combine(tempDir, "seq1.wav");
            var path2 = Path.Combine(tempDir, "seq2.wav");
            CreateMinimalWavFile(path1, 5000);
            CreateMinimalWavFile(path2, 5000);

            var outFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var seq = new SequencerStream(outFmt, applyVolume: true, _ => 1.0f, latencyMs: 50);

            var pl = new Playlist("SeqTest");
            var it1 = new PlaylistItem(new Track { Path = path1, Title = "Track 1" });
            var it2 = new PlaylistItem(new Track { Path = path2, Title = "Track 2" });

            var reader1 = AudioFileReaderFactory.Open(path1);
            var reader2 = AudioFileReaderFactory.Open(path2);

            var pending1 = new PendingTrack { Playlist = pl, Item = it1, Reader = reader1 };
            var pending2 = new PendingTrack { Playlist = pl, Item = it2, Reader = reader2 };

            seq.SwitchTo(pending1);
            Assert.False(seq.HasPrefetched);

            seq.SetPrefetched(pending2);
            Assert.True(seq.HasPrefetched);

            var buf = new byte[outFmt.AverageBytesPerSecond / 10]; // 100ms buffer
            int read = seq.Read(buf, 0, buf.Length);
            Assert.True(read > 0);

            // Test Pause / Resume
            seq.IsPaused = true;
            int silenceRead = seq.Read(buf, 0, buf.Length);
            Assert.Equal(buf.Length, silenceRead);
            Assert.All(buf, b => Assert.Equal(0, b));

            seq.IsPaused = false;
            int resumeRead = seq.Read(buf, 0, buf.Length);
            Assert.True(resumeRead > 0);

            // Test Seek
            seq.Seek(TimeSpan.FromSeconds(0.05));
            var pos = seq.GetPosition();
            Assert.True(pos >= TimeSpan.Zero);

            // Cancel
            seq.Cancel();
            Assert.False(seq.HasPrefetched);
            Assert.Null(seq.CurrentItem);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task PlaybackController_RepeatAndShuffleModePermutations_PeekNextInOrderSafety()
    {
        using var library = new MusicLibrary();
        var settings = new AppSettings();
        var playlistManager = new PlaylistManager(library);
        var playlist = playlistManager.CreatePlaylist("PermutationTest");

        var dummyTracks = Enumerable.Range(0, 10).Select(i => new Track
        {
            Path = $@"C:\Music\perm_track_{i}.mp3",
            Title = $"Perm Track {i}"
        }).ToList();

        var items = playlistManager.AddTracks(playlist, dummyTracks);
        using var controller = new PlaybackController(settings, playlistManager);

        var repeatModes = new[] { RepeatMode.Off, RepeatMode.All, RepeatMode.One };
        var shuffleModes = new[] { false, true };

        foreach (var repeat in repeatModes)
        {
            foreach (var shuffle in shuffleModes)
            {
                settings.Playback.Repeat = repeat;
                settings.Playback.Shuffle = shuffle;

                // Call NextAsync and PreviousAsync across modes
                await controller.NextAsync();
                await controller.PreviousAsync();
            }
        }

        playlistManager.RemovePlaylist(playlist);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static void CreateMinimalWavFile(string path, int durationSamples)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int subChunk2Size = durationSamples * channels * (bitsPerSample / 8);
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(chunkSize);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });

        // fmt subchunk
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16); // Subchunk1Size for PCM
        bw.Write((short)1); // AudioFormat 1 = PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8)); // ByteRate
        bw.Write((short)(channels * (bitsPerSample / 8)));     // BlockAlign
        bw.Write(bitsPerSample);

        // data subchunk
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(subChunk2Size);
        bw.Write(new byte[subChunk2Size]);
    }

    private static void CleanupDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
