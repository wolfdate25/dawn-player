using DawnPlayer.Core.Lyrics;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Adversarial gate for enhanced-LRC word-level tag parsing (plan §10.4).
/// Invariant I2: word starts are non-decreasing within a line and never precede the line time.
/// </summary>
public class LrcParserWordTimingTests
{
    [Fact]
    public void ParsesWordTagsIntoSegments()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Hello world", line.Text);
        Assert.NotNull(line.Words);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.0), line.Words[0].Start);
        Assert.Equal("Hello ", line.Words[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.5), line.Words[1].Start);
        Assert.Equal("world", line.Words[1].Text);
    }

    [Fact]
    public void TextBeforeFirstWordTagBecomesPreludeWithoutWord()
    {
        var doc = LrcParser.Parse("[00:01.00]Intro <00:01.50>rest");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Intro rest", line.Text);
        var word = Assert.Single(line.Words!);
        Assert.Equal(TimeSpan.FromSeconds(1.5), word.Start);
        Assert.Equal("rest", word.Text);
    }

    [Fact]
    public void NonMonotonicWordStartsAreClampedToPreviousWord()
    {
        var doc = LrcParser.Parse("[00:05.00]<00:05.00>bb<00:04.00>aa<00:06.00>cc");

        var words = Assert.Single(doc.Lines).Words!;
        Assert.Equal(TimeSpan.FromSeconds(5), words[0].Start);
        // 04.00 precedes the previous word start: clamped up, never backwards.
        Assert.Equal(TimeSpan.FromSeconds(5), words[1].Start);
        Assert.Equal("aa", words[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(6), words[2].Start);
        AssertWordsNonDecreasing(words);
    }

    [Fact]
    public void WordStartBeforeLineTimeIsClampedToLineTime()
    {
        var doc = LrcParser.Parse("[00:05.00]<00:03.00>early<00:06.00>late");

        var words = Assert.Single(doc.Lines).Words!;
        Assert.Equal(TimeSpan.FromSeconds(5), words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), words[1].Start);
        Assert.All(words, w => Assert.True(w.Start >= TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OffsetShiftsWordStartsWithTheLine_AndKeepsMonotonic()
    {
        // positive offset shifts lyrics earlier by 500 ms
        var doc = LrcParser.Parse("[offset:500]\n[00:05.00]<00:05.20>aa<00:05.80>bb");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(4.5), line.Time);
        var words = line.Words!;
        Assert.Equal(TimeSpan.FromSeconds(4.7), words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5.3), words[1].Start);
        AssertWordsNonDecreasing(words);
        Assert.All(words, w => Assert.True(w.Start >= line.Time));
    }

    [Fact]
    public void OffsetClampKeepsWordsAtOrAfterZeroLineTime()
    {
        // Positive offset shifts both line and words before zero; the zero clamp must keep
        // words at or after the (clamped) line time and non-decreasing.
        var doc = LrcParser.Parse("[00:00.40]<00:00.40>a<00:00.90>b\n[offset:1000]");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.Zero, line.Time);
        Assert.All(line.Words!, w => Assert.True(w.Start >= line.Time));
        AssertWordsNonDecreasing(line.Words!);
    }

    [Theory]
    [InlineData("<00:01.5>", 1.5)]
    [InlineData("<00:01.25>", 1.25)]
    [InlineData("<00:01.123>", 1.123)]
    [InlineData("<00:01:50>", 110.0)] // ':' fraction separator = tenths/hundredths digits
    [InlineData("<01:01:01.5>", 3661.5)] // hour form
    public void AcceptsTimestampShapeVariants(string tag, double expectedSeconds)
    {
        var doc = LrcParser.Parse($"[00:01.00]{tag}word");

        var word = Assert.Single(Assert.Single(doc.Lines).Words!);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), word.Start);
    }

    [Fact]
    public void LiteralAngleBracketTextIsPreservedWithoutWords()
    {
        // "<3" never closes a well-formed word tag — it must stay literal text.
        var doc = LrcParser.Parse("[00:01.00]I <3 you");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("I <3 you", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void TagsWithoutTextProduceNoWords()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00><00:02.00>");

        var line = Assert.Single(doc.Lines);
        Assert.Null(line.Words);
        Assert.NotEmpty(line.Text); // falls into the ♪ placeholder path, never a crash
    }

    [Fact]
    public void TrailingTagWithEmptySegmentIsDropped()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00>ab<00:02.00>");

        var words = Assert.Single(doc.Lines).Words!;
        Assert.Single(words);
        Assert.Equal("ab", words[0].Text);
    }

    [Fact]
    public void MultipleTimestampsDuplicateTheWordTimedText()
    {
        var doc = LrcParser.Parse("[00:10.00][00:50.00]<00:10.00>a<00:10.50>b");

        Assert.Equal(2, doc.Lines.Count);
        Assert.NotNull(doc.Lines[0].Words);
        Assert.NotNull(doc.Lines[1].Words);
        Assert.Equal("ab", doc.Lines[0].Text);
        Assert.Equal("ab", doc.Lines[1].Text);
    }

    [Fact]
    public void PlainTextDocumentNeverProducesWords()
    {
        var doc = LrcParser.Parse("just plain\nlines of text <00:01.00> not synced");

        Assert.True(doc.HasLines);
        Assert.All(doc.Lines, l => Assert.Null(l.Words));
    }

    [Fact]
    public void DocumentHasWordTimingsReflectsContent()
    {
        var withWords = LrcParser.Parse("[00:01.00]<00:01.00>hi\n[00:02.00]plain");
        var withoutWords = LrcParser.Parse("[00:01.00]plain\n[00:02.00]also plain");

        Assert.True(withWords.HasWordTimings);
        Assert.False(withoutWords.HasWordTimings);
    }

    private static void AssertWordsNonDecreasing(IReadOnlyList<LrcWord> words)
    {
        for (int i = 1; i < words.Count; i++)
            Assert.True(words[i].Start >= words[i - 1].Start,
                $"word[{i}].Start {words[i].Start} must not precede word[{i - 1}].Start {words[i - 1].Start}");
    }
}
