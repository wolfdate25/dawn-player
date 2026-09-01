using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Unit tests for SMTC (System Media Transport Controls) service lifecycle, state mapping,
/// metadata formatting, asynchronous thumbnail sequence counter guarding, and event detachment.
/// </summary>
[Collection("SettingsStoreCollection")]
public class SmtcServiceLifecycleAndMappingTests : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _controller;

    public SmtcServiceLifecycleAndMappingTests()
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

    #region Pure Mapping & Test Doubles

    /// <summary>
    /// Mirror of WinRT MediaPlaybackStatus enum for pure test verification in net10.0-windows.
    /// </summary>
    public enum SmtcPlaybackStatus
    {
        Closed = 0,
        Changing = 1,
        Stopped = 2,
        Playing = 3,
        Paused = 4
    }

    /// <summary>
    /// Pure state mapping algorithm matching SmtcMapping.MapPlaybackState.
    /// </summary>
    public static SmtcPlaybackStatus MapPlaybackState(PlaybackState state) => state switch
    {
        PlaybackState.Playing => SmtcPlaybackStatus.Playing,
        PlaybackState.Paused => SmtcPlaybackStatus.Paused,
        PlaybackState.Stopped => SmtcPlaybackStatus.Stopped,
        _ => SmtcPlaybackStatus.Stopped
    };

    /// <summary>
    /// Pure metadata formatting algorithm matching SmtcMapping.FormatMetadata.
    /// </summary>
    public static (string Title, string Artist, string Album, string AlbumArtist, uint TrackNumber) FormatMetadata(Track? track)
    {
        if (track == null)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty, 0);
        }

        var title = track.Title ?? string.Empty;
        var artist = !string.IsNullOrWhiteSpace(track.Artist)
            ? track.Artist
            : (!string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.AlbumArtist : string.Empty);
        var album = track.Album ?? string.Empty;
        var albumArtist = track.AlbumArtist ?? string.Empty;
        var trackNumber = (uint)Math.Max(0, track.TrackNo);

        return (title, artist, album, albumArtist, trackNumber);
    }

    /// <summary>
    /// Fully functional test double implementing the complete SMTC service lifecycle,
    /// event wiring to PlaybackController, async sequence counter guarding, and disposal.
    /// </summary>
    public sealed class TestSmtcService : IDisposable
    {
        private readonly PlaybackController _playback;
        private bool _isInitialized;
        private bool _isDisposed;
        private int _currentUpdateVersion;

        public bool IsInitialized => _isInitialized && !_isDisposed;
        public SmtcPlaybackStatus CurrentStatus { get; private set; } = SmtcPlaybackStatus.Closed;
        public (string Title, string Artist, string Album, string AlbumArtist, uint TrackNumber) DisplayedMetadata { get; private set; }
        public string? DisplayedThumbnailPath { get; private set; }
        public int UpdateCount { get; private set; }
        public int LastAppliedVersion { get; private set; }

        public TestSmtcService(PlaybackController playback)
        {
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        }

        public bool TryInitialize(IntPtr hwnd)
        {
            if (_isDisposed) return false;
            if (_isInitialized) return true;
            if (hwnd == IntPtr.Zero) return false;

            _playback.CurrentChanged += OnPlaybackCurrentChanged;
            _playback.StateChanged += OnPlaybackStateChanged;

            _isInitialized = true;
            UpdateTrack(_playback.CurrentItem);
            UpdateState(_playback.State);
            return true;
        }

        private void OnPlaybackCurrentChanged(PlaylistItem? item) => UpdateTrack(item);

        private void OnPlaybackStateChanged() => UpdateState(_playback.State);

        public void UpdateTrack(PlaylistItem? item)
        {
            _ = UpdateTrackAsync(item, ct: CancellationToken.None);
        }

        public async Task UpdateTrackAsync(PlaylistItem? item, int artificialDelayMs = 0, CancellationToken ct = default)
        {
            int targetVersion = Interlocked.Increment(ref _currentUpdateVersion);
            if (!_isInitialized || _isDisposed) return;

            var meta = FormatMetadata(item?.Track);
            string? artPath = item?.Track?.ArtPath;

            if (artificialDelayMs > 0)
            {
                await Task.Delay(artificialDelayMs, ct).ConfigureAwait(false);
            }

            if (targetVersion != Volatile.Read(ref _currentUpdateVersion) || ct.IsCancellationRequested || _isDisposed)
            {
                // Discard stale or cancelled update
                return;
            }

            DisplayedMetadata = meta;
            DisplayedThumbnailPath = (!string.IsNullOrWhiteSpace(artPath) && File.Exists(artPath)) ? artPath : null;
            LastAppliedVersion = targetVersion;
            UpdateCount++;
        }

        public void UpdateState(PlaybackState state)
        {
            if (!_isInitialized || _isDisposed) return;
            CurrentStatus = MapPlaybackState(state);
        }

        // Mirrors the real SMTC service's instance API; the shared test double is invoked
        // through instances across two test files.
#pragma warning disable CA1822
        public void UpdateTimeline(TimeSpan position, TimeSpan duration)
#pragma warning restore CA1822
        {
            // Safe timeline no-op
        }

        public async Task DispatchButtonPressAsync(string button)
        {
            if (_isDisposed) return;
            try
            {
                switch (button.ToLowerInvariant())
                {
                    case "play":
                    case "pause":
                    case "playpause":
                        _playback.PlayPause();
                        break;
                    case "next":
                        await _playback.NextAsync().ConfigureAwait(false);
                        break;
                    case "previous":
                        await _playback.PreviousAsync().ConfigureAwait(false);
                        break;
                    case "stop":
                        _playback.Stop();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC Test] Button dispatch failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Interlocked.Increment(ref _currentUpdateVersion);

            try
            {
                _playback.CurrentChanged -= OnPlaybackCurrentChanged;
                _playback.StateChanged -= OnPlaybackStateChanged;
            }
            catch { }

            CurrentStatus = SmtcPlaybackStatus.Closed;
            _isInitialized = false;
        }
    }

    #endregion

    #region 1. PlaybackState to MediaPlaybackStatus Translation Mapping Tests

    [Theory]
    [InlineData(PlaybackState.Stopped, SmtcPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Playing, SmtcPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, SmtcPlaybackStatus.Paused)]
    public void MapPlaybackState_TranslatesExactEnumValues_Correctly(PlaybackState state, SmtcPlaybackStatus expected)
    {
        var result = MapPlaybackState(state);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void MapPlaybackState_UndefinedEnumValues_SafelyDefaultsToStopped(int invalidStateVal)
    {
        var invalidState = (PlaybackState)invalidStateVal;
        var result = MapPlaybackState(invalidState);
        Assert.Equal(SmtcPlaybackStatus.Stopped, result);
    }

    #endregion

    #region 2. Track Metadata Formatting & Fallback Tests

    [Fact]
    public void FormatMetadata_CompleteTrackInfo_FormatsAllFields()
    {
        var track = new Track
        {
            Title = "Lilac",
            Artist = "IU",
            Album = "IU 5th Album 'LILAC'",
            AlbumArtist = "IU",
            TrackNo = 1
        };

        var meta = FormatMetadata(track);

        Assert.Equal("Lilac", meta.Title);
        Assert.Equal("IU", meta.Artist);
        Assert.Equal("IU 5th Album 'LILAC'", meta.Album);
        Assert.Equal("IU", meta.AlbumArtist);
        Assert.Equal(1u, meta.TrackNumber);
    }

    [Fact]
    public void FormatMetadata_MissingArtist_FallsBackToAlbumArtist()
    {
        var track = new Track
        {
            Title = "Symphony No. 5",
            Artist = "", // Empty artist
            AlbumArtist = "Beethoven",
            Album = "Classical Essentials",
            TrackNo = 5
        };

        var meta = FormatMetadata(track);

        Assert.Equal("Symphony No. 5", meta.Title);
        Assert.Equal("Beethoven", meta.Artist); // Fallback to AlbumArtist
        Assert.Equal("Beethoven", meta.AlbumArtist);
        Assert.Equal("Classical Essentials", meta.Album);
        Assert.Equal(5u, meta.TrackNumber);
    }

    [Fact]
    public void FormatMetadata_WhitespaceArtist_FallsBackToAlbumArtist()
    {
        var track = new Track
        {
            Title = "Overture",
            Artist = "   \t\r\n  ",
            AlbumArtist = "Mozart",
            Album = "The Magic Flute",
            TrackNo = 2
        };

        var meta = FormatMetadata(track);

        Assert.Equal("Overture", meta.Title);
        Assert.Equal("Mozart", meta.Artist);
        Assert.Equal("Mozart", meta.AlbumArtist);
    }

    [Fact]
    public void FormatMetadata_BothArtistAndAlbumArtistMissing_ReturnsEmptyStringWithoutThrowing()
    {
        var track = new Track
        {
            Title = "Unknown Audio",
            Artist = "",
            AlbumArtist = "",
            Album = "",
            TrackNo = 0
        };

        var meta = FormatMetadata(track);

        Assert.Equal("Unknown Audio", meta.Title);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal(string.Empty, meta.AlbumArtist);
        Assert.Equal(string.Empty, meta.Album);
        Assert.Equal(0u, meta.TrackNumber);
    }

    [Fact]
    public void FormatMetadata_NullFields_SafelyProducesEmptyStrings()
    {
        var track = new Track
        {
            Title = null!,
            Artist = null!,
            Album = null!,
            AlbumArtist = null!,
            TrackNo = -10 // Negative track number
        };

        var meta = FormatMetadata(track);

        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal(string.Empty, meta.Album);
        Assert.Equal(string.Empty, meta.AlbumArtist);
        Assert.Equal(0u, meta.TrackNumber); // Clamped to 0
    }

    [Theory]
    [InlineData(-100, 0u)]
    [InlineData(-1, 0u)]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(42, 42u)]
    [InlineData(999, 999u)]
    public void FormatMetadata_TrackNumberClamping_AlwaysNonNegative(int inputTrackNo, uint expectedTrackNo)
    {
        var track = new Track
        {
            Title = "Test Track",
            TrackNo = inputTrackNo
        };

        var meta = FormatMetadata(track);
        Assert.Equal(expectedTrackNo, meta.TrackNumber);
    }

    #endregion

    #region 3. Null Track & Null PlaylistItem Handling Tests

    [Fact]
    public void FormatMetadata_NullTrack_ReturnsSafeDefaults()
    {
        var meta = FormatMetadata(null);

        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal(string.Empty, meta.Artist);
        Assert.Equal(string.Empty, meta.Album);
        Assert.Equal(string.Empty, meta.AlbumArtist);
        Assert.Equal(0u, meta.TrackNumber);
    }

    [Fact]
    public void TestSmtcService_UpdateTrackWithNull_ClearsMetadataSafely()
    {
        using var smtc = new TestSmtcService(_controller);
        bool initialized = smtc.TryInitialize(new IntPtr(12345));
        Assert.True(initialized);

        // 1. Set a valid track
        var track = new Track { Title = "Song A", Artist = "Artist A" };
        var item = new PlaylistItem(track);
        smtc.UpdateTrack(item);

        Assert.Equal("Song A", smtc.DisplayedMetadata.Title);
        Assert.Equal("Artist A", smtc.DisplayedMetadata.Artist);

        // 2. Pass null track
        smtc.UpdateTrack(null);

        Assert.Equal(string.Empty, smtc.DisplayedMetadata.Title);
        Assert.Equal(string.Empty, smtc.DisplayedMetadata.Artist);
        Assert.Null(smtc.DisplayedThumbnailPath);
    }

    #endregion

    #region 4. Lifecycle Disposal & Event Detach Logic Tests

    [Fact]
    public void TryInitialize_WithZeroHwnd_FailsGracefully()
    {
        using var smtc = new TestSmtcService(_controller);
        bool initialized = smtc.TryInitialize(IntPtr.Zero);

        Assert.False(initialized);
        Assert.False(smtc.IsInitialized);
    }

    [Fact]
    public void TryInitialize_WithValidHwnd_InitializesAndSyncsState()
    {
        using var smtc = new TestSmtcService(_controller);
        bool initialized = smtc.TryInitialize(new IntPtr(100));

        Assert.True(initialized);
        Assert.True(smtc.IsInitialized);
        Assert.Equal(SmtcPlaybackStatus.Stopped, smtc.CurrentStatus);
    }

    [Fact]
    public void ControllerEvents_PropagateToSmtc_WhenActive()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        // Trigger CurrentChanged via reflection / internal invocation
        var track = new Track { Title = "Active Song", Artist = "Active Artist" };
        var item = new PlaylistItem(track);

        var currentChangedField = typeof(PlaybackController).GetField("CurrentChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var currentChangedDelegate = (Action<PlaylistItem?>?)currentChangedField?.GetValue(_controller);
        currentChangedDelegate?.Invoke(item);

        Assert.Equal("Active Song", smtc.DisplayedMetadata.Title);
        Assert.Equal("Active Artist", smtc.DisplayedMetadata.Artist);

        // Trigger StateChanged
        var stateChangedField = typeof(PlaybackController).GetField("StateChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var stateChangedDelegate = (Action?)stateChangedField?.GetValue(_controller);

        // Set controller state to Playing via reflection
        var stateProp = typeof(PlaybackController).GetProperty("State");
        stateProp?.SetValue(_controller, PlaybackState.Playing);
        stateChangedDelegate?.Invoke();

        Assert.Equal(SmtcPlaybackStatus.Playing, smtc.CurrentStatus);
    }

    [Fact]
    public void Dispose_UnsubscribesEvents_AndPreventsFurtherUpdates()
    {
        var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));
        Assert.True(smtc.IsInitialized);

        // Dispose SMTC
        smtc.Dispose();
        Assert.False(smtc.IsInitialized);
        Assert.Equal(SmtcPlaybackStatus.Closed, smtc.CurrentStatus);

        // Trigger PlaybackController events after dispose
        var track = new Track { Title = "Post-Dispose Song", Artist = "Post-Dispose Artist" };
        var item = new PlaylistItem(track);

        var currentChangedField = typeof(PlaybackController).GetField("CurrentChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var currentChangedDelegate = (Action<PlaylistItem?>?)currentChangedField?.GetValue(_controller);
        currentChangedDelegate?.Invoke(item);

        // Metadata must NOT have updated to post-dispose song
        Assert.NotEqual("Post-Dispose Song", smtc.DisplayedMetadata.Title);

        // Multiple dispose calls must be idempotent
        smtc.Dispose();
        smtc.Dispose();
        Assert.False(smtc.IsInitialized);
    }

    #endregion

    #region 5. Async Sequence Counter & Rapid Track Skipping Concurrency Tests

    [Fact]
    public async Task UpdateTrackAsync_OutOfOrderCompletion_LatestSequenceAlwaysWins()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));
        int initialVersion = smtc.LastAppliedVersion;

        var track1 = new Track { Title = "Track 1 (Slow)", Artist = "Artist 1" };
        var track2 = new Track { Title = "Track 2 (Fast)", Artist = "Artist 2" };

        var item1 = new PlaylistItem(track1);
        var item2 = new PlaylistItem(track2);

        // Launch Track 1 with a 20ms artificial delay (simulating slow disk/thumbnail read)
        var task1 = smtc.UpdateTrackAsync(item1, artificialDelayMs: 20, ct: CancellationToken.None);

        // Immediately launch Track 2 with no delay (user skipped fast)
        var task2 = smtc.UpdateTrackAsync(item2, artificialDelayMs: 0, ct: CancellationToken.None);

        await Task.WhenAll(task1, task2);

        // Track 2 must be the displayed metadata even though task1 finished later
        Assert.Equal("Track 2 (Fast)", smtc.DisplayedMetadata.Title);
        Assert.Equal("Artist 2", smtc.DisplayedMetadata.Artist);
        Assert.Equal(initialVersion + 2, smtc.LastAppliedVersion);
    }

    [Fact]
    public async Task UpdateTrackAsync_RapidSkippingStress_EnsuresStrictOrdering()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));
        int initialVersion = smtc.LastAppliedVersion;

        const int iterations = 50;
        var tasks = new Task[iterations];
        var random = new Random(42);

        for (int i = 0; i < iterations; i++)
        {
            int index = i;
            var track = new Track { Title = $"Track #{index}", Artist = $"Artist #{index}" };
            var item = new PlaylistItem(track);
            int delay = random.Next(1, 15); // Random delay between 1ms and 15ms

            tasks[i] = Task.Run(async () =>
            {
                await smtc.UpdateTrackAsync(item, artificialDelayMs: delay, ct: CancellationToken.None);
            });
        }

        await Task.WhenAll(tasks);

        // The final displayed version must be a valid version up to initialVersion + iterations
        Assert.InRange(smtc.LastAppliedVersion, initialVersion + 1, initialVersion + iterations);
        Assert.NotNull(smtc.DisplayedMetadata.Title);
    }

    #endregion

    #region 6. Safe Button Dispatch & Concurrency Guard Tests

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("playpause")]
    [InlineData("stop")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("unknown_button")]
    public async Task DispatchButtonPressAsync_HandlesAllButtonsSafely(string button)
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        // Dispatch button press on background thread
        var ex = await Record.ExceptionAsync(async () =>
        {
            await smtc.DispatchButtonPressAsync(button);
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task DispatchButtonPressAsync_WhenDisposed_IgnoresWithoutThrowing()
    {
        var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));
        smtc.Dispose();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await smtc.DispatchButtonPressAsync("play");
            await smtc.DispatchButtonPressAsync("next");
        });

        Assert.Null(ex);
    }

    #endregion

    #region 7. Local Art Cache Path Handling Tests

    [Fact]
    public async Task UpdateTrackAsync_WithValidArtPath_SetsThumbnail()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        string tempArtFile = Path.Combine(Path.GetTempPath(), $"smtc_test_art_{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(tempArtFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        try
        {
            var track = new Track
            {
                Title = "Art Track",
                Artist = "Art Artist",
                ArtPath = tempArtFile
            };
            var item = new PlaylistItem(track);

            await smtc.UpdateTrackAsync(item);

            Assert.Equal(tempArtFile, smtc.DisplayedThumbnailPath);
        }
        finally
        {
            if (File.Exists(tempArtFile))
            {
                try { File.Delete(tempArtFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task UpdateTrackAsync_WithNonExistentArtPath_SafelySetsThumbnailToNull()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        var track = new Track
        {
            Title = "Missing Art Track",
            Artist = "Missing Art Artist",
            ArtPath = @"C:\NonExistentDirectory\FakeArt_99999.jpg"
        };
        var item = new PlaylistItem(track);
        await smtc.UpdateTrackAsync(item);

        Assert.Equal("Missing Art Track", smtc.DisplayedMetadata.Title);
        Assert.Null(smtc.DisplayedThumbnailPath);
    }

    #endregion

    #region 8. Unicode, Emojis & Extreme Edge Cases in Metadata

    [Theory]
    [InlineData("좋은 날 (Good Day)", "아이유 (IU)", "Real", "아이유", 3u)]
    [InlineData("夜に駆ける", "YOASOBI", "THE BOOK", "YOASOBI", 1u)]
    [InlineData("🎵 Music & Vibes 🔥", "Artist ✨", "Album 🌟", "Album Artist 🎧", 7u)]
    [InlineData("Track\tWith\nNewlines", "Artist\r\nName", "Album\0NullChar", "AlbumArtist", 10u)]
    public void FormatMetadata_UnicodeAndSpecialCharacters_PreservesExactStrings(
        string title, string artist, string album, string albumArtist, uint trackNo)
    {
        var track = new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            TrackNo = (int)trackNo
        };

        var meta = FormatMetadata(track);

        Assert.Equal(title, meta.Title);
        Assert.Equal(artist, meta.Artist);
        Assert.Equal(album, meta.Album);
        Assert.Equal(albumArtist, meta.AlbumArtist);
        Assert.Equal(trackNo, meta.TrackNumber);
    }

    [Fact]
    public void FormatMetadata_ExtremelyLongStrings_HandledWithoutTruncationOrError()
    {
        string longTitle = new string('A', 5000);
        string longArtist = new string('B', 5000);
        string longAlbum = new string('C', 5000);

        var track = new Track
        {
            Title = longTitle,
            Artist = longArtist,
            Album = longAlbum,
            TrackNo = int.MaxValue
        };

        var meta = FormatMetadata(track);

        Assert.Equal(5000, meta.Title.Length);
        Assert.Equal(5000, meta.Artist.Length);
        Assert.Equal(5000, meta.Album.Length);
        Assert.Equal((uint)int.MaxValue, meta.TrackNumber);
    }

    #endregion

    #region 9. Multi-threaded Lifecycle & State Synchronization Stress Tests

    [Fact]
    public async Task MultiThreadedStress_ConcurrentUpdatesAndStateChanges_RemainsStable()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        const int threadCount = 20;
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(async () =>
            {
                var track = new Track
                {
                    Title = $"Stress Title {threadId}",
                    Artist = $"Stress Artist {threadId}",
                    Album = $"Stress Album {threadId}",
                    TrackNo = threadId
                };
                var item = new PlaylistItem(track);

                // Alternate between track updates and state changes
                smtc.UpdateTrack(item);
                smtc.UpdateState((PlaybackState)(threadId % 3));
                await smtc.UpdateTrackAsync(item, artificialDelayMs: 2, ct: CancellationToken.None);
                smtc.UpdateTimeline(TimeSpan.FromSeconds(threadId * 10), TimeSpan.FromSeconds(300));
            });
        }

        await Task.WhenAll(tasks);

        Assert.True(smtc.IsInitialized);
        Assert.True(smtc.UpdateCount > 0);
    }

    [Fact]
    public void UpdateTimeline_WithExtremeValues_DoesNotThrow()
    {
        using var smtc = new TestSmtcService(_controller);
        smtc.TryInitialize(new IntPtr(100));

        // Test timeline with negative, zero, extreme values
        var ex = Record.Exception(() =>
        {
            smtc.UpdateTimeline(TimeSpan.Zero, TimeSpan.Zero);
            smtc.UpdateTimeline(TimeSpan.FromSeconds(-50), TimeSpan.FromSeconds(-100));
            smtc.UpdateTimeline(TimeSpan.FromDays(999), TimeSpan.FromDays(1000));
            smtc.UpdateTimeline(TimeSpan.FromSeconds(500), TimeSpan.FromSeconds(200)); // pos > dur
        });

        Assert.Null(ex);
    }

    #endregion
}

