using System;
using DawnPlayer.App.Services;
using DawnPlayer.App.Shortcuts;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Master coordinator ViewModel for the Preferences (Settings) page.
/// Owns and aggregates all sub-panel ViewModels with zero direct UI or WinUI XAML couplings.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly IAudioSettingsService _audioSettingsService;
    private readonly IEqSettingsService _eqSettingsService;
    private readonly IAppearanceSettingsService _appearanceSettingsService;

    private int _selectedCategoryIndex;

    public AudioSettingsViewModel Audio { get; }
    public EqualizerSettingsViewModel Equalizer { get; }
    public PlaybackSettingsViewModel Playback { get; }
    public LibrarySettingsViewModel Library { get; }
    public LyricsSettingsViewModel Lyrics { get; }
    public LyricsOnlineSettingsViewModel OnlineLyrics { get; }
    public AppearanceSettingsViewModel Appearance { get; }
    public LayoutSettingsViewModel Layout { get; }
    public ShortcutSettingsViewModel Shortcuts { get; }

    public SettingsViewModel(
        AppSettings settings,
        IAudioSettingsService audioSettingsService,
        IEqSettingsService eqSettingsService,
        IAppearanceSettingsService appearanceSettingsService,
        Action? scanStarter = null,
        Action? lyricsChangedNotifier = null,
        Action<AppSettings>? settingsSaver = null,
        Func<bool>? isExclusiveSessionGetter = null,
        IShortcutBindingStore? shortcutStore = null,
        Action<string>? logger = null,
        ILyricsOnlineService? lyricsOnlineService = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audioSettingsService = audioSettingsService ?? throw new ArgumentNullException(nameof(audioSettingsService));
        _eqSettingsService = eqSettingsService ?? throw new ArgumentNullException(nameof(eqSettingsService));
        _appearanceSettingsService = appearanceSettingsService ?? throw new ArgumentNullException(nameof(appearanceSettingsService));

        Equalizer = new EqualizerSettingsViewModel(
            _eqSettingsService,
            _audioSettingsService,
            _settings,
            isExclusiveSessionGetter);

        Audio = new AudioSettingsViewModel(
            _audioSettingsService,
            _settings,
            onDriverOrDeviceChanged: () =>
            {
                Equalizer.RefreshDevicesAndBindings(_settings.Output.DeviceId);
            });

        Playback = new PlaybackSettingsViewModel(_audioSettingsService, _settings);

        Library = new LibrarySettingsViewModel(_settings, scanStarter, settingsSaver);

        Lyrics = new LyricsSettingsViewModel(_settings, lyricsChangedNotifier, settingsSaver);

        OnlineLyrics = new LyricsOnlineSettingsViewModel(_settings, lyricsOnlineService, lyricsChangedNotifier, settingsSaver);

        Appearance = new AppearanceSettingsViewModel(_appearanceSettingsService, _settings);

        Layout = new LayoutSettingsViewModel(_appearanceSettingsService, _settings);

        // Falls back to a detached in-memory store so the section still renders (and tests can
        // construct this ViewModel) when no shortcut service was supplied. In the app that
        // fallback means edits silently persist nowhere, so make it loud in the log.
        if (shortcutStore == null)
            logger?.Invoke("[SettingsViewModel] No IShortcutBindingStore supplied — shortcut section is using a detached in-memory store; changes will not persist.");
        Shortcuts = new ShortcutSettingsViewModel(shortcutStore ?? new ShortcutMapStore());
    }

    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set
        {
            if (SetProperty(ref _selectedCategoryIndex, value))
            {
                OnPropertyChanged(nameof(IsAudioCategorySelected));
                OnPropertyChanged(nameof(IsEqualizerCategorySelected));
                OnPropertyChanged(nameof(IsPlaybackCategorySelected));
                OnPropertyChanged(nameof(IsLibraryCategorySelected));
                OnPropertyChanged(nameof(IsLyricsCategorySelected));
                OnPropertyChanged(nameof(IsOnlineLyricsCategorySelected));
                OnPropertyChanged(nameof(IsAppearanceCategorySelected));
                OnPropertyChanged(nameof(IsLayoutCategorySelected));
                OnPropertyChanged(nameof(IsShortcutsCategorySelected));
                OnPropertyChanged(nameof(IsAboutCategorySelected));

                if (value == 1)
                {
                    Equalizer.RecalculateVisualizer();
                }
                else if (value == 5)
                {
                    OnlineLyrics.RefreshPlugins();
                }
            }
        }
    }

    public bool IsAudioCategorySelected => _selectedCategoryIndex == 0;
    public bool IsEqualizerCategorySelected => _selectedCategoryIndex == 1;
    public bool IsPlaybackCategorySelected => _selectedCategoryIndex == 2;
    public bool IsLibraryCategorySelected => _selectedCategoryIndex == 3;
    public bool IsLyricsCategorySelected => _selectedCategoryIndex == 4;
    public bool IsOnlineLyricsCategorySelected => _selectedCategoryIndex == 5;
    public bool IsAppearanceCategorySelected => _selectedCategoryIndex == 6;
    public bool IsLayoutCategorySelected => _selectedCategoryIndex == 7;
    public bool IsShortcutsCategorySelected => _selectedCategoryIndex == 8;
    public bool IsAboutCategorySelected => _selectedCategoryIndex == 9;

    public void HandleSessionChanged(SessionInfo info)
    {
        Equalizer.SetExclusiveSessionState(info.Exclusive);
    }

    public void RefreshAll()
    {
        Audio.RefreshDevices(_settings.Output.DeviceId);
        Equalizer.RefreshProfiles();
        Equalizer.RefreshDevicesAndBindings(_settings.Output.DeviceId);
        OnlineLyrics.RefreshPlugins();
    }
}
