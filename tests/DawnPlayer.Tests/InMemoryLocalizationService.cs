using DawnPlayer.App.Localization;

namespace DawnPlayer.Tests;

/// <summary>
/// In-memory localization service for pure unit tests without MRT Core / WinRT runtime dependencies.
/// </summary>
public sealed class InMemoryLocalizationService : LocalizationServiceBase
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);

    public void SetString(string culture, string key, string value)
    {
        if (!_tables.TryGetValue(culture, out var dict))
        {
            dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _tables[culture] = dict;
        }

        dict[key] = value;
    }

    protected override string? GetExact(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        var culture = CurrentLanguage ?? "en-US";
        if (_tables.TryGetValue(culture, out var dict) && dict.TryGetValue(key, out var val))
        {
            return val;
        }

        return null;
    }

    public override string Get(string key, string fallback = "")
    {
        if (string.IsNullOrEmpty(key)) return fallback;

        var exact = GetExact(key);
        if (!string.IsNullOrEmpty(exact))
        {
            return exact;
        }

        var culture = CurrentLanguage ?? "en-US";
        // Fallback to en-US if available
        if (culture != "en-US" && _tables.TryGetValue("en-US", out var defaultDict) && defaultDict.TryGetValue(key, out var defVal))
        {
            return defVal;
        }

        return fallback;
    }
}
