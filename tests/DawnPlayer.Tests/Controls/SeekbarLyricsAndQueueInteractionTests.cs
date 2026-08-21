using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Controls;

[Collection("PlaylistConcurrencyCollection")]
public sealed class SeekbarLyricsAndQueueInteractionTests
{
    [Fact]
    public void SeekbarScrubbingCalculator_FullLifecycle_And_BoundaryProtection()
    {
        var calc = new SeekbarScrubbingCalculator();
        Assert.False(calc.IsDragging);

        Assert.Null(calc.CompleteDrag(50.0, TimeSpan.FromSeconds(200)));

        calc.BeginDrag();
        Assert.True(calc.IsDragging);

        var progressDuringDrag = SeekbarScrubbingCalculator.CalculateSliderProgress(
            TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(200), 200.0, isDragging: true);
        Assert.False(progressDuringDrag.UpdateMax);
        Assert.Equal(75.0, progressDuringDrag.NewValue);

        var target1 = calc.CompleteDrag(120.5, TimeSpan.FromSeconds(200));
        Assert.NotNull(target1);
        Assert.Equal(TimeSpan.FromSeconds(120.5), target1!.Value);
        Assert.False(calc.IsDragging);

        calc.BeginDrag();
        calc.CancelDrag();
        Assert.False(calc.IsDragging);

        calc.BeginDrag();
        var targetOver = calc.CompleteDrag(350.0, TimeSpan.FromSeconds(200));
        Assert.Equal(TimeSpan.FromSeconds(200), targetOver);

        calc.BeginDrag();
        var targetNeg = calc.CompleteDrag(-50.0, TimeSpan.FromSeconds(200));
        Assert.Equal(TimeSpan.Zero, targetNeg);

        calc.BeginDrag();
        Assert.Equal(TimeSpan.Zero, calc.CompleteDrag(double.NaN, TimeSpan.FromSeconds(200)));

        calc.BeginDrag();
        Assert.Equal(TimeSpan.Zero, calc.CompleteDrag(double.PositiveInfinity, TimeSpan.FromSeconds(200)));
    }

    [Fact]
    public void LyricsScrollSynchronizer_FindActiveLineIndex_And_StateTransitions()
    {
        var lines = new List<LrcLineVm>
        {
            new() { Time = TimeSpan.FromSeconds(10), Text = "Line 1" },
            new() { Time = TimeSpan.FromSeconds(20), Text = "Line 2" },
            new() { Time = TimeSpan.FromSeconds(30), Text = "Line 3" }
        };

        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5), 0));
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(10), 0));
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(15), 0));
        Assert.Equal(1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(25), 0));
        Assert.Equal(2, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(35), 0));

        // Offset test (+5000ms delay -> active line is earlier)
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(22), 5000));

        int currentIndex = -1;
        bool changed = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 1);
        Assert.True(changed);
        Assert.Equal(1, currentIndex);
        Assert.True(lines[1].IsCurrent);

        changed = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref currentIndex, 2);
        Assert.True(changed);
        Assert.Equal(2, currentIndex);
        Assert.False(lines[1].IsCurrent);
        Assert.True(lines[2].IsCurrent);
    }

    [Fact]
    public void QueuePopupController_Sync_Badge_And_Remove()
    {
        var controller = new QueuePopupController();
        var playlist = new Playlist("Test Playlist");
        var items = new List<PlaylistItem>
        {
            new(new Track { Title = "Song 1", Artist = "Artist 1" }),
            new(new Track { Title = "Song 2", Artist = "Artist 2" })
        };
        var queue = new PlaybackQueue();
        queue.Enqueue(playlist, items);

        controller.SyncFromQueue(queue.Entries);
        Assert.Equal(2, controller.Entries.Count);
        Assert.Equal(1, controller.Entries[0].Index);
        Assert.Equal("Song 1", controller.Entries[0].Title);

        Assert.Equal("", QueuePopupController.FormatBadgeText(0));
        Assert.Equal("5", QueuePopupController.FormatBadgeText(5));
        Assert.Equal("99+", QueuePopupController.FormatBadgeText(150));

        controller.RequestRemoveAt(queue, 1);
        Assert.Equal(1, queue.Count);
        Assert.Equal("Song 2", queue.Entries[0].Title);
    }
}
