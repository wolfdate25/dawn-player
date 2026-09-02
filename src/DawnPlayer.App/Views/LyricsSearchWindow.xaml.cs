using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;
using DawnPlayer.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Views;

/// <summary>One row in the results list: the plugin it came from plus the (lazily fetched) lyrics.</summary>
public sealed class LyricsResultItemVm
{
    public LyricsPluginInfo Plugin { get; init; } = null!;
    public LyricsSearchResult Result { get; init; } = null!;

    /// <summary>Filled the first time this row is previewed, reused for apply/save (no refetch).</summary>
    public OnlineLyricsResult? Fetched { get; set; }

    public string PluginName => Plugin.Name;
    public string Title => string.IsNullOrWhiteSpace(Result.Title) ? AppStrings.Get("LyricsSearch_NoTitle", "(제목 없음)") : Result.Title!;
    public string Subtitle
    {
        get
        {
            var parts = new[] { Result.Artist, Result.Album }.Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" · ", parts);
        }
    }
    public string SyncLabel => Result.IsSynced ? AppStrings.Get("LyricsSearch_Sync_Synced", "동기") : AppStrings.Get("LyricsSearch_Sync_Unsynced", "비동기");
    public string DurationLabel => Result.DurationMs > 0 ? FormatDuration(Result.DurationMs) : "";
    public string ProviderKey => Plugin.Id;

    private static string FormatDuration(int milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }
}

public sealed partial class LyricsSearchWindow : Window
{
    private static LyricsSearchWindow? s_activeWindow;

    private Track _track = null!;
    private readonly ObservableCollection<LyricsResultItemVm> _results = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private int _previewGeneration;
    private bool _closed;

    public static void OpenForTrack(Track track)
    {
        if (s_activeWindow != null)
        {
            try
            {
                if (!ReferenceEquals(s_activeWindow._track, track))
                {
                    s_activeWindow.LoadTrack(track);
                }
                s_activeWindow.Activate();
                return;
            }
            catch { s_activeWindow = null; }
        }

        var win = new LyricsSearchWindow(track);
        s_activeWindow = win;
        win.Closed += (_, _) =>
        {
            if (s_activeWindow == win) s_activeWindow = null;
            win.CancelWork();
        };
        win.Activate();
    }

    public LyricsSearchWindow(Track track)
    {
        InitializeComponent();
        Title = AppStrings.Get("LyricsSearch_WindowTitle", "온라인 가사 검색 — Dawn Player");
        ResultsList.ItemsSource = _results;
        LoadTrack(track);
    }

    private void LoadTrack(Track track)
    {
        _track = track;
        TrackHeaderTitle.Text = track.Title;
        TrackHeaderArtist.Text = string.IsNullOrEmpty(track.Artist) ? "" : $"— {track.Artist}";
        TitleBox.Text = track.Title ?? "";
        ArtistBox.Text = track.Artist ?? "";
        AlbumBox.Text = track.Album ?? "";
        _results.Clear();
        PreviewText.Text = AppStrings.Get("LyricsSearch_PreviewPlaceholder", "왼쪽에서 검색 결과를 선택하세요.");
        ApplyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;

        if (AppServices.LyricsOnline == null || AppServices.LyricsOnline.Plugins.Count == 0)
        {
            StatusText.Text = AppStrings.Format("LyricsSearch_NoPluginsFound", AppPaths.PluginsDir);
            SearchButton.IsEnabled = false;
            return;
        }

        SearchButton.IsEnabled = true;
        StatusText.Text = "";
        // Opening on a track usually means "find lyrics for this one" — run the prefilled search.
        _ = RunSearchAsync();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        if (_closed || AppServices.LyricsOnline == null) return;

        var query = new LyricsSearchQuery
        {
            Title = NullIfBlank(TitleBox.Text),
            Artist = NullIfBlank(ArtistBox.Text),
            Album = NullIfBlank(AlbumBox.Text),
            DurationMs = _track.DurationMs > int.MaxValue ? int.MaxValue : (int)_track.DurationMs
        };

        if (query.Title == null && query.Artist == null && query.Album == null)
        {
            StatusText.Text = AppStrings.Get("LyricsSearch_SearchQueryEmpty", "제목, 아티스트 또는 앨범 중 하나는 입력해야 합니다.");
            return;
        }

        var previous = Interlocked.Exchange(ref _searchCts, new CancellationTokenSource());
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = _searchCts!;
        var token = cts.Token;

        SearchButton.IsEnabled = false;
        _results.Clear();
        StatusText.Text = AppStrings.Get("LyricsSearch_Searching", "검색 중...");
        var generation = ++_previewGeneration;

        try
        {
            var outcomes = await Task.Run(() => AppServices.LyricsOnline.SearchAsync(query, token)).ConfigureAwait(false);

            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_closed) return;
                foreach (var outcome in outcomes)
                {
                    foreach (var result in outcome.Results)
                        _results.Add(new LyricsResultItemVm { Plugin = outcome.Plugin, Result = result });
                }

                var failed = outcomes.Where(o => o.Error != null).Select(o => $"{o.Plugin.Name}: {o.Error}").ToList();
                StatusText.Text = _results.Count > 0
                    ? (failed.Count > 0
                        ? AppStrings.Format("LyricsSearch_ResultsWithFailures", _results.Count, failed.Count)
                        : AppStrings.Format("LyricsSearch_ResultsCount", _results.Count))
                    : (failed.Count > 0
                        ? AppStrings.Format("LyricsSearch_NoResultsWithErrors", string.Join(", ", failed))
                        : AppStrings.Get("LyricsSearch_NoResults", "결과 없음. 다른 검색어로 시도해 보세요."));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!_closed) StatusText.Text = AppStrings.Format("LyricsSearch_SearchFailed", ex.Message);
            });
        }
        finally
        {
            DispatcherQueue?.TryEnqueue(() => { if (!_closed) SearchButton.IsEnabled = true; });
            if (ReferenceEquals(_searchCts, cts))
                Interlocked.CompareExchange(ref _searchCts, null, cts);
            cts.Dispose();
        }
    }

    private async void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed) return;
        if (ResultsList.SelectedItem is not LyricsResultItemVm vm) return;

        if (vm.Fetched != null)
        {
            ShowPreview(vm.Fetched);
            return;
        }

        var generation = ++_previewGeneration;
        var previous = Interlocked.Exchange(ref _previewCts, new CancellationTokenSource());
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = _previewCts!;

        PreviewText.Text = AppStrings.Get("LyricsSearch_LoadingPreview", "불러오는 중...");
        try
        {
            var fetched = await Task.Run(() => AppServices.LyricsOnline.FetchAsync(vm.Plugin, vm.Result, _track, cts.Token)).ConfigureAwait(false);
            if (Volatile.Read(ref _previewGeneration) != generation) return;

            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_closed || !ReferenceEquals(ResultsList.SelectedItem, vm)) return;
                if (fetched is null)
                {
                    PreviewText.Text = AppStrings.Get("LyricsSearch_FetchFailedResult", "이 결과를 가져올 수 없습니다.");
                    return;
                }
                vm.Fetched = fetched;
                ShowPreview(fetched);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!_closed && ReferenceEquals(ResultsList.SelectedItem, vm))
                    PreviewText.Text = AppStrings.Format("LyricsSearch_FetchError", ex.Message);
            });
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
                Interlocked.CompareExchange(ref _previewCts, null, cts);
            cts.Dispose();
        }
    }

    private void ShowPreview(OnlineLyricsResult fetched)
    {
        PreviewText.Text = LrcParser.Format(fetched.Document);
        ApplyButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_closed || ResultsList.SelectedItem is not LyricsResultItemVm vm) return;
        var fetched = vm.Fetched ?? await FetchSelectedAsync(vm);
        if (fetched == null)
        {
            StatusText.Text = AppStrings.Get("LyricsSearch_ApplyFailed_NoLyrics", "가사를 가져올 수 없어 적용하지 못했습니다.");
            return;
        }
        AppServices.LyricsOnline!.ApplyResult(fetched, _track);
        StatusText.Text = AppStrings.Format("LyricsSearch_ApplySuccess", fetched.PluginName);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_closed || ResultsList.SelectedItem is not LyricsResultItemVm vm) return;
        var fetched = vm.Fetched ?? await FetchSelectedAsync(vm);
        if (fetched == null)
        {
            StatusText.Text = AppStrings.Get("LyricsSearch_SaveFailed_NoLyrics", "가사를 가져올 수 없어 저장하지 못했습니다.");
            return;
        }

        var outcome = AppServices.LyricsOnline!.SaveResult(fetched, _track);
        StatusText.Text = outcome.Result switch
        {
            LyricsSaveResult.Saved => AppStrings.Format("LyricsSearch_SaveResult_Saved", outcome.Path ?? ""),
            LyricsSaveResult.SkippedExisting => AppStrings.Format("LyricsSearch_SaveResult_Skipped", outcome.Path ?? ""),
            _ => outcome.Error ?? AppStrings.Get("LyricsSearch_SaveResult_Failed", "저장에 실패했습니다.")
        };
    }

    private async Task<OnlineLyricsResult?> FetchSelectedAsync(LyricsResultItemVm vm)
    {
        PreviewText.Text = AppStrings.Get("LyricsSearch_LoadingPreview", "불러오는 중...");
        var fetched = await Task.Run(() => AppServices.LyricsOnline!.FetchAsync(vm.Plugin, vm.Result, _track, CancellationToken.None)).ConfigureAwait(false);
        if (fetched != null)
        {
            vm.Fetched = fetched;
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (!_closed && ReferenceEquals(ResultsList.SelectedItem, vm)) ShowPreview(fetched);
            });
        }
        return fetched;
    }

    private void CancelWork()
    {
        _closed = true;
        try { Interlocked.Exchange(ref _searchCts, null)?.Cancel(); } catch (ObjectDisposedException) { }
        try { Interlocked.Exchange(ref _previewCts, null)?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
