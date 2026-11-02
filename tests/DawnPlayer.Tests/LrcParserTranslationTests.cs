using DawnPlayer.Core.Lyrics;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Adversarial gate for duplicate-timestamp translation pairing (plan §10.4).
/// Invariants I3/I4: pairing only merges an exact-2 group with two non-empty, normalized-different
/// texts; it never drops the original text nor changes the set of line times otherwise.
/// </summary>
public class LrcParserTranslationTests
{
    [Fact]
    public void MergesExactTwoLineGroupAsTranslation()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]안녕하세요");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Equal("안녕하세요", line.Translation);
    }

    [Fact]
    public void KeepsFirstLineTextAndInputOrder()
    {
        var doc = LrcParser.Parse("[00:01.00]가사\n[00:01.00]English words");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("가사", line.Text);
        Assert.Equal("English words", line.Translation);
    }

    [Fact]
    public void IdenticalTextsAreNotATranslationPair()
    {
        // repeated chorus written twice at the same stamp must stay two lines
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]Hello");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void CaseAndWhitespaceOnlyDifferencesAreNotTranslations()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]  HELLO  ");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void ThreeLineGroupIsAmbiguousAndStaysFlat()
    {
        var doc = LrcParser.Parse("[00:01.00]原文\n[00:01.00]romaji\n[00:01.00]translation");

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void WhitespaceOnlyTextDecaysToPlaceholderAndIsNeverPaired()
    {
        // the line-level Trim strips the spaces before text extraction, so "[00:01.00]   "
        // becomes a bare timestamp line whose text decays to the "♪" placeholder — and the
        // placeholder is excluded from pairing in both directions.
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]   ");

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.Null(doc.Lines[0].Translation);
        Assert.Equal("♪", doc.Lines[1].Text);
        Assert.Null(doc.Lines[1].Translation);
    }

    [Fact]
    public void PairingHappensAfterOffsetIsApplied()
    {
        var doc = LrcParser.Parse("[offset:1000]\n[00:02.00]Hello\n[00:02.00]안녕");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Time);
        Assert.Equal("안녕", line.Translation);
    }

    [Fact]
    public void PairingCanBeDisabledForTranslationSourceTexts()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]안녕", null, pairTranslationLines: false);

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void TranslationCandidateWithWordTimingsIsNotMerged()
    {
        // a karaoke-timed line is a real lyric line, never a translation
        var doc = LrcParser.Parse("[00:01.00]가사\n[00:01.00]<00:01.00>Ly<00:01.20>rics");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void MergedPairKeepsTheOriginalLinesWordTimings()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00>Hey<00:01.50>ho\n[00:01.00]번역");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal("번역", line.Translation);
    }

    [Fact]
    public void UnsyncedAllZeroDocumentIsNeverPaired()
    {
        // all-zero "documents" are unsynced text; duplicate adjacent lines are just text
        var doc = LrcParser.Parse("[00:00]first line\n[00:00]second line");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void OffsetClampCollisionsAreNotTranslationPairs()
    {
        // several lines pushed below zero collapse onto the same stamp — clamp artifacts,
        // not a translation pair written by a human.
        var doc = LrcParser.Parse("[offset:5000]\n[00:01.00]Very early line\n[00:03.00]Early line\n[00:10.00]Later line");

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void DifferentTimestampsStayIndependentLines()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:02.00]world");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Translation));
    }
}
