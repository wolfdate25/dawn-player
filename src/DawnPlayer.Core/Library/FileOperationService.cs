using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Library;

public enum FileOpMode
{
    Move,
    Copy
}

public enum CollisionPolicy
{
    Skip,
    AutoRename,
    Overwrite
}

public enum FileOpStatus
{
    Ready,
    Conflict,
    Invalid,
    Skipped,
    Success,
    Failed
}

public record FileOpPlan(
    Track Track,
    string SourcePath,
    string DestinationPath,
    FileOpStatus Status,
    string? Message = null);

public record FileOpResult(
    int Succeeded,
    int Failed,
    int Skipped,
    IReadOnlyList<string> Errors);

public static class FileOperationService
{
    /// <summary>
    /// Plans file move or copy operations based on metadata patterns and collision policy without touching the disk.
    /// </summary>
    public static IReadOnlyList<FileOpPlan> PlanOperations(
        IEnumerable<Track> tracks,
        string baseDirectory,
        string pattern,
        FileOpMode mode,
        CollisionPolicy collisionPolicy)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Base directory cannot be empty.", nameof(baseDirectory));

        var plans = new List<FileOpPlan>();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (track == null) continue;

            // Invariant 1: Reject virtual CUE tracks and remote stream tracks
            if (track.SourceKind != TrackSourceKind.File || track.Path.Contains("#cue="))
            {
                plans.Add(new FileOpPlan(
                    track,
                    track.Path,
                    "",
                    FileOpStatus.Invalid,
                    "Virtual CUE tracks and remote streams are not supported for file operations."));
                continue;
            }

            string src = AppPaths.PhysicalPath(track.Path);
            string rel = FilePatternFormatter.Format(pattern, track);
            string dest = Path.GetFullPath(Path.Combine(baseDirectory, rel));

            // Check if source and destination are completely identical
            if (string.Equals(src, dest, StringComparison.Ordinal))
            {
                plans.Add(new FileOpPlan(track, src, dest, FileOpStatus.Skipped, "Source and destination paths are identical."));
                continue;
            }

            // Check case-only rename (e.g. file.mp3 -> File.mp3 on Windows)
            bool isCaseOnlyRename = string.Equals(src, dest, StringComparison.OrdinalIgnoreCase);

            if (!isCaseOnlyRename)
            {
                bool destExistsOnDisk = File.Exists(dest);
                bool destReserved = reservedDestinations.Contains(dest);

                if (destExistsOnDisk || destReserved)
                {
                    switch (collisionPolicy)
                    {
                        case CollisionPolicy.Skip:
                            plans.Add(new FileOpPlan(track, src, dest, FileOpStatus.Skipped, "Destination already exists (skipped by policy)."));
                            continue;

                        case CollisionPolicy.Overwrite:
                            // Overwrite is legitimate only against a file that already exists on
                            // disk. A destination reserved by an earlier plan of this same batch
                            // would clobber that track's just-moved data (source is gone by then),
                            // so a batch-internal convergence is blocked as a conflict instead.
                            if (destReserved)
                            {
                                plans.Add(new FileOpPlan(track, src, dest, FileOpStatus.Conflict,
                                    "Another track in this batch already targets this destination."));
                                continue;
                            }
                            break;

                        case CollisionPolicy.AutoRename:
                            string dir = Path.GetDirectoryName(dest) ?? baseDirectory;
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(dest);
                            string ext = Path.GetExtension(dest);

                            int counter = 1;
                            string newDest;
                            do
                            {
                                newDest = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
                                counter++;
                            } while (File.Exists(newDest) || reservedDestinations.Contains(newDest));

                            dest = newDest;
                            break;
                    }
                }
            }

            reservedDestinations.Add(dest);
            plans.Add(new FileOpPlan(track, src, dest, FileOpStatus.Ready));
        }

        return plans;
    }

    /// <summary>
    /// Executes planned file operations and synchronizes library database and playlists when files are moved.
    /// </summary>
    public static async Task<FileOpResult> ExecuteAsync(
        IReadOnlyList<FileOpPlan> plans,
        FileOpMode mode,
        IMusicLibrary? libraryStore = null,
        IPlaylistManager? playlistManager = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var readyPlans = plans.Where(p => p.Status == FileOpStatus.Ready).ToList();
        int skippedCount = plans.Count - readyPlans.Count;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
        var relocations = new List<(string OldPath, Track Track)>();

        await Task.Run(() =>
        {
            // 실행 전에 재생목록 항목의 원래 경로를 스냅숏한다. 이동 루프가 공유 Track 인스턴스의
            // Path를 먼저 바꾸면 아래 동기화의 구-경로 룩업이 항상 미스해 SavePlaylist가 생략되고,
            // 디스크의 재생목록 파일에 구 경로가 남는다(재시작 시 끊긴 항목).
            var playlistRefs = new List<(Playlist Pl, PlaylistItem Item, string OldPath)>();
            if (playlistManager != null)
            {
                foreach (var pl in playlistManager.Playlists)
                {
                    foreach (var item in pl.Items)
                    {
                        playlistRefs.Add((pl, item, item.Track.Path));
                    }
                }
            }

            for (int i = 0; i < readyPlans.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var plan = readyPlans[i];
                progress?.Invoke(i, readyPlans.Count);

                try
                {
                    string src = plan.SourcePath;
                    string dest = plan.DestinationPath;
                    string destDir = Path.GetDirectoryName(dest)!;

                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    if (mode == FileOpMode.Move)
                    {
                        bool isCaseOnly = string.Equals(src, dest, StringComparison.OrdinalIgnoreCase) &&
                                          !string.Equals(src, dest, StringComparison.Ordinal);

                        if (isCaseOnly)
                        {
                            // Two-step rename for Windows case-only change
                            string tempPath = dest + ".dawn_case_tmp_" + Guid.NewGuid().ToString("N");
                            File.Move(src, tempPath);
                            File.Move(tempPath, dest);
                        }
                        else
                        {
                            // 단일 원자 호출: Delete+Move 이원화는 그 사이 크래시 시 파일을
                            // 소멸시킨다. overwrite 의미는 동일하다.
                            File.Move(src, dest, overwrite: true);
                        }

                        relocations.Add((src, plan.Track));
                        plan.Track.Path = dest;
                    }
                    else
                    {
                        File.Copy(src, dest, overwrite: true);
                    }

                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{plan.SourcePath} -> {plan.DestinationPath}: {ex.Message}");
                }
            }

            progress?.Invoke(readyPlans.Count, readyPlans.Count);

            // Synchronize library DB and in-memory map
            if (relocations.Count > 0 && libraryStore != null)
            {
                try
                {
                    libraryStore.RelocateTracks(relocations);
                }
                catch (Exception ex)
                {
                    errors.Add($"Library database update failed: {ex.Message}");
                }
            }

            // Synchronize loaded playlists against the pre-run snapshot: shared Track instances
            // were renamed in place by the move loop, so their Current path no longer matches the
            // relocation map — the snapshot's original path is what still resolves.
            if (relocations.Count > 0 && playlistManager != null)
            {
                try
                {
                    var map = relocations.ToDictionary(r => r.OldPath, r => r.Track.Path, StringComparer.OrdinalIgnoreCase);
                    var touched = new HashSet<Playlist>();
                    foreach (var (pl, item, oldPath) in playlistRefs)
                    {
                        if (map.TryGetValue(oldPath, out var newPath))
                        {
                            item.Track.Path = newPath;
                            touched.Add(pl);
                        }
                    }
                    foreach (var pl in touched)
                    {
                        playlistManager.SavePlaylist(pl);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Playlist update failed: {ex.Message}");
                }
            }
        }, ct);

        return new FileOpResult(succeeded, failed, skippedCount, errors);
    }
}
