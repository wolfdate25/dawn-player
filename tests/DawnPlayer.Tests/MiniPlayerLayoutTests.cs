using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Mini-player layout gate. Mini mode shrinks the window to 500px wide leaving only
/// NowPlayingBar, whose fixed-width content (56px art + 7 transport buttons + volume
/// cluster ≈ 584px) overflowed the 464px available, clipping volume/queue/lyrics.
/// A second defect hid behind it: the Wide visual state had no trigger so Compact
/// never engaged at any width. Rule: Wide owns the 640px breakpoint and Compact
/// sheds the fixed widths (volume slider, art size, padding, spacing) so the bar
/// fits 500px. Accessible names stay sourced from resw via x:Uid
/// (see <see cref="AutomationNameGateTests"/>).
/// </summary>
public class MiniPlayerLayoutTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string ReadNowPlayingBarXaml()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");
        var path = Path.Combine(root!.FullName, "src", "DawnPlayer.App", "Controls", "NowPlayingBar.xaml");
        Assert.True(File.Exists(path), $"NowPlayingBar.xaml not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Returns the MinWindowWidth trigger of the Wide state, or null when it has none
    /// (a triggerless first state always wins, so Compact would never engage).
    /// </summary>
    internal static int? FindWideBreakpoint(string xaml)
    {
        var stateAt = xaml.IndexOf("<VisualState x:Name=\"Wide\"", StringComparison.Ordinal);
        if (stateAt < 0) return null;
        var selfClose = xaml.IndexOf("/>", stateAt, StringComparison.Ordinal);
        var fullClose = xaml.IndexOf("</VisualState>", stateAt, StringComparison.Ordinal);
        string block = selfClose >= 0 && (fullClose < 0 || selfClose < fullClose)
            ? xaml.Substring(stateAt, selfClose - stateAt)
            : fullClose >= 0
                ? xaml.Substring(stateAt, fullClose - stateAt)
                : string.Empty;
        var m = Regex.Match(block, @"MinWindowWidth=""(\d+)""");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Returns the raw Compact visual-state block (empty when missing).</summary>
    internal static string FindCompactBlock(string xaml)
    {
        var stateAt = xaml.IndexOf("<VisualState x:Name=\"Compact\"", StringComparison.Ordinal);
        if (stateAt < 0) return string.Empty;
        var fullClose = xaml.IndexOf("</VisualState>", stateAt, StringComparison.Ordinal);
        return fullClose < 0 ? string.Empty : xaml.Substring(stateAt, fullClose - stateAt);
    }

    [Fact]
    public void WideState_OwnsBreakpoint()
    {
        // PT3-15 (2026-09-30 audit): the AdaptiveTrigger (window width) and the code-side
        // compact switch (control width) used to disagree (640 vs 730) and produced a mixed
        // state in between. Both now own the single 730 threshold.
        Assert.Equal(730, FindWideBreakpoint(ReadNowPlayingBarXaml()));
    }

    [Fact]
    public void Compact_CollapsesVolumeSlider()
    {
        var xaml = ReadNowPlayingBarXaml();
        var compact = FindCompactBlock(xaml);
        // The collapse target is the slider's host grid — the drag percent bubble overlay
        // lives in it too, so collapsing the slider alone would leave the overlay behind.
        Assert.Contains("Target=\"VolumeSliderHost.Visibility\"", compact, StringComparison.Ordinal);
        Assert.Contains("Value=\"Collapsed\"", compact, StringComparison.Ordinal);

        var host = Regex.Match(xaml, "<Grid x:Name=\"VolumeSliderHost\".*?</Grid>", RegexOptions.Singleline);
        Assert.True(host.Success, "VolumeSliderHost must wrap the volume slider.");
        Assert.Contains("x:Name=\"VolumeSlider\"", host.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_ShrinksArtAndChrome()
    {
        var compact = FindCompactBlock(ReadNowPlayingBarXaml());
        Assert.Contains("Target=\"ArtButton.Width\"", compact, StringComparison.Ordinal);
        Assert.Contains("Target=\"ArtButton.Height\"", compact, StringComparison.Ordinal);
        Assert.Contains("Target=\"RootLayout.Padding\"", compact, StringComparison.Ordinal);
        Assert.Contains("Target=\"TransportPanel.Spacing\"", compact, StringComparison.Ordinal);
        Assert.Contains("Target=\"ToolsPanel.Spacing\"", compact, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<VisualState x:Name=\"Wide\"/>", null)]
    [InlineData("<VisualState x:Name=\"Wide\"><VisualState.StateTriggers><AdaptiveTrigger MinWindowWidth=\"640\"/></VisualState.StateTriggers></VisualState>", 640)]
    public void Extractor_ReadsWideBreakpoint(string xaml, int? expected)
    {
        Assert.Equal(expected, FindWideBreakpoint(xaml));
    }

    [Theory]
    [InlineData("<VisualState x:Name=\"Compact\"><VisualState.Setters><Setter Target=\"VolumeSlider.Visibility\" Value=\"Collapsed\"/></VisualState.Setters></VisualState>", true)]
    [InlineData("<VisualState x:Name=\"Compact\"><VisualState.Setters><Setter Target=\"TrackArtist.Visibility\" Value=\"Collapsed\"/></VisualState.Setters></VisualState>", false)]
    public void Extractor_ReadsCompactBlock(string xaml, bool hasVolumeSetter)
    {
        Assert.Equal(hasVolumeSetter, FindCompactBlock(xaml).Contains("Target=\"VolumeSlider.Visibility\"", StringComparison.Ordinal));
    }
}
