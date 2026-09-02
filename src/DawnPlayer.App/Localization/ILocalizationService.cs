namespace DawnPlayer.App.Localization;

/// <summary>
/// Service contract for application localization, culture management, dynamic string formatting,
/// and pluralization lookups.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Raised after a successful <see cref="ApplyLanguage"/> call.</summary>
    event Action? LanguageChanged;

    /// <summary>Current applied BCP-47 language tag, or null to follow the system language.</summary>
    string? CurrentLanguage { get; }

    /// <summary>
    /// Applies the specified BCP-47 language tag (e.g. "ko-KR", "en-US", "ja-JP") or null/empty
    /// to follow the system default language.
    /// </summary>
    void ApplyLanguage(string? bcp47);

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>, or <paramref name="fallback"/> if not found.
    /// </summary>
    string Get(string key, string fallback = "");

    /// <summary>
    /// Resolves a composite format string by key and formats it using the active UI culture.
    /// </summary>
    string Format(string key, params object[] args);

    /// <summary>
    /// Resolves a composite format string by key (or uses <paramref name="fallbackFormat"/> if not found) and formats it using the active UI culture.
    /// </summary>
    string Format(string key, string fallbackFormat, params object[] args);

    /// <summary>
    /// Resolves a pluralized string using the <c>{baseKey}_{suffix}</c> naming convention
    /// (<c>_Zero</c>, <c>_One</c>, <c>_Other</c>) and substitutes formatted arguments.
    /// </summary>
    string GetPlural(string baseKey, int count, params object[] args);
}
