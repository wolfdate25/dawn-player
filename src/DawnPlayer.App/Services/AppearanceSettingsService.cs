using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>
/// Default implementation of <see cref="IAppearanceSettingsService"/>.
/// Handles mutation and persistence of theme, backdrop, accent color, font scale,
/// and layout dimensions, firing the <see cref="AppearanceChanged"/> event to synchronize the UI.
/// </summary>
public sealed class AppearanceSettingsService : IAppearanceSettingsService
{
    private readonly AppSettings _settings;

    public event Action? AppearanceChanged;

    public AppearanceSettingsService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void SetTheme(ThemeMode theme)
    {
        _settings.Ui.Theme = theme;
        SaveAndNotify();
    }

    public void SetAccentColor(AccentColorPreset preset)
    {
        _settings.Ui.AccentColor = preset;
        SaveAndNotify();
    }

    public void SetBackdrop(BackdropMode backdrop)
    {
        _settings.Ui.Backdrop = backdrop;
        SaveAndNotify();
    }

    public void SetAlbumCoverSize(double size)
    {
        _settings.Ui.AlbumCoverSize = Math.Clamp(size, 80.0, 260.0);
        SaveAndNotify();
    }

    public void SetCustomAccentHex(string hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            _settings.Ui.CustomAccentHex = hex;
            _settings.Ui.AccentColor = AccentColorPreset.Custom;
            SaveAndNotify();
        }
    }

    public void SetAutoAlbumArtAccent(bool enabled)
    {
        _settings.Ui.AutoAlbumArtAccent = enabled;
        SaveAndNotify();
    }

    public void RefreshAppearance()
    {
        AppearanceChanged?.Invoke();
    }

    public void ResetLayoutToDefaults()
    {
        _settings.Ui.LeftSidebarWidth = 220;
        _settings.Ui.RightSidebarWidth = 300;
        _settings.Ui.LyricsSidebarWidth = 300;
        _settings.Ui.AlbumCoverSize = 144;
        SaveAndNotify();
    }

    private void SaveAndNotify()
    {
        SettingsWriter.Schedule(_settings);
        AppearanceChanged?.Invoke();
    }
}
