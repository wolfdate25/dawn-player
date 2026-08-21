using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Playlists;

public enum PlaylistSort { Title, Artist, Album, TrackNo, Path, Random, Reverse }

/// <summary>Owns the playlist collection: creation, batch editing, parallel tag reading, and m3u8 persistence.</summary>
public sealed class PlaylistManager : IPlaylistManager
{
    private readonly IMusicLibrary _library;
    private readonly ConcurrentDictionary<Playlist, System.Threading.Timer> _saveTimers = new();
    private readonly object _saveLock = new();

    /// <summary>
    /// Serializes playlist-file writes against playlist-file deletes. Without it a debounced save
    /// that had already passed its "still registered?" check could run its M3u.Write *after*
    /// RemovePlaylist deleted the file, resurrecting a playlist the user had just deleted.
    /// Ordering is always _fileLock before _saveLock; nothing takes them the other way round.
    /// </summary>
    private readonly object _fileLock = new();

    public const string NowPlayingPlaylistName = "Now Playing";

    public ObservableCollection<Playlist> Playlists { get; } = new();
    private Playlist? _current;
    private Playlist? _nowPlaying;

    public Playlist NowPlaying
    {
        get
        {
            lock (_saveLock)
            {
                if (_nowPlaying != null && Playlists.Contains(_nowPlaying))
                    return _nowPlaying;

                var existing = Playlists.FirstOrDefault(p => p != null && p.IsSystem);
                if (existing != null)
                {
                    _nowPlaying = existing;
                    return _nowPlaying;
                }

                var newNp = new Playlist(NowPlayingPlaylistName) { IsSystem = true };
                newNp.Items.CollectionChanged += (_, _) => ScheduleSave(newNp);
                newNp.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Playlist.Name)) ScheduleSave(newNp); };
                Playlists.Insert(0, newNp);
                _nowPlaying = newNp;
                return newNp;
            }
        }
    }

    public Playlist Current
    {
        get
        {
            lock (_saveLock)
            {
                if (_current != null && Playlists.Contains(_current))
                    return _current;

                var userPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem);
                if (userPl != null)
                {
                    _current = userPl;
                    return _current;
                }

                _current = NowPlaying;
                return _current;
            }
        }
    }

    /// <summary>
    /// The current playlist if one exists, without creating anything.
    /// </summary>
    /// <remarks>
    /// <see cref="Current"/> and <see cref="NowPlaying"/> create a playlist and insert it into
    /// <see cref="Playlists"/>, which is bound straight to a WinUI control — so they are only safe
    /// on the UI thread. Play-order resolution runs on the thread pool and must use this instead.
    /// A null result means there is nothing to play, which is the correct answer there.
    /// </remarks>
    public Playlist? TryGetCurrent()
    {
        lock (_saveLock)
        {
            if (_current != null && Playlists.Contains(_current)) return _current;

            var userPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem);
            if (userPl != null) return userPl;

            if (_nowPlaying != null && Playlists.Contains(_nowPlaying)) return _nowPlaying;

            return Playlists.FirstOrDefault(p => p != null && p.IsSystem);
        }
    }

    public Playlist? ActivePlaylist
    {
        get => Current;
        set
        {
            if (value != null) SelectPlaylist(value);
        }
    }

    /// <summary>Items were removed from a playlist (used to purge them from the playback queue).</summary>
    public event Action<Playlist, IReadOnlyList<PlaylistItem>>? ItemsRemoved;

    /// <summary>
    /// Marshals collection writes onto the thread that owns the UI. <see cref="Playlists"/> and
    /// each <see cref="Playlist.Items"/> are bound directly to WinUI controls, which are
    /// thread-affine, while the async add/import paths complete on a thread-pool thread. Leave
    /// null (the default) for headless use and tests, where writes run inline on the caller.
    /// </summary>
    public Action<Action>? UiInvoke { get; set; }

    /// <summary>True while <see cref="LoadAll"/> is populating playlists from disk.</summary>
    private volatile bool _loading;

    public PlaylistManager(IMusicLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Playlists.CollectionChanged += OnPlaylistsChanged;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes when it has run.</summary>
    private Task RunOnUiAsync(Action action)
    {
        var invoke = UiInvoke;
        if (invoke == null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        invoke(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Playlist CreatePlaylist(string? name = null)
    {
        lock (_saveLock)
        {
            return CreatePlaylistLocked(name);
        }
    }

    public Playlist CreatePlaylistFromTracks(string? name, IEnumerable<Track> tracks)
    {
        var pl = CreatePlaylist(name);
        if (tracks != null)
        {
            AddTracks(pl, tracks);
        }
        return pl;
    }

    private Playlist CreatePlaylistLocked(string? name = null)
    {
        // Explicit names go through the uniqueness check too. Two playlists with the same name map
        // to the same .m3u8, and the second one's debounced save silently overwrote the first
        // (trivially reproducible with "Save queue" twice).
        name = UniqueNameLocked(name ?? "재생목록");
        var pl = new Playlist(name);
        pl.Items.CollectionChanged += (_, _) => ScheduleSave(pl);
        pl.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Playlist.Name)) ScheduleSave(pl); };
        Playlists.Add(pl);
        if (_current == null || _current.IsSystem)
        {
            _current = pl;
        }
        return pl;
    }

    public void AddPlaylist(Playlist playlist)
    {
        if (playlist == null) return;
        lock (_saveLock)
        {
            if (Playlists.Contains(playlist)) return;
            playlist.Items.CollectionChanged += (_, _) => ScheduleSave(playlist);
            playlist.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Playlist.Name)) ScheduleSave(playlist); };
            Playlists.Add(playlist);
            if (!playlist.IsSystem && (_current == null || _current.IsSystem))
            {
                _current = playlist;
            }
        }
    }

    private string UniqueNameLocked(string baseName)
    {
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = baseName + " ";
        int maxIndex = 1;
        bool baseNameFound = false;

        for (int i = 0; i < Playlists.Count; i++)
        {
            var p = Playlists[i];
            if (p == null || string.IsNullOrEmpty(p.Name)) continue;

            existingNames.Add(p.Name);
            // Names are also compared after sanitization, because that is what decides the file
            // name: "Rock/Pop" and "Rock_Pop" are different playlists but the same file.
            existingNames.Add(SanitizeFileName(p.Name));

            if (string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase))
            {
                baseNameFound = true;
            }
            else if (p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(p.Name.AsSpan(prefix.Length), out int num))
            {
                if (num > maxIndex) maxIndex = num;
            }
        }

        if (!baseNameFound && !existingNames.Contains(baseName) && !existingNames.Contains(SanitizeFileName(baseName)))
            return baseName;

        for (int i = 2; i <= maxIndex + 1; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!existingNames.Contains(candidate)) return candidate;
        }

        return $"{baseName} {maxIndex + 1}";
    }

    public void SelectPlaylist(Playlist pl)
    {
        if (pl == null) return;
        lock (_saveLock)
        {
            _current = pl;
        }
    }

    public void RemovePlaylist(Playlist pl)
    {
        if (pl == null) return;

        if (pl.IsSystem)
        {
            // System NowPlaying playlist cannot be deleted; clear its contents instead.
            RemoveAll(pl);
            return;
        }

        Timer? timerToDispose = null;
        int idx;
        lock (_saveLock)
        {
            idx = Playlists.IndexOf(pl);
            if (idx < 0) return;

            Playlists.RemoveAt(idx);

            _saveTimers.TryRemove(pl, out timerToDispose);

            if (_current == pl)
            {
                var nextUserPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem);
                _current = nextUserPl ?? (Playlists.Count > 0 ? Playlists[Math.Min(idx, Playlists.Count - 1)] : null);
            }

            if (!Playlists.Any(p => p != null && !p.IsSystem))
            {
                _current = CreatePlaylistLocked();
            }
        }

        timerToDispose?.Dispose();

        var removedItems = pl.GetSnapshot();
        if (removedItems.Length > 0)
        {
            ItemsRemoved?.Invoke(pl, removedItems);
        }

        DeletePlaylistFile(pl);
    }

    public void RemovePlaylist(string playlistIdOrName)
    {
        if (string.IsNullOrWhiteSpace(playlistIdOrName)) return;
        Playlist? pl;
        lock (_saveLock)
        {
            pl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem && string.Equals(p.Name, playlistIdOrName, StringComparison.OrdinalIgnoreCase))
                ?? Playlists.FirstOrDefault(p => p != null && string.Equals(p.Name, playlistIdOrName, StringComparison.OrdinalIgnoreCase));
        }
        if (pl != null)
        {
            RemovePlaylist(pl);
        }
    }

    public void RenamePlaylist(Playlist pl, string newName)
    {
        if (pl == null || pl.IsSystem || string.IsNullOrWhiteSpace(newName)) return;

        string old;
        lock (pl.SyncRoot)
        {
            old = pl.Name;
            if (string.Equals(old, newName, StringComparison.Ordinal)) return;
            pl.Name = newName;
        }

        var oldPath = PlaylistFilePath(old);
        var newPath = PlaylistFilePath(newName);

        SavePlaylist(pl);

        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            bool ownedByAnother;
            lock (_saveLock)
            {
                // Sanitization can make a different playlist resolve to the same file; deleting it
                // here would take that playlist's contents with it.
                ownedByAnother = Playlists.Any(p => p != null && !ReferenceEquals(p, pl) &&
                    string.Equals(PlaylistFilePath(p.Name), oldPath, StringComparison.OrdinalIgnoreCase));
            }

            if (!ownedByAnother)
            {
                lock (_fileLock)
                {
                    try
                    {
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                    }
                    catch { }
                }
            }
        }
    }

    private static List<string> EnumerateAudioFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                        .Where(AppPaths.IsSupportedAudioFile)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                else if (File.Exists(p) && AppPaths.IsSupportedAudioFile(p))
                {
                    files.Add(p);
                }
            }
            catch { /* inaccessible */ }
        }
        return files;
    }

    /// <summary>Adds files/folders (recursively) to a playlist. Returns newly added items.</summary>
    public List<PlaylistItem> AddPaths(Playlist pl, IEnumerable<string> paths, int? insertAt = null)
    {
        if (pl == null || paths == null) return new List<PlaylistItem>();
        var files = EnumerateAudioFiles(paths);
        return AddFiles(pl, files, insertAt);
    }

    /// <summary>Asynchronously and concurrently adds files/folders (recursively) to a playlist.</summary>
    public async Task<List<PlaylistItem>> AddPathsAsync(Playlist pl, IEnumerable<string> paths, int? insertAt = null, CancellationToken ct = default)
    {
        if (pl == null || paths == null) return new List<PlaylistItem>();
        var files = await Task.Run(() => EnumerateAudioFiles(paths), ct).ConfigureAwait(false);
        return await AddFilesAsync(pl, files, insertAt, ct).ConfigureAwait(false);
    }

    /// <summary>Adds audio files to a playlist, resolving each to a library track or an ad-hoc tag read.</summary>
    public List<PlaylistItem> AddFiles(Playlist pl, IEnumerable<string> files, int? insertAt = null)
    {
        if (pl == null || files == null) return new List<PlaylistItem>();
        var items = new List<PlaylistItem>();
        foreach (var file in files)
        {
            var track = _library.GetTrack(file) ?? TagReader.TryRead(file);
            if (track != null) items.Add(new PlaylistItem(track));
        }
        if (items.Count == 0) return items;
        InsertItems(pl, items, insertAt);
        return items;
    }

    /// <summary>Concurrently resolves audio files using library cache and Parallel.ForEachAsync for missing tags.</summary>
    public async Task<List<PlaylistItem>> AddFilesAsync(Playlist pl, IEnumerable<string> files, int? insertAt = null, CancellationToken ct = default)
    {
        if (pl == null || files == null) return new List<PlaylistItem>();
        var fileList = files.ToList();
        if (fileList.Count == 0) return new List<PlaylistItem>();

        var items = new PlaylistItem?[fileList.Count];
        var missingIndices = new List<int>();

        for (int i = 0; i < fileList.Count; i++)
        {
            var file = fileList[i];
            var cached = _library.GetTrack(file);
            if (cached != null)
            {
                items[i] = new PlaylistItem(cached);
            }
            else
            {
                missingIndices.Add(i);
            }
        }

        if (missingIndices.Count > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(missingIndices, parallelOptions, (idx, token) =>
            {
                token.ThrowIfCancellationRequested();
                var file = fileList[idx];
                var track = TagReader.TryRead(file);
                if (track != null)
                {
                    items[idx] = new PlaylistItem(track);
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }

        var result = items.Where(itm => itm != null).Cast<PlaylistItem>().ToList();
        if (result.Count > 0)
        {
            // Tag reading above ran off-thread on purpose; the collection write must not.
            await RunOnUiAsync(() => InsertItems(pl, result, insertAt)).ConfigureAwait(false);
        }
        return result;
    }

    public List<PlaylistItem> AddTracks(Playlist pl, IEnumerable<Track> tracks, int? insertAt = null)
    {
        if (pl == null || tracks == null) return new List<PlaylistItem>();
        var items = tracks.Select(t => new PlaylistItem(t)).ToList();
        InsertItems(pl, items, insertAt);
        return items;
    }

    public Playlist? ImportPlaylist(string filePath, string? playlistName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var entries = M3u.Read(filePath);
        var name = playlistName ?? Path.GetFileNameWithoutExtension(filePath);
        var pl = CreatePlaylist(name);
        var files = entries.Select(e => e.Path).Where(File.Exists).ToList();
        AddFiles(pl, files);
        return pl;
    }

    public async Task<Playlist?> ImportPlaylistAsync(string filePath, string? playlistName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var entries = await Task.Run(() => M3u.Read(filePath), ct).ConfigureAwait(false);
        var name = playlistName ?? Path.GetFileNameWithoutExtension(filePath);

        // Playlists is bound to the sidebar list, so creating the playlist has to happen on the
        // UI thread; AddFilesAsync marshals its own insert.
        Playlist? pl = null;
        await RunOnUiAsync(() => pl = CreatePlaylist(name)).ConfigureAwait(false);
        if (pl == null) return null;

        var files = entries.Select(e => e.Path).Where(File.Exists).ToList();
        await AddFilesAsync(pl, files, ct: ct).ConfigureAwait(false);
        return pl;
    }

    private void InsertItems(Playlist pl, List<PlaylistItem> items, int? insertAt)
    {
        if (items == null || items.Count == 0) return;
        lock (pl.SyncRoot)
        {
            if (insertAt is int idx and >= 0 && idx <= pl.Items.Count)
            {
                pl.Items.InsertRange(idx, items);
            }
            else
            {
                pl.Items.AddRange(items);
            }
        }
    }

    public void RemoveItems(Playlist pl, IReadOnlyList<PlaylistItem> items)
    {
        if (pl == null || items == null || items.Count == 0) return;
        lock (pl.SyncRoot)
        {
            pl.Items.RemoveRange(items);
        }
        ItemsRemoved?.Invoke(pl, items);
    }

    public void RemoveAll(Playlist pl)
    {
        if (pl == null) return;
        List<PlaylistItem> removed;
        lock (pl.SyncRoot)
        {
            removed = [.. pl.Items];
            pl.Items.Clear();
        }
        if (removed.Count > 0) ItemsRemoved?.Invoke(pl, removed);
    }

    public List<PlaylistItem> ReplaceWithTracks(Playlist pl, IEnumerable<Track> tracks)
    {
        if (pl == null || tracks == null) return new List<PlaylistItem>();
        var items = tracks.Select(t => new PlaylistItem(t)).ToList();
        List<PlaylistItem> oldItems;
        lock (pl.SyncRoot)
        {
            oldItems = [.. pl.Items];
            pl.Items.ReplaceAll(items);
        }
        if (oldItems.Count > 0) ItemsRemoved?.Invoke(pl, oldItems);
        return items;
    }

    public void Sort(Playlist pl, PlaylistSort sort)
    {
        if (pl == null) return;
        Comparison<PlaylistItem> cmp = sort switch
        {
            PlaylistSort.Title => (a, b) => string.Compare(a.Track.Title, b.Track.Title, StringComparison.CurrentCultureIgnoreCase),
            PlaylistSort.Artist => (a, b) => string.Compare(a.Track.Artist, b.Track.Artist, StringComparison.CurrentCultureIgnoreCase),
            PlaylistSort.Album => (a, b) => string.Compare(a.Track.Album + a.Track.AlbumSortKey, b.Track.Album + b.Track.AlbumSortKey, StringComparison.CurrentCultureIgnoreCase),
            PlaylistSort.TrackNo => (a, b) => a.Track.AlbumSortKey.CompareTo(b.Track.AlbumSortKey),
            PlaylistSort.Path => (a, b) => string.Compare(a.Track.Path, b.Track.Path, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => 0
        };
        lock (pl.SyncRoot)
        {
            var items = pl.Items.ToList();
            switch (sort)
            {
                case PlaylistSort.Reverse:
                    items.Reverse();
                    break;

                case PlaylistSort.Random:
                    // A random Comparison is not a valid ordering: List.Sort's introsort treats the
                    // pivot as a sentinel, so past the 16-element insertion-sort cutoff it can walk
                    // off the span and throw ArgumentException ("IComparer.Compare() method returns
                    // inconsistent results") — and when it did complete, the permutation was heavily
                    // biased rather than shuffled. Shuffle properly instead.
                    for (int i = items.Count - 1; i > 0; i--)
                    {
                        int j = Random.Shared.Next(i + 1);
                        (items[i], items[j]) = (items[j], items[i]);
                    }
                    break;

                default:
                    items.Sort(cmp);
                    break;
            }
            pl.Items.ReplaceAll(items);
        }
    }

    public void RemoveDuplicates(Playlist pl)
    {
        if (pl == null) return;
        List<PlaylistItem> dupes;
        lock (pl.SyncRoot)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dupes = pl.Items.Where(i => !seen.Add(i.Track.Path)).ToList();
        }
        if (dupes.Count > 0) RemoveItems(pl, dupes);
    }

    public bool MoveSelection(Playlist pl, IReadOnlyList<PlaylistItem> selected, bool up)
    {
        if (pl == null || selected == null || selected.Count == 0) return false;

        var selectedSet = new HashSet<PlaylistItem>(selected);
        bool moved = false;

        lock (pl.SyncRoot)
        {
            if (pl.Items.Count <= 1) return false;

            if (up)
            {
                for (int i = 1; i < pl.Items.Count; i++)
                {
                    if (selectedSet.Contains(pl.Items[i]) && !selectedSet.Contains(pl.Items[i - 1]))
                    {
                        pl.Items.Move(i, i - 1);
                        moved = true;
                    }
                }
            }
            else
            {
                for (int i = pl.Items.Count - 2; i >= 0; i--)
                {
                    if (selectedSet.Contains(pl.Items[i]) && !selectedSet.Contains(pl.Items[i + 1]))
                    {
                        pl.Items.Move(i, i + 1);
                        moved = true;
                    }
                }
            }
        }

        if (moved) ScheduleSave(pl);
        return moved;
    }

    public void MoveItem(Playlist pl, int oldIndex, int newIndex)
    {
        if (pl == null || oldIndex == newIndex) return;

        bool moved = false;
        lock (pl.SyncRoot)
        {
            if (oldIndex < 0 || oldIndex >= pl.Items.Count || newIndex < 0 || newIndex >= pl.Items.Count)
                return;

            pl.Items.Move(oldIndex, newIndex);
            moved = true;
        }

        if (moved) ScheduleSave(pl);
    }

    public int RemoveDeadItems(Playlist pl)
    {
        if (pl == null) return 0;
        var snapshot = pl.GetSnapshot();
        if (snapshot.Length == 0) return 0;
        var dead = snapshot.Where(i => !string.IsNullOrEmpty(i.Track.Path) && !File.Exists(i.Track.Path)).ToList();
        if (dead.Count > 0)
        {
            RemoveItems(pl, dead);
        }
        return dead.Count;
    }

    public async Task<int> RemoveDeadItemsAsync(Playlist pl, CancellationToken ct = default)
    {
        if (pl == null) return 0;
        var snapshot = pl.GetSnapshot();
        if (snapshot.Length == 0) return 0;
        var dead = await Task.Run(() =>
        {
            return snapshot.Where(i => !ct.IsCancellationRequested && !string.IsNullOrEmpty(i.Track.Path) && !File.Exists(i.Track.Path)).ToList();
        }, ct).ConfigureAwait(false);

        if (dead.Count > 0 && !ct.IsCancellationRequested)
        {
            await RunOnUiAsync(() => RemoveItems(pl, dead)).ConfigureAwait(false);
        }
        return dead.Count;
    }

    // ---------------- persistence ----------------

    private static string PlaylistFilePath(string name) =>
        Path.Combine(AppPaths.PlaylistsDir, SanitizeFileName(name) + ".m3u8");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    /// <summary>Debounce delay in milliseconds before saving a modified playlist to disk.</summary>
    public int DebounceDelayMs { get; set; } = 800;

    private void ScheduleSave(Playlist pl)
    {
        if (pl == null) return;

        // Loading is a read operation. The collection-changed handler installed at creation time
        // would otherwise arm this timer while LoadAll fills the playlist, and the debounced write
        // would then replace the .m3u8 on disk with whatever subset happened to load.
        if (_loading) return;

        lock (_saveLock)
        {
            if (!Playlists.Contains(pl)) return;

            int delay = Math.Max(10, DebounceDelayMs);
            if (_saveTimers.TryGetValue(pl, out var existing))
            {
                try { existing.Change(delay, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
                return;
            }

            var timer = new Timer(state =>
            {
                if (state is Playlist target)
                {
                    lock (_saveLock)
                    {
                        if (_saveTimers.TryRemove(target, out var oldT))
                        {
                            oldT.Dispose();
                        }
                    }
                    SavePlaylist(target);
                }
            }, pl, delay, Timeout.Infinite);

            _saveTimers[pl] = timer;
        }
    }

    public void SavePlaylist(Playlist pl)
    {
        if (pl == null) return;

        lock (_fileLock)
        {
            // Re-checked inside _fileLock so a concurrent RemovePlaylist either deletes the file
            // after this write, or is seen here and skips the write entirely.
            lock (_saveLock)
            {
                if (!Playlists.Contains(pl)) return; // Prevents ghost file recreation
            }

            var snapshot = pl.GetSnapshot();
            string name;
            List<string> unresolved;
            lock (pl.SyncRoot)
            {
                name = pl.Name;
                unresolved = [.. pl.UnresolvedPaths];
            }
            string path = PlaylistFilePath(name);

            try
            {
                M3u.Write(path, snapshot, name, unresolved);
            }
            catch { /* best effort */ }
        }
    }

    private void DeletePlaylistFile(Playlist pl)
    {
        if (pl == null) return;
        string name;
        lock (pl.SyncRoot) { name = pl.Name; }

        lock (_fileLock)
        {
            try
            {
                var path = PlaylistFilePath(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    public void SaveAll()
    {
        foreach (var (_, t) in _saveTimers) t.Dispose();
        _saveTimers.Clear();

        List<Playlist> current;
        lock (_saveLock)
        {
            current = [.. Playlists];
        }
        foreach (var pl in current)
        {
            if (pl != null)
            {
                SavePlaylist(pl);
            }
        }
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            var timersToDispose = new List<Timer>();
            foreach (Playlist pl in e.OldItems)
            {
                if (pl != null && _saveTimers.TryRemove(pl, out var t))
                {
                    timersToDispose.Add(t);
                }
            }
            if (timersToDispose.Count > 0)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var t in timersToDispose) t.Dispose();
                });
            }
        }
    }

    /// <summary>Loads playlists from %AppData%/DawnPlayer/playlists. Creates a default one when empty.</summary>
    public void LoadAll()
    {
        var files = Directory.Exists(AppPaths.PlaylistsDir)
            ? Directory.EnumerateFiles(AppPaths.PlaylistsDir, "*.m3u8").OrderBy(f => f).ToList()
            : new List<string>();

        // Filling a playlist raises CollectionChanged, which would arm the debounced auto-save and
        // rewrite the file on disk with only the entries that happened to resolve.
        _loading = true;
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var entries = M3u.Read(file);
                    var plName = Path.GetFileNameWithoutExtension(file);
                    bool isNp = string.Equals(plName, NowPlayingPlaylistName, StringComparison.OrdinalIgnoreCase);
                    var pl = isNp ? NowPlaying : CreatePlaylist(plName);
                    var items = new List<PlaylistItem>();
                    var unresolved = new List<string>();

                    foreach (var entry in entries)
                    {
                        Track? track = null;
                        if (File.Exists(entry.Path))
                        {
                            track = _library.GetTrack(entry.Path) ?? TagReader.TryRead(entry.Path);
                        }

                        if (track != null) items.Add(new PlaylistItem(track));
                        // Remember what we could not load rather than dropping it: the file may be
                        // on a volume that is merely offline right now.
                        else unresolved.Add(entry.Path);
                    }

                    lock (pl.SyncRoot)
                    {
                        pl.UnresolvedPaths.Clear();
                        pl.UnresolvedPaths.AddRange(unresolved);
                        pl.Items.AddRange(items);
                    }
                }
                catch { /* skip broken file */ }
            }
        }
        finally
        {
            _loading = false;
        }

        lock (_saveLock)
        {
            if (Playlists.Count == 0)
            {
                CreatePlaylistLocked();
            }
        }
    }
}
