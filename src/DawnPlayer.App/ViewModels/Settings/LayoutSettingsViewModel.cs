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

    public LayoutSettingsViewModel(
        IAppearanceSettingsService appearanceSettingsService,
        AppSettings settings)
    {
        _appearanceSettingsService = appearanceSettingsService ?? throw new ArgumentNullException(nameof(appearanceSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

    public void ResetLayoutToDefaults()
    {
        _appearanceSettingsService.ResetLayoutToDefaults();
        OnPropertyChanged(nameof(AlbumCoverSize));
        OnPropertyChanged(nameof(AlbumCoverSizeText));
    }
}
