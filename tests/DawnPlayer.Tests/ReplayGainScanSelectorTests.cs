using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Invariant I12 gate (plan §12.3): every RG scan path (whole library, rescan-all, scoped
/// album/track context-menu scan) selects targets through one filter — stream URLs never enter,
/// and the missing-tag filter applies only to the automatic library pass.
/// </summary>
public class ReplayGainScanSelectorTests
{
    private static Track Tagged(string path) => new()
    {
        Path = path,
        RgTrackGainDb = -8.0, RgTrackPeak = 0.5, RgAlbumGainDb = -8.0, RgAlbumPeak = 0.5
    };

    private static Track Untagged(string path) => new() { Path = path };

    [Fact]
    public void AutoPassKeepsOnlyUntaggedTracks()
    {
        var tracks = new List<Track> { Tagged("a.flac"), Untagged("b.flac"), Tagged("c.flac"), Untagged("d.mp3") };

        var selected = ReplayGainScanSelector.Select(tracks, rescanAll: false);

        Assert.Equal(new[] { "b.flac", "d.mp3" }, selected.Select(t => t.Path));
    }

    [Fact]
    public void RescanAllKeepsEverything()
    {
        var tracks = new List<Track> { Tagged("a.flac"), Untagged("b.flac") };

        var selected = ReplayGainScanSelector.Select(tracks, rescanAll: true);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void StreamUrlsNeverEnter()
    {
        var tracks = new List<Track>
        {
            Tagged("http://stream.example/radio"),
            Untagged("http://stream.example/other"),
            Untagged("e.flac"),
        };

        var selected = ReplayGainScanSelector.Select(tracks, rescanAll: true);

        Assert.Single(selected);
        Assert.Equal("e.flac", selected[0].Path);
    }

    [Fact]
    public void ScopedSelectionIsRescanAllSemantics()
    {
        // an explicit album/track context-menu scan analyzes the picked tracks even when tags
        // already exist (the user asked for it), but streams still never enter
        var tracks = new List<Track> { Tagged("a.flac"), Tagged("b.flac"), Tagged("http://s/x") };

        var selected = ReplayGainScanSelector.Select(tracks, rescanAll: true);

        Assert.Equal(new[] { "a.flac", "b.flac" }, selected.Select(t => t.Path));
    }
}
