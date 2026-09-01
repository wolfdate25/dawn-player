using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DawnPlayer.App.Localization;

/// <summary>
/// One-stop access to localized UI strings via the Windows App SDK / MRT resource pipeline.
/// XAML uses <c>x:Uid="SomeKey"</c> for the framework to inject the right .resw entry into
/// matching properties at runtime; C# code calls <see cref="Get"/> with the same key name.
///
/// Key convention: <c>&lt;Element&gt;.&lt;Property&gt;</c> for x:Uid-driven bindings (the
/// framework replaces <c>.</c> with a path separator internally) or a plain identifier
/// for free-form strings looked up from code.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>Raised after a successful <see cref="ApplyLanguage"/> call.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Current applied BCP-47 language tag, or null to follow the system language.</summary>
    public static string? CurrentLanguage { get; private set; }

    /// <summary>
    /// Sets <see cref="Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride"/>
    /// so subsequent ResourceLoader lookups honor the user's choice. UI already-bound
    /// strings (via <c>x:Uid</c>) need a window reload to re-resolve.
    /// </summary>
    public static void ApplyLanguage(string? bcp47)
    {
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = bcp47 ?? string.Empty;
        CurrentLanguage = string.IsNullOrEmpty(bcp47) ? null : bcp47;
        LanguageChanged?.Invoke();
    }

    /// <summary>Localized value for <paramref name="key"/>, or <paramref name="fallback"/> when missing.</summary>
    public static string Get(string key, string fallback = "")
    {
        if (string.IsNullOrEmpty(key)) return fallback;
        try
        {
            var value = Loader.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Resolves a composite format string from resources and substitutes placeholders.
    /// Placeholder order is left to the caller (matches .resw value's {0} {1} ...).
    /// </summary>
    public static string Format(string key, params object[] args)
    {
        var format = Get(key, key);
        try
        {
            // CurrentUICulture is the right choice for the user's UI language: the resource
            // set was selected with that culture, so the placeholders were authored in it.
#pragma warning disable CA1305
            return string.Format(CultureInfo.CurrentUICulture, format, args);
#pragma warning restore CA1305
        }
        catch
        {
            return format;
        }
    }

    /// <summary>Plural-form helper. Reads <c>{baseKey}_{suffix}</c> where suffix is one of
    /// <c>Zero</c>, <c>One</c>, <c>Other</c>; falls back to <c>{baseKey}_Other</c> when the
    /// specific key is missing (e.g. only Other is defined for the language).</summary>
    public static string GetPlural(string baseKey, int count, params object[] args)
    {
        string suffix = count switch
        {
            0 => "Zero",
            1 => "One",
            _ => "Other"
        };

        var value = Get($"{baseKey}_{suffix}");
        if (string.IsNullOrEmpty(value)) value = Get($"{baseKey}_Other");
        if (string.IsNullOrEmpty(value)) return baseKey;

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
}
