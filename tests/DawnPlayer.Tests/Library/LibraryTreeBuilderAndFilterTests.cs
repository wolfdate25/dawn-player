using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Library;

public sealed class LibraryTreeBuilderAndFilterTests
{
    private static Track MakeTrack(
        string path = @"C:\Music\Artist1\Album1\01-Track.mp3",
        string title = "Song A",
        string artist = "Artist One",
        string albumArtist = "",
        string album = "Album One",
        string genre = "Rock",
        int trackNo = 1,
        int year = 2024,
        long durationMs = 180000)
    {
        return new Track
        {
            Path = path,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            Genre = genre,
            TrackNo = trackNo,
            Year = year,
            DurationMs = durationMs
        };
    }

    // =========================================================================
    // 1. LibraryTreeModelBuilder: 7가지 그룹핑 모드와 계층 구조 정확성
    // =========================================================================

    [Fact]
    public void BuildTree_EmptyTrackList_ReturnsOnlyAllNode()
    {
        var tracks = new List<Track>();
        var roots = new List<LibraryTreeNode>();

        foreach (var mode in Enum.GetValues<TreeGroupMode>())
        {
            var all = LibraryTreeModelBuilder.BuildTree(tracks, mode, roots);

            Assert.Single(roots);
            Assert.Same(all, roots[0]);
            Assert.Equal("전체 (All)", all.Title);
            Assert.Equal(0, all.Count);
            Assert.Equal("All", all.FilterType);
            Assert.True(all.DefaultExpanded);
        }
    }

    [Fact]
    public void BuildTree_SingleTrack_AllModesProduceValidNodes()
    {
        var tracks = new List<Track>
        {
            MakeTrack(path: @"C:\Music\ArtistX\AlbumY\01.mp3", artist: "Artist X", album: "Album Y", genre: "Jazz", title: "Track 1")
        };
        var roots = new List<LibraryTreeNode>();

        foreach (var mode in Enum.GetValues<TreeGroupMode>())
        {
            var all = LibraryTreeModelBuilder.BuildTree(tracks, mode, roots);

            Assert.Equal(2, roots.Count); // "전체" 루트 + 카테고리 루트 1개
            Assert.Equal(1, all.Count);
        }
    }

    [Fact]
    public void BuildTree_ArtistAlbumMode_CreatesTwoTierHierarchy()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Queen", album: "A Night at the Opera", title: "Bohemian Rhapsody", trackNo: 11),
            MakeTrack(artist: "Queen", album: "News of the World", title: "We Will Rock You", trackNo: 1),
            MakeTrack(artist: "Pink Floyd", album: "The Dark Side of the Moon", title: "Time", trackNo: 4),
            MakeTrack(artist: "Queen", album: "", title: "Live Improv", trackNo: 1)
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.ArtistAlbum, roots);

        // roots[0]은 "전체", 이후 아티스트 알파벳 순: Pink Floyd, Queen
        Assert.Equal(3, roots.Count);
        Assert.Equal("전체 (All)", roots[0].Title);

        var pinkFloyd = roots[1];
        Assert.Equal("Pink Floyd", pinkFloyd.Title);
        Assert.Equal(1, pinkFloyd.Count);
        Assert.Single(pinkFloyd.Children);

        var queen = roots[2];
        Assert.Equal("Queen", queen.Title);
        Assert.Equal(3, queen.Count);
        Assert.Equal(3, queen.Children.Count);

        var unknownAlbum = queen.Children.FirstOrDefault(c => c.Title == "(Single / Unknown)");
        Assert.NotNull(unknownAlbum);
        Assert.Equal("", unknownAlbum.FilterValue);
        Assert.Equal("Queen", unknownAlbum.FilterExtra);
        Assert.Equal(1, unknownAlbum.Count);
    }

    [Fact]
    public void BuildTree_ArtistAlbumMode_SameAlbumNameDifferentArtists_SeparatesCorrectly()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Artist 1", album: "Greatest Hits"),
            MakeTrack(artist: "Artist 2", album: "Greatest Hits")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.ArtistAlbum, roots);

        Assert.Equal(3, roots.Count);
        Assert.Equal("Artist 1", roots[1].Title);
        Assert.Equal("Artist 2", roots[2].Title);

        var album1 = roots[1].Children[0];
        Assert.Equal("Greatest Hits", album1.Title);
        Assert.Equal("Artist 1", album1.FilterExtra);

        var album2 = roots[2].Children[0];
        Assert.Equal("Greatest Hits", album2.Title);
        Assert.Equal("Artist 2", album2.FilterExtra);
    }

    [Fact]
    public void BuildTree_ArtistMode_FlatArtistNodes()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Zebra", album: "A"),
            MakeTrack(artist: "Alpha", album: "B"),
            MakeTrack(artist: "Alpha", album: "C")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Artist, roots);

        Assert.Equal(3, roots.Count);

        Assert.Equal("Alpha", roots[1].Title);
        Assert.Equal(2, roots[1].Count);
        Assert.Empty(roots[1].Children);

        Assert.Equal("Zebra", roots[2].Title);
        Assert.Equal(1, roots[2].Count);
        Assert.Empty(roots[2].Children);
    }

    [Fact]
    public void BuildTree_GenreArtistMode_CreatesGenreAndArtistHierarchy()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Miles Davis", genre: "Jazz"),
            MakeTrack(artist: "John Coltrane", genre: "Jazz"),
            MakeTrack(artist: "Queen", genre: "Rock"),
            MakeTrack(artist: "NoGenre", genre: "") // 장르 없는 트랙은 장르 트리에서 제외
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.GenreArtist, roots);

        // 전체(4), Jazz(2), Rock(1)
        Assert.Equal(3, roots.Count);

        var jazz = roots[1];
        Assert.Equal("Jazz", jazz.Title);
        Assert.Equal(2, jazz.Count);
        Assert.Equal(2, jazz.Children.Count);

        var artist1 = jazz.Children[0];
        Assert.Equal("John Coltrane", artist1.Title);
        Assert.Equal("GenreArtist", artist1.FilterType);
        Assert.Equal("John Coltrane", artist1.FilterValue);
        Assert.Equal("Jazz", artist1.FilterExtra);
    }

    [Fact]
    public void BuildTree_GenreArtistAlbumMode_CreatesThreeTierHierarchy()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Daft Punk", album: "Discovery", genre: "Electronic", title: "One More Time"),
            MakeTrack(artist: "Daft Punk", album: "RAM", genre: "Electronic", title: "Get Lucky"),
            MakeTrack(artist: "Kraftwerk", album: "Autobahn", genre: "Electronic", title: "Autobahn")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.GenreArtistAlbum, roots);

        Assert.Equal(2, roots.Count); // 전체 + Electronic
        var electronic = roots[1];
        Assert.Equal(2, electronic.Children.Count); // Daft Punk + Kraftwerk

        var daftPunk = electronic.Children.First(c => c.Title == "Daft Punk");
        Assert.Equal(2, daftPunk.Children.Count); // Discovery + RAM

        var ram = daftPunk.Children.First(c => c.Title == "RAM");
        Assert.Equal("GenreArtistAlbum", ram.FilterType);
        Assert.Equal("RAM", ram.FilterValue);
        Assert.Equal("Daft Punk", ram.FilterExtra);
        Assert.Equal("Electronic", ram.FilterExtra2);
    }

    [Fact]
    public void BuildTree_AlbumMode_FiltersEmptyAlbumsAndDisplaysArtist()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Radiohead", album: "OK Computer"),
            MakeTrack(artist: "Radiohead", album: "Kid A"),
            MakeTrack(artist: "Various", album: "") // 앨범 없는 트랙은 제외
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Album, roots);

        // 전체(3), Kid A(1), OK Computer(1)
        Assert.Equal(3, roots.Count);
        Assert.Equal("Kid A — Radiohead", roots[1].Title);
        Assert.Equal("Album", roots[1].FilterType);
    }

    [Fact]
    public void BuildTree_GenreMode_FlatGenreNodes()
    {
        var tracks = new List<Track>
        {
            MakeTrack(genre: "Classical"),
            MakeTrack(genre: "Classical"),
            MakeTrack(genre: "Pop"),
            MakeTrack(genre: "")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Genre, roots);

        Assert.Equal(3, roots.Count); // 전체, Classical, Pop
        Assert.Equal("Classical", roots[1].Title);
        Assert.Equal(2, roots[1].Count);
        Assert.Empty(roots[1].Children);
    }

    // =========================================================================
    // 2. 폴더 계층: 재귀 트리, 서브트리 카운트, 단일 자식 체인 단순화
    // =========================================================================

    [Fact]
    public void BuildTree_FolderMode_SingleChildChain_SimplifiesRootToDeepestAncestor()
    {
        var tracks = new List<Track>
        {
            MakeTrack(path: @"C:\Music\Rock\2020\AlbumA\track1.mp3"),
            MakeTrack(path: @"C:\Music\Rock\2020\AlbumA\track2.mp3"),
            MakeTrack(path: @"C:\Music\Rock\2020\AlbumB\track3.mp3")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);

        // C:\, Music, Rock은 각각 직접 파일 0개 + 자식 1개이므로 C:\Music\Rock\2020까지 접힌다
        Assert.Equal(2, roots.Count);

        var folderRoot = roots[1];
        Assert.Equal(@"C:\Music\Rock\2020", folderRoot.Title);
        Assert.Equal(@"C:\Music\Rock\2020", folderRoot.FilterValue);
        Assert.Equal(3, folderRoot.Count);
        Assert.True(folderRoot.DefaultExpanded);

        Assert.Equal(2, folderRoot.Children.Count);
        var albumA = folderRoot.Children[0];
        Assert.Equal("AlbumA", albumA.Title);
        Assert.Equal(@"C:\Music\Rock\2020\AlbumA", albumA.FilterValue);
        Assert.Equal(2, albumA.Count);

        var albumB = folderRoot.Children[1];
        Assert.Equal("AlbumB", albumB.Title);
        Assert.Equal(@"C:\Music\Rock\2020\AlbumB", albumB.FilterValue);
        Assert.Equal(1, albumB.Count);
    }

    [Fact]
    public void BuildTree_FolderMode_DirectFilesAtRoot_DoesNotSimplifyAwayRoot()
    {
        var tracks = new List<Track>
        {
            MakeTrack(path: @"C:\Music\RootSong.mp3"),
            MakeTrack(path: @"C:\Music\Sub\SubSong.mp3")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);

        // C:\Music에 직접 파일이 있으므로 C:\Music\Sub로 접히면 안 된다
        Assert.Equal(2, roots.Count);
        var folderRoot = roots[1];
        Assert.Equal(@"C:\Music", folderRoot.Title);
        Assert.Equal(2, folderRoot.Count); // 직접 1개 + 하위 1개
        Assert.Single(folderRoot.Children);

        var sub = folderRoot.Children[0];
        Assert.Equal("Sub", sub.Title);
        Assert.Equal(1, sub.Count);
    }

    [Fact]
    public void BuildTree_FolderMode_MultipleDrives_ConstructsSeparateRoots()
    {
        var tracks = new List<Track>
        {
            MakeTrack(path: @"C:\Audio\trackC.flac"),
            MakeTrack(path: @"D:\FLAC\trackD.flac"),
            MakeTrack(path: @"E:\Library\HighRes\trackE.dsd")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);

        // 전체 + C:\Audio + D:\FLAC + E:\Library\HighRes
        Assert.Equal(4, roots.Count);
        Assert.Equal("전체 (All)", roots[0].Title);
        Assert.Equal(@"C:\Audio", roots[1].Title);
        Assert.Equal(@"D:\FLAC", roots[2].Title);
        Assert.Equal(@"E:\Library\HighRes", roots[3].Title);
    }

    [Fact]
    public void BuildTree_FolderMode_ForwardSlashesAndBackslashes_NormalizeToSameFolder()
    {
        var tracks = new List<Track>
        {
            MakeTrack(path: "C:/Normalized/Path/track1.mp3"),
            MakeTrack(path: "C:\\Normalized\\Path\\track2.mp3")
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);

        Assert.Equal(2, roots.Count);
        var folderRoot = roots[1];
        Assert.Equal(@"C:\Normalized\Path", folderRoot.Title);
        Assert.Equal(2, folderRoot.Count);
    }

    [Fact]
    public void BuildTree_FolderMode_Deep50LevelHierarchy_CalculatesSubtreeCountsAccurately()
    {
        var parts = new List<string> { "C:" };
        for (int i = 1; i <= 50; i++) parts.Add($"Level_{i}");

        var deepDir = Path.Combine(parts.ToArray());
        var tracks = new List<Track>
        {
            MakeTrack(path: Path.Combine(deepDir, "deep_track1.mp3")),
            MakeTrack(path: Path.Combine(deepDir, "deep_track2.mp3"))
        };
        var roots = new List<LibraryTreeNode>();

        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);

        Assert.Equal(2, roots.Count);
        var folderRoot = roots[1];
        Assert.Equal(deepDir, folderRoot.Title);
        Assert.Equal(2, folderRoot.Count);
        Assert.Empty(folderRoot.Children);
    }

    // =========================================================================
    // 3. 노드 검색 (FindNodeRecursive)
    // =========================================================================

    [Fact]
    public void FindNodeRecursive_DeeplyNestedNode_FindsLeafByFilterCriteria()
    {
        var tracks = new List<Track>
        {
            MakeTrack(artist: "Dream Theater", album: "Scenes from a Memory", genre: "ProgMetal", title: "Overture 1928")
        };
        var roots = new List<LibraryTreeNode>();
        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.GenreArtistAlbum, roots);

        var found = LibraryTreeModelBuilder.FindNodeRecursive(roots, "GenreArtistAlbum", "Scenes from a Memory", "Dream Theater");

        Assert.NotNull(found);
        Assert.Equal("Scenes from a Memory", found.FilterValue);
        Assert.Equal("Dream Theater", found.FilterExtra);
        Assert.Equal("ProgMetal", found.FilterExtra2);
    }

    [Fact]
    public void FindNodeRecursive_NonExistentCriteria_ReturnsNull()
    {
        var tracks = new List<Track> { MakeTrack(artist: "Led Zeppelin") };
        var roots = new List<LibraryTreeNode>();
        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Artist, roots);

        var result = LibraryTreeModelBuilder.FindNodeRecursive(roots, "Artist", "NonExistentArtist", null);

        Assert.Null(result);
    }

    // =========================================================================
    // 4. LibraryFilterService: 다중 조건 필터링
    // =========================================================================

    [Fact]
    public void FilterAndSort_EachFilterType_FiltersCorrectly()
    {
        var t1 = MakeTrack(path: @"C:\Music\Rock\Track1.mp3", artist: "Artist A", album: "Album 1", genre: "Rock", trackNo: 1);
        var t2 = MakeTrack(path: @"C:\Music\Rock\Track2.mp3", artist: "Artist A", album: "Album 2", genre: "Rock", trackNo: 2);
        var t3 = MakeTrack(path: @"C:\Music\Pop\Track3.mp3", artist: "Artist B", album: "Album 3", genre: "Pop", trackNo: 1);
        var tracks = new List<Track> { t1, t2, t3 };

        var resAll = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "All" }, "", SortColumn.None, true);
        Assert.Equal(3, resAll.Count);

        var resArtist = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "Artist", FilterValue = "Artist A" }, "", SortColumn.None, true);
        Assert.Equal(2, resArtist.Count);
        Assert.All(resArtist, t => Assert.Equal("Artist A", t.SortArtist));

        var resAlbum = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "Album", FilterValue = t1.AlbumKey }, "", SortColumn.None, true);
        Assert.Single(resAlbum);
        Assert.Equal("Album 1", resAlbum[0].Album);

        var resArtistAlbum = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "ArtistAlbum", FilterValue = "Album 2", FilterExtra = "Artist A" }, "", SortColumn.None, true);
        Assert.Single(resArtistAlbum);
        Assert.Equal("Album 2", resArtistAlbum[0].Album);

        var resGenre = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "Genre", FilterValue = "Pop" }, "", SortColumn.None, true);
        Assert.Single(resGenre);
        Assert.Equal("Artist B", resGenre[0].Artist);

        var resGenreArtist = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "GenreArtist", FilterValue = "Artist A", FilterExtra = "Rock" }, "", SortColumn.None, true);
        Assert.Equal(2, resGenreArtist.Count);

        var resGenreArtistAlbum = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "GenreArtistAlbum", FilterValue = "Album 1", FilterExtra = "Artist A", FilterExtra2 = "Rock" }, "", SortColumn.None, true);
        Assert.Single(resGenreArtistAlbum);
        Assert.Equal("Album 1", resGenreArtistAlbum[0].Album);

        var resFolder = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "Folder", FilterValue = @"C:\Music\Rock" }, "", SortColumn.None, true);
        Assert.Equal(2, resFolder.Count);

        // 알 수 없는 FilterType은 전체 반환으로 폴백
        var resUnknown = LibraryFilterService.FilterAndSort(tracks, new LibraryTreeNode { FilterType = "CustomUnknown" }, "", SortColumn.None, true);
        Assert.Equal(3, resUnknown.Count);
    }

    [Fact]
    public void FilterAndSort_SearchQuery_MatchesTitleArtistOrAlbum_CaseInsensitive()
    {
        var t1 = MakeTrack(title: "Hotel California", artist: "Eagles", album: "Hotel California (1976)");
        var t2 = MakeTrack(title: "Desperado", artist: "Eagles", album: "Desperado");
        var t3 = MakeTrack(title: "California Dreamin'", artist: "The Mamas & The Papas", album: "If You Can Believe");
        var tracks = new List<Track> { t1, t2, t3 };

        // 앞뒤 공백은 제거되고 대소문자 무시로 매칭된다
        var res1 = LibraryFilterService.FilterAndSort(tracks, null, "  CALIFORNIA  ", SortColumn.None, true);
        Assert.Equal(2, res1.Count);
        Assert.Contains(t1, res1);
        Assert.Contains(t3, res1);

        var res2 = LibraryFilterService.FilterAndSort(tracks, null, "eagles", SortColumn.None, true);
        Assert.Equal(2, res2.Count);
        Assert.Contains(t1, res2);
        Assert.Contains(t2, res2);

        // 정규식 특수문자는 리터럴 부분 문자열로 취급된다
        var res3 = LibraryFilterService.FilterAndSort(tracks, null, "(1976)", SortColumn.None, true);
        Assert.Single(res3);
        Assert.Equal(t1, res3[0]);

        var res4 = LibraryFilterService.FilterAndSort(tracks, null, "NonExistentSong123", SortColumn.None, true);
        Assert.Empty(res4);
    }

    [Fact]
    public void FilterAndSort_UnicodeAndHangulSearch_MatchesCorrectTracks()
    {
        var t1 = MakeTrack(title: "좋은 날", artist: "아이유", album: "Real");
        var t2 = MakeTrack(title: "Hype Boy", artist: "NewJeans", album: "New Jeans");
        var t3 = MakeTrack(title: "Ditto", artist: "NewJeans", album: "OMG");
        var tracks = new List<Track> { t1, t2, t3 };

        var resHangul = LibraryFilterService.FilterAndSort(tracks, null, "아이유", SortColumn.None, true);
        Assert.Single(resHangul);
        Assert.Equal("좋은 날", resHangul[0].Title);

        var resAlbum = LibraryFilterService.FilterAndSort(tracks, null, "omg", SortColumn.None, true);
        Assert.Single(resAlbum);
        Assert.Equal("Ditto", resAlbum[0].Title);
    }

    // =========================================================================
    // 5. LibraryFilterService: 5개 컬럼 정렬 (오름차순 / 내림차순)
    // =========================================================================

    [Fact]
    public void FilterAndSort_FiveColumnSorting_AscendingAndDescending()
    {
        var t1 = MakeTrack(title: "C Track", artist: "B Artist", album: "Z Album", trackNo: 3, durationMs: 100000);
        var t2 = MakeTrack(title: "A Track", artist: "C Artist", album: "A Album", trackNo: 1, durationMs: 300000);
        var t3 = MakeTrack(title: "B Track", artist: "A Artist", album: "A Album", trackNo: 2, durationMs: 200000);
        var tracks = new List<Track> { t1, t2, t3 };

        var ascTrackNo = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.TrackNo, true);
        Assert.Equal(new[] { 1, 2, 3 }, ascTrackNo.Select(t => t.TrackNo));
        var descTrackNo = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.TrackNo, false);
        Assert.Equal(new[] { 3, 2, 1 }, descTrackNo.Select(t => t.TrackNo));

        var ascTitle = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Title, true);
        Assert.Equal(new[] { "A Track", "B Track", "C Track" }, ascTitle.Select(t => t.Title));
        var descTitle = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Title, false);
        Assert.Equal(new[] { "C Track", "B Track", "A Track" }, descTitle.Select(t => t.Title));

        var ascArtist = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Artist, true);
        Assert.Equal(new[] { "A Artist", "B Artist", "C Artist" }, ascArtist.Select(t => t.SortArtist));
        var descArtist = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Artist, false);
        Assert.Equal(new[] { "C Artist", "B Artist", "A Artist" }, descArtist.Select(t => t.SortArtist));

        // 앨범 정렬은 2차 키로 TrackNo를 쓴다
        var ascAlbum = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Album, true);
        Assert.Equal(new[] { t2, t3, t1 }, ascAlbum);
        var descAlbum = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Album, false);
        Assert.Equal(new[] { t1, t3, t2 }, descAlbum);

        var ascDur = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Duration, true);
        Assert.Equal(new long[] { 100000, 200000, 300000 }, ascDur.Select(t => t.DurationMs));
        var descDur = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Duration, false);
        Assert.Equal(new long[] { 300000, 200000, 100000 }, descDur.Select(t => t.DurationMs));
    }

    [Fact]
    public void FilterAndSort_SortByArtist_RespectsAlbumArtistPrecedence()
    {
        var t1 = MakeTrack(artist: "Soloist 1", albumArtist: "Orchestra A", title: "Concerto 1");
        var t2 = MakeTrack(artist: "Soloist 2", albumArtist: "Orchestra B", title: "Concerto 2");
        var t3 = MakeTrack(artist: "Soloist A", albumArtist: "", title: "Solo Piece");
        var tracks = new List<Track> { t1, t2, t3 };

        var sorted = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Artist, true);

        Assert.Equal(new[] { "Orchestra A", "Orchestra B", "Soloist A" }, sorted.Select(t => t.SortArtist));
    }

    // =========================================================================
    // 6. BuildAlbumCardModels: 그룹핑과 폴백
    // =========================================================================

    [Fact]
    public void BuildAlbumCardModels_MissingTags_AppliesFallbacksAndGroupsByAlbumKey()
    {
        var t1 = MakeTrack(album: "Abbey Road", artist: "The Beatles", year: 1969);
        var t2 = MakeTrack(album: "Abbey Road", artist: "The Beatles", year: 1969);
        var t3 = MakeTrack(album: "", artist: "", year: 0); // 앨범/아티스트 태그 없음
        var tracks = new List<Track> { t1, t2, t3 };

        var cards = LibraryFilterService.BuildAlbumCardModels(tracks);

        Assert.Equal(2, cards.Count);

        var abbey = cards[0];
        Assert.Equal(t1.AlbumKey, abbey.Key);
        Assert.Equal("Abbey Road", abbey.Album);
        Assert.Equal("The Beatles", abbey.Artist);
        Assert.Equal(1969, abbey.Year);
        Assert.Equal(2, abbey.Tracks.Count);

        var unknown = cards[1];
        Assert.Equal("(앨범 없음)", unknown.Album);
        Assert.Equal("(아티스트 없음)", unknown.Artist);
        Assert.Equal(0, unknown.Year);
        Assert.Single(unknown.Tracks);
    }

    // =========================================================================
    // 7. 대규모 스트레스 테스트 (10,000 트랙)
    // =========================================================================

    [Fact]
    public void BuildTreeAndFilterAndCards_10000Tracks_PerformanceAndCorrectness()
    {
        var rng = new Random(42);
        var tracks = new List<Track>(10000);

        for (int i = 0; i < 10000; i++)
        {
            int artistId = i % 100;
            int albumId = i % 500;
            int genreId = i % 10;
            int folderId = i % 20;

            tracks.Add(new Track
            {
                Path = $@"C:\Music\Genre_{genreId}\Folder_{folderId}\Artist_{artistId}\Album_{albumId}\track_{i % 12}.mp3",
                Title = $"Track Title {i}",
                Artist = $"Artist {artistId}",
                AlbumArtist = (i % 5 == 0) ? $"AlbumArtist {artistId}" : "",
                Album = $"Album {albumId}",
                Genre = $"Genre {genreId}",
                TrackNo = (i % 12) + 1,
                Year = 2000 + (i % 25),
                DurationMs = 120000 + rng.Next(180000)
            });
        }

        var roots = new List<LibraryTreeNode>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var mode in Enum.GetValues<TreeGroupMode>())
        {
            LibraryTreeModelBuilder.BuildTree(tracks, mode, roots);
            Assert.NotEmpty(roots);
        }
        sw.Stop();
        // 10,000 트랙 기준 7개 모드 전체 빌드가 1500ms 안에 충분히 끝나야 한다
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Tree build took {sw.ElapsedMilliseconds}ms which exceeded threshold");

        sw.Restart();
        var filtered = LibraryFilterService.FilterAndSort(
            tracks,
            new LibraryTreeNode { FilterType = "Artist", FilterValue = "Artist 42" },
            "Title 42",
            SortColumn.Duration,
            false);
        sw.Stop();
        Assert.NotEmpty(filtered);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Filter & sort took {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        var cards = LibraryFilterService.BuildAlbumCardModels(tracks);
        sw.Stop();
        Assert.Equal(500, cards.Count);
        Assert.True(sw.ElapsedMilliseconds < 200, $"BuildAlbumCardModels took {sw.ElapsedMilliseconds}ms");
    }
}
