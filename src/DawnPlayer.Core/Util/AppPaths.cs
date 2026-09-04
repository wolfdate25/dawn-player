namespace DawnPlayer.Core.Util;

/// <summary>Application data locations and supported file types.</summary>
public static class AppPaths
{
    private static string? _customBaseDir;
    private static readonly Lazy<bool> _isPortableLazy = new(DetectPortableMode);

    /// <summary>Indicates whether the application is running in portable mode.</summary>
    public static bool IsPortable => !string.IsNullOrEmpty(_customBaseDir)
        ? IsPortableDir(_customBaseDir)
        : _isPortableLazy.Value;

    public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

    public static string BaseDir
    {
        get
        {
            if (!string.IsNullOrEmpty(_customBaseDir)) return _customBaseDir;

            var envDir = Environment.GetEnvironmentVariable("DAWNPLAYER_DATA_DIR");
            if (!string.IsNullOrEmpty(envDir)) return envDir;

            if (_isPortableLazy.Value)
            {
                return Path.Combine(AppDir, "data");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DawnPlayer");
        }
    }

    private static bool DetectPortableMode()
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(appDir)) return false;

            var markerFiles = new[] { "portable.dat", "portable.flag", "portable" };
            foreach (var marker in markerFiles)
            {
                if (File.Exists(Path.Combine(appDir, marker))) return true;
            }

            var dataDir = Path.Combine(appDir, "data");
            if (Directory.Exists(dataDir)) return true;
        }
        catch { }

        return false;
    }

    private static bool IsPortableDir(string dir)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(appDir)) return false;
            return dir.StartsWith(appDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string SettingsFile => SettingsFileIn(BaseDir);
    public static string LibraryDbPath => LibraryDbPathIn(BaseDir);
    public static string ArtCacheDir => ArtCacheDirIn(BaseDir);
    public static string PlaylistsDir => PlaylistsDirIn(BaseDir);
    public static string LogFile => LogFileIn(BaseDir);
    public static string PluginsDir => PluginsDirIn(BaseDir);
    public static string PluginsDataDir => PluginsDataDirIn(BaseDir);

    // Pure composition helpers. The layout of a data directory is worth asserting on its own,
    // and going through these keeps such checks from having to redirect the process-wide base
    // directory — which is observable by every other thread.
    public static string SettingsFileIn(string baseDir) => Path.Combine(baseDir, "settings.json");
    public static string LibraryDbPathIn(string baseDir) => Path.Combine(baseDir, "library.db");
    public static string ArtCacheDirIn(string baseDir) => Path.Combine(baseDir, "artcache");
    public static string PlaylistsDirIn(string baseDir) => Path.Combine(baseDir, "playlists");
    public static string LogFileIn(string baseDir) => Path.Combine(baseDir, "dawnplayer.log");
    public static string PluginsDirIn(string baseDir) => Path.Combine(baseDir, "plugins");
    public static string PluginsDataDirIn(string baseDir) => Path.Combine(baseDir, "plugins-data");

    /// <summary>
    /// Guards changes to the process-wide base directory. Redirecting it affects every thread, so
    /// anything that temporarily overrides it must hold this while it does.
    /// </summary>
    public static object BaseDirGate { get; } = new();

    public static void SetCustomBaseDir(string? dir)
    {
        _customBaseDir = dir;
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureDirectories();
        }
    }

    public static void ResetBaseDir() => _customBaseDir = null;

    /// <summary>Audio extensions the player accepts (lowercase, with dot).</summary>
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".m4b", ".mp4", ".flac", ".ogg", ".oga", ".opus", ".wav", ".alac"
    };

    public static bool IsSupportedAudioFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>Marker suffix that turns a physical audio path into a cue-sheet virtual track.
    /// Everything keyed on <c>Track.Path</c> (DB rows, M3U8 lines, stats) keeps working because
    /// the fragment makes the path unique per cue track and encodes the play range:
    /// <c>album.flac#cue=0-212500</c> (milliseconds, start inclusive, end exclusive-ish).</summary>
    public const string CueFragmentMarker = "#cue=";

    /// <summary>True when the path addresses a cue range inside a physical file.</summary>
    public static bool IsCueFragment(string path) =>
        path != null && path.Contains(CueFragmentMarker, StringComparison.Ordinal);

    /// <summary>The real file underneath a path: the cue fragment is stripped when present.</summary>
    public static string PhysicalPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        int marker = path.LastIndexOf(CueFragmentMarker, StringComparison.Ordinal);
        return marker > 0 ? path[..marker] : path;
    }

    /// <summary>Builds the virtual-track path for a cue range. End milliseconds of 0 means "until
    /// the end of the file" and is resolved to a concrete value by the scanner.</summary>
    public static string MakeCuePath(string physicalPath, long startMs, long endMs) =>
        physicalPath + CueFragmentMarker + startMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "-" + endMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Splits a virtual-track path back into its physical file and range.</summary>
    public static bool TryDecodeCuePath(string path, out string physicalPath, out long startMs, out long endMs)
    {
        physicalPath = "";
        startMs = 0;
        endMs = 0;
        int marker = path?.LastIndexOf(CueFragmentMarker, StringComparison.Ordinal) ?? -1;
        if (marker <= 0 || marker + CueFragmentMarker.Length >= path!.Length) return false;

        physicalPath = path[..marker];
        var range = path[(marker + CueFragmentMarker.Length)..];
        int dash = range.IndexOf('-');
        if (dash <= 0 || dash == range.Length - 1) return false;

        return long.TryParse(range.AsSpan(0, dash), System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out startMs)
               && long.TryParse(range.AsSpan(dash + 1), System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out endMs)
               && startMs >= 0 && endMs >= startMs;
    }

    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            Directory.CreateDirectory(ArtCacheDir);
            Directory.CreateDirectory(PlaylistsDir);
            Directory.CreateDirectory(PluginsDataDir);
        }
        catch { }
    }

    static AppPaths()
    {
        EnsureDirectories();
    }
}
