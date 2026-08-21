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

    // Pure composition helpers. The layout of a data directory is worth asserting on its own,
    // and going through these keeps such checks from having to redirect the process-wide base
    // directory — which is observable by every other thread.
    public static string SettingsFileIn(string baseDir) => Path.Combine(baseDir, "settings.json");
    public static string LibraryDbPathIn(string baseDir) => Path.Combine(baseDir, "library.db");
    public static string ArtCacheDirIn(string baseDir) => Path.Combine(baseDir, "artcache");
    public static string PlaylistsDirIn(string baseDir) => Path.Combine(baseDir, "playlists");
    public static string LogFileIn(string baseDir) => Path.Combine(baseDir, "dawnplayer.log");

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
        ".mp3", ".aac", ".m4a", ".m4b", ".mp4", ".flac", ".ogg", ".oga", ".wav", ".alac"
    };

    public static bool IsSupportedAudioFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            Directory.CreateDirectory(ArtCacheDir);
            Directory.CreateDirectory(PlaylistsDir);
        }
        catch { }
    }

    static AppPaths()
    {
        EnsureDirectories();
    }
}
