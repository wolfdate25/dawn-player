using System.Globalization;

namespace DawnPlayer.App.Localization;

/// <summary>
/// Platform-independent base class for localization services. Handles culture synchronization,
/// string formatting, pluralization lookups, and language change event dispatching.
/// </summary>
public abstract class LocalizationServiceBase : ILocalizationService
{
    /// <summary>A no-op fallback implementation that returns the key or fallback string.</summary>
    public static readonly ILocalizationService Null = new NullLocalizationService();

    private string? _currentLanguage;

    public event Action? LanguageChanged;

    public string? CurrentLanguage => _currentLanguage;

    public virtual void ApplyLanguage(string? bcp47)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(bcp47))
            {
                var culture = CultureInfo.GetCultureInfo(bcp47);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            else
            {
                CultureInfo.CurrentUICulture = CultureInfo.InstalledUICulture;
                CultureInfo.DefaultThreadCurrentUICulture = null;
            }
        }
        catch
        {
            // Culture fallback
        }

        _currentLanguage = string.IsNullOrWhiteSpace(bcp47) ? null : bcp47;
        OnLanguageChanged();
    }

    protected virtual void OnLanguageChanged()
    {
        LanguageChanged?.Invoke();
    }

    public abstract string Get(string key, string fallback = "");

    /// <summary>
    /// Looks up a resource strictly within the active culture before falling back across languages.
    /// </summary>
    protected virtual string? GetExact(string key) => Get(key);

    public virtual string Format(string key, params object[] args)
    {
        return Format(key, key, args);
    }

    public virtual string Format(string key, string fallbackFormat, params object[] args)
    {
        var format = Get(key, fallbackFormat);
        if (string.IsNullOrEmpty(format))
        {
            format = fallbackFormat;
        }

        try
        {
#pragma warning disable CA1305
            return string.Format(CultureInfo.CurrentUICulture, format, args);
#pragma warning restore CA1305
        }
        catch
        {
            return format;
        }
    }

    public virtual string GetPlural(string baseKey, int count, params object[] args)
    {
        string? value = null;
        if (count == 0)
        {
            value = GetExact($"{baseKey}_Zero");
        }
        else if (count == 1)
        {
            value = GetExact($"{baseKey}_One");
        }

        if (string.IsNullOrEmpty(value))
        {
            value = GetExact($"{baseKey}_Other");
        }

        if (string.IsNullOrEmpty(value))
        {
            string suffix = count switch
            {
                0 => "Zero",
                1 => "One",
                _ => "Other"
            };
            value = Get($"{baseKey}_{suffix}");
            if (string.IsNullOrEmpty(value))
            {
                value = Get($"{baseKey}_Other", baseKey);
            }
        }

        try
        {
#pragma warning disable CA1305
            return string.Format(CultureInfo.CurrentUICulture, value, args);
#pragma warning restore CA1305
        }
        catch
        {
            return value;
        }
    }

    private sealed class NullLocalizationService : LocalizationServiceBase
    {
        public override string Get(string key, string fallback = "") => string.IsNullOrEmpty(fallback) ? key : fallback;
    }
}
