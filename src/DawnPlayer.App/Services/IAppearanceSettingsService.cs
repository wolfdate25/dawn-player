using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>
/// Service contract for managing application appearance settings: theme mode (Light/Dark/System),
/// accent color preset, Windows Fluent backdrop effect, and sidebar/cover layout defaults.
/// </summary>
public interface IAppearanceSettingsService
{
    /// <summary>
    /// Raised whenever any appearance or layout setting is modified and persisted.
    /// </summary>
    event Action? AppearanceChanged;

    /// <summary>
    /// Sets the application theme mode (System, Light, Dark).
    /// </summary>
    void SetTheme(ThemeMode theme);

    /// <summary>
    /// Sets the UI accent color preset.
    /// </summary>
    void SetAccentColor(AccentColorPreset preset);

    /// <summary>
    /// Sets the window backdrop material (Mica, Mica Alt, Acrylic, Solid).
    /// </summary>
    void SetBackdrop(BackdropMode backdrop);

    /// <summary>
    /// Sets the default album cover size in pixels (clamped to [80, 260]).
    /// </summary>
    void SetAlbumCoverSize(double size);

    /// <summary>
    /// Sets a custom user accent color HEX string.
    /// </summary>
    void SetCustomAccentHex(string hex);

    /// <summary>
    /// Sets whether to automatically derive UI accent colors from current playing album art.
    /// </summary>
    void SetAutoAlbumArtAccent(bool enabled);

    /// <summary>
    /// Manually triggers the AppearanceChanged notification.
    /// </summary>
    void RefreshAppearance();

    /// <summary>
    /// Resets sidebar panel widths and album cover size to factory default layout values.
    /// </summary>
    void ResetLayoutToDefaults();
}
