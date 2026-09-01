using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON with atomic file replacement and transient I/O retry resilience.
/// </summary>
public static class SettingsStore
{
    private static readonly object SyncLock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        lock (SyncLock)
        {
            var targetPath = Util.AppPaths.SettingsFile;

            var settings = TryLoadFrom(targetPath);
            // A truncated or half-written settings.json used to silently reset every preference
            // the user had ever set. Try the previous generation before defaulting.
            settings ??= TryLoadFrom(targetPath + ".bak");

            return Normalize(settings ?? AppSettings.CreateDefault());
        }
    }

    private static AppSettings? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var json = ReadAllTextWithRetry(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (JsonException)
        {
            // Corrupted JSON content → caller falls back
            return null;
        }
        catch (Exception)
        {
            // Unrecoverable read error → caller falls back
            return null;
        }
    }

    /// <summary>
    /// Fills in sections that deserialized to null. A hand-edited or partially written file with
    /// <c>"Playback": null</c> deserializes successfully and then throws a NullReferenceException
    /// deep in the playback path instead of at load time.
    /// </summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Output ??= new OutputSettings();
        settings.Playback ??= new PlaybackSettings();
        settings.Normalizer ??= new NormalizerSettings();
        settings.Equalizer ??= new EqualizerSettings();
        settings.Library ??= new LibrarySettings();
        settings.Lyrics ??= new LyricsSettings();
        settings.LyricsOnline ??= new LyricsOnlineSettings();
        settings.Ui ??= new UiSettings();
        settings.Shortcuts ??= new ShortcutSettings();
        return settings;
    }

    public static void Save(AppSettings settings) => WriteJson(Serialize(settings));

    /// <summary>
    /// Serializes settings to JSON. Split out from <see cref="Save"/> so a caller can take a
    /// consistent snapshot on the thread that owns the settings object and hand the slow file
    /// write to a background thread (see <see cref="SettingsWriter"/>).
    /// </summary>
    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    /// <summary>Atomically replaces the settings file with <paramref name="json"/>.</summary>
    public static void WriteJson(string json) => WriteJson(json, Util.AppPaths.SettingsFile);

    /// <summary>
    /// Atomically replaces <paramref name="targetPath"/> with <paramref name="json"/>. The path is
    /// a parameter so a debounced writer can capture it alongside the snapshot — resolving
    /// <see cref="Util.AppPaths.SettingsFile"/> at flush time would write a stale snapshot into
    /// whatever directory happened to be current by then.
    /// </summary>
    public static void WriteJson(string json, string targetPath)
    {
        lock (SyncLock)
        {
            try
            {
                // Sweep leftovers from an earlier crash once per process; enumerating the
                // directory on every single save is pure overhead on a hot path.
                if (Interlocked.Exchange(ref _tempSweepDone, 1) == 0)
                {
                    AtomicFile.CleanupStaleTempFiles(targetPath);
                }

                AtomicFile.WriteAllText(targetPath, json, keepBackup: true, flushToDisk: true);
            }
            catch
            {
                // Best-effort persistence
            }
        }
    }

    private static int _tempSweepDone;

    private static string? ReadAllTextWithRetry(string path, int maxRetries = 15)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxRetries - 1) throw;
                Thread.Sleep(5 + (attempt * 5));
            }
        }

        return null;
    }

}

