using DawnPlayer.App.Localization;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DawnPlayer.App.Services;

/// <summary>Composition root: owns core services and marshals core events to the UI thread.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    public static MusicLibrary Library { get; private set; } = null!;
    public static PlaylistManager Playlists { get; private set; } = null!;
    public static PlaybackController Playback { get; private set; } = null!;
    public static ISmtcService Smtc { get; set; } = null!;
    public static IAudioSettingsService AudioSettings { get; set; } = null!;
    public static IEqSettingsService EqSettings { get; set; } = null!;
    public static IAppearanceSettingsService AppearanceSettings { get; set; } = null!;
    public static IShortcutService Shortcuts { get; set; } = null!;
    public static ILyricsOnlineService LyricsOnline { get; set; } = null!;
    public static SleepTimerService SleepTimer { get; private set; } = null!;

    public static DispatcherQueue? Ui { get; private set; }
    public static IntPtr MainWindowHandle { get; private set; }

    // UI-thread notifications
    public static event Action<PlaylistItem?>? CurrentTrackChanged;
    public static event Action? PlaybackStateChanged;
    public static event Action? StopAfterCurrentChanged;
    public static event Action<string>? WarningRaised;
    public static event Action? LibraryChanged;
    public static event Action? QueueChanged;
    public static event Action<SessionInfo>? OutputSessionChanged;
    public static event Action? LyricsSettingsChanged;
    public static event Action<Track?>? LyricsChanged;

    public static void RaiseLyricsSettingsChanged() => RunOnUi(() => LyricsSettingsChanged?.Invoke());
    public static void RaiseLyricsChanged(Track? track) => RunOnUi(() => LyricsChanged?.Invoke(track));

    private static CancellationTokenSource? _scanCts;
    private static Task? _scanTask;

    /// <summary>
    /// Loads settings and applies the saved UI language. Must run before any XAML is loaded:
    /// PrimaryLanguageOverride only affects resources resolved after it is set, so calling this
    /// from the App constructor (before <c>InitializeComponent</c>) is what makes x:Uid strings
    /// honor the user's language on the very first frame. Unpackaged apps must re-apply the
    /// override every launch because it is not persisted.
    /// </summary>
    public static void ApplyStartupLanguage()
    {
        if (Settings == null)
        {
            Settings = SettingsStore.Load();
        }

        AppStrings.Instance = new MrtLocalizationService();
        AppStrings.ApplyLanguage(Bcp47(Settings.Ui.Language));
    }

    public static void Initialize(Window window)
    {
        Ui = window.DispatcherQueue;
        MainWindowHandle = WindowNative.GetWindowHandle(window);
        PlaylistItem.UiDispatcher = RunOnUi;
        Controls.PlaybackUiHelper.Logger = App.Log;

        if (Settings == null)
        {
            Settings = SettingsStore.Load();
            AppStrings.ApplyLanguage(Bcp47(Settings.Ui.Language));
        }
        // Core cannot reach the app's resource pipeline; hand it localized formatters instead.
        AlbumGroup.SongCountFormatter =
            count => AppStrings.Format("Library_TrackCountFormat", "{0}곡", count);
        Library = OpenLibraryResilient(out var dbRecoveryMessage);
        Playlists = new PlaylistManager(Library)
        {
            // Playlists and Playlist.Items are bound straight to WinUI controls, so the async
            // add/import paths must apply their results on the UI thread.
            UiInvoke = RunOnUi
        };
        Playlists.LoadAll();
        // Names are injected because Core cannot reach the app's localized resources; a language
        // change recreates them on the next launch.
        Playlists.EnsureSmartPlaylists(new[]
        {
            (SmartPlaylistKind.MostPlayed, AppStrings.Get("Smart_MostPlayed", "많이 재생")),
            (SmartPlaylistKind.RecentlyAdded, AppStrings.Get("Smart_RecentlyAdded", "최근 추가")),
            (SmartPlaylistKind.NotRecentlyPlayed, AppStrings.Get("Smart_NotRecentlyPlayed", "한동안 안 들은")),
        });
        Playback = new PlaybackController(Settings, Playlists);
        SleepTimer = new SleepTimerService();
        Playlists.ItemsRemoved += (_, items) => Playback.Queue.RemoveItems(items);

        AudioSettings = new AudioSettingsService(Settings, Playback);
        EqSettings = new EqSettingsService(Settings, Playback);
        AppearanceSettings = new AppearanceSettingsService(Settings);
        Shortcuts = new ShortcutService(Settings);
        var lyricsOnline = new LyricsOnlineService(() => Settings, App.Log);
        LyricsOnline = lyricsOnline;
        lyricsOnline.Initialize();
        AppearanceSettings.AppearanceChanged += () => RunOnUi(() =>
        {
            App.MainWin?.ApplyTheme();
            // Close-to-tray may have just been toggled: keep the tray icon's lifetime in sync. A
            // disable while the window is hidden would strand the app with no visible surface, so
            // the window comes back up before the icon goes away.
            if (Settings.Ui.CloseToTray)
            {
                TrayIconService.EnsureCreated();
            }
            else if (TrayIconService.IsRunning)
            {
                if (TrayIconService.IsWindowHidden) TrayIconService.RestoreFromTray();
                TrayIconService.Destroy();
            }
        });

        Smtc = new SmtcService(Playback);
        Smtc.TryInitialize(MainWindowHandle);

        Playback.CurrentChanged += item => RunOnUi(() => CurrentTrackChanged?.Invoke(item));
        Playback.StateChanged += () => RunOnUi(() => PlaybackStateChanged?.Invoke());
        Playback.StopAfterCurrentChanged += () => RunOnUi(() =>
        {
            // The one-shot stop flag drops back to false the moment the stop lands — feed that
            // into the sleep timer so "sleep after this track" resets its menu state too.
            if (!Playback.StopAfterCurrent) SleepTimer.OnStopAfterCurrentConsumed();
            StopAfterCurrentChanged?.Invoke();
        });
        Playback.AbRepeatChanged += () => RunOnUi(() => AbRepeatChanged?.Invoke());
        Playback.TrackLeft += OnPlaybackTrackLeft;
        Playback.Warning += msg => { App.Log($"[Playback] {msg}"); RunOnUi(() => WarningRaised?.Invoke(msg)); };
        Playback.SessionStarted += info => { App.Log($"[Session] {info.DeviceName} exclusive={info.Exclusive} {info.FormatDescription}"); RunOnUi(() => OutputSessionChanged?.Invoke(info)); };
        Library.TracksChanged += () =>
        {
            // A scan re-sorts every smart playlist; coalesce here so the UI event and the refresh
            // land in the same dispatch.
            RunOnUi(() =>
            {
                Playlists.RefreshSmartPlaylists();
                LibraryChanged?.Invoke();
            });
        };
        Library.ScanProgress += p => RunOnUi(() => ScanProgressChanged?.Invoke(p));
        Playback.Queue.Changed += () => RunOnUi(() => QueueChanged?.Invoke());

        if (dbRecoveryMessage != null) RaiseWarning(dbRecoveryMessage);
    }

    /// <summary>Raised on the UI thread after the A-B repeat stage changed (see PlaybackController).</summary>
    public static event Action? AbRepeatChanged;

    // Play-count heuristics, shared by every leave reason: a track counts as played when it
    // drained on its own or was left past 75% of its length, and as skipped only for an early
    // manual jump on a substantial track (a 20 s jingle skipped at 10 s heard most of it).
    private static readonly TimeSpan SkippedMaxPosition = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SkippedMinDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stats sink for <see cref="PlaybackController.TrackLeft"/>: applies the play/skip heuristics
    /// to the working-set track, persists the counts, and refreshes the smart playlists. Runs on a
    /// ThreadPool thread; the Track model mutations are plain field writes the UI never binds.
    /// </summary>
    private static void OnPlaybackTrackLeft(PlaylistItem item, TimeSpan position, PlaybackLeaveReason reason)
    {
        try
        {
            var track = item.Track;
            var duration = track.Duration;
            bool counted = reason == PlaybackLeaveReason.NaturalEnd
                           || (duration > TimeSpan.Zero && position >= TimeSpan.FromTicks(duration.Ticks * 3 / 4));
            bool skipped = !counted
                           && reason == PlaybackLeaveReason.ManualAdvance
                           && duration >= SkippedMinDuration
                           && position <= SkippedMaxPosition
                           && duration > TimeSpan.Zero
                           && position < TimeSpan.FromTicks(duration.Ticks / 4);

            if (counted)
            {
                track.PlayCount++;
                track.LastPlayedUtcTicks = DateTime.UtcNow.Ticks;
            }
            else if (skipped)
            {
                track.SkipCount++;
            }
            else
            {
                return;
            }

            Library.UpdateStats(track);
            RunOnUi(Playlists.RefreshSmartPlaylists);
        }
        catch (Exception ex)
        {
            App.Log($"[stats] failed to record: {ex}");
        }
    }

    /// <summary>
    /// Opens the library database, recovering from a corrupt or unreadable file. A throw here used
    /// to escape the MainWindow constructor, and because App.UnhandledException marks everything
    /// handled the process survived with no window at all — invisible and unrecoverable without
    /// deleting library.db by hand.
    /// </summary>
    private static MusicLibrary OpenLibraryResilient(out string? recoveryMessage)
    {
        recoveryMessage = null;
        try
        {
            var library = new MusicLibrary();
            library.LoadFromDb();
            return library;
        }
        catch (Exception ex)
        {
            App.Log($"[Library open failed] {ex}");
        }

        // Move the unusable file aside and start clean so the app launches and can rescan.
        try
        {
            var dbPath = AppPaths.LibraryDbPath;
            if (File.Exists(dbPath))
            {
                File.Move(dbPath, $"{dbPath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Library quarantine failed] {ex}");
        }

        var fresh = new MusicLibrary();
        fresh.LoadFromDb();
        recoveryMessage = AppStrings.Get("Msg_DbRecoveryMessage", "라이브러리 데이터베이스를 열 수 없어 새로 만들었습니다. 설정에서 다시 스캔해 주세요.");
        return fresh;
    }

    public static event Action<DawnPlayer.Core.Library.ScanProgress>? ScanProgressChanged;
    public static event Action<UiLanguage>? LanguageChanged;

    /// <summary>
    /// Maps a <see cref="UiLanguage"/> enum to a BCP-47 tag for <see cref="AppStrings.ApplyLanguage"/>
    /// and the PrimaryLanguageOverride setter. Null means "follow the system language".
    /// </summary>
    public static string? Bcp47(UiLanguage language) => language switch
    {
        UiLanguage.KoKR => "ko-KR",
        UiLanguage.EnUS => "en-US",
        UiLanguage.JaJP => "ja-JP",
        _ => null
    };

    public static void RunOnUi(Action action) => Ui?.TryEnqueue(() => action());

    /// <summary>Raises <see cref="WarningRaised"/> from anywhere (marshals to UI).</summary>
    public static void RaiseWarning(string message) => RunOnUi(() => WarningRaised?.Invoke(message));

    /// <summary>
    /// Updates the user's language preference, applies it to the resource pipeline, and raises
    /// <see cref="LanguageChanged"/> on the UI thread. Strings fetched through <see cref="AppStrings"/>
    /// switch immediately, but already-loaded x:Uid content does not re-resolve, so the handler
    /// of <see cref="LanguageChanged"/> offers an app restart.
    /// </summary>
    public static void ChangeLanguage(UiLanguage language)
    {
        if (Settings.Ui.Language == language) return;
        Settings.Ui.Language = language;
        AppStrings.ApplyLanguage(Bcp47(language));
        SettingsWriter.Schedule(Settings);
        RunOnUi(() => LanguageChanged?.Invoke(language));
    }

    /// <summary>
    /// Saves settings synchronously, starts a new process, and closes the main window through
    /// the normal shutdown path (session save, placement save). Used after a language switch,
    /// where the debounced <see cref="SettingsWriter.Schedule"/> write could otherwise be lost.
    /// </summary>
    public static void RestartApp()
    {
        SettingsWriter.FlushNow(Settings);
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Log($"[restart] failed to launch new process: {ex}");
            }
        }
        App.MainWin?.Close();
    }

    /// <summary>Starts a library scan (cancels any running one).</summary>
    public static void StartLibraryScan()
    {
        // Each scan owns its own CTS and disposes it only after the scan has stopped using the
        // token. Disposing the previous CTS here instead would let an in-flight scan register a
        // cancellation callback on a disposed source and die with ObjectDisposedException.
        var previous = Interlocked.Exchange(ref _scanCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var ct = cts.Token;

        _scanTask = Task.Run(async () =>
        {
            try { await Library.ScanAsync(Settings, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RunOnUi(() => WarningRaised?.Invoke(AppStrings.Format("Msg_LibraryScanFailed", ex.Message))); }
            finally
            {
                Interlocked.CompareExchange(ref _scanCts, null, cts);
                cts.Dispose();
            }
        });
    }

    public static void Shutdown()
    {
        // Stop the scan before disposing the library: a scan still running would write through
        // the SQLite connection Library.Dispose() is about to close.
        try
        {
            var cts = Interlocked.Exchange(ref _scanCts, null);
            cts?.Cancel();
            _scanTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }

        try
        {
            Playlists.SaveAll();
            SettingsWriter.FlushNow(Settings);
        }
        catch { }
        try { Playback.Dispose(); } catch { }
        try { Library.Dispose(); } catch { }
        try { Smtc.Dispose(); } catch { }
    }
}
