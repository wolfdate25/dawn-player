using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

[Collection("AudioDeviceCollection")]
public class PlaybackConcurrencyAndUiHardeningTests : IDisposable
{
    private readonly MusicLibrary _library;
    private readonly AppSettings _settings;
    private readonly PlaylistManager _playlists;
    private readonly PlaybackController _controller;

    public PlaybackConcurrencyAndUiHardeningTests()
    {
        _library = new MusicLibrary();
        _playlists = new PlaylistManager(_library);
        _settings = new AppSettings();
        _controller = new PlaybackController(_settings, _playlists);
    }

    public void Dispose()
    {
        _controller.Dispose();
        PlaylistItem.UiDispatcher = null;
    }

    [Fact]
    public void PlaylistItem_UiDispatcher_MarshalsPropertyChangedEvents()
    {
        int uiDispatchCount = 0;
        PlaylistItem.UiDispatcher = action =>
        {
            Interlocked.Increment(ref uiDispatchCount);
            action();
        };

        var track = new Track { Path = "C:\\test\\song.mp3", Title = "Test Song", DurationMs = 180000 };
        var item = new PlaylistItem(track);

        int propChangedCount = 0;
        item.PropertyChanged += (_, e) =>
        {
            Interlocked.Increment(ref propChangedCount);
        };

        // Mutate properties
        item.IsPlaying = true;
        item.RemainingTimeText = "-2:30";
        item.QueueIndex = 1;

        Assert.True(uiDispatchCount > 0, "UI dispatcher must be invoked for property changes.");
        Assert.Equal(uiDispatchCount, propChangedCount);
    }

    [Fact]
    public async Task PlaybackController_RapidPlayAsync_CancelsSupersededCommandsSafely()
    {
        var pl = _playlists.CreatePlaylist("RapidTest");
        var t1 = new Track { Path = "C:\\nonexistent\\1.mp3", Title = "Track 1", DurationMs = 120000 };
        var t2 = new Track { Path = "C:\\nonexistent\\2.mp3", Title = "Track 2", DurationMs = 130000 };
        var t3 = new Track { Path = "C:\\nonexistent\\3.mp3", Title = "Track 3", DurationMs = 140000 };

        var items = _playlists.AddTracks(pl, new[] { t1, t2, t3 });

        var warnings = new ConcurrentBag<string>();
        _controller.Warning += warnings.Add;

        // Fire 10 rapid concurrent PlayAsync calls
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var item = items[i % items.Count];
            tasks.Add(_controller.PlayAsync(pl, item));
        }

        await Task.WhenAll(tasks);

        // Every open fails with AudioOpenException, which PlayAsync converts into exactly one
        // Warning and an early return before any session is built.
        Assert.Equal(10, warnings.Count);
        Assert.All(warnings, w => Assert.Contains(".mp3", w));
        Assert.Equal(PlaybackState.Stopped, _controller.State);
        Assert.Null(_controller.CurrentItem);

        // A superseded burst must leave nothing latched: a further command still runs to completion.
        await _controller.PlayAsync(pl, items[0]);
        Assert.Equal(11, warnings.Count);
        Assert.Equal(PlaybackState.Stopped, _controller.State);
        Assert.Null(_controller.CurrentItem);
    }

    [Fact]
    public async Task PlaybackController_RapidNextPrevious_SerializesGracefully()
    {
        var pl = _playlists.CreatePlaylist("NextPrevTest");
        var tracks = Enumerable.Range(1, 5)
            .Select(i => new Track { Path = $"C:\\nonexistent\\{i}.mp3", Title = $"Track {i}", DurationMs = 100000 })
            .ToList();
        _playlists.AddTracks(pl, tracks);

        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(_controller.NextAsync());
            tasks.Add(_controller.PreviousAsync());
        }

        await Task.WhenAll(tasks);

        // Nothing was ever playing and the history is empty, so no advance can resolve a track:
        // every path bottoms out in a Warning without touching the session.
        Assert.Equal(PlaybackState.Stopped, _controller.State);
        Assert.Null(_controller.CurrentItem);
    }

    [Fact]
    public void SeekbarScrubbingCalculator_IsDragging_SuppressesTimerUpdates()
    {
        var calc = new SeekbarScrubbingCalculator();
        var duration = TimeSpan.FromMinutes(4);
        var pos = TimeSpan.FromMinutes(2);

        // Before drag: slider progress calculated normally
        var p1 = SeekbarScrubbingCalculator.CalculateSliderProgress(pos, duration, 100, isDragging: false);
        Assert.Equal(120.0, p1.NewValue);

        // Begin drag
        calc.BeginDrag();
        Assert.True(calc.IsDragging);

        // When dragging: progress value should not override user scrubbing
        var p2 = SeekbarScrubbingCalculator.CalculateSliderProgress(pos, duration, 100, isDragging: calc.IsDragging);
        Assert.Equal(120.0, p2.NewValue);

        // Complete drag to 180s
        var target = calc.CompleteDrag(180, duration);
        Assert.False(calc.IsDragging);
        Assert.NotNull(target);
        Assert.Equal(TimeSpan.FromSeconds(180), target.Value);
    }
}
