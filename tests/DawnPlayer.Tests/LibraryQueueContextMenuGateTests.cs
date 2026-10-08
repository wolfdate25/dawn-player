using System;
using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Library right-panel queue sidebar context-menu gate (2026-10-08 user request: 우클릭으로
/// 이 곡 재생 / 재생목록에서 곡 삭제). The sidebar (<c>RightQueueList</c>) renders the NowPlaying
/// playlist grouped by album, but unlike the playlist page its rows had no context menu.
///
/// Adversarial invariants encoded here:
/// 1. The menu must act on the row UNDER the POINTER. WinUI list trap (fixed on the playlist
///    page, PT2-11): a right-press never moves the selection, so a menu that reads SelectedItem
///    without a right-tap selection refresh deletes/plays the previously selected row — or
///    nobody. The refresh must resolve the row through the ListView container
///    (FindAncestor&lt;ListViewItem&gt; + ItemFromContainer): x:Bind item templates leave template
///    elements without a DataContext, so a DataContext walk silently fails.
/// 2. An unresolvable target (album group header, blank area) must leave the selection
///    untouched — the null-container guard must precede the SelectedItem assignment.
/// 3. Both menu actions must target AppServices.Playlists.NowPlaying — the exact playlist the
///    sidebar renders — read at click time, and must no-op when nothing is selected
///    (menu opened over a blank area must not surprise-play or surprise-delete anything).
/// </summary>
public class LibraryQueueContextMenuGateTests
{
    private static (string Page, string Code) ReadSources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var page = File.ReadAllText(Path.Combine(dir.FullName,
                "src", "DawnPlayer.App", "Views", "LibraryPage.xaml"));
            var code = File.ReadAllText(Path.Combine(dir.FullName,
                "src", "DawnPlayer.App", "Views", "LibraryPage.xaml.cs"));
            return (page, code);
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
        return (string.Empty, string.Empty);
    }

    /// <summary>Extracts the member starting at <paramref name="signature"/> up to the next
    /// class-level member (a line indented by exactly four spaces that is not a body brace).
    /// Body lines are deeper-indented or are the 4-space <c>{</c>/<c>}</c> delimiters, so they
    /// never match. Over-capture into the next member is harmless — every assertion below is a
    /// Contains within the region.</summary>
    private static string ExtractMember(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"member not found: {signature}");
        var next = Regex.Match(src[(start + 1)..], @"(?m)^    (?=[^\s{}])");
        Assert.True(next.Success, $"member never ends: {signature}");
        return src[start..(start + 1 + next.Index)];
    }

    private static string ExtractQueueListElement(string page)
    {
        var open = page.IndexOf("<ListView x:Name=\"RightQueueList\"", StringComparison.Ordinal);
        Assert.True(open >= 0, "RightQueueList element not found in LibraryPage.xaml");
        var close = page.IndexOf("</ListView>", open, StringComparison.Ordinal);
        Assert.True(close > open, "RightQueueList closing tag not found");
        return page[open..close];
    }

    [Fact]
    public void QueueList_WiresRightTapAndContextRequested_WithFlyout()
    {
        var element = ExtractQueueListElement(ReadSources().Page);

        // Both entry points are required: RightTapped catches pointer gestures (the platform
        // never delivers ContextRequested for them before the ListView swallows it), while
        // ContextRequested covers the keyboard menu key.
        Assert.Contains("RightTapped=\"OnRightQueueListRightTapped\"", element);
        Assert.Contains("ContextRequested=\"OnRightQueueListContextRequested\"", element);
        Assert.Contains("<ListView.ContextFlyout>", element);

        // The flyout must expose exactly the two requested actions, each wired to a handler.
        Assert.Contains("x:Uid=\"Library_QueueMenu_Play\"", element);
        Assert.Contains("Click=\"OnRightQueueMenuPlay\"", element);
        Assert.Contains("x:Uid=\"Library_QueueMenu_Remove\"", element);
        Assert.Contains("Click=\"OnRightQueueMenuRemove\"", element);
    }

    [Fact]
    public void SelectionRefresh_ResolvesRowThroughContainer_AndGuardsBlankTargets()
    {
        var body = ExtractMember(ReadSources().Code,
            "private void SelectQueueRowForContext(DependencyObject? source)");

        // Container resolution, not a DataContext walk (the x:Bind trap).
        Assert.Contains("FindAncestor<ListViewItem>(source)", body);
        Assert.Contains("ItemFromContainer(container)", body);
        Assert.DoesNotContain("FindAncestorDataContext", body);

        // The null-container guard must precede the selection write, so a right-click on an
        // album header or blank area cannot clobber the current selection.
        var guard = body.IndexOf("if (container == null) return;", StringComparison.Ordinal);
        var itemMatch = body.IndexOf("is PlaylistItem item", StringComparison.Ordinal);
        Assert.True(guard >= 0, "null-container guard missing");
        Assert.True(itemMatch > guard, "item resolution must follow the null-container guard");
        var assign = body.IndexOf("RightQueueList.SelectedItem =", itemMatch, StringComparison.Ordinal);
        Assert.True(assign > itemMatch, "selection write missing");
    }

    [Fact]
    public void MenuHandlers_TargetNowPlaying_AndNoOpWithoutSelection()
    {
        var code = ReadSources().Code;

        var play = ExtractMember(code, "private async void OnRightQueueMenuPlay");
        Assert.Contains("RightQueueList.SelectedItem is not PlaylistItem item", play);
        Assert.Contains("PlaybackUiHelper.PlayItemAsync(AppServices.Playback, AppServices.Playlists.NowPlaying, item)", play);

        var remove = ExtractMember(code, "private void OnRightQueueMenuRemove");
        Assert.Contains("RightQueueList.SelectedItem is not PlaylistItem item", remove);
        Assert.Contains("PlaybackUiHelper.RemoveItems(AppServices.Playlists, AppServices.Playlists.NowPlaying", remove);

        // Both handlers read the sidebar's own selection — never a click-coordinate re-resolution.
        // The selection refresh (pointer → row) is what guarantees they act on the clicked row.
        Assert.DoesNotContain("ResolveItem", play);
        Assert.DoesNotContain("ResolveItem", remove);
    }

    [Fact]
    public void RightTapAndContextRequested_RouteToTheSameSelectionRefresh()
    {
        var code = ReadSources().Code;

        var rightTap = ExtractMember(code, "private void OnRightQueueListRightTapped");
        var requested = ExtractMember(code, "private void OnRightQueueListContextRequested");
        Assert.Contains("SelectQueueRowForContext(e.OriginalSource as DependencyObject)", rightTap);
        Assert.Contains("SelectQueueRowForContext(e.OriginalSource as DependencyObject)", requested);
    }
}
