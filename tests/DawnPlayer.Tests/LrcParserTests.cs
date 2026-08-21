using System.Globalization;
using System.IO;
using System.Text;
using DawnPlayer.Core.Lyrics;
using Xunit;

namespace DawnPlayer.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParsesBasicTimestamps()
    {
        var doc = LrcParser.Parse("""
            [ti:Test Title]
            [ar:Test Artist]
            [00:01.50]first line
            [00:03]second line
            [01:02.25]third line
            """);

        Assert.Equal("Test Title", doc.Title);
        Assert.Equal("Test Artist", doc.Artist);
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), doc.Lines[0].Time);
        Assert.Equal("first line", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(62.25), doc.Lines[2].Time);
    }

    [Fact]
    public void HandlesMultipleTimestampsPerLine()
    {
        var doc = LrcParser.Parse("[00:10.00][00:50.00]chorus\n[00:30.00]verse");

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(30), doc.Lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(50), doc.Lines[2].Time);
        Assert.All(doc.Lines, l => Assert.NotEmpty(l.Text));
    }

    [Fact]
    public void AppliesOffsetTag()
    {
        // positive offset shifts lyrics earlier
        var doc = LrcParser.Parse("[offset:500]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromSeconds(9.5), doc.Lines[0].Time);
    }

    [Fact]
    public void AppliesOffsetTag_WhenOffsetTagIsAtBottom_TwoPassProcessing()
    {
        // Global offset at bottom should apply to lines parsed before it
        var text = """
            [00:10.00]first line
            [00:20.00]second line
            [offset:1000]
            """;
        var doc = LrcParser.Parse(text);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(9.0), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(19.0), doc.Lines[1].Time);
    }

    [Fact]
    public void AppliesNegativeOffset_ShiftsLyricsLater()
    {
        var text = """
            [00:10.00]first line
            [offset:-500]
            """;
        var doc = LrcParser.Parse(text);

        Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(10.5), doc.Lines[0].Time);
    }

    [Fact]
    public void AppliesOffset_ClampsNegativeTimestampsToZero()
    {
        var text = """
            [00:01.00]early line
            [offset:3000]
            """;
        var doc = LrcParser.Parse(text);

        Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
    }

    [Fact]
    public void ParsesHourMinuteSecondFormat()
    {
        var text = """
            [01:05:10:500]one hour five mins
            [01:15:30.50]one hour fifteen mins
            [02:00:00.00]two hours
            """;
        var doc = LrcParser.Parse(text);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1 * 3600 + 5 * 60 + 10.50), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(1 * 3600 + 15 * 60 + 30.50), doc.Lines[1].Time);
        Assert.Equal(TimeSpan.FromHours(2), doc.Lines[2].Time);
    }

    [Fact]
    public void ParsesThreeDigitMillisecondFractions()
    {
        var text = "[01:23.456]three digit millis";
        var doc = LrcParser.Parse(text);

        Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(83456), doc.Lines[0].Time);
    }

    [Fact]
    public void StripsEnhancedWordTags()
    {
        var doc = LrcParser.Parse("[00:05.00]hello <00:05.20>world <00:05.60>!");
        Assert.Equal("hello world !", doc.Lines[0].Text);
    }

    [Fact]
    public void LineIndexAtFindsCurrentLine()
    {
        var doc = LrcParser.Parse("[00:01.00]a\n[00:05.00]b\n[00:10.00]c");

        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.Zero));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(4.99)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, doc.LineIndexAt(TimeSpan.FromSeconds(999)));
    }

    [Fact]
    public void ColonFractionSupported()
    {
        var doc = LrcParser.Parse("[00:01:50]colon fraction");
        Assert.Equal(TimeSpan.FromSeconds(1.5), doc.Lines[0].Time);
    }

    [Fact]
    public void ParseFile_WithUtf8Bom_DecodesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "LrcTestUtf8Bom_" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            var content = "[ti:UTF8 Test]\n[00:05.00]가사 테스트입니다\n";
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
            File.WriteAllBytes(tempFile, bytes);

            var doc = LrcParser.ParseFile(tempFile);
            Assert.Equal("UTF8 Test", doc.Title);
            Assert.Single(doc.Lines);
            Assert.Equal("가사 테스트입니다", doc.Lines[0].Text);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseFile_WithUtf16LeBom_DecodesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "LrcTestUtf16LeBom_" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            var content = "[ti:Unicode Test]\n[00:08.50]日本語歌詞テスト 🎵\n";
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray();
            File.WriteAllBytes(tempFile, bytes);

            var doc = LrcParser.ParseFile(tempFile);
            Assert.Equal("Unicode Test", doc.Title);
            Assert.Single(doc.Lines);
            Assert.Equal("日本語歌詞テスト 🎵", doc.Lines[0].Text);
            Assert.Equal(TimeSpan.FromSeconds(8.5), doc.Lines[0].Time);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseFile_WithUtf16BeBom_DecodesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "LrcTestUtf16BeBom_" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            var content = "[ti:BigEndian Test]\n[00:12.00]Big Endian Lyrics\n";
            var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(content)).ToArray();
            File.WriteAllBytes(tempFile, bytes);

            var doc = LrcParser.ParseFile(tempFile);
            Assert.Equal("BigEndian Test", doc.Title);
            Assert.Single(doc.Lines);
            Assert.Equal("Big Endian Lyrics", doc.Lines[0].Text);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("[00:00.00]zero", 0.0)]
    [InlineData("[00:00:00]zero colon", 0.0)]
    [InlineData("[00:00.0]one digit frac", 0.0)]
    [InlineData("[00:01.5]one and half", 1.5)]
    [InlineData("[00:01.25]quarter sec", 1.25)]
    [InlineData("[00:01.123]three digit frac", 1.123)]
    [InlineData("[01:30.00]ninety secs", 90.0)]
    [InlineData("[1:30.00]single digit min", 90.0)]
    [InlineData("[120:00.00]two hours in minutes", 7200.0)]
    [InlineData("[01:00:00.00]one hour", 3600.0)]
    [InlineData("[01:15:30.50]one hour fifteen mins thirty point five secs", 4530.5)]
    [InlineData("[02:30:45:678]two hours colon frac", 9045.678)]
    [InlineData("[99:59:59.999]maximum timestamp", 99 * 3600 + 59 * 60 + 59.999)]
    public void LrcParser_TimestampFormats_ParsesAccurately(string lrcLine, double expectedSeconds)
    {
        var doc = LrcParser.Parse(lrcLine);
        Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds).TotalMilliseconds, doc.Lines[0].Time.TotalMilliseconds, precision: 1);
    }

    [Fact]
    public void LrcParser_MultipleTimestampsOnSingleLine_ProducesMultipleChronologicalLines()
    {
        var lrc = "[00:05.00][00:15.00][00:25.00]Repeated Chorus Line";
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), doc.Lines[0].Time);
        Assert.Equal("Repeated Chorus Line", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(15), doc.Lines[1].Time);
        Assert.Equal("Repeated Chorus Line", doc.Lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(25), doc.Lines[2].Time);
        Assert.Equal("Repeated Chorus Line", doc.Lines[2].Text);
    }

    [Fact]
    public void LrcParser_UnsortedLines_SortsCorrectlyInFinalDocument()
    {
        var lrc = """
            [00:30.00]Third line
            [00:10.00]First line
            [00:20.00]Second line
            """;
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal("First line", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), doc.Lines[0].Time);
        Assert.Equal("Second line", doc.Lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(20), doc.Lines[1].Time);
        Assert.Equal("Third line", doc.Lines[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(30), doc.Lines[2].Time);
    }

    [Fact]
    public void LrcParser_IdenticalTimestamps_PreservesAllLines()
    {
        var lrc = """
            [00:10.00]Line A
            [00:10.00]Line B
            [00:10.00]Line C
            """;
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal(TimeSpan.FromSeconds(10), l.Time));
        var texts = doc.Lines.Select(l => l.Text).ToList();
        Assert.Contains("Line A", texts);
        Assert.Contains("Line B", texts);
        Assert.Contains("Line C", texts);
    }

    [Fact]
    public void LrcParser_EmptyOrWhitespaceLyrics_ReplacedWithMusicalNote()
    {
        var lrc = "[00:01.00]\n[00:02.00]    \n[00:03.00]\t\n";
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("♪", l.Text));
    }

    [Fact]
    public void LrcParser_BracketsInLyricsText_PreservedAsText()
    {
        var lrc = """
            [00:01.00]Intro: [Guitar Solo] (feat. DJ)
            [00:05.00]Verse [1] - [Loud]
            """;
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Intro: [Guitar Solo] (feat. DJ)", doc.Lines[0].Text);
        Assert.Equal("Verse [1] - [Loud]", doc.Lines[1].Text);
    }

    [Fact]
    public void LrcParser_MetadataTags_ParsedCaseInsensitivelyAndTrimmed()
    {
        var lrc = """
            [TI: Bohemian Rhapsody ]
            [ar: Queen ]
            [AL: A Night at the Opera ]
            [by: Freddie Mercury ]
            [unknown_tag: should_be_ignored]
            [00:05.00]Is this the real life?
            """;
        var doc = LrcParser.Parse(lrc);

        Assert.Equal("Bohemian Rhapsody", doc.Title);
        Assert.Equal("Queen", doc.Artist);
        Assert.Equal("A Night at the Opera", doc.Album);
        Assert.Equal("Freddie Mercury", doc.By);
        Assert.Single(doc.Lines);
        Assert.Equal("Is this the real life?", doc.Lines[0].Text);
    }

    [Fact]
    public void LrcParser_MalformedLinesAndEmptyInputs_DoesNotCrash()
    {
        var malformedInputs = new[]
        {
            "",
            "   ",
            "\r\n\r\n\r\n",
            "[ti:]",
            "[:]",
            "[::]",
            "[invalid timestamp]not a timestamp",
            "[99:99:99:99:99]invalid format",
            "just random text with no timestamps at all",
            "[\n]\n[\r]",
            "[offset:]",
            "[offset:abc]"
        };

        foreach (var input in malformedInputs)
        {
            var doc = LrcParser.Parse(input);
            Assert.NotNull(doc);
        }
    }

    [Fact]
    public void LrcParser_OffsetVariations_AppliesUniformly()
    {
        // Positive offset moves lyrics earlier
        var docPos = LrcParser.Parse("[offset: 500ms]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromSeconds(9.5), docPos.Lines[0].Time);

        // Negative offset moves lyrics later
        var docNeg = LrcParser.Parse("[offset: -500ms]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromSeconds(10.5), docNeg.Lines[0].Time);

        // Unicode minus
        var docUni = LrcParser.Parse("[offset: −300]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromSeconds(10.3), docUni.Lines[0].Time);

        // Explicit plus
        var docPlus = LrcParser.Parse("[offset: +200]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromSeconds(9.8), docPlus.Lines[0].Time);
    }

    [Fact]
    public void LrcParser_MultipleOffsetTags_AccumulatesTotalOffset()
    {
        var lrc = """
            [offset: 500]
            [00:10.00]First line
            [offset: -200]
            [00:20.00]Second line
            [offset: 100]
            """;
        // Total offset = 500 - 200 + 100 = +400ms -> shifts earlier by 0.4s
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(9.6), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(19.6), doc.Lines[1].Time);
    }

    [Fact]
    public void LrcParser_OffsetClampsBelowZeroToTimeSpanZero()
    {
        var lrc = """
            [offset: 5000]
            [00:01.00]Very early line
            [00:03.00]Early line
            [00:10.00]Later line
            """;
        // Offset +5000ms shifts by -5s:
        // 1s - 5s = -4s -> clamped to 0s
        // 3s - 5s = -2s -> clamped to 0s
        // 10s - 5s = 5s
        var doc = LrcParser.Parse(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
        Assert.Equal(TimeSpan.Zero, doc.Lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(5.0), doc.Lines[2].Time);
    }

    [Fact]
    public void LrcParser_HighMinutes_EnhancedTags_AndOffset()
    {
        // Offset interacting with >99-minute timestamps and enhanced word tags in one document.
        string lrc = @"
[ti:Epic Symphony]
[ar:Composer]
[offset:500]
[00:00.00]Intro
[01:30.50]<01:30.50>Word1 <01:31.00>Word2
[120:00.00]Two hours later
";

        var doc = LrcParser.Parse(lrc);

        Assert.Equal("Epic Symphony", doc.Title);
        Assert.Equal("Composer", doc.Artist);
        Assert.Equal(3, doc.Lines.Count);

        // Line 1: [00:00.00] - 500ms offset -> clamped to TimeSpan.Zero
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
        Assert.Equal("Intro", doc.Lines[0].Text);

        // Line 2: [01:30.50] = 90.5s - 0.5s = 90.0s = 01:30.00
        Assert.Equal(TimeSpan.FromSeconds(90), doc.Lines[1].Time);
        Assert.Equal("Word1 Word2", doc.Lines[1].Text); // Stripped <01:30.50> tags

        // Line 3: [120:00.00] = 7200s - 0.5s = 7199.5s
        Assert.Equal(TimeSpan.FromSeconds(7199.5), doc.Lines[2].Time);
        Assert.Equal("Two hours later", doc.Lines[2].Text);
    }

    [Fact]
    public void LyricsDocument_LineIndexAt_EmptyOrNull_ReturnsMinusOne()
    {
        var emptyDoc = LyricsDocument.Empty;
        Assert.Equal(-1, emptyDoc.LineIndexAt(TimeSpan.Zero));
        Assert.Equal(-1, emptyDoc.LineIndexAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(-1, emptyDoc.LineIndexAt(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void LyricsDocument_LineIndexAt_SingleLine_BoundaryChecks()
    {
        var doc = LrcParser.Parse("[00:05.00]Solo line");

        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.FromSeconds(-1)));
        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.Zero));
        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.FromSeconds(4.999)));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(5.000)));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(5.001)));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(100.0)));
    }

    [Fact]
    public void LyricsDocument_LineIndexAt_LargeScaleRandomFuzzing_MatchesLinearOracle()
    {
        // Generate 1000 lines with strictly increasing timestamps
        var sb = new StringBuilder();
        var random = new Random(42);
        double currentTime = 0.5;

        for (int i = 0; i < 1000; i++)
        {
            currentTime += 0.5 + random.NextDouble() * 2.0; // increments by 0.5 .. 2.5s
            int min = (int)(currentTime / 60);
            double sec = currentTime % 60;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0:D2}:{1:00.00}]Line {2}", min, sec, i));
        }

        var doc = LrcParser.Parse(sb.ToString());
        Assert.Equal(1000, doc.Lines.Count);

        // Linear search oracle
        int LinearOracle(TimeSpan t)
        {
            int found = -1;
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                if (doc.Lines[i].Time <= t) found = i;
                else break;
            }
            return found;
        }

        // Test 2000 random query timestamps
        for (int q = 0; q < 2000; q++)
        {
            double querySec = -5.0 + random.NextDouble() * (currentTime + 10.0);
            var queryTime = TimeSpan.FromSeconds(querySec);

            int binaryResult = doc.LineIndexAt(queryTime);
            int oracleResult = LinearOracle(queryTime);

            Assert.Equal(oracleResult, binaryResult);
        }
    }

    [Fact]
    public void LrcParser_ParseFile_WithUtf32LeAndBeBom_DecodesCorrectly()
    {
        var tempLe = Path.Combine(Path.GetTempPath(), "LrcTestUtf32Le_" + Guid.NewGuid().ToString("N") + ".lrc");
        var tempBe = Path.Combine(Path.GetTempPath(), "LrcTestUtf32Be_" + Guid.NewGuid().ToString("N") + ".lrc");

        try
        {
            var content = "[ti:UTF32 Test]\n[00:05.00]UTF32 Lyrics Testing 🎵\n";

            // UTF-32 LE with BOM (0xFF, 0xFE, 0x00, 0x00)
            var utf32LePreamble = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
            var utf32LeBytes = utf32LePreamble.Concat(Encoding.UTF32.GetBytes(content)).ToArray();
            File.WriteAllBytes(tempLe, utf32LeBytes);

            var docLe = LrcParser.ParseFile(tempLe);
            Assert.Equal("UTF32 Test", docLe.Title);
            Assert.Single(docLe.Lines);
            Assert.Equal("UTF32 Lyrics Testing 🎵", docLe.Lines[0].Text);

            // UTF-32 BE with BOM (0x00, 0x00, 0xFE, 0xFF)
            var utf32BeEncoding = Encoding.GetEncoding("utf-32BE");
            var utf32BePreamble = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
            var utf32BeBytes = utf32BePreamble.Concat(utf32BeEncoding.GetBytes(content)).ToArray();
            File.WriteAllBytes(tempBe, utf32BeBytes);

            var docBe = LrcParser.ParseFile(tempBe);
            Assert.Equal("UTF32 Test", docBe.Title);
            Assert.Single(docBe.Lines);
            Assert.Equal("UTF32 Lyrics Testing 🎵", docBe.Lines[0].Text);
        }
        finally
        {
            if (File.Exists(tempLe)) File.Delete(tempLe);
            if (File.Exists(tempBe)) File.Delete(tempBe);
        }
    }

    [Fact]
    public void LrcParser_ParseFile_ZeroByteFile_ReturnsEmptyDocument()
    {
        var tempZero = Path.Combine(Path.GetTempPath(), "LrcTestZero_" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            File.WriteAllBytes(tempZero, Array.Empty<byte>());
            var doc = LrcParser.ParseFile(tempZero);
            Assert.NotNull(doc);
            Assert.Empty(doc.Lines);
        }
        finally
        {
            if (File.Exists(tempZero)) File.Delete(tempZero);
        }
    }
}
