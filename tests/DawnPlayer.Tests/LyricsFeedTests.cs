using DawnPlayer.App.Controls;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Gate for the shared lyrics engine (plan §11.3/§11.4): load precedence (applied pick →
/// offline → session), generation-free VM build with karaoke/translation toggles, the toggle
/// flip detector, and the line+word tick mirror (I9). Both lyrics surfaces must read these
/// decisions from one place — a divergence here forks the UI behavior.
/// </summary>
public class LyricsFeedTests : IDisposable
{
    private readonly string _tempDir;

    public LyricsFeedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dawn-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>In-memory stub of the online-lyrics facade — the precedence decisions under
    /// test live in the feed, not in the service.</summary>
    private sealed class FakeOnline : ILyricsOnlineService
    {
        public Dictionary<string, OnlineLyricsResult> Applied { get; } = new();
        public Dictionary<string, OnlineLyricsResult> Session { get; } = new();

        public IReadOnlyList<LyricsPluginInfo> Plugins => Array.Empty<LyricsPluginInfo>();
        public IReadOnlyList<string> LoadErrors => Array.Empty<string>();
        public void ReloadPlugins() { }
        public OnlineLyricsResult? GetSessionLyrics(string trackPath) =>
            Session.TryGetValue(trackPath, out var result) ? result : null;
        public OnlineLyricsResult? GetAppliedResult(string trackPath) =>
            Applied.TryGetValue(trackPath, out var result) ? result : null;
        public void ClearAppliedResult(string trackPath) => Applied.Remove(trackPath);
        public Task<IReadOnlyList<PluginSearchOutcome>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSearchOutcome>>(Array.Empty<PluginSearchOutcome>());
        public Task<OnlineLyricsResult?> FetchAsync(LyricsPluginInfo plugin, LyricsSearchResult result, Track track, CancellationToken cancellationToken) =>
            Task.FromResult<OnlineLyricsResult?>(null);
        public void ApplyResult(OnlineLyricsResult result, Track track) => Session[track.Path ?? ""] = result;
        public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track) => default;
        public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track, bool overwriteOnce) => default;
    }

    private static (LyricsFeed Feed, FakeOnline Online, AppSettings Settings) CreateFeed()
    {
        var settings = AppSettings.CreateDefault();
        var online = new FakeOnline();
        return (new LyricsFeed(online, () => settings), online, settings);
    }

    private static OnlineLyricsResult MakeResult(string lrc, string plugin = "TestPlugin", bool synced = true)
    {
        var doc = LrcParser.Parse(lrc, "test-source");
        return new OnlineLyricsResult(doc, "testplugin", plugin, synced,
            new LyricsSearchResult { ResultId = "r1" });
    }

    private Track MakeTrack(string fileName)
    {
        return new Track { Path = Path.Combine(_tempDir, fileName), Title = Path.GetFileNameWithoutExtension(fileName) };
    }

    // ---------- load precedence ----------

    [Fact]
    public void AppliedOverrideIsServedWithBadge()
    {
        var (feed, online, _) = CreateFeed();
        online.Applied["C:\\song.mp3"] = MakeResult("[00:01.00]Hello");

        var peeked = feed.PeekAppliedOverride("C:\\song.mp3");

        Assert.NotNull(peeked);
        Assert.Equal("Hello", peeked!.Document.Lines[0].Text);
        Assert.Contains("TestPlugin", peeked.Source);
    }

    [Fact]
    public void AppliedOverrideWithoutLinesIsIgnored()
    {
        var (feed, online, _) = CreateFeed();
        online.Applied["C:\\song.mp3"] =
            new OnlineLyricsResult(LyricsDocument.Empty, "t", "TestPlugin", false,
                new LyricsSearchResult { ResultId = "r1" });

        Assert.Null(feed.PeekAppliedOverride("C:\\song.mp3"));
    }

    [Fact]
    public void NoOverrideReturnsNull()
    {
        var (feed, _, _) = CreateFeed();
        Assert.Null(feed.PeekAppliedOverride("C:\\nothing.mp3"));
    }

    [Fact]
    public void SessionFallbackIsServedAndBadged()
    {
        var (feed, online, _) = CreateFeed();
        var synced = MakeResult("[00:01.00]synced text");
        online.Session["C:\\a.mp3"] = synced;
        var unsynced = MakeResult("plain text only", synced: false);
        online.Session["C:\\b.mp3"] = unsynced;

        var a = feed.PeekSessionLyrics("C:\\a.mp3");
        var b = feed.PeekSessionLyrics("C:\\b.mp3");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Contains("TestPlugin", a!.Source);
        Assert.NotEqual(a.Source, b!.Source); // synced vs unsynced carry distinct badges
    }

    [Fact]
    public void LoadOfflineReadsTheSidecarFile()
    {
        var (feed, _, settings) = CreateFeed();
        settings.Lyrics.ReadEmbeddedLyrics = false; // keep TagLib away from the dummy file
        File.WriteAllText(Path.Combine(_tempDir, "song.lrc"), "[00:05.00]offline line", System.Text.Encoding.UTF8);
        var track = MakeTrack("song.mp3");

        var loaded = feed.LoadOffline(track, settings);

        Assert.NotNull(loaded);
        Assert.Equal("offline line", loaded!.Document.Lines[0].Text);
        Assert.Equal("", loaded.Source);
    }

    [Fact]
    public void LoadOfflineIsNullWhenNothingExists()
    {
        var (feed, _, settings) = CreateFeed();
        settings.Lyrics.ReadEmbeddedLyrics = false;
        Assert.Null(feed.LoadOffline(MakeTrack("missing.mp3"), settings));
    }

    // ---------- VM build + toggles ----------

    private static LyricsDocument MixedDocument() => LrcParser.Parse(
        "[00:01.00]<00:01.00>ab <00:02.00>cd\n[00:01.00]번역\n[00:05.00]plain");

    [Fact]
    public void BuildLinesCarriesTranslationWordsAndKaraoke()
    {
        var (feed, _, _) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument());

        Assert.Equal(2, lines.Count); // translation pair merged at parse
        Assert.Equal("번역", lines[0].Translation);
        Assert.NotNull(lines[0].Words);
        Assert.True(lines[0].UseKaraoke);
        Assert.True(lines[0].ShowTranslationSubLine);
        Assert.True(lines[0].HasVisibleTranslation);
        Assert.Null(lines[1].Words);
        Assert.False(lines[1].UseKaraoke);
        Assert.False(lines[1].HasVisibleTranslation);
    }

    [Fact]
    public void KaraokeToggleOffDisablesKaraokeTemplate()
    {
        var (feed, _, settings) = CreateFeed();
        settings.Lyrics.KaraokeHighlight = false;
        var lines = feed.BuildLines(MixedDocument());
        Assert.All(lines, l => Assert.False(l.UseKaraoke));
        Assert.NotNull(lines[0].Words); // data preserved, template choice off
    }

    [Fact]
    public void TranslationToggleOffHidesTheSubLine()
    {
        var (feed, _, settings) = CreateFeed();
        settings.Lyrics.ShowTranslation = false;
        var lines = feed.BuildLines(MixedDocument());
        Assert.All(lines, l => Assert.False(l.HasVisibleTranslation));
    }

    [Fact]
    public void EmptyDocumentBuildsNoLines()
    {
        var (feed, _, _) = CreateFeed();
        Assert.Empty(feed.BuildLines(null));
        Assert.Empty(feed.BuildLines(LyricsDocument.Empty));
    }

    [Fact]
    public void FontScaleMultipliesTypography()
    {
        var (feed, _, settings) = CreateFeed();
        double scale = 1.6;
        var lines = feed.BuildLines(MixedDocument(), scale, scale * 0.95);

        Assert.Equal(settings.Lyrics.FontSize * scale, lines[0].BaseFontSize, 10);
        Assert.Equal(settings.Lyrics.ActiveFontSize * scale * 0.95, lines[0].ActiveFontSize, 10);
        Assert.Equal(settings.Lyrics.LineHeight * scale, lines[0].LineHeight, 10);
    }

    // ---------- toggle flip detector ----------

    [Fact]
    public void ToggleFlipIsConsumedOncePerBuild()
    {
        var (feed, _, settings) = CreateFeed();
        feed.BuildLines(MixedDocument()); // accepts current toggles

        settings.Lyrics.KaraokeHighlight = false;
        Assert.True(feed.ConsumeToggleChange(settings)); // caller rebuilds on true…

        feed.BuildLines(MixedDocument()); // …which re-accepts
        Assert.False(feed.ConsumeToggleChange(settings));
    }

    [Fact]
    public void TranslationFlipAlsoTriggers()
    {
        var (feed, _, settings) = CreateFeed();
        feed.BuildLines(MixedDocument());
        settings.Lyrics.ShowTranslation = false;
        Assert.True(feed.ConsumeToggleChange(settings));
    }

    // ---------- tick (I9) ----------

    [Fact]
    public void TickBeforeFirstLineStaysDormant()
    {
        var (feed, _, _) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument());

        var tick = feed.Tick(lines, -1, TimeSpan.FromMilliseconds(500));

        Assert.Equal(-1, tick.Index);
        Assert.False(tick.Changed);
        Assert.False(tick.Fast);
    }

    [Fact]
    public void TickActivatesKaraokeLineAndMirrorsTheWord()
    {
        var (feed, _, _) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument());

        var tick = feed.Tick(lines, -1, TimeSpan.FromSeconds(1.5));

        Assert.Equal(0, tick.Index);
        Assert.True(tick.Changed);
        Assert.True(tick.Fast);
        Assert.Equal(KaraokeTimeline.WordIndexAt(lines[0].Words, TimeSpan.FromSeconds(1.5)),
            lines[0].ActiveWordIndex);
    }

    [Fact]
    public void TickAdvancesWordWithinTheLine()
    {
        var (feed, _, _) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument());
        feed.Tick(lines, -1, TimeSpan.FromSeconds(1.0));

        var tick = feed.Tick(lines, 0, TimeSpan.FromSeconds(2.5));

        Assert.Equal(0, tick.Index);
        Assert.False(tick.Changed);
        Assert.True(tick.Fast);
        Assert.Equal(1, lines[0].ActiveWordIndex); // second word starts at 2s
    }

    [Fact]
    public void TickLineMoveResetsTheOldLineAndSlowsDown()
    {
        var (feed, _, _) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument());
        feed.Tick(lines, -1, TimeSpan.FromSeconds(1.0));
        Assert.Equal(0, lines[0].ActiveWordIndex); // warm state

        var tick = feed.Tick(lines, 0, TimeSpan.FromSeconds(5.5));

        Assert.Equal(1, tick.Index);
        Assert.True(tick.Changed);
        Assert.False(tick.Fast); // plain line → slow cadence
        Assert.Equal(-1, lines[0].ActiveWordIndex); // karaoke residue reset
    }

    // ---------- style ----------

    [Fact]
    public void ApplyStyleRepropagatesScaleAndToggles()
    {
        var (feed, _, settings) = CreateFeed();
        var lines = feed.BuildLines(MixedDocument(), 1.6, 1.6);

        settings.Lyrics.ShowTranslation = false;
        feed.ApplyStyle(lines, 1.0, 1.0);

        Assert.Equal(settings.Lyrics.FontSize, lines[0].BaseFontSize, 10);
        Assert.False(lines[0].ShowTranslationSubLine);
    }
}
