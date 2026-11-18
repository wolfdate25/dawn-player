using System.Globalization;
using System.Text;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using DawnPlayer.Tests.Fuzzing;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Mutation-based fuzzing gate for the parsers that consume untrusted input (external files and
/// byte streams). Each target is declared with its invariants BEFORE the corpus is generated —
/// a fuzz iteration that violates one is a reproducible defect (seed + iteration + input are in
/// the failure message, and the same seed replays the identical corpus).
///
/// Invariants under test:
///   I-F1  Parsers never throw on any input of their declared surface (AutoEq documents this as
///         an API contract; LRC/M3U are reached from import paths that have no try/catch).
///   I-F2  LrcParser: every line Time ≥ 0, lines are stably sorted, word starts are
///         non-decreasing and never precede their line (documented I2).
///   I-F3  LyricsDocument.LineIndexAt agrees with the sorted timeline (reported index == actual
///         last line at-or-before the probe — the 8ebfe12 "reported ≠ actual" class).
///   I-F4  Format∘Parse reaches a fixpoint: F(P(F(P(x)))) == F(P(x)). One lossy round is
///         allowed (bracket-shaped plain-text lines are metadata-excluded by design, 3+ same-
///         stamp groups flatten translation pairs); a SECOND lossy round is not — the output
///         must be stable, which is what the editor's save path relies on.
///   I-F5  CueSheet: a non-null document only carries entries with TrackNumber > 0, Start ≥ 0;
///         TryParseTime never returns a negative time for any accepted token.
///   I-F6  M3u.Read: readable files produce entries with non-null paths and non-negative
///         durations; the (#EXTINF d&lt;0 → null) contract holds.
///   I-F7  AutoEqParser.Parse / DeserializeJson: clamped ranges (Freq 20..20000, Gain ±15,
///         Q 0.1..8, Preamp ±12, ≤ 20 bands) on every surviving band, documented contract.
///   I-F8  LegacyTextDecoder: any byte sequence decodes to a non-null string.
///
/// Runtime: the default iteration count keeps the whole gate in the low single-digit seconds
/// so the full suite (AGENTS.md §4) stays fast; set DAWN_PLAYER_FUZZ_ITERATIONS for a deep
/// local burst (e.g. 100000) without touching the CI budget.
/// </summary>
public sealed class ParserFuzzingTests
{
    private const int TimedCorpusSeed = 20261101;
    private const int PlainCorpusSeed = 20261102;
    private const int ParseFileSeed = 20261103;
    private const int CueParseSeed = 20261104;
    private const int CueTimeSeed = 20261105;
    private const int CueFileSeed = 20261106;
    private const int M3uSeed = 20261107;
    private const int AutoEqSeed = 20261108;
    private const int AutoEqJsonSeed = 20261109;
    private const int DecoderSeed = 20261110;

    static ParserFuzzingTests()
    {
        // CP949 corpus construction needs the code-page provider on .NET Core (idempotent; the
        // production parsers register the same provider in their own static ctors).
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    // ------------------------------------------------------------------ corpora

    /// <summary>Grammatically complete LRC: metadata, offset, multi-stamp lines, enhanced word
    /// tags, a duplicate-stamp translation pair, an offset-clamp candidate and a plain tail.</summary>
    private const string LrcTimedCorpus = """
        [ti:야경 (Night View)]
        [ar:테스트 아티스트]
        [al:Fuzz Album]
        [by:Tagger v2]
        [offset:250]
        [00:00.100]초기 라인 (클램프 후보)
        [00:12.00]첫 번째 줄
        [00:14.50][00:59.10]반복 후렴 구간
        [00:18.20]<00:18.20>이 <00:19.00>단어 <00:20.10>카라오케
        [00:18.20]Translation of the karaoke line
        [99:59.999]마지막 줄
        """;

    /// <summary>Unsynced plain-text document: exercises the timestamp-free branch where
    /// bracket-shaped lines are metadata-excluded by design.</summary>
    private const string LrcPlainCorpus = """
        가사 없는 노래
        [Chorus]
        두 번째 절
        """;

    private const string CueCorpus = """
        REM COMMENT "ExactAudioCopy 1.6"
        PERFORMER "앨범 아티스트"
        TITLE "퍼즈 앨범"
        FILE "Artist - Album.flac" WAVE
          TRACK 01 AUDIO
            TITLE "첫 곡"
            PERFORMER "곡 아티스트"
            INDEX 00 00:00:00
            INDEX 01 00:02:00
          TRACK 02 AUDIO
            TITLE "둘째 곡"
            INDEX 01 04:33:74
        FILE "C:\abs path\other.flac" WAVE
          TRACK 03 AUDIO
            INDEX 01 123:45:67
        """;

    private const string M3uCorpus = """
        #EXTM3U
        #PLAYLIST:퍼즈 목록
        #DPTRACK:2|Title|Artist|Album|183.5|http://art
        #EXTINF:213,Artist - 제목, 부제
        Artist - Album.flac
        #EXTINF:-1,Radio 스트림
        http://stream.example.com:8000/live
        #DPUNRESOLVED:missing song.mp3
        C:\Music\절대 경로.mp3
        ../상대/곡.mp3
        """;

    private const string AutoEqCorpus = """
        Preamp: -3.2 dB
        # 주석 줄
        // 슬래시 주석
        Filter 1: ON PK Fc 31 Hz Gain 2.5 dB Q 0.7
        Filter 2: ON LSC Fc 105 Hz Gain -1.0 dB Q 0.707
        Filter 3: OFF HSC Fc 8000 Hz Gain 1.0 dB Q 0.5
        Filter 4: ON HP Fc 20 Hz Gain 6 dB Q 0.5
        Filter 5: ON LP Fc 19000.5 Hz Gain -6.25 dB Q 1.2
        """;

    private const string EqJsonCorpus =
        """{"Id":"abc123","Name":"퍼즈 프리셋","Enabled":true,"PreampDb":3.5,"Bands":[""" +
        """{"Type":2,"FrequencyHz":100.0,"GainDb":-2.5,"Q":1.4},{"Type":"PeakEq","FrequencyHz":8000,"GainDb":6,"Q":0.7}]}""";

    // ------------------------------------------------------------------ LrcParser

    [Fact]
    public void LrcParser_Fuzz_TimedCorpus_TimelineAndFixpointInvariants()
    {
        var inputs = FuzzInputGenerator.MutateText(LrcTimedCorpus, Iterations(), TimedCorpusSeed);
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            FuzzAssert(TimedCorpusSeed, i, input, () =>
            {
                var doc = LrcParser.Parse(input);
                ValidateTimelineInvariants(doc, $"timed corpus iteration {i}");

                var s1 = LrcParser.Format(doc);
                var doc2 = LrcParser.Parse(s1);
                ValidateTimelineInvariants(doc2, $"timed corpus iteration {i} (reparse)");
                var s2 = LrcParser.Format(doc2);
                var s3 = LrcParser.Format(LrcParser.Parse(s2));
                Assert.True(s2 == s3, $"I-F4 fixpoint violated at iteration {i}\n{DiffContext(s2, s3)}");
            });
        }
    }

    [Fact]
    public void LrcParser_Fuzz_PlainCorpus_TimelineAndFixpointInvariants()
    {
        var inputs = FuzzInputGenerator.MutateText(LrcPlainCorpus, Iterations(), PlainCorpusSeed);
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            FuzzAssert(PlainCorpusSeed, i, input, () =>
            {
                var doc = LrcParser.Parse(input);
                ValidateTimelineInvariants(doc, $"plain corpus iteration {i}");

                var s1 = LrcParser.Format(doc);
                var doc2 = LrcParser.Parse(s1);
                ValidateTimelineInvariants(doc2, $"plain corpus iteration {i} (reparse)");
                var s2 = LrcParser.Format(doc2);
                var s3 = LrcParser.Format(LrcParser.Parse(s2));
                Assert.True(s2 == s3, $"I-F4 fixpoint violated at iteration {i}\n{DiffContext(s2, s3)}");
            });
        }
    }

    /// <summary>I-F1 for the byte decode path: BOM sniffing, strict-UTF-8 validation and the
    /// legacy decoder must survive arbitrary bytes without throwing.</summary>
    [Fact]
    public void LrcParser_Fuzz_ParseFile_DecodeBytesInvariants()
    {
        static byte[] Prefixed(byte[] prefix, string text, Encoding enc)
        {
            var body = enc.GetBytes(text);
            var all = new byte[prefix.Length + body.Length];
            Array.Copy(prefix, all, prefix.Length);
            Array.Copy(body, 0, all, prefix.Length, body.Length);
            return all;
        }

        // Collection expressions are avoided as collection-initializer elements — the parser
        // reads a leading '[' as an indexer on the previous element.
        var corpora = new List<byte[]>
        {
            Prefixed(Encoding.UTF8.GetPreamble(), LrcTimedCorpus, new UTF8Encoding(false)),
            Prefixed(Encoding.Unicode.GetPreamble(), LrcTimedCorpus, Encoding.Unicode),
            Prefixed(Encoding.BigEndianUnicode.GetPreamble(), LrcTimedCorpus, Encoding.BigEndianUnicode),
            Prefixed(Array.Empty<byte>(), LrcTimedCorpus, Encoding.GetEncoding(949)),
            Prefixed(Encoding.UTF32.GetPreamble(), LrcTimedCorpus, Encoding.UTF32),
            Prefixed(Array.Empty<byte>(), M3uCorpus, Encoding.ASCII),
            new byte[] { 0x41, 0x00, 0x42 },
        };
        var mutated = FuzzInputGenerator.MutateBytes(corpora, DiskIterations(), ParseFileSeed);
        var noise = FuzzInputGenerator.RandomBytes(Math.Max(16, DiskIterations() / 2), ParseFileSeed + 1, 2048);

        var dir = NewTempDir();
        try
        {
            int i = 0;
            foreach (var bytes in mutated.Concat(noise))
            {
                var path = Path.Combine(dir, $"fuzz_{i}.lrc");
                File.WriteAllBytes(path, bytes);
                FuzzAssert(ParseFileSeed, i, HexRepr(bytes), () =>
                {
                    var doc = LrcParser.ParseFile(path);
                    Assert.NotNull(doc);
                    ValidateTimelineInvariants(doc, $"parse-file iteration {i}");
                });
                i++;
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ CueSheet

    [Fact]
    public void CueSheet_Fuzz_Parse_EntryInvariants()
    {
        var inputs = FuzzInputGenerator.MutateText(CueCorpus, Iterations(), CueParseSeed);
        var dir = NewTempDir();
        try
        {
            var cuePath = Path.Combine(dir, "fuzz.cue");
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                FuzzAssert(CueParseSeed, i, input, () =>
                {
                    var doc = CueSheet.Parse(input, cuePath);
                    if (doc == null) return;
                    Assert.All(doc.Entries, e =>
                    {
                        Assert.True(e.TrackNumber > 0, $"I-F5 track number {e.TrackNumber} at iteration {i}");
                        Assert.True(e.Start >= TimeSpan.Zero, $"I-F5 negative start {e.Start} at iteration {i}");
                        Assert.True(e.AudioPath.Length > 0, $"I-F5 empty audio path at iteration {i}");
                    });
                });
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>I-F5 direct: any token TryParseTime accepts must yield a non-negative time.
    /// Deterministic extremes pin the integer-overflow boundary (mm near int.MaxValue wraps
    /// the mm*60 multiply negative).</summary>
    [Fact]
    public void CueSheet_Fuzz_TryParseTime_NegativeTimeForbidden()
    {
        var extremes = new[]
        {
            "2147483647:00:00", "35791394:00:74", "-1:00:00", "0:0:0", "99999999999:00:00",
            "00:59:74", "123:45:67", "1:2", "12:34.56", "0000000001:59:75",
        };
        var mutated = FuzzInputGenerator.MutateText(string.Join('\n', extremes), Iterations(), CueTimeSeed)
            .SelectMany(s => s.Split('\n'));
        foreach (var token in extremes.Concat(mutated))
        {
            FuzzAssert(CueTimeSeed, 0, token, () =>
            {
                var ok = CueSheet.TryParseTime([token], out var time);
                if (ok)
                    Assert.True(time >= TimeSpan.Zero, $"I-F5 accepted '{token}' → negative {time}");
            });
        }
    }

    [Fact]
    public void CueSheet_Fuzz_TryParseFile_Bytes()
    {
        var corpora = new List<byte[]>
        {
            CorpusWithBom(Encoding.UTF8.GetPreamble(), CueCorpus, Encoding.UTF8),
            CorpusWithBom(Encoding.Unicode.GetPreamble(), CueCorpus, Encoding.Unicode),
            CorpusWithBom(Array.Empty<byte>(), CueCorpus, Encoding.GetEncoding(949)),
            CorpusWithBom(Array.Empty<byte>(), CueCorpus, Encoding.Latin1),
        };
        var inputs = FuzzInputGenerator.MutateBytes(corpora, DiskIterations(), CueFileSeed);

        var dir = NewTempDir();
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                var path = Path.Combine(dir, $"fuzz_{i}.cue");
                File.WriteAllBytes(path, inputs[i]);
                FuzzAssert(CueFileSeed, i, HexRepr(inputs[i]), () =>
                {
                    var doc = CueSheet.TryParseFile(path);
                    if (doc == null) return;
                    Assert.All(doc.Entries, e =>
                    {
                        Assert.True(e.TrackNumber > 0, $"I-F5 track number at iteration {i}");
                        Assert.True(e.Start >= TimeSpan.Zero, $"I-F5 negative start at iteration {i}");
                    });
                });
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ M3u

    /// <summary>I-F1 + I-F6: a readable playlist file must not crash Read (ImportPlaylist has
    /// no try/catch around it) and every entry must honor the duration contract.</summary>
    [Fact]
    public void M3u_Fuzz_Read_NoThrow_EntryInvariants()
    {
        var corpora = new List<byte[]>
        {
            CorpusWithBom(Encoding.UTF8.GetPreamble(), M3uCorpus, Encoding.UTF8),
            CorpusWithBom(Encoding.Unicode.GetPreamble(), M3uCorpus, Encoding.Unicode),
            CorpusWithBom(Array.Empty<byte>(), M3uCorpus, Encoding.GetEncoding(949)),
            CorpusWithBom(Array.Empty<byte>(), M3uCorpus, Encoding.Latin1),
        };
        var inputs = FuzzInputGenerator.MutateBytes(corpora, DiskIterations(), M3uSeed);

        var dir = NewTempDir();
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                // Alternate the extensions so both BOM-driven and m3u8-implies-UTF8 paths run.
                var path = Path.Combine(dir, $"fuzz_{i}{(i % 2 == 0 ? ".m3u" : ".m3u8")}");
                File.WriteAllBytes(path, inputs[i]);
                FuzzAssert(M3uSeed, i, HexRepr(inputs[i]), () =>
                {
                    var entries = M3u.Read(path);
                    Assert.All(entries, e =>
                    {
                        Assert.NotNull(e.Path);
                        Assert.True(e.DurationSeconds is null or > 0, $"I-F6 negative duration at iteration {i}");
                    });
                });
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ AutoEq

    /// <summary>I-F1 + I-F7: the documented "guaranteed not to throw" contract plus the clamp
    /// ranges on every band that survives parsing.</summary>
    [Fact]
    public void AutoEq_Fuzz_Parse_ClampContract()
    {
        var inputs = FuzzInputGenerator.MutateText(AutoEqCorpus, Iterations(), AutoEqSeed);
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            FuzzAssert(AutoEqSeed, i, input, () =>
            {
                var profile = AutoEqParser.Parse(input, "fuzz");
                Assert.NotNull(profile);
                Assert.InRange(profile.PreampDb, -12.0, 12.0);
                Assert.True(profile.Bands.Count <= 20, $"I-F7 band count {profile.Bands.Count} at iteration {i}");
                Assert.All(profile.Bands, b =>
                {
                    Assert.InRange(b.FrequencyHz, 20.0, 20000.0);
                    Assert.InRange(b.GainDb, -15.0, 15.0);
                    Assert.InRange(b.Q, 0.1, 8.0);
                });
            });
        }
    }

    [Fact]
    public void AutoEq_Fuzz_DeserializeJson_ClampContract()
    {
        var inputs = FuzzInputGenerator.MutateText(EqJsonCorpus, Iterations(), AutoEqJsonSeed);
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            FuzzAssert(AutoEqJsonSeed, i, input, () =>
            {
                var profile = AutoEqParser.DeserializeJson(input);
                if (profile == null) return;
                Assert.InRange(profile.PreampDb, -12.0, 12.0);
                if (profile.Bands == null) return;
                Assert.True(profile.Bands.Count <= 20, $"I-F7 band count at iteration {i}");
                Assert.All(profile.Bands, b =>
                {
                    Assert.InRange(b.FrequencyHz, 20.0, 20000.0);
                    Assert.InRange(b.GainDb, -15.0, 15.0);
                    Assert.InRange(b.Q, 0.1, 8.0);
                });
            });
        }
    }

    // ------------------------------------------------------------------ LegacyTextDecoder

    /// <summary>I-F8: every byte sequence decodes to a non-null string — BOM guesses, invalid
    /// UTF-8 (→ legacy CP949-vs-ANSI weighing) and pure noise all included.</summary>
    [Fact]
    public void LegacyTextDecoder_Fuzz_DecodeNonUtf8_NoThrow()
    {
        var corpora = new List<byte[]>
        {
            CorpusWithBom(Array.Empty<byte>(), LrcTimedCorpus, Encoding.GetEncoding(949)),
            CorpusWithBom(Array.Empty<byte>(), CueCorpus, Encoding.GetEncoding(949)),
            CorpusWithBom(Array.Empty<byte>(), LrcTimedCorpus, Encoding.UTF8),
            CorpusWithBom(Array.Empty<byte>(), M3uCorpus, Encoding.Latin1),
            new byte[] { 0xFF, 0xFE, 0x41, 0x00 },
            new byte[] { 0xFE, 0xFF, 0x00, 0x41 },
        };
        var mutated = FuzzInputGenerator.MutateBytes(corpora, Iterations(), DecoderSeed);
        var noise = FuzzInputGenerator.RandomBytes(Iterations(), DecoderSeed + 1, 4096);

        int i = 0;
        foreach (var bytes in mutated.Concat(noise))
        {
            FuzzAssert(DecoderSeed, i, HexRepr(bytes), () =>
            {
                var text = LegacyTextDecoder.DecodeNonUtf8(bytes);
                Assert.NotNull(text);
            });
            i++;
        }
    }

    // --------------------------------------------------- minimum repros (fuzz discoveries)

    // Each of these pins one defect the fuzz gate caught, independent of iteration counts/seeds.
    // seed/iteration of the original discovery are in the comment for the record.

    [Fact] // seed 20261101, iteration 153 — "[offset:803529000649456225016207200503]" threw OverflowException
    public void LrcParser_HugeOffsetTag_ClampedInsteadOfCrash()
    {
        var doc = LrcParser.Parse("[offset:803529000649456225016207200503]\n[00:01.000]한 줄");
        Assert.All(doc.Lines, l => Assert.True(l.Time >= TimeSpan.Zero));
    }

    [Fact] // seed 20261107, iteration 82 — an embedded-NUL path threw ArgumentException out of Read
    public void M3u_LineWithNulCharacter_KeepsRawEntryInsteadOfCrash()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "nul.m3u");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("nul" + '\0' + "path.mp3"));
            var entries = M3u.Read(path);
            Assert.Single(entries);
            Assert.Equal("nul" + '\0' + "path.mp3", entries[0].Path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact] // seed 20261104, iteration 82 — an embedded-NUL FILE argument discarded the whole album
    public void CueSheet_NulCharacterFileArgument_KeepsEntryInsteadOfCrash()
    {
        var dir = NewTempDir();
        try
        {
            var cuePath = Path.Combine(dir, "nul.cue");
            var doc = CueSheet.Parse("FILE \"bad" + '\0' + "name.flac\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:01:00\n", cuePath);
            Assert.NotNull(doc);
            Assert.Single(doc.Entries);
            Assert.Equal("bad" + '\0' + "name.flac", doc.Entries[0].AudioPath);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact] // extreme mm wrapped the mm*60 multiply into a negative track start
    public void CueSheet_ParseTime_OverflowMinutes_Rejected()
    {
        // 35791394 * 60 == 2,147,483,640 — the last mm that still fits; one more minute wraps.
        Assert.True(CueSheet.TryParseTime(["35791394:00:00"], out var boundary));
        Assert.Equal(TimeSpan.FromSeconds(int.MaxValue - 7), boundary);
        Assert.False(CueSheet.TryParseTime(["35791395:00:00"], out var pastBoundary));
        Assert.True(pastBoundary >= TimeSpan.Zero);
        Assert.False(CueSheet.TryParseTime(["2147483647:00:00"], out var wrapped));
        Assert.True(wrapped >= TimeSpan.Zero);
        Assert.True(CueSheet.TryParseTime(["123:45:67"], out var sane));
        Assert.Equal(TimeSpan.FromSeconds(123 * 60 + 45), sane.Add(-TimeSpan.FromTicks((long)Math.Round(67 * TimeSpan.TicksPerSecond / 75.0))));
    }

    [Fact] // seed 20261101, iteration 306 — FromSeconds(0.087) == 869,999 ticks walked the stamp −1 ms per round trip
    public void LrcParser_TimestampMilliseconds_ParseExactly()
    {
        var doc = LrcParser.Parse("[00:12.087]한 줄");
        Assert.Equal(new TimeSpan(12 * TimeSpan.TicksPerSecond + 87 * TimeSpan.TicksPerMillisecond), doc.Lines[0].Time);

        // Tenths/hundredths scale exactly too, and the cycle no longer drifts.
        Assert.Equal(TimeSpan.FromTicks(1 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond), LrcParser.Parse("[00:01.50]x").Lines[0].Time);
        Assert.Equal(TimeSpan.FromTicks(2 * TimeSpan.TicksPerSecond + 100 * TimeSpan.TicksPerMillisecond), LrcParser.Parse("[00:02.1]x").Lines[0].Time);

        var once = LrcParser.Format(LrcParser.Parse("[00:12.087]한 줄\n[01:18.987]두 줄"));
        Assert.Equal(once, LrcParser.Format(LrcParser.Parse(once)));
    }

    [Fact] // seed 20261104, iteration 1021 @20k — a doubled-quote FILE ("") emitted an empty-path entry
    public void CueSheet_EmptyFileToken_DropsTrackInsteadOfEmptyPath()
    {
        var dir = NewTempDir();
        try
        {
            var cuePath = Path.Combine(dir, "empty.cue");
            var doc = CueSheet.Parse("FILE \"\"broken\" x.flac\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:01:00\n", cuePath);
            Assert.True(doc == null || doc.Entries.All(e => e.AudioPath.Length > 0), "empty AudioPath entry leaked");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static int Iterations()
    {
        var raw = Environment.GetEnvironmentVariable("DAWN_PLAYER_FUZZ_ITERATIONS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : 1000;
    }

    /// <summary>Disk-bound targets (temp files) run a quarter of the string-target budget so the
    /// default suite stays fast; the env override scales both.</summary>
    private static int DiskIterations() => Math.Max(10, Iterations() / 4);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DawnFuzz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Corpus builder: <paramref name="prefix"/> (a BOM or nothing) followed by the text
    /// encoded with <paramref name="encoding"/>.</summary>
    private static byte[] CorpusWithBom(byte[] prefix, string text, Encoding encoding)
    {
        var body = encoding.GetBytes(text);
        var all = new byte[prefix.Length + body.Length];
        Array.Copy(prefix, all, prefix.Length);
        Array.Copy(body, 0, all, prefix.Length, body.Length);
        return all;
    }

    /// <summary>Every iteration failure must identify itself: the (seed, iteration, input) triple
    /// reproduces the corpus deterministically, so the message alone is the bug report.</summary>
    private static void FuzzAssert(int seed, int iteration, string inputRepr, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            var safeInput = Repr(inputRepr);
            Assert.Fail($"""
                FUZZ FAILURE seed={seed} iteration={iteration}
                {ex.GetType().Name}: {ex.Message}
                {ex.StackTrace}
                INPUT ({inputRepr.Length} chars): {safeInput}
                """);
        }
    }

    /// <summary>Shows exactly where (and with which bytes) two serializer outputs diverge, so a
    /// fixpoint failure message carries its own diagnosis.</summary>
    private static string DiffContext(string a, string b)
    {
        int i = 0;
        int min = Math.Min(a.Length, b.Length);
        while (i < min && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 80);
        int to = Math.Min(Math.Max(a.Length, b.Length), i + 160);
        return $"""
            first difference at index {i} (a={a.Length} chars, b={b.Length} chars)
            a: {Repr(a[from..Math.Min(to, a.Length)])}
            b: {Repr(b[from..Math.Min(to, b.Length)])}
            """;
    }

    /// <summary>Console-safe repr: control chars and anything outside ASCII become \uXXXX, so the
    /// failure log preserves the exact input even on mojibake-prone consoles.</summary>
    private static string Repr(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c <= '\u007E' && !char.IsControl(c) ? c : $"\\u{(int)c:X4}");
        return sb.ToString();
    }

    private static void ValidateTimelineInvariants(LyricsDocument doc, string context)
    {
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            var line = doc.Lines[i];
            Assert.True(line.Time >= TimeSpan.Zero, $"I-F2 negative time at {context} line {i}: {line.Time}");
            if (i > 0)
                Assert.True(doc.Lines[i - 1].Time <= line.Time, $"I-F2 unsorted at {context} line {i}");

            Assert.True(line.Translation == null || line.Translation.Length > 0, $"I-F2 empty translation at {context} line {i}");

            if (line.Words is { Count: > 0 } words)
            {
                Assert.True(words[0].Start >= line.Time, $"I-F2 word precedes line (I2) at {context} line {i}");
                for (int w = 1; w < words.Count; w++)
                    Assert.True(words[w - 1].Start <= words[w].Start, $"I-F2 word order at {context} line {i}/{w}");
            }
        }

        // I-F3: LineIndexAt is a binary search over the sorted timeline — the reported index
        // must be exactly "the last line at-or-before the probe".
        var probes = new List<TimeSpan>
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(-5),
            TimeSpan.FromHours(1),
        };
        foreach (var line in doc.Lines)
        {
            probes.Add(line.Time);
            probes.Add(line.Time + TimeSpan.FromMilliseconds(1));
        }
        foreach (var probe in probes)
        {
            int idx = doc.LineIndexAt(probe);
            Assert.InRange(idx, -1, doc.Lines.Count - 1);
            Assert.True(idx == -1 || doc.Lines[idx].Time <= probe, $"I-F3 index late at {context} probe {probe}");
            Assert.True(idx + 1 >= doc.Lines.Count || doc.Lines[idx + 1].Time > probe, $"I-F3 index early at {context} probe {probe}");
        }
    }

    private static string HexRepr(byte[] bytes)
    {
        const int max = 256;
        var sb = new StringBuilder(Math.Min(bytes.Length, max) * 3 + 16);
        sb.Append(bytes.Length).Append(" bytes: ");
        for (int i = 0; i < Math.Min(bytes.Length, max); i++)
            sb.Append(bytes[i].ToString("X2")).Append(' ');
        if (bytes.Length > max) sb.Append('…');
        return sb.ToString();
    }
}
