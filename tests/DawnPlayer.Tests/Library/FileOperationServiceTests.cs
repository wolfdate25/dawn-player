using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Library;

public class FileOperationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileOperationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_FileOpTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void PlanOperations_FiltersVirtualCueAndStreamTracks()
    {
        var cueTrack = new Track
        {
            Path = @"C:\Music\album.flac#cue=2",
            Artist = "Artist",
            Title = "Track 2",
            SourceKind = TrackSourceKind.File
        };
        var streamTrack = new Track
        {
            Path = "http://stream.example.com/radio.mp3",
            Artist = "Radio",
            Title = "Live Stream",
            SourceKind = TrackSourceKind.Radio
        };
        var normalTrack = new Track
        {
            Path = @"C:\Music\real.mp3",
            Artist = "Real Artist",
            Title = "Real Song",
            SourceKind = TrackSourceKind.File
        };

        var plans = FileOperationService.PlanOperations(
            [cueTrack, streamTrack, normalTrack],
            _tempDir,
            "%artist% - %title%",
            FileOpMode.Move,
            CollisionPolicy.AutoRename);

        Assert.Equal(3, plans.Count);
        Assert.Equal(FileOpStatus.Invalid, plans[0].Status); // cueTrack
        Assert.Equal(FileOpStatus.Invalid, plans[1].Status); // streamTrack
        Assert.Equal(FileOpStatus.Ready, plans[2].Status);   // normalTrack
    }

    [Fact]
    public void PlanOperations_CollisionAutoRename_DisambiguatesDuplicates()
    {
        var t1 = new Track { Path = @"C:\src\track1.mp3", Artist = "Artist", Title = "Same Song", SourceKind = TrackSourceKind.File };
        var t2 = new Track { Path = @"C:\src\track2.mp3", Artist = "Artist", Title = "Same Song", SourceKind = TrackSourceKind.File };

        var plans = FileOperationService.PlanOperations(
            [t1, t2],
            _tempDir,
            "%artist% - %title%",
            FileOpMode.Copy,
            CollisionPolicy.AutoRename);

        Assert.Equal(2, plans.Count);
        Assert.Equal(FileOpStatus.Ready, plans[0].Status);
        Assert.Equal(FileOpStatus.Ready, plans[1].Status);
        Assert.NotEqual(plans[0].DestinationPath, plans[1].DestinationPath);
        Assert.Contains("(1)", plans[1].DestinationPath);
    }

    [Fact]
    public void PlanOperations_CollisionSkip_MarksExistingFileAsSkipped()
    {
        // Pre-create destination file on disk
        string existingDest = Path.Combine(_tempDir, "Artist - Song.mp3");
        File.WriteAllText(existingDest, "existing");

        var t = new Track { Path = @"C:\src\input.mp3", Artist = "Artist", Title = "Song", SourceKind = TrackSourceKind.File };

        var plans = FileOperationService.PlanOperations(
            [t],
            _tempDir,
            "%artist% - %title%",
            FileOpMode.Copy,
            CollisionPolicy.Skip);

        Assert.Single(plans);
        Assert.Equal(FileOpStatus.Skipped, plans[0].Status);
    }

    [Fact]
    public async Task ExecuteAsync_MoveAndCopy_PerformsActionsCorrectly()
    {
        string srcFile1 = Path.Combine(_tempDir, "src1.mp3");
        string srcFile2 = Path.Combine(_tempDir, "src2.mp3");
        File.WriteAllText(srcFile1, "hello src1");
        File.WriteAllText(srcFile2, "hello src2");

        var t1 = new Track { Path = srcFile1, Artist = "Artist1", Title = "Track1", SourceKind = TrackSourceKind.File };
        var t2 = new Track { Path = srcFile2, Artist = "Artist2", Title = "Track2", SourceKind = TrackSourceKind.File };

        // Test Move
        var movePlans = FileOperationService.PlanOperations(
            [t1],
            _tempDir,
            "Organized/%artist%/%title%",
            FileOpMode.Move,
            CollisionPolicy.Overwrite);

        var moveResult = await FileOperationService.ExecuteAsync(movePlans, FileOpMode.Move);
        Assert.Equal(1, moveResult.Succeeded);
        Assert.False(File.Exists(srcFile1));
        Assert.True(File.Exists(movePlans[0].DestinationPath));
        Assert.Equal("hello src1", File.ReadAllText(movePlans[0].DestinationPath));

        // Test Copy
        var copyPlans = FileOperationService.PlanOperations(
            [t2],
            _tempDir,
            "Organized/%artist%/%title%",
            FileOpMode.Copy,
            CollisionPolicy.Overwrite);

        var copyResult = await FileOperationService.ExecuteAsync(copyPlans, FileOpMode.Copy);
        Assert.Equal(1, copyResult.Succeeded);
        Assert.True(File.Exists(srcFile2)); // Original still exists
        Assert.True(File.Exists(copyPlans[0].DestinationPath));
        Assert.Equal("hello src2", File.ReadAllText(copyPlans[0].DestinationPath));
    }

    [Fact]
    public async Task ExecuteAsync_CaseOnlyRename_SucceedsOnWindows()
    {
        string srcFile = Path.Combine(_tempDir, "lowercasename.mp3");
        File.WriteAllText(srcFile, "data");

        var t = new Track { Path = srcFile, Artist = "Band", Title = "Song", SourceKind = TrackSourceKind.File };

        // Plan rename in same folder to different case
        string newName = Path.Combine(_tempDir, "LOWERCASEname.mp3");
        var plan = new FileOpPlan(t, srcFile, newName, FileOpStatus.Ready);

        var result = await FileOperationService.ExecuteAsync([plan], FileOpMode.Move);
        Assert.Equal(1, result.Succeeded);
        Assert.True(File.Exists(newName));
    }

    [Fact]
    public void PlanOperations_OverwriteBatchCollision_LaterTrackConflictsInsteadOfClobbering()
    {
        // 불변식: Overwrite 정책이 허용하는 것은 "디스크에 이미 있던 파일"의 대체뿐이다.
        // 같은 배치의 선행 트랙이 방금 예약한 dest를 후행 트랙이 덮어쓰면 선행 트랙의 데이터가
        // 원본과 함께 소멸하므로, 배치 내 수렴은 실행 전에 충돌로 봉쇄되어야 한다.
        var t1 = new Track { Path = @"C:\src\a1.mp3", Artist = "Artist", Title = "Same Song", SourceKind = TrackSourceKind.File };
        var t2 = new Track { Path = @"C:\src\a2.mp3", Artist = "Artist", Title = "Same Song", SourceKind = TrackSourceKind.File };

        var plans = FileOperationService.PlanOperations(
            [t1, t2],
            _tempDir,
            "%artist% - %title%",
            FileOpMode.Move,
            CollisionPolicy.Overwrite);

        Assert.Equal(2, plans.Count);
        Assert.Equal(FileOpStatus.Ready, plans[0].Status);
        Assert.Equal(FileOpStatus.Conflict, plans[1].Status);
    }

    [Fact]
    public async Task ExecuteAsync_Move_UpdatesSharedAndDistinctPlaylistTracksAndSaves()
    {
        // 불변식: 이동 실행 후 (1) 재생목록의 트랙 경로가 새 경로로 갱신되고 (2) 재생목록 저장이
        // 트리거되어야 한다. 공유 Track 인스턴스의 Path가 먼저 바뀌면 구-경로 룩업이 항상
        // 미스해 저장이 생략됐었다 — 디스크의 재생목록 파일에 구 경로가 남는 회귀.
        string srcFile = Path.Combine(_tempDir, "shared.mp3");
        File.WriteAllText(srcFile, "music");

        var sharedTrack = new Track { Path = srcFile, Artist = "Artist", Title = "Song", SourceKind = TrackSourceKind.File };
        var shadowTrack = new Track { Path = srcFile, Artist = "Artist", Title = "Song", SourceKind = TrackSourceKind.File };

        var fake = new RecordingPlaylistManager();
        var plShared = new Playlist("Shared Instance");
        plShared.Items.Add(new PlaylistItem(sharedTrack));
        var plDistinct = new Playlist("Distinct Instance");
        plDistinct.Items.Add(new PlaylistItem(shadowTrack));
        fake.Playlists.Add(plShared);
        fake.Playlists.Add(plDistinct);

        var plans = FileOperationService.PlanOperations(
            [sharedTrack],
            _tempDir,
            "Moved/%title%",
            FileOpMode.Move,
            CollisionPolicy.AutoRename);

        var result = await FileOperationService.ExecuteAsync(plans, FileOpMode.Move, libraryStore: null, playlistManager: fake);

        Assert.Equal(1, result.Succeeded);
        Assert.False(File.Exists(srcFile));
        string dest = plans[0].DestinationPath;
        Assert.True(File.Exists(dest));

        Assert.Equal(dest, plShared.Items[0].Track.Path);
        Assert.Equal(dest, plDistinct.Items[0].Track.Path);
        Assert.Contains(plShared, fake.SavedPlaylists);
        Assert.Contains(plDistinct, fake.SavedPlaylists);
    }

    /// <summary>SavePlaylist 호출만 관찰하는 최소 위조 — 이 테스트는 재생목록 동기화 경로만 검증한다.</summary>
    private sealed class RecordingPlaylistManager : IPlaylistManager
    {
        public System.Collections.Generic.List<Playlist> SavedPlaylists { get; } = [];
        public ObservableCollection<Playlist> Playlists { get; } = [];

        public void SavePlaylist(Playlist playlist) => SavedPlaylists.Add(playlist);

        public Playlist Current => throw new NotSupportedException();
        public Playlist? TryGetCurrent() => throw new NotSupportedException();
        public Playlist NowPlaying => throw new NotSupportedException();
        public Playlist? ActivePlaylist { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public event Action<Playlist, IReadOnlyList<PlaylistItem>>? ItemsRemoved { add { } remove { } }
        public Playlist CreatePlaylist(string? name = null) => throw new NotSupportedException();
        public Playlist CreatePlaylistFromTracks(string? name, IEnumerable<Track> tracks) => throw new NotSupportedException();
        public void AddPlaylist(Playlist playlist) => throw new NotSupportedException();
        public void SelectPlaylist(Playlist pl) => throw new NotSupportedException();
        public void RemovePlaylist(Playlist pl) => throw new NotSupportedException();
        public void RemovePlaylist(string playlistIdOrName) => throw new NotSupportedException();
        public void RenamePlaylist(Playlist pl, string newName) => throw new NotSupportedException();
        public System.Collections.Generic.List<PlaylistItem> AddPaths(Playlist pl, IEnumerable<string> paths, int? insertAt = null) => throw new NotSupportedException();
        public Task<System.Collections.Generic.List<PlaylistItem>> AddPathsAsync(Playlist pl, IEnumerable<string> paths, int? insertAt = null, CancellationToken ct = default) => throw new NotSupportedException();
        public System.Collections.Generic.List<PlaylistItem> AddFiles(Playlist pl, IEnumerable<string> files, int? insertAt = null) => throw new NotSupportedException();
        public Task<System.Collections.Generic.List<PlaylistItem>> AddFilesAsync(Playlist pl, IEnumerable<string> files, int? insertAt = null, CancellationToken ct = default) => throw new NotSupportedException();
        public System.Collections.Generic.List<PlaylistItem> AddTracks(Playlist pl, IEnumerable<Track> tracks, int? insertAt = null) => throw new NotSupportedException();
        public Playlist? ImportPlaylist(string filePath, string? playlistName = null) => throw new NotSupportedException();
        public Task<Playlist?> ImportPlaylistAsync(string filePath, string? playlistName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void RemoveItems(Playlist pl, IReadOnlyList<PlaylistItem> items) => throw new NotSupportedException();
        public void RemoveAll(Playlist pl) => throw new NotSupportedException();
        public System.Collections.Generic.List<PlaylistItem> ReplaceWithTracks(Playlist pl, IEnumerable<Track> tracks) => throw new NotSupportedException();
        public void Sort(Playlist pl, PlaylistSort sort) => throw new NotSupportedException();
        public void RemoveDuplicates(Playlist pl) => throw new NotSupportedException();
        public bool MoveSelection(Playlist pl, IReadOnlyList<PlaylistItem> selected, bool up) => throw new NotSupportedException();
        public void MoveItem(Playlist pl, int oldIndex, int newIndex) => throw new NotSupportedException();
        public int RemoveDeadItems(Playlist pl) => throw new NotSupportedException();
        public Task<int> RemoveDeadItemsAsync(Playlist pl, CancellationToken ct = default) => throw new NotSupportedException();
        public bool CanUndo(Playlist pl) => throw new NotSupportedException();
        public bool CanRedo(Playlist pl) => throw new NotSupportedException();
        public bool Undo(Playlist pl) => throw new NotSupportedException();
        public bool Redo(Playlist pl) => throw new NotSupportedException();
        public void SaveAll() => throw new NotSupportedException();
        public void LoadAll() => throw new NotSupportedException();
    }
}
