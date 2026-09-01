using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing lyrics typography, live preview parameters, focus effects, and .lrc search patterns.
/// </summary>
public sealed class LyricsSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action? _lyricsChangedNotifier;
    private readonly Action<AppSettings>? _settingsSaver;

    private int _fontFamilyIndex;
    private string _customFontFamily = "";
    private string _lrcPatternsText = "";

    public LyricsSettingsViewModel(
        AppSettings settings,
        Action? lyricsChangedNotifier = null,
        Action<AppSettings>? settingsSaver = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lyricsChangedNotifier = lyricsChangedNotifier;
        _settingsSaver = settingsSaver ?? (s => SettingsWriter.Schedule(s));

        InitializeFontState();
        _lrcPatternsText = string.Join(Environment.NewLine, _settings.Lyrics.FilePatterns);
    }

    private void InitializeFontState()
    {
        var fam = _settings.Lyrics.FontFamily;
        if (fam.Contains("Segoe UI Variable", StringComparison.OrdinalIgnoreCase))
        {
            _fontFamilyIndex = 0;
            _customFontFamily = "";
        }
        else if (fam.Contains("Pretendard", StringComparison.OrdinalIgnoreCase))
        {
            _fontFamilyIndex = 1;
            _customFontFamily = "";
        }
        else if (fam.Contains("Malgun Gothic", StringComparison.OrdinalIgnoreCase) || fam.Contains("맑은 고딕", StringComparison.OrdinalIgnoreCase))
        {
            _fontFamilyIndex = 2;
            _customFontFamily = "";
        }
        else if (fam.Contains("NanumGothic", StringComparison.OrdinalIgnoreCase) || fam.Contains("나눔고딕", StringComparison.OrdinalIgnoreCase))
        {
            _fontFamilyIndex = 3;
            _customFontFamily = "";
        }
        else
        {
            _fontFamilyIndex = 4;
            _customFontFamily = fam;
        }
    }

    public int FontFamilyIndex
    {
        get => _fontFamilyIndex;
        set
        {
            if (SetProperty(ref _fontFamilyIndex, value))
            {
                OnPropertyChanged(nameof(IsCustomFontVisible));
                ApplyFontFamily();
            }
        }
    }

    public string CustomFontFamily
    {
        get => _customFontFamily;
        set
        {
            if (SetProperty(ref _customFontFamily, value) && _fontFamilyIndex == 4)
            {
                ApplyFontFamily();
            }
        }
    }

    public bool IsCustomFontVisible => _fontFamilyIndex == 4;

    public string EffectiveFontFamily => _settings.Lyrics.FontFamily;

    private void ApplyFontFamily()
    {
        if (_fontFamilyIndex == 4)
        {
            if (!string.IsNullOrWhiteSpace(_customFontFamily))
            {
                _settings.Lyrics.FontFamily = _customFontFamily.Trim();
            }
        }
        else
        {
            _settings.Lyrics.FontFamily = _fontFamilyIndex switch
            {
                1 => "Pretendard, Segoe UI Variable, Malgun Gothic",
                2 => "Malgun Gothic, Segoe UI Variable",
                3 => "NanumGothic, Segoe UI Variable, Malgun Gothic",
                _ => "Segoe UI Variable, Malgun Gothic"
            };
        }

        OnPropertyChanged(nameof(EffectiveFontFamily));
        SaveAndNotify();
    }

    public double FontSize
    {
        get => _settings.Lyrics.FontSize;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), 10.0, 24.0);
            if (Math.Abs(_settings.Lyrics.FontSize - clamped) > 0.01)
            {
                _settings.Lyrics.FontSize = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FontSizeLabel));
                SaveAndNotify();
            }
        }
    }

    public string FontSizeLabel => $"{FontSize:0.#}px";

    public double ActiveFontSize
    {
        get => _settings.Lyrics.ActiveFontSize;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), 12.0, 32.0);
            if (Math.Abs(_settings.Lyrics.ActiveFontSize - clamped) > 0.01)
            {
                _settings.Lyrics.ActiveFontSize = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveFontSizeLabel));
                SaveAndNotify();
            }
        }
    }

    public string ActiveFontSizeLabel => $"{ActiveFontSize:0.#}px";

    public int CharacterSpacing
    {
        get => _settings.Lyrics.CharacterSpacing;
        set
        {
            int clamped = Math.Clamp(value, -50, 200);
            if (_settings.Lyrics.CharacterSpacing != clamped)
            {
                _settings.Lyrics.CharacterSpacing = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CharacterSpacingLabel));
                SaveAndNotify();
            }
        }
    }

    public string CharacterSpacingLabel => $"{CharacterSpacing}";

    public double LineHeight
    {
        get => _settings.Lyrics.LineHeight;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 0), 18.0, 48.0);
            if (Math.Abs(_settings.Lyrics.LineHeight - clamped) > 0.01)
            {
                _settings.Lyrics.LineHeight = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LineHeightLabel));
                SaveAndNotify();
            }
        }
    }

    public string LineHeightLabel => $"{(int)LineHeight}px";

    public int AlignmentIndex
    {
        get => _settings.Lyrics.Alignment switch
        {
            "Left" => 1,
            "Right" => 2,
            _ => 0
        };
        set
        {
            string align = value switch
            {
                1 => "Left",
                2 => "Right",
                _ => "Center"
            };

            if (_settings.Lyrics.Alignment != align)
            {
                _settings.Lyrics.Alignment = align;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Alignment));
                SaveAndNotify();
            }
        }
    }

    public string Alignment => _settings.Lyrics.Alignment;

    public bool EnableFocusEffect
    {
        get => _settings.Lyrics.EnableFocusEffect;
        set
        {
            if (_settings.Lyrics.EnableFocusEffect != value)
            {
                _settings.Lyrics.EnableFocusEffect = value;
                OnPropertyChanged();
                SaveAndNotify();
            }
        }
    }

    public bool ReadEmbeddedLyrics
    {
        get => _settings.Lyrics.ReadEmbeddedLyrics;
        set
        {
            if (_settings.Lyrics.ReadEmbeddedLyrics != value)
            {
                _settings.Lyrics.ReadEmbeddedLyrics = value;
                OnPropertyChanged();
                SaveAndNotify();
            }
        }
    }

    public string LrcPatternsText
    {
        get => _lrcPatternsText;
        set => SetProperty(ref _lrcPatternsText, value);
    }

    private static readonly char[] LrcPatternSeparators = { '\r', '\n' };

    public void SaveLrcPatterns(string? rawText = null)
    {
        string text = rawText ?? _lrcPatternsText;
        var patterns = text
            .Split(LrcPatternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (patterns.Count > 0)
        {
            _settings.Lyrics.FilePatterns = patterns;
            _lrcPatternsText = string.Join(Environment.NewLine, patterns);
            OnPropertyChanged(nameof(LrcPatternsText));
            _settingsSaver?.Invoke(_settings);
        }
    }

    public void ResetLrcPatternsToDefault()
    {
        var defaultPatterns = new List<string>
        {
            "%filename%.lrc",
            "%artist% - %title%.lrc",
            "%title%.lrc"
        };
        _settings.Lyrics.FilePatterns = new List<string>(defaultPatterns);
        _lrcPatternsText = string.Join(Environment.NewLine, defaultPatterns);
        OnPropertyChanged(nameof(LrcPatternsText));
        _settingsSaver?.Invoke(_settings);
    }

    private void SaveAndNotify()
    {
        _settingsSaver?.Invoke(_settings);
        _lyricsChangedNotifier?.Invoke();
    }
}
