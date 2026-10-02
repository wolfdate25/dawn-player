using System;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// PT3-11 (2026-10-02 audit remediation): buffering feedback. Two halves —
/// 1. <see cref="StreamStallTracker"/>, the pure hysteresis behind a live stream's "alive but
///    underrun" flag (read side asserts, fill side clears only above the resume threshold);
/// 2. the controller's <see cref="PlaybackState.Buffering"/> open-window contract, whose one
///    unforgivable failure mode is sticking there: every bail-out (open failed, nothing to
///    resolve, nothing to go back to) must land on a state that tells the truth.
/// </summary>
public class PlaybackBufferingStateTests
{
    // ---------------- StreamStallTracker (pure) ----------------

    [Fact]
    public void Tracker_SilenceSetsStall_Idempotently()
    {
        var tracker = new StreamStallTracker();
        Assert.False(tracker.IsStalled);

        tracker.NotifyServedSilence();
        Assert.True(tracker.IsStalled);

        tracker.NotifyServedSilence();
        Assert.True(tracker.IsStalled); // repeated underrun reads never clear it
    }

    [Fact]
    public void Tracker_RefillBelowResumeThreshold_KeepsStall()
    {
        var tracker = new StreamStallTracker();
        tracker.NotifyServedSilence();

        // One refilled chunk (10 ms of 48 kHz stereo s16) must not flip the flag the next
        // read would immediately re-assert.
        tracker.NotifyBufferFilled(bufferedBytes: 1920, bytesPerSecond: 192000);

        Assert.True(tracker.IsStalled);
    }

    [Fact]
    public void Tracker_RefillAtResumeThreshold_ClearsStall()
    {
        var tracker = new StreamStallTracker();
        tracker.NotifyServedSilence();

        tracker.NotifyBufferFilled(bufferedBytes: 48000, bytesPerSecond: 192000); // exactly 0.25 s

        Assert.False(tracker.IsStalled);
    }

    [Fact]
    public void Tracker_ClearThenSilence_Restalls()
    {
        var tracker = new StreamStallTracker();
        tracker.NotifyServedSilence();
        tracker.NotifyBufferFilled(bufferedBytes: 96000, bytesPerSecond: 192000);
        Assert.False(tracker.IsStalled);

        tracker.NotifyServedSilence();
        Assert.True(tracker.IsStalled);
    }

    // ---------------- controller: the open-window contract ----------------

    private static Track StreamTrack(string path) =>
        new() { Path = path, Title = "stream", SourceKind = TrackSourceKind.Radio };

    private static (PlaybackController, Playlist, PlaylistManager) CreateController()
    {
        var playlists = new PlaylistManager(new MusicLibrary());
        var controller = new PlaybackController(new AppSettings(), playlists);
        return (controller, playlists.NowPlaying, playlists);
    }

    [Fact]
    public void IsBuffering_IdleController_IsFalse()
    {
        using var controller = CreateController().Item1;

        Assert.False(controller.IsBuffering);
        Assert.Equal(PlaybackState.Stopped, controller.State);
    }

    [Fact]
    public async Task PlayAsync_UnopenableStream_FailsWithoutStickingInBuffering()
    {
        var (controller, pl, playlists) = CreateController();
        var items = playlists.AddTracks(pl, new[] { StreamTrack(@"Z:\missing\stream.mp3") });
        bool warned = false;
        controller.Warning += _ => warned = true;

        // Radio kind + non-http path: no provider in the radio-pinned chain claims it, so the
        // open fails immediately (no network) — exactly the bail-out that used to be able to
        // strand the state between "opening" and "playing".
        await controller.PlayAsync(pl, items[0]);

        Assert.True(warned);
        Assert.NotEqual(PlaybackState.Buffering, controller.State);
        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.False(controller.IsBuffering);
    }

    [Fact]
    public async Task NextAsync_NothingToResolve_RestoresStoppedState()
    {
        using var controller = CreateController().Item1;
        bool warned = false;
        controller.Warning += _ => warned = true;

        await controller.NextAsync();

        Assert.True(warned);
        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.False(controller.IsBuffering);
    }

    [Fact]
    public async Task PreviousAsync_NoHistory_FromStopped_LandsCleanlyOnStopped()
    {
        using var controller = CreateController().Item1;

        await controller.PreviousAsync();

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.False(controller.IsBuffering);
    }

    /// <summary>Review F2 (2026-10-02): the sleep timer needs a cancel that never resumes — the
    /// PlayPause Buffering arm un-pauses the surviving session, which would start music exactly
    /// when the user asked for silence. This pins the API and its Stopped-path honesty; the
    /// paused-session case needs a live Buffering window (a slow open) no unit harness can
    /// fabricate, so it is pinned by review, not test.</summary>
    [Fact]
    public void CancelPendingOpen_FromStopped_IsANoopThatStaysStopped()
    {
        using var controller = CreateController().Item1;
        bool changed = false;
        controller.StateChanged += () => changed = true;

        controller.CancelPendingOpen();

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.False(controller.IsBuffering);
        Assert.False(changed);
    }
}
