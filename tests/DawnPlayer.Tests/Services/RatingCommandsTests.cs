using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Services;

/// <summary>
/// L11 평점 UX 고도화 — 순수 평점 명령 계층의 적대적 케이스. 불변식:
/// (1) 스트림 URL은 어떤 입력 조합으로도 평점 대상이 되지 않는다.
/// (2) 경로 중복(대소문자 무시) 트랙은 한 번만 기록된다.
/// (3) 이미 동일 별점인 트랙은 대상에서 제외된다(전부 무변경이면 빈 목록 = 전체 노옵).
/// (4) 큐 트랙의 태그 쓰기 경로는 프래그먼트가 제거된 물리 경로다.
/// (5) 태그 쓰기 실패 0건은 사용자 통보(null)로 이어지지 않는다.
/// </summary>
public sealed class RatingCommandsTests
{
    private static Track Track(string path, int rating = 0) => new() { Path = path, Rating = rating };

    // ---------------- Normalize (clamp) ----------------

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    public void Normalize_ClampsToStarRange(int input, int expected)
    {
        Assert.Equal(expected, RatingCommands.Normalize(input));
    }

    // ---------------- IsRateable ----------------

    [Fact]
    public void IsRateable_NullTrack_ReturnsFalse()
    {
        Assert.False(RatingCommands.IsRateable(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://stream.example.com:8000/radio")]
    [InlineData("https://stream.example.com:8000/radio")]
    [InlineData("HTTP://UPPERCASE.EXAMPLE.COM/STREAM")]
    public void IsRateable_EmptyOrStreamUrl_ReturnsFalse(string path)
    {
        Assert.False(RatingCommands.IsRateable(Track(path)));
    }

    [Theory]
    [InlineData(@"C:\Music\song.flac")]
    [InlineData(@"C:\Music\album.flac#cue=0-212500")]
    public void IsRateable_FileAndCuePaths_ReturnsTrue(string path)
    {
        Assert.True(RatingCommands.IsRateable(Track(path)));
    }

    // ---------------- SelectTargets ----------------

    [Fact]
    public void SelectTargets_NullOrEmptyInput_ReturnsEmpty()
    {
        Assert.Empty(RatingCommands.SelectTargets(null, 3));
        Assert.Empty(RatingCommands.SelectTargets(new List<Track?>(), 3));
    }

    [Fact]
    public void SelectTargets_AllUnchanged_ReturnsEmpty_NoOpGuard()
    {
        var tracks = new List<Track?> { Track(@"C:\a.flac", 3), Track(@"C:\b.flac", 3) };

        var targets = RatingCommands.SelectTargets(tracks, 3);

        Assert.Empty(targets);
    }

    [Fact]
    public void SelectTargets_MixedRatings_OnlyChangedTracksSelected()
    {
        var unchanged = Track(@"C:\same.flac", 3);
        var changed = Track(@"C:\diff.flac", 2);
        var tracks = new List<Track?> { unchanged, changed };

        var targets = RatingCommands.SelectTargets(tracks, 3);

        var target = Assert.Single(targets);
        Assert.Same(changed, target);
    }

    [Fact]
    public void SelectTargets_DuplicatePathsCaseInsensitive_MergedToFirstOccurrence()
    {
        var first = Track(@"C:\Music\Song.flac", 0);
        var second = Track(@"C:\music\song.flac", 0);
        var tracks = new List<Track?> { first, second };

        var targets = RatingCommands.SelectTargets(tracks, 4);

        var target = Assert.Single(targets);
        Assert.Same(first, target);
    }

    [Fact]
    public void SelectTargets_StreamsAndNullsFiltered_ButCueKept()
    {
        var file = Track(@"C:\a.flac");
        var cue = Track(@"C:\b.flac#cue=0-1000");
        var stream = Track("http://radio.example.com/stream");
        var empty = Track("");
        var tracks = new List<Track?> { null, file, cue, stream, empty };

        var targets = RatingCommands.SelectTargets(tracks, 5);

        Assert.Equal(2, targets.Count);
        Assert.Same(file, targets[0]);
        Assert.Same(cue, targets[1]);
    }

    [Fact]
    public void SelectTargets_ClampsBeforeUnchangedComparison()
    {
        // 9 → 5로 클램프되므로, 이미 5점인 트랙은 무변경으로 스킵된다.
        var rated5 = Track(@"C:\a.flac", 5);
        var rated3 = Track(@"C:\b.flac", 3);

        var targets = RatingCommands.SelectTargets(new List<Track?> { rated5, rated3 }, 9);

        var target = Assert.Single(targets);
        Assert.Same(rated3, target);
    }

    // ---------------- TagWritePath ----------------

    [Theory]
    [InlineData(@"C:\Music\album.flac#cue=0-212500", @"C:\Music\album.flac")]
    [InlineData(@"C:\Music\song.mp3", @"C:\Music\song.mp3")]
    [InlineData("", "")]
    public void TagWritePath_StripsCueFragmentOnly(string path, string expected)
    {
        Assert.Equal(expected, RatingCommands.TagWritePath(path));
    }

    // ---------------- FormatTagWriteFailure ----------------

    [Fact]
    public void FormatTagWriteFailure_ZeroFailures_ReturnsNull_NoUserNotification()
    {
        Assert.Null(RatingCommands.FormatTagWriteFailure(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void FormatTagWriteFailure_FailuresPresent_ReturnsLocalizedMessageWithCount(int failed)
    {
        var message = RatingCommands.FormatTagWriteFailure(failed);

        Assert.False(string.IsNullOrEmpty(message));
        Assert.Contains(failed.ToString(), message, StringComparison.Ordinal);
    }

    // ---------------- 별점 표시/접근성 텍스트 (셀 렌더링 계약) ----------------

    [Theory]
    [InlineData(0, "☆")]
    [InlineData(-1, "☆")]
    [InlineData(3, "★★★")]
    [InlineData(5, "★★★★★")]
    [InlineData(7, "★★★★★")]
    public void RatingDisplayText_UnratedShowsOutlineStar_RatedShowsFilledClamped(int rating, string expected)
    {
        Assert.Equal(expected, RatingCommands.DisplayText(rating));
    }

    [Theory]
    [InlineData(0, "unrated")]
    [InlineData(-2, "unrated")]
    [InlineData(3, "rated3")]
    [InlineData(8, "rated5")]
    public void RatingAccessibilityText_CoversClampedRange(int rating, string bucket)
    {
        var text = RatingCommands.AccessibilityText(rating);

        Assert.False(string.IsNullOrEmpty(text));
        switch (bucket)
        {
            case "unrated":
                Assert.DoesNotContain("/", text, StringComparison.Ordinal);
                break;
            case "rated3":
                Assert.Contains("3", text, StringComparison.Ordinal);
                Assert.Contains("/", text, StringComparison.Ordinal);
                break;
            case "rated5":
                Assert.Contains("5", text, StringComparison.Ordinal);
                Assert.Contains("/", text, StringComparison.Ordinal);
                break;
        }
    }

    // ---------------- 라이브러리 평점 정렬 ----------------

    private static List<Track> Rated(params int[] ratings) =>
        ratings.Select((r, i) => Track($@"C:\t{i}.flac", r)).ToList();

    [Fact]
    public void FilterAndSort_RatingAscending_UnratedFirstThenLowestToHighest()
    {
        var tracks = Rated(3, 0, 5, 1);

        var sorted = DawnPlayer.App.Views.LibraryFilterService.FilterAndSort(
            tracks, null, "", DawnPlayer.App.Views.SortColumn.Rating, sortAscending: true);

        Assert.Equal(new[] { 0, 1, 3, 5 }, sorted.Select(t => t.Rating));
    }

    [Fact]
    public void FilterAndSort_RatingDescending_HighestFirstUnratedLast()
    {
        var tracks = Rated(3, 0, 5, 1);

        var sorted = DawnPlayer.App.Views.LibraryFilterService.FilterAndSort(
            tracks, null, "", DawnPlayer.App.Views.SortColumn.Rating, sortAscending: false);

        Assert.Equal(new[] { 5, 3, 1, 0 }, sorted.Select(t => t.Rating));
    }

    [Fact]
    public void FilterAndSort_RatingEqualRatings_PreserveInputOrder_StableSort()
    {
        var tracks = Rated(2, 4, 2, 4, 2);

        var sorted = DawnPlayer.App.Views.LibraryFilterService.FilterAndSort(
            tracks, null, "", DawnPlayer.App.Views.SortColumn.Rating, sortAscending: true);

        Assert.Equal(new[] { 2, 2, 2, 4, 4 }, sorted.Select(t => t.Rating));
        Assert.Equal(new[] { @"C:\t0.flac", @"C:\t2.flac", @"C:\t4.flac", @"C:\t1.flac", @"C:\t3.flac" },
            sorted.Select(t => t.Path));
    }

    [Fact]
    public void FilterAndSort_RatingAllUnrated_StaysStable()
    {
        var tracks = Rated(0, 0, 0);

        var sorted = DawnPlayer.App.Views.LibraryFilterService.FilterAndSort(
            tracks, null, "", DawnPlayer.App.Views.SortColumn.Rating, sortAscending: true);

        Assert.Equal(3, sorted.Count);
        Assert.All(sorted, t => Assert.Equal(0, t.Rating));
    }
}
