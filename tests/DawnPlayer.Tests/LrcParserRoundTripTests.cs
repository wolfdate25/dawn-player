using System.Linq;
using DawnPlayer.Core.Lyrics;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Invariant I1 gate: Parse → Format → Parse is lossless for standard lines, enhanced word tags,
/// duplicate-timestamp translations, dual timestamps and plain-text documents. SaveOffsetToFile
/// and the lyrics editor both rewrite user .lrc files through this path — any loss destroys
/// curated files.
/// </summary>
public class LrcParserRoundTripTests
{
    private const string MixedDocument = """
        [ti:Round Trip]
        [ar:Artist]
        [offset:250]
        [00:01.00]plain line
        [00:02.00]<00:02.00>Sing <00:02.50>it <00:03.10>loud
        [00:02.00]번역 행
        [00:10.00][00:40.00]chorus
        [00:20.00]last line
        """;

    [Fact]
    public void MixedDocumentRoundTripsLosslessly()
    {
        var first = LrcParser.Parse(MixedDocument);
        var second = LrcParser.Parse(LrcParser.Format(first));

        AssertDocumentEqual(first, second);
    }

    [Fact]
    public void WordOnlyDocumentRoundTrips()
    {
        var first = LrcParser.Parse("[00:01.00]<00:01.00>One <00:01.40>two<00:02.00> three");
        var second = LrcParser.Parse(LrcParser.Format(first));

        AssertDocumentEqual(first, second);
    }

    [Fact]
    public void TranslationOnlyDocumentRoundTrips()
    {
        var first = LrcParser.Parse("[00:01.00]Hello\n[00:01.00]안녕\n[00:05.00]Bye\n[00:05.00]안녕히");
        var second = LrcParser.Parse(LrcParser.Format(first));

        AssertDocumentEqual(first, second);
    }

    [Fact]
    public void PlainTextDocumentRoundTrips()
    {
        var first = LrcParser.Parse("unsynced line one\nunsynced line two");
        var second = LrcParser.Parse(LrcParser.Format(first));

        AssertDocumentEqual(first, second);
    }

    [Fact]
    public void AllZeroTimestampedDocumentRoundTrips()
    {
        var first = LrcParser.Parse("[00:00]first line\n[00:00]second line");
        var second = LrcParser.Parse(LrcParser.Format(first));

        AssertDocumentEqual(first, second);
    }

    [Fact]
    public void SaveOffsetToFilePreservesWordsAndTranslations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dawn-rt-{Guid.NewGuid():N}.lrc");
        try
        {
            File.WriteAllText(path, "[00:01.00]<00:01.00>Hey<00:01.50>ho\n[00:01.00]번역\n[00:05.00]plain");

            Assert.True(LrcParser.SaveOffsetToFile(path, 500));

            var doc = LrcParser.ParseFile(path);
            var merged = doc.Lines.Single(l => l.Text == "Heyho");
            Assert.Equal(2, merged.Words!.Count);
            Assert.Equal("번역", merged.Translation);
            Assert.Equal(TimeSpan.FromSeconds(0.5), merged.Time);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertDocumentEqual(LyricsDocument expected, LyricsDocument actual)
    {
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Artist, actual.Artist);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);

        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var a = expected.Lines[i];
            var b = actual.Lines[i];
            Assert.True(a.Time == b.Time, $"line[{i}].Time {a.Time} != {b.Time}");
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(a.Translation, b.Translation);

            if (a.Words is null)
            {
                Assert.Null(b.Words);
                continue;
            }
            Assert.NotNull(b.Words);
            Assert.Equal(a.Words.Count, b.Words!.Count);
            for (int w = 0; w < a.Words.Count; w++)
            {
                Assert.True(a.Words[w].Start == b.Words[w].Start, $"line[{i}].word[{w}].Start differs");
                Assert.Equal(a.Words[w].Text, b.Words[w].Text);
            }
        }
    }
}
