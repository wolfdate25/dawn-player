using System;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing album artwork grid tile size
/// and sidebar width restoration to factory defaults.
/// </summary>
public sealed class LayoutSettingsViewModel : ViewModelBase
{
    private readonly IAppearanceSettingsService _appearanceSettingsService;
    private readonly AppSettings _settings;
    private readonly Action<UiLanguage>? _languageChanger;

    public LayoutSettingsViewModel(
        IAppearanceSettingsService appearanceSettingsService,
        AppSettings settings,
        Action<UiLanguage>? languageChanger = null)
    {
        _appearanceSettingsService = appearanceSettingsService ?? throw new ArgumentNullException(nameof(appearanceSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _languageChanger = languageChanger;
    }

    public double AlbumCoverSize
    {
        get => _settings.Ui.AlbumCoverSize > 0 ? _settings.Ui.AlbumCoverSize : 144.0;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 0), 80.0, 260.0);
            if (Math.Abs(_settings.Ui.AlbumCoverSize - clamped) > 0.1)
            {
                _appearanceSettingsService.SetAlbumCoverSize(clamped);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlbumCoverSizeText));
            }
        }
    }

    public string AlbumCoverSizeText => $"{(int)AlbumCoverSize}px";

    /// <summary>Selected UI language, by enum index in the combo box.</summary>
    public int LanguageIndex
    {
        get => (int)_settings.Ui.Language;
        set
        {
            if (value < 0 || value > 4) return;
            var newLanguage = (UiLanguage)value;
            if (_settings.Ui.Language == newLanguage) return;
            _settings.Ui.Language = newLanguage;
            // The page wires AppServices.ChangeLanguage at construction; tests pass null and
            // can verify the persisted setting without dragging the composition root in.
            _languageChanger?.Invoke(newLanguage);
            OnPropertyChanged();
        }
    }

    public void ResetLayoutToDefaults()
    {
        _appearanceSettingsService.ResetLayoutToDefaults();
        OnPropertyChanged(nameof(AlbumCoverSize));
        OnPropertyChanged(nameof(AlbumCoverSizeText));
    }
}
