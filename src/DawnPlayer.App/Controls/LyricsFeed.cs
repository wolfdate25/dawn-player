using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Shared lyrics engine for every lyrics surface (side pane, fullscreen): load precedence
/// (applied search-window pick → offline .lrc/embedded → session online fallback), VM list
/// build, style propagation, and the line+word tick mirror. Pure logic — no controls and no
/// threading: callers own workers, dispatching, scrolling and chrome. With two consumers the
/// decisions must live here, or the surfaces fork behavior (M5 extraction trigger).
/// </summary>
public sealed class LyricsFeed
{
    private readonly ILyricsOnlineService? _online;
    private readonly Func<AppSettings?> _settings;
    private bool _lastKaraokeHighlight;
    private bool _lastShowTranslation;

    public LyricsFeed(ILyricsOnlineService? online, Func<AppSettings?> settings)
    {
        _online = online;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>A loaded document plus the badge string describing where it came from.</summary>
    public sealed record FeedDocument(LyricsDocument Document, string Source);

    // ---------- load precedence (thread-safe; callers pick the worker) ----------

    /// <summary>The user's explicit search-window pick when it has lines. Instant (in-memory caches).</summary>
    public FeedDocument? PeekAppliedOverride(string trackPath)
    {
        var applied = _online?.GetAppliedResult(trackPath);
        if (applied is not { Document.HasLines: true }) return null;
        return new FeedDocument(applied.Document, OnlineBadge(applied.IsSynced, applied.PluginName));
    }

    /// <summary>Offline walk (pattern×folder probes plus embedded lyrics). Call from a worker thread.</summary>
    public FeedDocument? LoadOffline(Track track, AppSettings settings)
    {
        var doc = LyricsFinder.LoadLyrics(track, settings);
        return doc is { HasLines: true } ? new FeedDocument(doc, "") : null;
    }

    /// <summary>Session online result when the offline walk came up empty (no network touched).</summary>
    public FeedDocument? PeekSessionLyrics(string trackPath)
    {
        var online = _online?.GetSessionLyrics(trackPath);
        if (online is not { Document.HasLines: true }) return null;
        return new FeedDocument(online.Document, OnlineBadge(online.IsSynced, online.PluginName));
    }

    public static string OnlineBadge(bool isSynced, string pluginName) => isSynced
        ? AppStrings.Format("LyricsPane_SourceOnlineFormat", "온라인 · {0}", pluginName)
        : AppStrings.Format("LyricsPane_SourceOnlineUnsyncedFormat", "온라인 · {0} · 비동기", pluginName);

    // ---------- VM build / style ----------

    /// <summary>
    /// Builds the display VM list from a document. The font scales let a surface render the
    /// same settings at its own size (pane 1.0, fullscreen larger) without forking the wiring.
    /// </summary>
    public List<LrcLineVm> BuildLines(LyricsDocument? doc, double fontScale = 1.0, double activeScale = 1.0)
    {
        var lines = new List<LrcLineVm>();
        var settings = _settings();
        if (doc is not { HasLines: true } || settings == null) return lines;
        var s = settings.Lyrics;
        AcceptToggles(s);

        foreach (var line in doc.Lines)
        {
            lines.Add(new LrcLineVm
            {
                Time = line.Time,
                Text = line.Text,
                Translation = line.Translation,
                Words = line.Words is { Count: > 0 } ? line.Words : null,
                ShowTranslationSubLine = s.ShowTranslation,
                UseKaraoke = s.KaraokeHighlight && line.Words is { Count: > 0 },
                BaseFontSize = s.FontSize * fontScale,
                ActiveFontSize = s.ActiveFontSize * activeScale,
                EnableFocusEffect = s.EnableFocusEffect,
                FontFamily = s.FontFamily,
                CharacterSpacing = s.CharacterSpacing,
                LineHeight = s.LineHeight * fontScale,
                TextAlignment = s.Alignment
            });
        }
        return lines;
    }

    /// <summary>
    /// True when KaraokeHighlight/ShowTranslation flipped since the last BuildLines/ApplyStyle
    /// accepted them — a template-affecting switch needs a rebuild (a realized container picks
    /// its template only at realization). Callers rebuild on true, which re-accepts.
    /// </summary>
    public bool ConsumeToggleChange(AppSettings settings)
    {
        var s = settings.Lyrics;
        return s.KaraokeHighlight != _lastKaraokeHighlight || s.ShowTranslation != _lastShowTranslation;
    }

    private void AcceptToggles(LyricsSettings s)
    {
        _lastKaraokeHighlight = s.KaraokeHighlight;
        _lastShowTranslation = s.ShowTranslation;
    }

    /// <summary>Re-propagates typography and the translation switch onto existing VMs.</summary>
    public void ApplyStyle(List<LrcLineVm> lines, double fontScale = 1.0, double activeScale = 1.0)
    {
        var settings = _settings();
        if (settings == null) return;
        var s = settings.Lyrics;
        foreach (var l in lines)
        {
            l.BaseFontSize = s.FontSize * fontScale;
            l.ActiveFontSize = s.ActiveFontSize * activeScale;
            l.EnableFocusEffect = s.EnableFocusEffect;
            l.FontFamily = s.FontFamily;
            l.CharacterSpacing = s.CharacterSpacing;
            l.LineHeight = s.LineHeight * fontScale;
            l.TextAlignment = s.Alignment;
            l.ShowTranslationSubLine = s.ShowTranslation;
            l.NotifyTypographyChanged();
        }
    }

    // ---------- tick ----------

    public readonly record struct FeedTick(int Index, bool Changed, bool Fast);

    /// <summary>
    /// Line+word state mirror for one playback position (invariant I9: the highlighted word is
    /// the computed word). Stateless per document position — every tick recomputes from the
    /// position, so a dormant surface converges on its first tick after waking.
    /// </summary>
    public FeedTick Tick(List<LrcLineVm> lines, int currentIndex, TimeSpan position)
    {
        int index = currentIndex;
        var previous = currentIndex;
        int target = LyricsScrollSynchronizer.FindActiveLineIndex(lines, position, 0);
        bool changed = LyricsScrollSynchronizer.UpdateActiveLineState(lines, ref index, target);

        if (changed && previous >= 0 && previous < lines.Count)
        {
            lines[previous].ActiveWordIndex = -1; // no karaoke residue on the past line
        }

        var vm = index >= 0 && index < lines.Count ? lines[index] : null;
        if (vm is { UseKaraoke: true })
        {
            vm.ActiveWordIndex = KaraokeTimeline.WordIndexAt(vm.Words, position);
        }

        return new FeedTick(index, changed, vm is { UseKaraoke: true });
    }
}
