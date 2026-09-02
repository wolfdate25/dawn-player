using System;
using System.Text.RegularExpressions;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing application visual theme, backdrop materials,
/// dynamic album art accenting, accent color presets, and custom hex color configuration.
/// </summary>
public sealed class AppearanceSettingsViewModel : ViewModelBase
{
    private static readonly Regex HexColorRegex = new(
        @"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAppearanceSettingsService _appearanceSettingsService;
    private readonly AppSettings _settings;
    private readonly Action<UiLanguage>? _onLanguageChanged;

    public AppearanceSettingsViewModel(
        IAppearanceSettingsService appearanceSettingsService,
        AppSettings settings,
        Action<UiLanguage>? onLanguageChanged = null)
    {
        _appearanceSettingsService = appearanceSettingsService ?? throw new ArgumentNullException(nameof(appearanceSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _onLanguageChanged = onLanguageChanged;
    }

    public UiLanguage Language
    {
        get => _settings.Ui.Language;
        set
        {
            if (_settings.Ui.Language != value)
            {
                _settings.Ui.Language = value;
                _onLanguageChanged?.Invoke(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(LanguageIndex));
            }
        }
    }

    public int LanguageIndex
    {
        get => _settings.Ui.Language switch
        {
            UiLanguage.KoKR => 1,
            UiLanguage.EnUS => 2,
            UiLanguage.JaJP => 3,
            _ => 0
        };
        set
        {
            var language = value switch
            {
                1 => UiLanguage.KoKR,
                2 => UiLanguage.EnUS,
                3 => UiLanguage.JaJP,
                _ => UiLanguage.System
            };
            if (_settings.Ui.Language != language)
            {
                Language = language;
            }
        }
    }

    public ThemeMode Theme
    {
        get => _settings.Ui.Theme;
        set
        {
            if (_settings.Ui.Theme != value)
            {
                _appearanceSettingsService.SetTheme(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeIndex));
            }
        }
    }

    public int ThemeIndex
    {
        get => _settings.Ui.Theme switch
        {
            ThemeMode.System => 0,
            ThemeMode.Light => 1,
            ThemeMode.OledBlack => 3,
            _ => 2
        };
        set
        {
            var theme = value switch
            {
                0 => ThemeMode.System,
                1 => ThemeMode.Light,
                3 => ThemeMode.OledBlack,
                _ => ThemeMode.Dark
            };
            if (_settings.Ui.Theme != theme)
            {
                Theme = theme;
            }
        }
    }

    public BackdropMode Backdrop
    {
        get => _settings.Ui.Backdrop;
        set
        {
            if (_settings.Ui.Backdrop != value)
            {
                _appearanceSettingsService.SetBackdrop(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BackdropIndex));
            }
        }
    }

    public int BackdropIndex
    {
        get => _settings.Ui.Backdrop switch
        {
            BackdropMode.MicaAlt => 1,
            BackdropMode.Acrylic => 2,
            BackdropMode.Solid => 3,
            BackdropMode.AlbumArtBlur => 4,
            _ => 0
        };
        set
        {
            var backdrop = value switch
            {
                1 => BackdropMode.MicaAlt,
                2 => BackdropMode.Acrylic,
                3 => BackdropMode.Solid,
                4 => BackdropMode.AlbumArtBlur,
                _ => BackdropMode.Mica
            };
            if (_settings.Ui.Backdrop != backdrop)
            {
                Backdrop = backdrop;
            }
        }
    }

    public bool AutoAlbumArtAccent
    {
        get => _settings.Ui.AutoAlbumArtAccent;
        set
        {
            if (_settings.Ui.AutoAlbumArtAccent != value)
            {
                _appearanceSettingsService.SetAutoAlbumArtAccent(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Close button hides to the tray instead of exiting (the tray menu exits for real).</summary>
    public bool CloseToTray
    {
        get => _settings.Ui.CloseToTray;
        set
        {
            if (_settings.Ui.CloseToTray != value)
            {
                _appearanceSettingsService.SetCloseToTray(value);
                OnPropertyChanged();
            }
        }
    }

    public AccentColorPreset AccentPreset
    {
        get => _settings.Ui.AccentColor;
        set
        {
            if (_settings.Ui.AccentColor != value)
            {
                _appearanceSettingsService.SetAccentColor(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccentIndex));
                OnPropertyChanged(nameof(IsCustomColorVisible));
            }
        }
    }

    public int AccentIndex
    {
        get => _settings.Ui.AccentColor switch
        {
            AccentColorPreset.ElectricGold => 1,
            AccentColorPreset.ForestEmerald => 2,
            AccentColorPreset.CyanSapphire => 3,
            AccentColorPreset.CrimsonRed => 4,
            AccentColorPreset.ModernSlate => 5,
            AccentColorPreset.NordFrost => 6,
            AccentColorPreset.TokyoNight => 7,
            AccentColorPreset.CatppuccinMocha => 8,
            AccentColorPreset.RosePine => 9,
            AccentColorPreset.SunsetViolet => 10,
            AccentColorPreset.Custom => 11,
            _ => 0
        };
        set
        {
            var preset = value switch
            {
                1 => AccentColorPreset.ElectricGold,
                2 => AccentColorPreset.ForestEmerald,
                3 => AccentColorPreset.CyanSapphire,
                4 => AccentColorPreset.CrimsonRed,
                5 => AccentColorPreset.ModernSlate,
                6 => AccentColorPreset.NordFrost,
                7 => AccentColorPreset.TokyoNight,
                8 => AccentColorPreset.CatppuccinMocha,
                9 => AccentColorPreset.RosePine,
                10 => AccentColorPreset.SunsetViolet,
                11 => AccentColorPreset.Custom,
                _ => AccentColorPreset.EoleAmber
            };
            if (_settings.Ui.AccentColor != preset)
            {
                AccentPreset = preset;
            }
        }
    }

    public bool IsCustomColorVisible => AccentPreset == AccentColorPreset.Custom;

    public string CustomAccentHex
    {
        get => string.IsNullOrWhiteSpace(_settings.Ui.CustomAccentHex) ? "#FFE8A33D" : _settings.Ui.CustomAccentHex;
        set
        {
            TrySetCustomAccentHex(value);
        }
    }

    public bool TrySetCustomAccentHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;

        string trimmed = hex.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = "#" + trimmed;
        }

        if (!HexColorRegex.IsMatch(trimmed))
        {
            return false;
        }

        if (_settings.Ui.CustomAccentHex != trimmed)
        {
            _appearanceSettingsService.SetCustomAccentHex(trimmed);
            OnPropertyChanged(nameof(CustomAccentHex));
        }

        return true;
    }
}
