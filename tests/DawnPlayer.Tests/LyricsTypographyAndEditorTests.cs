using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawnPlayer.App.Controls;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public class LyricsTypographyAndEditorTests
{
    [Fact]
    public void LyricsSettings_DefaultValues_AreSensibleAndSafe()
    {
        var s = new LyricsSettings();

        Assert.Equal("Segoe UI Variable, Malgun Gothic", s.FontFamily);
        Assert.Equal(13.5, s.FontSize);
        Assert.Equal(16.5, s.ActiveFontSize);
        Assert.Equal(0, s.CharacterSpacing);
        Assert.Equal(24.0, s.LineHeight);
        Assert.Equal("Center", s.Alignment);
        Assert.True(s.BoldActiveLine);
        Assert.True(s.EnableFocusEffect);
        Assert.True(s.ReadEmbeddedLyrics);
        // 0.5 ms was below the 1 ms resolution of the editor list and the saved stamps — one
        // click of ± changed nothing anywhere. 500 ms (0.5 s) is the default now.
        Assert.Equal(500, s.DefaultOffsetStepMs);
    }

    [Fact]
    public void LrcParser_Format_SerializesMetadataAndTimestampsCorrectly()
    {
        var doc = new LyricsDocument
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Lines = new[]
            {
                new LrcLine(TimeSpan.FromSeconds(12.345), "First Line"),
                new LrcLine(TimeSpan.FromSeconds(25.678), "Second Line")
            }
        };

        var formatted = LrcParser.Format(doc, offsetMs: 500);

        Assert.Contains("[ti:Test Song]", formatted);
        Assert.Contains("[ar:Test Artist]", formatted);
        Assert.Contains("[al:Test Album]", formatted);
        Assert.Contains("[offset:500]", formatted);
        Assert.Contains("[00:12.345] First Line", formatted);
        Assert.Contains("[00:25.678] Second Line", formatted);
    }

    [Fact]
    public void LrcParser_PlainLyricsWithoutTimestamps_ParsesGracefully()
    {
        var plainText = "첫 번째 가사 라인\r\n두 번째 가사 라인\r\n세 번째 가사 라인";
        var doc = LrcParser.Parse(plainText);

        Assert.True(doc.HasLines);
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal("첫 번째 가사 라인", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
        Assert.Equal("두 번째 가사 라인", doc.Lines[1].Text);
        Assert.Equal("세 번째 가사 라인", doc.Lines[2].Text);
    }

    [Fact]
    public void LrcParser_SaveToFile_WritesUtf8WithBom()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"lrc_test_{Guid.NewGuid():N}.lrc");
        try
        {
            var content = "[ti:Hello]\r\n[00:10.000] Hello World\r\n";
            LrcParser.SaveToFile(tempFile, content);

            Assert.True(File.Exists(tempFile));
            var bytes = File.ReadAllBytes(tempFile);
            Assert.True(bytes.Length >= 3);
            // Check UTF-8 BOM: 0xEF, 0xBB, 0xBF
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);

            var parsed = LrcParser.ParseFile(tempFile);
            Assert.Equal("Hello", parsed.Title);
            Assert.Single(parsed.Lines);
            Assert.Equal("Hello World", parsed.Lines[0].Text);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void LrcLineVm_TypographyProperties_ReflectStateAndNotify()
    {
        var vm = new LrcLineVm
        {
            Time = TimeSpan.FromSeconds(5),
            Text = "Sample Line",
            BaseFontSize = 14.0,
            ActiveFontSize = 18.0,
            EnableFocusEffect = true,
            FontFamily = "Pretendard",
            CharacterSpacing = 20,
            LineHeight = 28.0,
            TextAlignment = "Center"
        };

        // When inactive
        Assert.False(vm.IsCurrent);
        Assert.Equal(14.0, vm.FontSize);
        Assert.Equal(0.40, vm.Opacity);

        // When active
        vm.IsCurrent = true;
        Assert.True(vm.IsCurrent);
        Assert.Equal(18.0, vm.FontSize);
        Assert.Equal(1.0, vm.Opacity);

        // Notify style changed
        int notifyCount = 0;
        vm.PropertyChanged += (_, _) => notifyCount++;
        vm.NotifyTypographyChanged();
        Assert.True(notifyCount > 0);
    }

    // =========================================================================
    // Offset repair gates (2026-11 가사 오프셋 수리). The old default step (0.5 ms) was
    // below the 1 ms resolution of the editor list and the saved [mm:ss.fff] stamps: one
    // click changed nothing visible. These gates pin the invariants that kill that class
    // of defect: displayed == saved == actual, and the default step is human-scale.
    // =========================================================================

    [Fact]
    public void EditorOffset_DefaultStepMove_IsVisibleInListAndSavedFile()
    {
        // A single click at the default 500 ms step must move both the list display and the
        // serialized stamp — the exact path that used to be a silent no-op.
        var line = new LrcEditLineVm { Time = TimeSpan.FromSeconds(12.345), Text = "hook" };

        line.Time = line.Time + TimeSpan.FromMilliseconds(500);

        Assert.Equal("00:12.845", line.TimeString);
        var doc = new LyricsDocument { Lines = new[] { new LrcLine(line.Time, line.Text) } };
        Assert.Contains("[00:12.845] hook", LrcParser.Format(doc));
    }

    [Theory]
    [InlineData(0.5)]     // legacy sub-ms default — two clicks must land on a whole ms
    [InlineData(0.25)]
    [InlineData(10.5)]    // fractional step rounds, never truncates into a third value
    [InlineData(100)]
    [InlineData(-100.5)]
    public void EditorOffset_FractionalSteps_AlwaysSnapToWholeMilliseconds(double stepMs)
    {
        var line = new LrcEditLineVm { Time = TimeSpan.FromSeconds(5), Text = "x" };
        line.Time = line.Time + TimeSpan.FromMilliseconds(stepMs);
        line.Time = line.Time + TimeSpan.FromMilliseconds(stepMs);

        // Invariant: displayed == saved == actual — the time is whole-ms, so the TimeString
        // (which formats whole ms) and the Format stamp can never disagree with the value.
        Assert.Equal(0, line.Time.Ticks % TimeSpan.TicksPerMillisecond);
        var doc = new LyricsDocument { Lines = new[] { new LrcLine(line.Time, line.Text) } };
        Assert.Contains($"[{line.TimeString}] ", LrcParser.Format(doc));
    }

    [Fact]
    public void EditorOffset_NegativeMove_ClampsAtZeroAndRestoresFromOriginal()
    {
        // A negative move past zero clamps (the line cannot start before the song) — and the
        // load-time original must survive that clamp so "restore original times" can recover.
        var original = TimeSpan.FromMilliseconds(300);
        var line = new LrcEditLineVm { Time = original, Text = "x" };
        var snapshot = (line.Time, line.Words);

        line.Time = line.Time - TimeSpan.FromMilliseconds(1000); // clamps
        Assert.Equal(TimeSpan.Zero, line.Time);

        line.Time = snapshot.Time; // restore path
        Assert.Equal(original, line.Time);
        Assert.Equal("00:00.300", line.TimeString);
    }

    [Fact]
    public void EditorOffset_SubMsTimeEntry_SnapsToWholeMsEverywhere()
    {
        // Direct entry paths (typing in the time box, F5 stamping a sub-ms playback position)
        // funnel through the same snap — what gets SAVED is always whole-ms. The time box may
        // keep the raw typed text until the next programmatic touch (rewriting it mid-keystroke
        // would fight the caret), so the gate pins the saved value, not the transient box text.
        var stamped = new LrcEditLineVm { Text = "x" };
        stamped.Time = TimeSpan.FromSeconds(3.4567);
        Assert.Equal("00:03.457", stamped.TimeString); // rounded, not truncated to .456

        var typed = new LrcEditLineVm { Text = "y" };
        typed.TimeString = "00:03.4567";
        Assert.Equal(0, typed.Time.Ticks % TimeSpan.TicksPerMillisecond);
        var doc = new LyricsDocument { Lines = new[] { new LrcLine(typed.Time, typed.Text) } };
        Assert.Contains("[00:03.457] y", LrcParser.Format(doc));
    }

    [Fact]
    public void EditorPlayhead_FindActiveLineIndex_WorksOnEditorLineViewModels()
    {
        // The editor playhead reuses the pane's binary search through the shared
        // ITimedLyricsLine interface — boundary semantics must match the pane exactly.
        List<LrcEditLineVm> lines = new()
        {
            new() { Time = TimeSpan.FromSeconds(1) },
            new() { Time = TimeSpan.FromSeconds(3) },
            new() { Time = TimeSpan.FromSeconds(5) },
        };

        Assert.Equal(-1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(0.999), 0));
        Assert.Equal(0, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(1), 0));
        Assert.Equal(1, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(3.5), 0));
        Assert.Equal(2, LyricsScrollSynchronizer.FindActiveLineIndex(lines, TimeSpan.FromSeconds(99), 0));
    }

    [Fact]
    public void SettingsStore_LegacySubMsOffsetStep_MigratesToDefaultOnLoad()
    {
        // Exercised through Normalize directly — redirecting AppPaths.BaseDir in a test would
        // unset the sandbox base dir the isolation gate depends on.
        // Legacy default (v1.0–v1.9), a zero hand-edit, and a sane value: sub-1 values bump to
        // 500, real values survive untouched.
        foreach (var (json, expected) in new[]
        {
            ("{\"Lyrics\":{\"DefaultOffsetStepMs\":0.5}}", 500.0),
            ("{\"Lyrics\":{\"DefaultOffsetStepMs\":0}}", 500.0),
            ("{\"Lyrics\":{\"DefaultOffsetStepMs\":0.9}}", 500.0),
            ("{\"Lyrics\":{\"DefaultOffsetStepMs\":1}}", 1.0),
            ("{\"Lyrics\":{\"DefaultOffsetStepMs\":1000}}", 1000.0),
        })
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal(expected, SettingsStore.Normalize(settings).Lyrics.DefaultOffsetStepMs);
        }
    }
}
