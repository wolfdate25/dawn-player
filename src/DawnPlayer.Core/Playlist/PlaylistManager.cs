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

/// <summary>The built-in library queries smart playlists are generated from.</summary>
public enum SmartPlaylistKind
{
    /// <summary>Most played tracks first.</summary>
    MostPlayed,
    /// <summary>Newest additions to the library first.</summary>
    RecentlyAdded,
    /// <summary>Least recently played (never-played included) — the "forgotten music" view.</summary>
    NotRecentlyPlayed
}

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

    private readonly PlaylistUndoManager _undoManager = new();

    public ObservableCollection<Playlist> Playlists { get; } = new();
    private Playlist? _current;
    private Playlist? _nowPlaying;
    private readonly Dictionary<SmartPlaylistKind, Playlist> _smartPlaylists = new();
    private readonly List<Playlist> _userSmartPlaylists = new();

    /// <summary>Raised (on the calling thread) whenever a user smart playlist is added, edited or
    /// deleted, so the host can persist the definitions. Not raised for content refreshes.</summary>
    public event Action? UserSmartPlaylistsChanged;

    /// <summary>Upper bound on the size of each generated smart playlist.</summary>
    private const int SmartPlaylistCap = 100;

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

    /// <summary>
    /// Creates (once) the built-in smart playlists and places them right under Now Playing.
    /// Names come from the caller because Core cannot reach the app's localized resources; pass
    /// the display names in the order the sidebar should show them. Idempotent and safe to call
    /// again after a language change.
    /// </summary>
    public void EnsureSmartPlaylists(IReadOnlyList<(SmartPlaylistKind Kind, string Name)> definitions)
    {
        if (definitions == null) return;

        _ = NowPlaying; // pin the system playlist at index 0 first

        lock (_saveLock)
        {
            int insertAt = 1;
            foreach (var (kind, name) in definitions)
            {
                if (_smartPlaylists.TryGetValue(kind, out var existing) && Playlists.Contains(existing))
                {
                    existing.Name = name;
                }
                else
                {
                    var pl = new Playlist(name) { IsSmart = true };
                    // No CollectionChanged → ScheduleSave subscription on purpose: the contents are
                    // generated, and every refresh would otherwise rewrite an M3U8 for them.
                    _smartPlaylists[kind] = pl;
                    Playlists.Insert(Math.Min(insertAt, Playlists.Count), pl);
                }
                insertAt++;
            }
        }

        RefreshSmartPlaylists();
    }

    /// <summary>Regenerates the contents of every smart playlist from the current library state.
    /// Call on the UI thread (items collections are bound) after a scan, a load, or a stats update.</summary>
    public void RefreshSmartPlaylists()
    {
        foreach (var kv in _smartPlaylists)
        {
            if (!Playlists.Contains(kv.Value)) continue;
            ReplaceWithTracks(kv.Value, QuerySmartPlaylist(kv.Key));
        }

        foreach (var pl in _userSmartPlaylists)
        {
            if (!Playlists.Contains(pl)) continue;
            ReplaceWithTracks(pl, QueryUserSmartPlaylist(pl));
        }
    }

    private IEnumerable<Track> QuerySmartPlaylist(SmartPlaylistKind kind)
    {
        var tracks = _library.Tracks;
        switch (kind)
        {
            case SmartPlaylistKind.MostPlayed:
                return tracks
                    .Where(t => t.PlayCount > 0)
                    .OrderByDescending(t => t.PlayCount)
                    .ThenByDescending(t => t.LastPlayedUtcTicks)
                    .ThenBy(t => t.AlbumSortKey)
                    .Take(SmartPlaylistCap);

            case SmartPlaylistKind.RecentlyAdded:
                return tracks
                    .OrderByDescending(t => t.FirstSeenUtcTicks)
                    .ThenBy(t => t.AlbumSortKey)
                    .Take(SmartPlaylistCap);

                default:
                    // Never-played tracks (last_played = 0) sort first, which is the point: this view
                    // surfaces music the library has on disk but the user has not heard.
                    return tracks
                        .OrderBy(t => t.LastPlayedUtcTicks)
                        .ThenBy(t => t.PlayCount)
                        .ThenBy(t => t.AlbumSortKey)
                        .Take(SmartPlaylistCap);
        }
    }

    // ---------------- user-defined smart playlists ----------------

    /// <summary>The user smart playlist definitions, in sidebar order — the shape the host
    /// persists. Definitions whose stored query no longer parses are skipped (empty playlist).</summary>
    public IReadOnlyList<(string Name, string Query)> GetUserSmartPlaylists() =>
        _userSmartPlaylists.Select(p => (p.Name, p.SmartQuery ?? "")).ToList();

    /// <summary>Recreates the persisted user smart playlists after startup (or a language change
    /// is irrelevant here — names are user-chosen). Invalid stored queries are kept as definitions
    /// but generate empty playlists, so editing them can fix the query without losing the name.</summary>
    public void EnsureUserSmartPlaylists(IReadOnlyList<(string Name, string Query)>? definitions)
    {
        if (definitions == null) return;

        _ = NowPlaying;

        lock (_saveLock)
        {
            foreach (var (name, query) in definitions)
            {
                if (_userSmartPlaylists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

                var pl = new Playlist(name) { IsSmart = true, SmartQuery = query };
                _userSmartPlaylists.Add(pl);
                Playlists.Insert(Math.Min(SmartInsertIndexLocked(), Playlists.Count), pl);
            }
        }

        RefreshSmartPlaylists();
    }

    /// <summary>Creates a query-driven smart playlist. Validates the query first; on failure
    /// returns null and fills <paramref name="error"/>.</summary>
    public Playlist? AddUserSmartPlaylist(string name, string query, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The playlist needs a name.";
            return null;
        }
        if (!SmartPlaylistQuery.TryParse(query, out var parsed, out var parseError))
        {
            error = parseError;
            return null;
        }

        Playlist pl;
        lock (_saveLock)
        {
            if (Playlists.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = "A playlist with that name already exists.";
                return null;
            }

            pl = new Playlist(name.Trim()) { IsSmart = true, SmartQuery = parsed!.Source };
            _userSmartPlaylists.Add(pl);
            Playlists.Insert(Math.Min(SmartInsertIndexLocked(), Playlists.Count), pl);
        }

        ReplaceWithTracks(pl, parsed!.Apply(_library.Tracks));
        UserSmartPlaylistsChanged?.Invoke();
        return pl;
    }

    /// <summary>Renames and/or re-queries a user smart playlist in one step (the name is its
    /// identity, so renaming goes through here rather than <see cref="RenamePlaylist"/>).</summary>
    public bool TryUpdateUserSmartPlaylist(Playlist pl, string name, string query, out string? error)
    {
        error = null;
        if (pl == null || pl.SmartQuery == null)
        {
            error = "Not a user smart playlist.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The playlist needs a name.";
            return false;
        }
        if (!SmartPlaylistQuery.TryParse(query, out var parsed, out var parseError))
        {
            error = parseError;
            return false;
        }

        lock (_saveLock)
        {
            if (Playlists.Any(p => !ReferenceEquals(p, pl) &&
                                   string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = "A playlist with that name already exists.";
                return false;
            }
            pl.Name = name.Trim();
            pl.SmartQuery = parsed!.Source;
        }

        ReplaceWithTracks(pl, parsed!.Apply(_library.Tracks));
        UserSmartPlaylistsChanged?.Invoke();
        return true;
    }

    private IEnumerable<Track> QueryUserSmartPlaylist(Playlist pl) =>
        SmartPlaylistQuery.TryParse(pl.SmartQuery, out var parsed, out _)
            ? parsed!.Apply(_library.Tracks)
            : Enumerable.Empty<Track>();

    /// <summary>First sidebar slot after the built-in smart block, so user smart playlists sit
    /// with their siblings and above the plain user playlists.</summary>
    private int SmartInsertIndexLocked()
    {
        int index = 1; // after Now Playing
        for (int i = 0; i < Playlists.Count; i++)
        {
            if (Playlists[i] is { IsSmart: true }) index = i + 1;
        }
        return index;
    }

    public Playlist Current
    {
        get
        {
            lock (_saveLock)
            {
                if (_current != null && Playlists.Contains(_current))
                    return _current;

                var userPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem && !p.IsSmart);
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

            var userPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem && !p.IsSmart);
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
    /// <summary>The UI-thread seam, typed as <see cref="IUiDispatcher"/>. Null (default) runs
    /// writes inline — headless/tests. The App sets a <see cref="DelegateUiDispatcher"/> over
    /// its RunOnUi.</summary>
    public IUiDispatcher? UiDispatcher { get; set; }

    /// <summary>Compatibility shim over <see cref="UiDispatcher"/> for existing Action-based
    /// wiring; new code should set <see cref="UiDispatcher"/>.</summary>
    public Action<Action>? UiInvoke
    {
        get => UiDispatcher is DelegateUiDispatcher d ? d.Post : null;
        set => UiDispatcher = value == null ? null : new DelegateUiDispatcher(value);
    }

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
        var dispatcher = UiDispatcher;
        if (dispatcher == null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
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
        return RegisterPlaylistLocked(name);
    }

    /// <summary>Registers a playlist under the exact name given, without the uniqueness scan.
    /// Used by <see cref="LoadAll"/>, whose names come from on-disk filenames (unique per folder
    /// by definition) — running the O(existing) uniqueness scan per file made loading a large
    /// playlists directory quadratic and stalled startup at ~100% CPU for minutes.</summary>
    private Playlist RegisterPlaylistLocked(string name)
    {
        var pl = new Playlist(name);
        pl.Items.CollectionChanged += (_, _) => ScheduleSave(pl);
        pl.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Playlist.Name)) ScheduleSave(pl); };
        Playlists.Add(pl);
        if (_current == null || _current.IsSystem || _current.IsSmart)
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

        if (pl.IsSmart)
        {
            // Built-in smart playlists are code-owned; a deleted one would resurrect on the next
            // refresh. User-defined ones (SmartQuery set) are deletable like any user playlist.
            if (pl.SmartQuery == null) return;
        }

        Timer? timerToDispose = null;
        int idx;
        lock (_saveLock)
        {
            idx = Playlists.IndexOf(pl);
            if (idx < 0) return;

            Playlists.RemoveAt(idx);
            _userSmartPlaylists.Remove(pl);

            _saveTimers.TryRemove(pl, out timerToDispose);

            if (_current == pl)
            {
                var nextUserPl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem && !p.IsSmart);
                _current = nextUserPl ?? (Playlists.Count > 0 ? Playlists[Math.Min(idx, Playlists.Count - 1)] : null);
            }

            if (!Playlists.Any(p => p != null && !p.IsSystem && !p.IsSmart))
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

        // User smart playlists have no file on disk; the others do.
        if (pl.SmartQuery == null)
        {
            DeletePlaylistFile(pl);
        }
        if (pl.IsSmart)
        {
            UserSmartPlaylistsChanged?.Invoke();
        }
    }

    public void RemovePlaylist(string playlistIdOrName)
    {
        if (string.IsNullOrWhiteSpace(playlistIdOrName)) return;
        Playlist? pl;
        lock (_saveLock)
        {
                pl = Playlists.FirstOrDefault(p => p != null && !p.IsSystem && !p.IsSmart && string.Equals(p.Name, playlistIdOrName, StringComparison.OrdinalIgnoreCase))
                ?? Playlists.FirstOrDefault(p => p != null && string.Equals(p.Name, playlistIdOrName, StringComparison.OrdinalIgnoreCase));
        }
        if (pl != null)
        {
            RemovePlaylist(pl);
        }
    }

    /// <summary>Renames a user playlist. Returns false when the new name is taken (another
    /// playlist with that name, a name that sanitizes to another playlist's file, or the system
    /// playlist's name) — without this gate the rename wrote this playlist's contents over the
    /// other one's .m3u8 and the next SaveAll permanently lost one of the two.</summary>
    public bool RenamePlaylist(Playlist pl, string newName)
    {
        if (pl == null || pl.IsSystem || pl.IsSmart || string.IsNullOrWhiteSpace(newName)) return false;

        lock (_saveLock)
        {
            if (string.Equals(pl.Name, newName, StringComparison.Ordinal)) return true;

            var candidatePath = PlaylistFilePath(newName);
            bool taken = string.Equals(newName, NowPlayingPlaylistName, StringComparison.OrdinalIgnoreCase) ||
                         Playlists.Any(p => p != null && !ReferenceEquals(p, pl) &&
                             (string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(PlaylistFilePath(p.Name), candidatePath, StringComparison.OrdinalIgnoreCase)));
            if (taken) return false;
        }

        string old;
        lock (pl.SyncRoot)
        {
            old = pl.Name;
            if (string.Equals(old, newName, StringComparison.Ordinal)) return true;
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
                    catch (Exception ex) { Log.Debug($"[playlists] stale playlist file delete failed '{oldPath}': {ex.Message}"); }
                }
            }
        }

        return true;
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
                    // Manual recursion with per-entry guards: Directory.EnumerateFiles(AllDirectories)
                    // threw out of the whole folder on the first access-denied subdirectory, which
                    // surfaced as "0 tracks added" for a drop that was mostly readable.
                    CollectAudioFiles(p, files);
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                }
                // Cue virtual tracks arrive as fragment paths (file.ext#cue=...) whose
                // extension no longer parses as audio, so route them through their physical half.
                else if ((AppPaths.IsCueFragment(p) && File.Exists(AppPaths.PhysicalPath(p))) ||
                         (File.Exists(p) && AppPaths.IsSupportedAudioFile(p)))
                {
                    files.Add(p);
                }
            }
            catch (Exception ex) { Log.Debug($"[playlists] path inaccessible '{p}': {ex.Message}"); }
        }
        return files;
    }

    private static void CollectAudioFiles(string directory, List<string> into)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (AppPaths.IsSupportedAudioFile(file)) into.Add(file);
            }
        }
        catch (Exception ex) { Log.Debug($"[playlists] files inaccessible '{directory}': {ex.Message}"); }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                CollectAudioFiles(sub, into);
            }
        }
        catch (Exception ex) { Log.Debug($"[playlists] subfolders inaccessible '{directory}': {ex.Message}"); }
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
            var track = ResolveEntry(file);
            if (track != null) items.Add(new PlaylistItem(track));
        }
        if (items.Count == 0) return items;
        InsertItems(pl, items, insertAt);
        return items;
    }

    /// <summary>Library cache → radio URL → tag read, in that order.</summary>
    private Models.Track? ResolveEntry(string file)
    {
        var track = _library.GetTrack(file);
        if (track != null) return track;
        if (Audio.RadioTrack.IsStreamUrl(file)) return Audio.RadioTrack.Create(file);
        return TagReader.TryRead(file);
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
                var track = Audio.RadioTrack.IsStreamUrl(file)
                    ? Audio.RadioTrack.Create(file)
                    : TagReader.TryRead(file);
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

    /// <summary>Classifies M3U8 entries the way <see cref="LoadAll"/> does: #DPTRACK directives
    /// become remote tracks, stream URLs stay as radio items, and only plain paths go through the
    /// File.Exists filter. Importing what this app exported used to drop every remote entry
    /// (File.Exists is false for any URL), breaking the export → import round trip.</summary>
    private static (List<string> Files, List<Models.Track> RemoteTracks) ResolveImportEntries(IEnumerable<M3uEntry> entries)
    {
        var files = new List<string>();
        var remote = new List<Models.Track>();
        foreach (var e in entries)
        {
            if (DpTrackMeta.TryDecode(e.DpTrackDirective) is { } meta)
            {
                if (Persistence.RemoteTrackCodec.ToTrack(e.Path, meta) is { } t) { remote.Add(t); continue; }
            }
            if (Audio.RadioTrack.IsStreamUrl(e.Path))
            {
                files.Add(e.Path);
                continue;
            }
            if (File.Exists(AppPaths.PhysicalPath(e.Path)))
            {
                files.Add(e.Path);
            }
        }
        return (files, remote);
    }

    public Playlist? ImportPlaylist(string filePath, string? playlistName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var entries = M3u.Read(filePath);
        var name = playlistName ?? Path.GetFileNameWithoutExtension(filePath);
        var pl = CreatePlaylist(name);
        var (files, remote) = ResolveImportEntries(entries);
        AddFiles(pl, files);
        if (remote.Count > 0) AddTracks(pl, remote);
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

        var (files, remote) = ResolveImportEntries(entries);
        await AddFilesAsync(pl, files, ct: ct).ConfigureAwait(false);
        if (remote.Count > 0)
        {
            await RunOnUiAsync(() => AddTracks(pl, remote)).ConfigureAwait(false);
        }
        return pl;
    }

    private void InsertItems(Playlist pl, List<PlaylistItem> items, int? insertAt)
    {
        if (items == null || items.Count == 0) return;
        _undoManager.RecordSnapshot(pl, "Add Items");
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
        _undoManager.RecordSnapshot(pl, "Remove Items");
        lock (pl.SyncRoot)
        {
            pl.Items.RemoveRange(items);
        }
        ItemsRemoved?.Invoke(pl, items);
    }

    public void RemoveAll(Playlist pl)
    {
        if (pl == null) return;
        _undoManager.RecordSnapshot(pl, "Clear Playlist");
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
        _undoManager.RecordSnapshot(pl, "Replace Tracks");
        var incoming = tracks.ToList();
        List<PlaylistItem> oldItems;
        lock (pl.SyncRoot)
        {
            // No-change fast path: a smart refresh that resolved to the same track sequence must
            // not churn the collection — ReplaceAll fired ItemsRemoved, which purged the playback
            // queue on every track transition, and a Reset rebuilt the whole visible list
            // (scroll/selection loss). Reference equality on the Track: a re-resolved instance
            // with the same path but new metadata IS a change and must flow through.
            if (pl.Items.Count == incoming.Count)
            {
                bool same = true;
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (!ReferenceEquals(pl.Items[i].Track, incoming[i]) ||
                        !string.Equals(pl.Items[i].Track.Path, incoming[i].Path, StringComparison.OrdinalIgnoreCase))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return pl.Items.ToList();
            }

            // Reuse existing item instances whose Track is the SAME INSTANCE the refresh resolved
            // (the library hands out its live instances, so unchanged tracks keep identity and
            // their queue entries), so the play-order resolver still finds the playing item. A
            // re-resolved instance with new metadata builds a fresh item — keying reuse by PATH
            // would swallow the metadata change instead.
            var itemByTrack = new Dictionary<Track, PlaylistItem>();
            foreach (var item in pl.Items)
            {
                if (!itemByTrack.ContainsKey(item.Track)) itemByTrack[item.Track] = item;
            }
            var claimed = new HashSet<PlaylistItem>();
            var items = new List<PlaylistItem>(incoming.Count);
            foreach (var t in incoming)
            {
                // A repeated track claims the reused instance once; further occurrences need
                // their own item — sharing one instance in two rows breaks selection and queue
                // identity.
                if (itemByTrack.TryGetValue(t, out var reuse) && claimed.Add(reuse))
                    items.Add(reuse);
                else
                    items.Add(new PlaylistItem(t));
            }

            oldItems = [.. pl.Items];
            pl.Items.ReplaceAll(items);
        }
        // Only the items that actually disappeared leave the queue: reusing instances above means
        // the items the refresh kept are still the ones the queue holds.
        var removed = oldItems.Where(i => !pl.Items.Contains(i)).ToList();
        if (removed.Count > 0) ItemsRemoved?.Invoke(pl, removed);
        return pl.Items.ToList();
    }

    /// <summary>Records the playlist's current order as one undo step. Flat-mode drag reorder
    /// mutates the list through the framework directly, so without this the move never entered
    /// the undo history.</summary>
    public void RecordReorderSnapshot(Playlist pl) => _undoManager.RecordSnapshot(pl, "Reorder");

    public void Sort(Playlist pl, PlaylistSort sort)
    {
        if (pl == null) return;
        _undoManager.RecordSnapshot(pl, $"Sort {sort}");
        Comparison<PlaylistItem> cmp = sort switch
        {
            // Culture-aware on purpose: these are the linguistic sorts the user sees (Korean
            // collation); ordinal comparison would visibly reorder Korean titles.
#pragma warning disable CA1309
            PlaylistSort.Title => (a, b) => string.Compare(a.Track.Title, b.Track.Title, StringComparison.CurrentCultureIgnoreCase),
            PlaylistSort.Artist => (a, b) => string.Compare(a.Track.Artist, b.Track.Artist, StringComparison.CurrentCultureIgnoreCase),
            PlaylistSort.Album => (a, b) => string.Compare(a.Track.Album + a.Track.AlbumSortKey, b.Track.Album + b.Track.AlbumSortKey, StringComparison.CurrentCultureIgnoreCase),
#pragma warning restore CA1309
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
            _undoManager.RecordSnapshot(pl, "Move Selection");

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

            _undoManager.RecordSnapshot(pl, "Move Item");
            pl.Items.Move(oldIndex, newIndex);
            moved = true;
        }

        if (moved) ScheduleSave(pl);
    }

    public bool CanUndo(Playlist pl) => _undoManager.CanUndo(pl);
    public bool CanRedo(Playlist pl) => _undoManager.CanRedo(pl);

    public bool Undo(Playlist pl)
    {
        if (pl == null) return false;
        var before = pl.GetSnapshot();
        bool ok = _undoManager.Undo(pl);
        if (ok)
        {
            // The undo manager swaps the whole list directly, so removals it performs never
            // passed through RemoveItems — queue entries for vanished tracks stayed stranded
            // (a Q badge on a row that no longer exists, and the track still playing).
            PurgeQueueForVanished(pl, before);
            ScheduleSave(pl);
        }
        return ok;
    }

    public bool Redo(Playlist pl)
    {
        if (pl == null) return false;
        var before = pl.GetSnapshot();
        bool ok = _undoManager.Redo(pl);
        if (ok)
        {
            PurgeQueueForVanished(pl, before);
            ScheduleSave(pl);
        }
        return ok;
    }

    /// <summary>Fires <see cref="ItemsRemoved"/> for items an undo/redo made disappear, so the
    /// queue purge runs through the same path a direct removal uses.</summary>
    private void PurgeQueueForVanished(Playlist pl, PlaylistItem[] before)
    {
        var current = pl.GetSnapshot();
        var removed = before.Where(i => Array.IndexOf(current, i) < 0).ToArray();
        if (removed.Length > 0) ItemsRemoved?.Invoke(pl, removed);
    }

    public int RemoveDeadItems(Playlist pl)
    {
        if (pl == null) return 0;
        var snapshot = pl.GetSnapshot();
        if (snapshot.Length == 0) return 0;
        var dead = snapshot.Where(i => !string.IsNullOrEmpty(i.Track.Path) && !Audio.RadioTrack.IsStreamUrl(i.Track.Path) && !File.Exists(AppPaths.PhysicalPath(i.Track.Path))).ToList();
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
            return snapshot.Where(i => !ct.IsCancellationRequested && !string.IsNullOrEmpty(i.Track.Path) && !Audio.RadioTrack.IsStreamUrl(i.Track.Path) && !File.Exists(AppPaths.PhysicalPath(i.Track.Path))).ToList();
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
                try
                {
                    existing.Change(delay, Timeout.Infinite);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // The timer fired and disposed between our TryGetValue and the Change call
                    // (SaveAll's sweep, for one). Fall through and create a fresh one — returning
                    // here silently dropped this change's debounced save.
                }
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

    public void SavePlaylist(Playlist playlist)
    {
        if (playlist == null) return;

        // Smart playlists are generated from library state; an M3U8 snapshot would be stale the
        // moment it landed (and would resurrect as a user playlist on the next load).
        if (playlist.IsSmart) return;

        lock (_fileLock)
        {
            // Re-checked inside _fileLock so a concurrent RemovePlaylist either deletes the file
            // after this write, or is seen here and skips the write entirely.
            lock (_saveLock)
            {
                if (!Playlists.Contains(playlist)) return; // Prevents ghost file recreation
            }

            var snapshot = playlist.GetSnapshot();
            string name;
            List<(int Index, string Path)> unresolved;
            lock (playlist.SyncRoot)
            {
                name = playlist.Name;
                unresolved = [.. playlist.UnresolvedEntries];
            }
            string path = PlaylistFilePath(name);

            try
            {
                M3u.Write(path, snapshot, name, unresolved);
            }
            catch (Exception ex) { Log.Warn($"[playlists] m3u8 write failed for '{name}': {ex.Message}"); } /* best effort */
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
            catch (Exception ex) { Log.Debug($"[playlists] playlist file delete failed '{name}': {ex.Message}"); }
        }
    }

    public void SaveAll()
    {
        List<Playlist> current;
        // Dispose the debounced timers under the same lock ScheduleSave takes: sweeping them
        // unlocked let an in-flight ScheduleSave grab a just-disposed timer and (before the
        // recreate-on-disposed fix) silently drop that change.
        lock (_saveLock)
        {
            foreach (var (_, t) in _saveTimers) t.Dispose();
            _saveTimers.Clear();
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
                    var pl = isNp ? NowPlaying : RegisterPlaylistLocked(plName);
                    var items = new List<PlaylistItem>();
                    var unresolved = new List<(int Index, string Path)>();

                    foreach (var entry in entries)
                    {
                        // A #DPUNRESOLVED: marker line is carried positionally to the next save.
                        if (entry.Unresolved)
                        {
                            unresolved.Add((items.Count, entry.Path));
                            continue;
                        }

                        Track? track = null;
                        if (DpTrackMeta.TryDecode(entry.DpTrackDirective) is { } meta)
                        {
                            track = Persistence.RemoteTrackCodec.ToTrack(entry.Path, meta);
                        }
                        else if (Audio.RadioTrack.IsStreamUrl(entry.Path))
                        {
                            track = Audio.RadioTrack.Create(entry.Path);
                        }
                        else if (File.Exists(AppPaths.PhysicalPath(entry.Path)))
                        {
                            // Virtual cue rows only resolve through the library (their path is a
                            // fragment, not a file); physical files fall back to a fresh tag read.
                            track = _library.GetTrack(entry.Path) ??
                                    (AppPaths.IsCueFragment(entry.Path) ? null : TagReader.TryRead(entry.Path));
                        }

                        if (track != null) items.Add(new PlaylistItem(track));
                        // Remember what we could not load rather than dropping it: the file may be
                        // on a volume that is merely offline right now. The index keeps its list
                        // position so the next save re-emits it where it was.
                        else unresolved.Add((items.Count, entry.Path));
                    }

                    lock (pl.SyncRoot)
                    {
                        pl.UnresolvedEntries.Clear();
                        pl.UnresolvedEntries.AddRange(unresolved);
                        pl.Items.AddRange(items);
                    }
                }
                catch (Exception ex) { Log.Debug($"[playlists] skip broken playlist file '{file}': {ex.Message}"); }
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
