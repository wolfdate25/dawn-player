using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
// 'Playlist' would collide with this file's namespace; alias the Core type.
using PlaylistEntity = DawnPlayer.Core.Playlists.Playlist;

namespace DawnPlayer.App.ViewModels.Playlist;

/// <summary>
/// The playlist page's state and decision logic, extracted from code-behind so it is testable
/// without a WinUI surface. The page keeps the visuals (lists, menus, dialogs) and delegates
/// every decision here; selection arrays come in from the page, item mutations go out.
/// </summary>
public sealed class PlaylistViewModel
{
    private readonly AppSettings _settings;

    public PlaylistViewModel(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _grouped = settings.Ui.PlaylistGroupedView;
    }

    private bool _grouped;

    /// <summary>Whether the list renders grouped by album (persisted across launches).</summary>
    public bool Grouped => _grouped;

    /// <summary>Applies the group toggle. Returns true when the view must rebuild.</summary>
    public bool SetGrouped(bool grouped)
    {
        if (_grouped == grouped) return false;
        _grouped = grouped;
        _settings.Ui.PlaylistGroupedView = grouped;
        DawnPlayer.Core.Persistence.SettingsWriter.Schedule(_settings);
        return true;
    }

    /// <summary>Whether sorting applies: a single-row (or empty) playlist has nothing to sort.</summary>
    public static bool CanSort(PlaylistEntity? playlist) => playlist != null && playlist.Items.Count > 1;

    /// <summary>Whether a rating change applies to the selection.</summary>
    public static bool CanRate(IReadOnlyList<PlaylistItem> selection) => selection.Count > 0;

    /// <summary>Whether a move applies, and which items should stay selected afterwards.</summary>
    public static bool CanMove(IReadOnlyList<PlaylistItem> selection) => selection.Count > 0;

    /// <summary>Which warning to surface after a dead-item sweep. Returns null when nothing
    /// should be shown (the success-with-zero case shows the "nothing missing" note).</summary>
    public static (string Key, string Fallback, object[] Args)? DescribeDeadItemSweepOutcome(int removed)
    {
        return removed > 0
            ? ("Msg_RemovedMissingFiles", "존재하지 않는 파일 {0}곡을 재생목록에서 제거했습니다.", new object[] { removed })
            : ("Msg_NoMissingFiles", "제거할 누락된 파일이 없습니다.", Array.Empty<object>());
    }

    /// <summary>The menu check state after mirroring: always the controller's flag. The flag is
    /// also flipped by the keyboard shortcut and cleared by the controller once the track ends,
    /// so the menu follows the controller — never the reverse reading.</summary>
    public static bool SyncStopAfterCurrentMenu(bool controllerFlag, bool menuChecked) => controllerFlag;
}

/// <summary>
/// The library page's search/filter/layout decision logic. The page owns the visual tree and
/// the timers' plumbing; this class decides when a rebuild is due and clamps persisted values.
/// </summary>
public sealed class LibraryViewModel
{
    public const int SearchDebounceMs = 220;
    public const int RebuildDebounceMs = 250;

    private readonly AppSettings _settings;
    private string _searchText = string.Empty;
    private long _lastEditTick = -1;

    public LibraryViewModel(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The active search text (normalized: trimmed).</summary>
    public string SearchText => _searchText;

    /// <summary>Records a keystroke and reports whether a debounced rebuild should fire now.</summary>
    public bool OnSearchEdited(string text, long nowTickMs)
    {
        _searchText = text?.Trim() ?? string.Empty;
        var changed = _lastEditTick < 0 || nowTickMs - _lastEditTick >= SearchDebounceMs;
        _lastEditTick = nowTickMs;
        return changed;
    }

    /// <summary>Whether the pending search text differs from the currently applied filter.</summary>
    public bool IsSearchPending(string appliedFilter) =>
        !string.Equals(_searchText, appliedFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps the cover-grid zoom level the Ctrl+wheel handler produces.</summary>
    public static double ClampCoverZoom(double zoom) => Math.Clamp(zoom, 80, 260);

    /// <summary>Clamps a persisted sidebar width to the splitter's resizer range.</summary>
    public static double ClampSidebarWidth(double width, double min, double max) =>
        Math.Clamp(width, min, max);

    /// <summary>Persists the sidebar width if it moved meaningfully (avoids a settings write
    /// per pixel of a drag).</summary>
    public bool SaveSidebarWidth(double width, double current, double min, double max)
    {
        var clamped = ClampSidebarWidth(width, min, max);
        if (Math.Abs(clamped - current) < 1) return false;
        _settings.Ui.LeftSidebarWidth = clamped;
        DawnPlayer.Core.Persistence.SettingsWriter.Schedule(_settings);
        return true;
    }
}
