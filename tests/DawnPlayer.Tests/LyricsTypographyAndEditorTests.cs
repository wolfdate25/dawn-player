using System;
using System.IO;
using System.Linq;
using System.Text;
using DawnPlayer.App.Controls;
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
        Assert.Equal(4.0, s.LineSpacing);
        Assert.Equal("Center", s.Alignment);
        Assert.True(s.BoldActiveLine);
        Assert.True(s.EnableFocusEffect);
        Assert.True(s.ReadEmbeddedLyrics);
        Assert.Equal(0.5, s.DefaultOffsetStepMs);
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
    public void LrcParser_SaveOffsetToFile_UpdatesOffsetProperly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"lrc_offset_{Guid.NewGuid():N}.lrc");
        try
        {
            var initialContent = "[ti:Offset Test]\r\n[00:10.000] Line 1\r\n";
            LrcParser.SaveToFile(tempFile, initialContent);

            bool ok = LrcParser.SaveOffsetToFile(tempFile, 250);
            Assert.True(ok);

            var updatedText = File.ReadAllText(tempFile, Encoding.UTF8);
            Assert.Contains("[offset:250]", updatedText);
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

    [Fact]
    public void LyricsOffsetCalculation_VariableStep_PreservesPrecision()
    {
        double currentOffsetMs = 0.0;
        double stepMs = 0.5;

        // Add 5 times (+2.5ms)
        for (int i = 0; i < 5; i++)
        {
            currentOffsetMs = Math.Round(currentOffsetMs + stepMs, 4);
        }
        Assert.Equal(2.5, currentOffsetMs);

        // Add tiny 0.001ms step
        currentOffsetMs = Math.Round(currentOffsetMs + 0.001, 4);
        Assert.Equal(2.501, currentOffsetMs);

        // Subtract 0.5ms step
        currentOffsetMs = Math.Round(currentOffsetMs - stepMs, 4);
        Assert.Equal(2.001, currentOffsetMs);
    }
}
