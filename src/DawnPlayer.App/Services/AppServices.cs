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
using Microsoft.Extensions.DependencyInjection;

namespace DawnPlayer.App.Services;

/// <summary>Composition root: owns core services and marshals core events to the UI thread.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    /// <summary>
    /// The composition container. Service construction lives in the registrations inside
    /// <see cref="Initialize"/>; these static accessors remain as the (documented) compatibility
    /// seam — pages and controls read services through them, and the static event bus stays
    /// until page view models replace direct subscriptions. The container is never disposed:
    /// Shutdown disposes the disposable services explicitly, in a deliberate order.
    /// </summary>
    public static System.IServiceProvider Container { get; private set; } = null!;

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
    public static ScrobbleService Scrobbler { get; private set; } = null!;
    public static Core.Audio.Dsp.Plugins.DspPluginLoader DspPlugins { get; private set; } = null!;
    public static MotionService Motion { get; private set; } = null!;
    public static Core.Persistence.RadioStationStore Stations { get; private set; } = null!;
    public static Core.Persistence.YouTubeRecentStore YouTubeRecent { get; private set; } = null!;

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
    /// <summary>A rating command just landed for these tracks (UI thread, after the in-memory and
    /// DB writes). Lets surfaces that don't bind PlaylistItem proxies — the now-playing bar, the
    /// library track list — refresh their rating cells when the change happened elsewhere.</summary>
    public static event Action<IReadOnlyList<Track>>? RatingsApplied;
    /// <summary>Live now-playing metadata (radio ICY station/song), already marshaled to the UI
    /// thread and with <see cref="PlaylistItem.NowPlayingSubtitle"/> already applied.</summary>
    public static event Action<Core.Audio.LiveStreamMetadata>? LiveStreamTitleChanged;

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
        // The YouTube reader spawns external tools; the runner carries the user's configured
        // binary paths (empty = PATH resolution) and can be swapped at runtime from the section.
        Core.Network.YouTube.YouTubeProcess.Runner =
            new Core.Network.YouTube.YouTubeProcessRunner(Settings.YouTube.YtDlpPath, Settings.YouTube.FfmpegPath);
        // Core reads the DSD playback policy through a process-wide hook (it has no settings
        // service); keep both sides in sync here and wherever the setting changes.
        Core.Audio.DsdSupport.PlaybackMode = Settings.Output.DsdPlaybackMode;
        // Core cannot reach the app's resource pipeline; hand it localized formatters instead.
        AlbumGroup.SongCountFormatter =
            count => AppStrings.Format("Library_TrackCountFormat", "{0}곡", count);
        Library = OpenLibraryResilient(out var dbRecoveryMessage);

        // Service composition moved into a DI container: each registration owns its
        // construction (and its own side effects); cross-service event wiring stays imperative
        // below, where the full graph exists. Statics are assigned from resolved instances.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(sp => Settings);
        services.AddSingleton(Library);
        services.AddSingleton(sp =>
        {
            var playlists = new PlaylistManager(Library)
            {
                // Playlists and Playlist.Items are bound straight to WinUI controls, so the async
                // add/import paths must apply their results on the UI thread.
                UiDispatcher = new DelegateUiDispatcher(RunOnUi)
            };
            playlists.LoadAll();
            // Names are injected because Core cannot reach the app's localized resources; a language
            // change recreates them on the next launch.
            playlists.EnsureSmartPlaylists(new[]
            {
                (SmartPlaylistKind.MostPlayed, AppStrings.Get("Smart_MostPlayed", "많이 재생")),
                (SmartPlaylistKind.RecentlyAdded, AppStrings.Get("Smart_RecentlyAdded", "최근 추가")),
                (SmartPlaylistKind.NotRecentlyPlayed, AppStrings.Get("Smart_NotRecentlyPlayed", "한동안 안 들은")),
            });
            playlists.EnsureUserSmartPlaylists(
                Settings.Playlist.UserSmartPlaylists.Select(d => (d.Name, d.Query)).ToList());
            playlists.UserSmartPlaylistsChanged += () =>
            {
                Settings.Playlist.UserSmartPlaylists = playlists.GetUserSmartPlaylists()
                    .Select(t => new SmartPlaylistDefinition(t.Name, t.Query))
                    .ToList();
                SettingsWriter.Schedule(Settings);
            };
            return playlists;
        });
        services.AddSingleton<PlaybackController>(sp =>
            new PlaybackController(Settings, sp.GetRequiredService<PlaylistManager>()));
        services.AddSingleton(sp => new SleepTimerService());
        services.AddSingleton(sp => new ScrobbleService(() => Settings, msg => App.Log(msg)));
        services.AddSingleton(sp =>
        {
            var loader = new Core.Audio.Dsp.Plugins.DspPluginLoader(msg => App.Log($"[dsp-plugins] {msg}"));
            loader.Reload();
            return loader;
        });
        services.AddSingleton(sp => new AudioSettingsService(Settings, sp.GetRequiredService<PlaybackController>()));
        services.AddSingleton(sp => new EqSettingsService(Settings, sp.GetRequiredService<PlaybackController>()));
        services.AddSingleton(sp => new AppearanceSettingsService(Settings));
        services.AddSingleton(sp => new MotionService(new WindowsMotionSource(), () => Settings.Ui.MotionEnabled));
        services.AddSingleton(sp => new ShortcutService(Settings));
        services.AddSingleton(sp =>
        {
            var lyricsOnline = new LyricsOnlineService(() => Settings, App.Log);
            lyricsOnline.Initialize();
            return lyricsOnline;
        });
        services.AddSingleton(sp => new Core.Persistence.RadioStationStore());
        Container = services.BuildServiceProvider();

        // U3: seed the list-density resources from the persisted preset before any page XAML
        // realizes rows; appearance changes re-apply it (see the AppearanceChanged hook below).
        ApplyDensity(Settings.Ui.DensityMode);

        Playlists = Container.GetRequiredService<PlaylistManager>();
        Playback = Container.GetRequiredService<PlaybackController>();
        SleepTimer = Container.GetRequiredService<SleepTimerService>();
        Scrobbler = Container.GetRequiredService<ScrobbleService>();
        DspPlugins = Container.GetRequiredService<Core.Audio.Dsp.Plugins.DspPluginLoader>();
        // The controller's chain effect reads this loader when a session builds its graph.
        Playback.AttachDspPlugins(DspPlugins);
        Playlists.ItemsRemoved += (_, items) => Playback.Queue.RemoveItems(items);

        AudioSettings = Container.GetRequiredService<AudioSettingsService>();
        EqSettings = Container.GetRequiredService<EqSettingsService>();
        AppearanceSettings = Container.GetRequiredService<AppearanceSettingsService>();
        Motion = Container.GetRequiredService<MotionService>();
        Shortcuts = Container.GetRequiredService<ShortcutService>();
        Stations = Container.GetRequiredService<Core.Persistence.RadioStationStore>();
        YouTubeRecent = new Core.Persistence.YouTubeRecentStore();
        var lyricsOnline = Container.GetRequiredService<LyricsOnlineService>();
        LyricsOnline = lyricsOnline;
        AppearanceSettings.AppearanceChanged += () => RunOnUi(() =>
        {
            App.MainWin?.ApplyTheme();
            // Accent/palette changes must reach the auxiliary windows (lyrics editor/search)
            // too — they used to keep the stale accent forever.
            ThemeService.RefreshAuxiliaryWindows(Settings.Ui);
            // U3: density preset may have changed — re-seed the row-metric resources. Rows that
            // are already realized pick the new metrics up on their next rebuild trigger
            // (filter/zoom/track change); brand-new realizations use them immediately.
            ApplyDensity(Settings.Ui.DensityMode);
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

        Playback.CurrentChanged += item => RunOnUi(() =>
        {
            CurrentTrackChanged?.Invoke(item);
            if (item != null && Playback.State == PlaybackState.Playing)
            {
                Scrobbler.NotifyTrackStarted(item.Track);
            }
        });
        Playback.StateChanged += () => RunOnUi(() =>
        {
            // Resuming a restored session also counts as "started playing now" for now-playing.
            if (Playback.State == PlaybackState.Playing && Playback.CurrentItem != null)
            {
                Scrobbler.NotifyTrackStarted(Playback.CurrentItem.Track);
            }
        });
        // Radio ICY: format and store the live subtitle on the item, then broadcast for views/SMTC.
        Playback.StreamTitleChanged += m => RunOnUi(() =>
        {
            m.Item.NowPlayingSubtitle = Controls.RadioSubtitleFormatter.Format(m.StationName, m.StreamTitle);
            LiveStreamTitleChanged?.Invoke(m);
        });
        Playback.StateChanged += () => RunOnUi(() => PlaybackStateChanged?.Invoke());
        Playback.StopAfterCurrentChanged += () => RunOnUi(() =>
        {
            // The one-shot stop flag drops back to false the moment the stop lands — feed that
            // into the sleep timer so "sleep after this track" resets its menu state too.
            if (!Playback.StopAfterCurrent) SleepTimer.OnStopAfterCurrentConsumed();
            StopAfterCurrentChanged?.Invoke();
        });
        Playback.AbRepeatChanged += () => RunOnUi(() => AbRepeatChanged?.Invoke());
        Playback.AbRepeatRejected += reason => RunOnUi(() => AbRepeatRejected?.Invoke(reason));
        Playback.RemoteArtResolved += track => RunOnUi(() => RemoteArtResolved?.Invoke(track));
        Playback.TrackLeft += OnPlaybackTrackLeft;
        Playback.Warning += msg =>
        {
            // Core emits keyed sentences (it has no resource catalog); translate here so the
            // InfoBar shows the UI language while the log keeps the raw wire format.
            var localized = Localization.AppStrings.LocalizeCoreMessage(msg);
            App.Log($"[Playback] {msg}");
            RunOnUi(() => WarningRaised?.Invoke(localized));
        };
        Playback.SessionStarted += info =>
        {
            App.Log($"[Session] {info.DeviceName} exclusive={info.Exclusive} {info.FormatDescription}");
            RunOnUi(() => OutputSessionChanged?.Invoke(info));
            // A fresh sequencer carries no impulse; the convolver re-applies per session.
            Playback.ApplyConvolution();
            Playback.ApplyPluginDsp();
        };
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

    /// <summary>Raised on the UI thread when an A-B repeat press was refused (see PlaybackController).</summary>
    public static event Action<Core.Audio.AbRepeatRejectionReason>? AbRepeatRejected;

    /// <summary>Raised on the UI thread when a remote track's art finished resolving (see PlaybackController).</summary>
    public static event Action<Core.Models.Track>? RemoteArtResolved;

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
                Library.RecordPlayEvent(track); // per-play history for the listening report
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
            if (counted)
            {
                Scrobbler.NotifyTrackPlayed(track, TimeSpan.Zero);
            }
            RunOnUi(Playlists.RefreshSmartPlaylists);
        }
        catch (Exception ex)
        {
            App.Log($"[stats] failed to record: {ex}");
        }
    }

    // ---------------- rating ----------------

    /// <summary>
    /// Applies a 0-5 star rating to the working-set tracks: the in-memory value and the DB column
    /// update immediately, and the file tags are rewritten on the thread pool through the atomic
    /// tag writer (best effort — a read-only file just keeps its DB rating, and every failure is
    /// surfaced to the user through the InfoBar, never left in the log alone). Target selection,
    /// the no-op guard and the tag-write path all come from <see cref="RatingCommands.SelectTargets"/>:
    /// streams are never rateable, duplicate paths merge, and a batch whose ratings already match
    /// is a complete no-op (no DB churn, no tag rewrite, no smart-playlist refresh, no event).
    /// Refreshes smart playlists afterwards because queries can filter on %rating%. Must be called
    /// on the UI thread: it notifies the bound rating proxies of every playlist item sharing the
    /// track, and raises <see cref="RatingsApplied"/> for the surfaces without proxies.
    /// </summary>
    public static void RateTracks(IReadOnlyList<Track> tracks, int stars)
    {
        if (tracks == null || tracks.Count == 0) return;
        var distinct = RatingCommands.SelectTargets(tracks, stars);
        if (distinct.Count == 0) return;
        stars = RatingCommands.Normalize(stars);

        foreach (var track in distinct)
        {
            track.Rating = stars;
            Library.UpdateRating(track);
        }

        foreach (var pl in Playlists.Playlists)
        {
            if (pl == null) continue;
            foreach (var item in pl.GetSnapshot())
            {
                if (item != null && distinct.Any(t => ReferenceEquals(t, item.Track)))
                {
                    item.SyncRating();
                }
            }
        }

        var paths = distinct.Select(t => RatingCommands.TagWritePath(t.Path)).ToList();
        Task.Run(() =>
        {
            var failed = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (!Core.Library.TagWriter.TrySetRating(path, stars))
                    {
                        failed++;
                        App.Log($"[rating] tag write failed: {path}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    App.Log($"[rating] tag write threw: {path}: {ex.Message}");
                }
            }

            var notice = RatingCommands.FormatTagWriteFailure(failed);
            if (notice != null) RaiseWarning(notice);
        });

        RatingsApplied?.Invoke(distinct);
        Playlists.RefreshSmartPlaylists();
    }

    // ---------------- listening report ----------------

    /// <summary>Window the listening report aggregates over.</summary>
    public enum ReportPeriod { AllTime, Last30Days, ThisYear }

    /// <summary>Builds the listening report on a thread-pool thread; call from the UI thread and
    /// await. Falls back to the aggregate columns when no play history exists (pre-v3 database).</summary>
    public static Task<Core.Library.ListeningReport> BuildListeningReportAsync(ReportPeriod period)
    {
        var now = DateTime.UtcNow;
        var since = period switch
        {
            ReportPeriod.Last30Days => now - TimeSpan.FromDays(30),
            ReportPeriod.ThisYear => new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => DateTime.MinValue,
        };

        return Task.Run(() =>
        {
            var events = Library.ReadPlayEvents(since.Ticks);
            return Core.Library.ListeningReportBuilder.Build(Library.Tracks, events, since, now);
        });
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
            catch (Exception ex) { RunOnUi(() => WarningRaised?.Invoke(AppStrings.Format("Msg_LibraryScanFailed", "라이브러리 스캔 실패: {0}", ex.Message))); }
            finally
            {
                Interlocked.CompareExchange(ref _scanCts, null, cts);
                cts.Dispose();
            }
        });
    }

    // ---------------- ReplayGain batch scan ----------------

    private static CancellationTokenSource? _rgScanCts;
    private static Task? _rgScanTask;

    public static event Action<string>? RgScanProgressChanged;
    public static bool IsRgScanRunning => Volatile.Read(ref _rgScanCts) != null;

    /// <summary>
    /// Scans the library for loudness (EBU R128, ReplayGain 2.0 at −18 LUFS), storing track and
    /// album values in the DB and writing REPLAYGAIN_* tags back to the files. Cancels any running
    /// RG scan; a library scan running concurrently is left alone (both only read the files).
    /// </summary>
    /// <param name="rescanAll">False analyzes only tracks whose tags are missing.</param>
    public static void StartReplayGainScan(bool rescanAll)
    {
        var previous = Interlocked.Exchange(ref _rgScanCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        var cts = new CancellationTokenSource();
        _rgScanCts = cts;
        var ct = cts.Token;

        _rgScanTask = Task.Run(() => RunReplayGainScan(rescanAll, ct), ct)
            .ContinueWith(t =>
            {
                Interlocked.CompareExchange(ref _rgScanCts, null, cts);
                cts.Dispose();
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.InnerException;
                    if (ex != null && ex is not OperationCanceledException)
                    {
                        RunOnUi(() => WarningRaised?.Invoke(AppStrings.Format("Msg_RgScanFailed", "ReplayGain 분석 실패: {0}", ex.Message)));
                    }
                }
            }, TaskScheduler.Default);
    }

    public static void CancelReplayGainScan()
    {
        var cts = Interlocked.Exchange(ref _rgScanCts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static void RunReplayGainScan(bool rescanAll, CancellationToken ct)
    {
        var tracks = Library.Tracks
            .Where(t => rescanAll
                || !t.RgTrackGainDb.HasValue || !t.RgTrackPeak.HasValue
                || !t.RgAlbumGainDb.HasValue || !t.RgAlbumPeak.HasValue)
            .ToList();
        // Radio URLs are not in the library, but guard anyway: decoding a live stream would
        // never finish.
        tracks.RemoveAll(t => Core.Audio.RadioTrack.IsStreamUrl(t.Path));
        int total = tracks.Count;
        if (total == 0)
        {
            RunOnUi(() => RgScanProgressChanged?.Invoke(AppStrings.Get(
                "Settings_Library_Rg_NothingToScan", "분석할 트랙이 없습니다 (모두 ReplayGain 태그가 있습니다).")));
            return;
        }

        int done = 0;
        int failures = 0;

        foreach (var group in tracks.GroupBy(t => t.AlbumKey))
        {
            ct.ThrowIfCancellationRequested();
            var members = group.ToList();

            // Album values integrate every block of the album: one scanner is fed across all of
            // the album's tracks while each track is finished separately for its track values.
            Core.Audio.Dsp.LoudnessScanner? albumScanner = null;

            foreach (var track in members)
            {
                ct.ThrowIfCancellationRequested();
                RunOnUi(() => RgScanProgressChanged?.Invoke(AppStrings.Format(
                    "Settings_Library_Rg_ScanningFormat", "{0}/{1} · {2}",
                    Interlocked.Increment(ref done), total, track.Title)));

                try
                {
                    using var reader = Core.Audio.AudioFileReaderFactory.Open(track.Path);
                    var fmt = reader.SourceFormat;
                    var weights = SurroundWeights(fmt.Channels);

                    var trackScanner = new Core.Audio.Dsp.LoudnessScanner(fmt.SampleRate, fmt.Channels);
                    var buf = new float[fmt.SampleRate * fmt.Channels]; // ~1 s slices
                    int read;
                    while ((read = reader.Samples.Read(buf)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        trackScanner.ProcessSamples(buf, 0, read, fmt.Channels, weights);
                    }

                    var result = trackScanner.Finish();
                    albumScanner ??= new Core.Audio.Dsp.LoudnessScanner(fmt.SampleRate, fmt.Channels);
                    albumScanner.AppendBlocks(trackScanner.BlockEnergies);

                    track.RgTrackGainDb = Math.Round(SafeGainDb(result), 2);
                    track.RgTrackPeak = Math.Round(Math.Min(result.Peak, 1.0), 6);
                    Library.UpdateReplayGain(track);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.Log($"[rg-scan] {track.Path}: {ex.Message}");
                    failures++;
                }
            }

            if (albumScanner != null)
            {
                var albumResult = albumScanner.Finish();
                double albumGain = SafeGainDb(albumResult);
                double albumPeak = Math.Min(albumResult.Peak, 1.0);
                foreach (var track in members)
                {
                    if (!track.RgTrackGainDb.HasValue) continue; // member failed above
                    track.RgAlbumGainDb = Math.Round(albumGain, 2);
                    track.RgAlbumPeak = Math.Round(albumPeak, 6);
                    Library.UpdateReplayGain(track);
                    if (!Core.Library.TagWriter.TrySetReplayGain(Core.Util.AppPaths.PhysicalPath(track.Path),
                        track.RgTrackGainDb.Value, track.RgTrackPeak ?? 0,
                        track.RgAlbumGainDb, track.RgAlbumPeak,
                        writeR128: Settings.Library.WriteR128Tags))
                    {
                        App.Log($"[rg-scan] tag write failed: {track.Path}");
                    }
                }
            }
        }

        RunOnUi(() => RgScanProgressChanged?.Invoke(AppStrings.Format(
            "Settings_Library_Rg_DoneFormat", "완료: {0}개 분석, {1}개 실패", total - failures, failures)));
    }

    /// <summary>Gain for a result, treating silence (dropped gates) as unity instead of −inf.</summary>
    private static double SafeGainDb(Core.Audio.Dsp.LoudnessResult result) =>
        double.IsNegativeInfinity(result.IntegratedLufs) ? 0.0 : result.TrackGainDb;

    private static double[]? SurroundWeights(int channels) => channels switch
    {
        1 => new double[] { 1.0 },
        2 => new double[] { 1.0, 1.0 },
        6 => new double[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41 },
        _ => null,
    };

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

        // The RG batch scan writes through the same SQLite connection.
        try
        {
            var rgCts = Interlocked.Exchange(ref _rgScanCts, null);
            rgCts?.Cancel();
            _rgScanTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }

        try
        {
            Playlists.SaveAll();
            Stations?.Save();
            YouTubeRecent?.Save();
            SettingsWriter.FlushNow(Settings);
        }
        catch { }
        try { Playback.Dispose(); } catch { }
        try { Library.Dispose(); } catch { }
        try { Smtc.Dispose(); } catch { }
    }

    /// <summary>U3: writes the density preset's row metrics into the application resource scope.
    /// Kept here (not in DensityScale) so the pure mapping stays linkable into the test project,
    /// which cannot reference WinUI types.</summary>
    private static void ApplyDensity(string mode)
    {
        var m = DensityScale.For(mode);
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        resources[DensityScale.ResourceKeys.TrackRowMinHeight] = m.MinHeight;
        resources[DensityScale.ResourceKeys.TrackRowSpacing] = m.Spacing;
        resources[DensityScale.ResourceKeys.ListCoverSize] = m.CoverListSize;
    }
}
