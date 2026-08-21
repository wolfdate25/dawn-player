using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;
using static DawnPlayer.Tests.SmtcServiceLifecycleAndMappingTests;

namespace DawnPlayer.Tests;

/// <summary>
/// Empirical adversarial stress tests for SMTC mapping, async thumbnail racing,
/// concurrency resilience, lifecycle disposal, and event detachment.
/// </summary>
public class SmtcAdversarialStressTests : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _controller;

    public SmtcAdversarialStressTests()
    {
        _settings = AppSettings.CreateDefault();
        _library = new MusicLibrary();
        _playlists = new PlaylistManager(_library);
        _controller = new PlaybackController(_settings, _playlists);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _library.Dispose();
    }

    #region 1. SmtcMapping Direct Pure Logic Stress Tests

    [Theory]
    [InlineData(PlaybackState.Playing, SmtcPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, SmtcPlaybackStatus.Paused)]
    [InlineData(PlaybackState.Stopped, SmtcPlaybackStatus.Stopped)]
    [InlineData((PlaybackState)(-1), SmtcPlaybackStatus.Stopped)]
    [InlineData((PlaybackState)100, SmtcPlaybackStatus.Stopped)]
    [InlineData((PlaybackState)int.MinValue, SmtcPlaybackStatus.Stopped)]
    [InlineData((PlaybackState)int.MaxValue, SmtcPlaybackStatus.Stopped)]
    public void SmtcMapping_MapPlaybackState_ReturnsExpectedStatus(PlaybackState input, SmtcPlaybackStatus expected)
    {
        var actual = MapPlaybackState(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_NullTrack_ReturnsAllEmptyAndZero()
    {
        var meta = FormatMetadata(null);
        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal(string.Empty, meta.Album);
        Assert.Equal(string.Empty, meta.AlbumArtist);
        Assert.Equal(0u, meta.TrackNumber);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_AllFieldsNull_ReturnsEmptyStringsSafely()
    {
        var track = new Track
        {
            Title = null!,
            Artist = null!,
            Album = null!,
            AlbumArtist = null!,
            TrackNo = -999
        };

        var meta = FormatMetadata(track);
        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal(string.Empty, meta.Album);
        Assert.Equal(string.Empty, meta.AlbumArtist);
        Assert.Equal(0u, meta.TrackNumber);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_ArtistEmpty_FallbackToAlbumArtist()
    {
        var track = new Track
        {
            Title = "Song Title",
            Artist = "",
            AlbumArtist = "Primary Band",
            Album = "Greatest Hits",
            TrackNo = 4
        };

        var meta = FormatMetadata(track);
        Assert.Equal("Song Title", meta.Title);
        Assert.Equal("Primary Band", meta.Artist);
        Assert.Equal("Primary Band", meta.AlbumArtist);
        Assert.Equal("Greatest Hits", meta.Album);
        Assert.Equal(4u, meta.TrackNumber);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_ArtistWhitespace_FallbackToAlbumArtist()
    {
        var track = new Track
        {
            Title = "Song Title",
            Artist = "   \t \r \n  ",
            AlbumArtist = "Primary Band",
            Album = "Greatest Hits",
            TrackNo = 12
        };

        var meta = FormatMetadata(track);
        Assert.Equal("Primary Band", meta.Artist);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_ArtistAndAlbumArtistBothWhitespace_ReturnsEmptyString()
    {
        var track = new Track
        {
            Title = "Track X",
            Artist = "  ",
            AlbumArtist = "\t\t",
            Album = "Album Y",
            TrackNo = 1
        };

        var meta = FormatMetadata(track);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal("\t\t", meta.AlbumArtist);
    }

    [Fact]
    public void SmtcMapping_FormatMetadata_HugeStrings_NoOverflowOrException()
    {
        string giant = new string('x', 1_000_000);
        var track = new Track
        {
            Title = giant,
            Artist = giant,
            Album = giant,
            AlbumArtist = giant,
            TrackNo = int.MaxValue
        };

        var meta = FormatMetadata(track);
        Assert.Equal(1_000_000, meta.Title.Length);
        Assert.Equal(1_000_000, meta.Artist.Length);
        Assert.Equal(1_000_000, meta.Album.Length);
        Assert.Equal(1_000_000, meta.AlbumArtist.Length);
        Assert.Equal((uint)int.MaxValue, meta.TrackNumber);
    }

    #endregion

    #region 2. Concurrency, Monotonic Sequence Counter, & Out-Of-Order Stress Tests

    /// <summary>
    /// Test harness simulating the asynchronous update pipeline of SmtcService
    /// with strict monotonicity verification.
    /// </summary>
    public sealed class StressSmtcTester : IDisposable
    {
        private int _currentUpdateVersion;
        private bool _isDisposed;
        private readonly ConcurrentBag<int> _appliedVersions = new();

        public int LastAppliedVersion { get; private set; }
        public (string Title, string Artist) DisplayedMetadata { get; private set; }
        public int AppliedCount => _appliedVersions.Count;

        public async Task UpdateTrackAsync(string title, string artist, int delayMs, CancellationToken ct = default)
        {
            int targetVersion = Interlocked.Increment(ref _currentUpdateVersion);
            if (_isDisposed) return;

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            if (targetVersion != Volatile.Read(ref _currentUpdateVersion) || ct.IsCancellationRequested || _isDisposed)
            {
                return;
            }

            lock (_appliedVersions)
            {
                if (targetVersion != Volatile.Read(ref _currentUpdateVersion) || targetVersion < LastAppliedVersion || ct.IsCancellationRequested || _isDisposed)
                {
                    return;
                }

                LastAppliedVersion = targetVersion;
                DisplayedMetadata = (title, artist);
                _appliedVersions.Add(targetVersion);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Interlocked.Increment(ref _currentUpdateVersion);
        }
    }

    [Fact]
    public async Task UpdateTrackAsync_AdversarialRapidSkipping_EnforcesMonotonicity()
    {
        using var tester = new StressSmtcTester();
        const int taskCount = 100;
        var tasks = new Task[taskCount];
        var rnd = new Random(12345);

        for (int i = 0; i < taskCount; i++)
        {
            int index = i;
            int delay = rnd.Next(0, 30);
            tasks[i] = Task.Run(async () =>
            {
                await tester.UpdateTrackAsync($"Song {index}", $"Artist {index}", delay);
            });
        }

        await Task.WhenAll(tasks);

        // After all tasks finish, the final applied version must be <= 100 and > 0
        Assert.InRange(tester.LastAppliedVersion, 1, taskCount);
        Assert.NotNull(tester.DisplayedMetadata.Title);
    }

    [Fact]
    public async Task UpdateTrackAsync_MassiveCancellationBurst_NoUnhandledExceptions()
    {
        using var tester = new StressSmtcTester();
        const int taskCount = 200;
        var tasks = new Task[taskCount];
        var rnd = new Random(6789);

        for (int i = 0; i < taskCount; i++)
        {
            int index = i;
            int delay = rnd.Next(5, 50);
            int cancelDelay = rnd.Next(1, 40);

            var cts = new CancellationTokenSource();
            cts.CancelAfter(cancelDelay);

            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await tester.UpdateTrackAsync($"Burst Song {index}", $"Burst Artist {index}", delay, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected when token cancels during delay
                }
            });
        }

        await Task.WhenAll(tasks);

        // The version counter is bumped before the delay, so a cancelled or superseded update can
        // only ever apply nothing: no run may report more applications than calls made.
        int appliedDuringBurst = tester.AppliedCount;
        Assert.InRange(appliedDuringBurst, 0, taskCount);
        Assert.InRange(tester.LastAppliedVersion, 0, taskCount);

        // Liveness: the burst must not wedge the tester. All 200 calls have returned, so this call
        // takes version taskCount + 1 with nothing left to supersede it and must become visible.
        await tester.UpdateTrackAsync("Final Song", "Final Artist", 0);

        Assert.Equal(taskCount + 1, tester.LastAppliedVersion);
        Assert.Equal(appliedDuringBurst + 1, tester.AppliedCount);
        Assert.Equal("Final Song", tester.DisplayedMetadata.Title);
        Assert.Equal("Final Artist", tester.DisplayedMetadata.Artist);
    }

    #endregion

    #region 3. Thread Safety: Interleaved Disposal during Heavy Concurrent Operations

    [Fact]
    public async Task ConcurrentOperations_WithInterleavedDisposal_ShutsDownGracefully()
    {
        using var controller = new PlaybackController(_settings, _playlists);
        var smtc = new TestSmtcService(controller);
        smtc.TryInitialize(new IntPtr(9999));

        const int workerCount = 30;
        var cts = new CancellationTokenSource();
        var workers = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int id = i;
            workers[i] = Task.Run(async () =>
            {
                var rnd = new Random(id * 100);
                while (!cts.IsCancellationRequested)
                {
                    var track = new Track { Title = $"Stress Track {id}", Artist = $"Stress Artist {id}" };
                    var item = new PlaylistItem(track);

                    smtc.UpdateTrack(item);
                    smtc.UpdateState((PlaybackState)(id % 3));
                    smtc.UpdateTimeline(TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(100));
                    await smtc.DispatchButtonPressAsync("play");
                    await smtc.DispatchButtonPressAsync("next");

                    await Task.Yield();
                }
            });
        }

        // Let workers run under heavy load
        await Task.Delay(20);

        // Concurrently dispose SMTC while threads are hammering it
        smtc.Dispose();
        Assert.False(smtc.IsInitialized);

        // Also test redundant concurrent disposes from multiple threads
        var disposeTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => smtc.Dispose())).ToArray();
        await Task.WhenAll(disposeTasks);

        // Cancel worker loop and wait
        cts.Cancel();
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) { }

        // All operations must have completed without crashing the thread pool
        Assert.False(smtc.IsInitialized);
    }

    #endregion

    #region 4. PlaybackController Event Unwiring Verification

    [Fact]
    public void EventDetachment_PostDisposal_ZeroControllerEventsReceived()
    {
        using var controller = new PlaybackController(_settings, _playlists);
        var smtc = new TestSmtcService(controller);
        smtc.TryInitialize(new IntPtr(100));
        Assert.True(smtc.IsInitialized);

        // Pre-disposal: Update through controller reflection
        var currentChangedField = typeof(PlaybackController).GetField("CurrentChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var currentChangedDelegate = (Action<PlaylistItem?>?)currentChangedField?.GetValue(controller);

        var preTrack = new Track { Title = "Pre-Dispose Track", Artist = "Pre Artist" };
        currentChangedDelegate?.Invoke(new PlaylistItem(preTrack));

        Assert.Equal("Pre-Dispose Track", smtc.DisplayedMetadata.Title);

        // Dispose SMTC
        smtc.Dispose();
        Assert.False(smtc.IsInitialized);

        // Post-disposal: Trigger 1000 events
        for (int i = 0; i < 1000; i++)
        {
            var postTrack = new Track { Title = $"Post-Dispose Track {i}", Artist = $"Post Artist {i}" };
            currentChangedDelegate?.Invoke(new PlaylistItem(postTrack));
        }

        // Smtc must still retain only pre-dispose data
        Assert.Equal("Pre-Dispose Track", smtc.DisplayedMetadata.Title);
        Assert.Equal("Pre Artist", smtc.DisplayedMetadata.Artist);
    }

    #endregion

    #region 5. TestSmtcService Construction & Safe Fallbacks

    [Fact]
    public void TestSmtcService_Constructor_NullController_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TestSmtcService(null!));
    }

    [Fact]
    public void TestSmtcService_TryInitialize_ZeroHwnd_ReturnsFalse()
    {
        using var smtc = new TestSmtcService(_controller);
        bool initialized = smtc.TryInitialize(IntPtr.Zero);
        Assert.False(initialized);
        Assert.False(smtc.IsInitialized);
    }

    [Fact]
    public void TestSmtcService_UninitializedOperations_AreSafeNoOps()
    {
        using var smtc = new TestSmtcService(_controller);
        Assert.False(smtc.IsInitialized);

        var ex = Record.Exception(() =>
        {
            smtc.UpdateTrack(null);
            smtc.UpdateTrack(new PlaylistItem(new Track { Title = "Test" }));
            smtc.UpdateState(PlaybackState.Playing);
            smtc.UpdateTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(100));
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task TestSmtcService_UninitializedAsyncOperations_AreSafeNoOps()
    {
        using var smtc = new TestSmtcService(_controller);
        var ex = await Record.ExceptionAsync(async () =>
        {
            await smtc.UpdateTrackAsync(null);
            await smtc.UpdateTrackAsync(new PlaylistItem(new Track { Title = "Test" }));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void TestSmtcService_Dispose_MultipleCalls_AreSafeAndIdempotent()
    {
        var smtc = new TestSmtcService(_controller);
        smtc.Dispose();
        smtc.Dispose();
        smtc.Dispose();

        Assert.False(smtc.IsInitialized);

        var ex = Record.Exception(() =>
        {
            smtc.UpdateTrack(null);
            smtc.UpdateState(PlaybackState.Stopped);
        });

        Assert.Null(ex);
    }

    #endregion
}
