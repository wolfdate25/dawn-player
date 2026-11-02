using DawnPlayer.Core.Lyrics;
using DawnPlayer.Plugins;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Gate for plugin-supplied translated LRC merging (plan §10.3 I3/I4, I8): exact-timestamp
/// matches only, existing translations respected, original text never replaced, and the new
/// LyricsContent property must not disturb the construction path existing plugins compile to.
/// </summary>
public class TranslationMergerTests
{
    [Fact]
    public void MergeByExactTimestamp()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:02.00]World");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]안녕\n[00:02.00]세상");

        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal("안녕", merged.Lines[0].Translation);
        Assert.Equal("세상", merged.Lines[1].Translation);
        Assert.Equal("Hello", merged.Lines[0].Text);
    }

    [Fact]
    public void UnmatchedTranslationLinesAreDropped()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]안녕\n[00:09.00]유령 번역");

        var line = Assert.Single(merged.Lines);
        Assert.Equal("안녕", line.Translation);
    }

    [Fact]
    public void ExistingTranslationIsNeverOverwritten()
    {
        // duplicate-timestamp pairing already filled this one
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]기존 번역");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]플러그인 번역");

        Assert.Equal("기존 번역", Assert.Single(merged.Lines).Translation);
    }

    [Fact]
    public void TranslationIdenticalToOriginalIsIgnored()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]hello");

        Assert.Null(Assert.Single(merged.Lines).Translation);
    }

    [Fact]
    public void InstrumentalPlaceholderInTranslationSourceIsSkipped()
    {
        var doc = LrcParser.Parse("[00:01.00]Hello\n[00:05.00]More");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]♪\n[00:05.00]더");

        Assert.Null(merged.Lines[0].Translation);
        Assert.Equal("더", merged.Lines[1].Translation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTranslatedLrcReturnsDocumentUnchanged(string? translatedLrc)
    {
        var doc = LrcParser.Parse("[00:01.00]Hello");
        var merged = LyricsTranslationMerger.Merge(doc, translatedLrc);

        Assert.Equal(doc.Lines, merged.Lines);
    }

    [Fact]
    public void DocumentMetadataIsPreserved()
    {
        var doc = LrcParser.Parse("[ti:T]\n[ar:A]\n[00:01.00]Hello");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:01.00]안녕");

        Assert.Equal("T", merged.Title);
        Assert.Equal("A", merged.Artist);
        Assert.Equal(doc.SourcePath, merged.SourcePath);
    }

    [Fact]
    public void UnsynchronizedDocumentConsumesTheZeroStampOnce()
    {
        // every unsynced line shares timestamp zero — one translation entry must not stamp
        // every line, so a map entry is consumed by the first match.
        var doc = LrcParser.Parse("plain line\nsecond plain line");
        var merged = LyricsTranslationMerger.Merge(doc, "[00:00.00]자동 번역");

        Assert.Equal("자동 번역", merged.Lines[0].Translation);
        Assert.Null(merged.Lines[1].Translation);
    }

    [Fact]
    public void TranslatedLrcPropertyKeepsExistingPluginConstructionPath()
    {
        // I8: old plugins construct with an object initializer over SyncedLrc/PlainText only.
        var content = new LyricsContent { SyncedLrc = "[00:01.00]x", PlainText = "x" };

        Assert.True(content.HasContent);
        Assert.Null(content.TranslatedLrc);

        var extended = content with { TranslatedLrc = "[00:01.00]y" };
        Assert.Equal("[00:01.00]y", extended.TranslatedLrc);
    }
}
