using DawnPlayer.Core.Lyrics;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Invariant I7 gate: the reported active word index must equal the value KaraokeTimeline
/// computes for the playback position — the UI is not allowed to diverge from the timeline.
/// </summary>
public class KaraokeTimelineTests
{
    private static List<LrcWord> Words(params (double start, string text)[] items) =>
        items.Select(i => new LrcWord(TimeSpan.FromSeconds(i.start), i.text)).ToList();

    [Fact]
    public void EmptyOrNullWordsReturnNegativeOne()
    {
        Assert.Equal(-1, KaraokeTimeline.WordIndexAt(null, TimeSpan.FromSeconds(1)));
        Assert.Equal(-1, KaraokeTimeline.WordIndexAt(Array.Empty<LrcWord>(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void BeforeFirstWordReturnsNegativeOne()
    {
        var words = Words((1.0, "a"), (2.0, "b"));

        Assert.Equal(-1, KaraokeTimeline.WordIndexAt(words, TimeSpan.Zero));
        Assert.Equal(-1, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(0.999)));
    }

    [Fact]
    public void ExactStartSelectsTheWord()
    {
        var words = Words((1.0, "a"), (2.0, "b"));

        Assert.Equal(0, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void BetweenWordsSelectsTheEarlierWord()
    {
        var words = Words((1.0, "a"), (2.0, "b"));

        Assert.Equal(0, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(1.999)));
    }

    [Fact]
    public void AfterLastWordStaysOnTheLastWord()
    {
        var words = Words((1.0, "a"), (2.0, "b"));

        Assert.Equal(1, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ClampedEqualStartsResolveToTheLastOfTheGroup()
    {
        // clamped lines produce equal starts; the search must still return a valid index
        var words = Words((5.0, "bb"), (5.0, "aa"), (6.0, "cc"));

        Assert.Equal(1, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void SingleWordTimeline()
    {
        var words = Words((0.5, "only"));

        Assert.Equal(-1, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(0.4)));
        Assert.Equal(0, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(0, KaraokeTimeline.WordIndexAt(words, TimeSpan.FromSeconds(9)));
    }
}
