using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace DawnPlayer.App.Localization;

/// <summary>
/// Windows App SDK / MRT Core resource pipeline implementation of <see cref="ILocalizationService"/>.
/// Language selection goes through <see cref="ApplicationLanguages.PrimaryLanguageOverride"/>
/// (WinAppSDK projection, works for both packaged and unpackaged apps), which drives both
/// <see cref="ResourceLoader"/> lookups and XAML <c>x:Uid</c> resolution. The override is not
/// persisted for unpackaged apps, so the composition root must re-apply it on every launch
/// before any resource is loaded.
/// </summary>
public sealed class MrtLocalizationService : LocalizationServiceBase
{
    private readonly ResourceLoader _loader;

    public MrtLocalizationService()
    {
        _loader = new ResourceLoader();
    }

    public override void ApplyLanguage(string? bcp47)
    {
        // Process-wide language override. Empty string clears it (follow the system language).
        // Docs: set during app load before any resource is loaded; later changes only affect
        // resources loaded afterwards, so a runtime switch still needs a UI reload/restart.
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = string.IsNullOrWhiteSpace(bcp47) ? string.Empty : bcp47;
        }
        catch
        {
            // Pre-1.6 WinAppSDK runtimes throw without package identity; the thread-culture
            // sync in the base class still keeps AppStrings lookups usable.
        }

        base.ApplyLanguage(bcp47);
    }

    protected override string? GetExact(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        try
        {
            var value = _loader.GetString(key);
            // GetString echoes the key (or returns empty) when the candidate is missing.
            if (string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal))
            {
                return null;
            }

            return value;
        }
        catch
        {
            return null;
        }
    }

    public override string Get(string key, string fallback = "")
    {
        if (string.IsNullOrEmpty(key)) return fallback;

        var exact = GetExact(key);
        if (!string.IsNullOrEmpty(exact))
        {
            return exact;
        }

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }
}
