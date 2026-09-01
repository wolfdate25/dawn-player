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

    public static void Initialize(Window window)
    {
        Ui = window.DispatcherQueue;
        MainWindowHandle = WindowNative.GetWindowHandle(window);
        PlaylistItem.UiDispatcher = RunOnUi;
        Controls.PlaybackUiHelper.Logger = App.Log;

        Settings = SettingsStore.Load();
        // Apply language before any UI/service is built so the first frame already sees the
        // user's choice and downstream components that read CurrentUICulture match.
        StringsLoader.ApplyLanguage(Settings.Ui.Language);
        Library = OpenLibraryResilient(out var dbRecoveryMessage);
        Playlists = new PlaylistManager(Library)
        {
            // Playlists and Playlist.Items are bound straight to WinUI controls, so the async
            // add/import paths must apply their results on the UI thread.
            UiInvoke = RunOnUi
        };
        Playlists.LoadAll();
        Playback = new PlaybackController(Settings, Playlists);
        Playlists.ItemsRemoved += (_, items) => Playback.Queue.RemoveItems(items);

        AudioSettings = new AudioSettingsService(Settings, Playback);
        EqSettings = new EqSettingsService(Settings, Playback);
        AppearanceSettings = new AppearanceSettingsService(Settings);
        Shortcuts = new ShortcutService(Settings);
        var lyricsOnline = new LyricsOnlineService(() => Settings, App.Log);
        LyricsOnline = lyricsOnline;
        lyricsOnline.Initialize();
        AppearanceSettings.AppearanceChanged += () => RunOnUi(() => App.MainWin?.ApplyTheme());

        Smtc = new SmtcService(Playback);
        Smtc.TryInitialize(MainWindowHandle);

        Playback.CurrentChanged += item => RunOnUi(() => CurrentTrackChanged?.Invoke(item));
        Playback.StateChanged += () => RunOnUi(() => PlaybackStateChanged?.Invoke());
        Playback.StopAfterCurrentChanged += () => RunOnUi(() => StopAfterCurrentChanged?.Invoke());
        Playback.Warning += msg => { App.Log($"[Playback] {msg}"); RunOnUi(() => WarningRaised?.Invoke(msg)); };
        Playback.SessionStarted += info => { App.Log($"[Session] {info.DeviceName} exclusive={info.Exclusive} {info.FormatDescription}"); RunOnUi(() => OutputSessionChanged?.Invoke(info)); };
        Library.TracksChanged += () => RunOnUi(() => LibraryChanged?.Invoke());
        Library.ScanProgress += p => RunOnUi(() => ScanProgressChanged?.Invoke(p));
        Playback.Queue.Changed += () => RunOnUi(() => QueueChanged?.Invoke());

        if (dbRecoveryMessage != null) RaiseWarning(dbRecoveryMessage);
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
        recoveryMessage = "라이브러리 데이터베이스를 열 수 없어 새로 만들었습니다. 설정에서 다시 스캔해 주세요.";
        return fresh;
    }

    public static event Action<DawnPlayer.Core.Library.ScanProgress>? ScanProgressChanged;
    public static event Action<UiLanguage>? LanguageChanged;

    public static void RunOnUi(Action action) => Ui?.TryEnqueue(() => action());

    /// <summary>Raises <see cref="WarningRaised"/> from anywhere (marshals to UI).</summary>
    public static void RaiseWarning(string message) => RunOnUi(() => WarningRaised?.Invoke(message));

    /// <summary>Applies the new language and broadcasts the change so the UI can react.</summary>
    public static void ChangeLanguage(UiLanguage language)
    {
        Settings.Ui.Language = language;
        StringsLoader.ApplyLanguage(language);
        SettingsWriter.Schedule(Settings);
        RunOnUi(() => LanguageChanged?.Invoke(language));
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
            catch (Exception ex) { RunOnUi(() => WarningRaised?.Invoke($"라이브러리 스캔 실패: {ex.Message}")); }
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
