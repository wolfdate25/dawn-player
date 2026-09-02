namespace DawnPlayer.App.Localization;

/// <summary>
/// Static facade for localized UI strings. Delegates all calls to the underlying <see cref="ILocalizationService"/>.
/// XAML uses <c>x:Uid="SomeKey"</c> for the framework to inject the right .resw entry into
/// matching properties at runtime; C# code calls <see cref="Get"/> with the same key name.
/// </summary>
public static class AppStrings
{
    // Null until the composition root installs the real service: unit tests compile this file
    // without WinAppSDK, so the default must not touch ResourceLoader. Lookups before
    // initialization return the caller's fallback, which is always the shipped ko-KR string.
    private static ILocalizationService _instance = LocalizationServiceBase.Null;

    /// <summary>
    /// The active <see cref="ILocalizationService"/> instance. Can be replaced in tests.
    /// </summary>
    public static ILocalizationService Instance
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Raised after a successful <see cref="ApplyLanguage"/> call.</summary>
    public static event Action? LanguageChanged
    {
        add => _instance.LanguageChanged += value;
        remove => _instance.LanguageChanged -= value;
    }

    /// <summary>Current applied BCP-47 language tag, or null to follow the system language.</summary>
    public static string? CurrentLanguage => _instance.CurrentLanguage;

    /// <summary>
    /// Sets language overrides for both Packaged (PrimaryLanguageOverride) and Unpackaged
    /// (ResourceManager + ResourceContext + Thread Culture) environments.
    /// </summary>
    public static void ApplyLanguage(string? bcp47) => _instance.ApplyLanguage(bcp47);

    /// <summary>Localized value for <paramref name="key"/>, or <paramref name="fallback"/> when missing.</summary>
    public static string Get(string key, string fallback = "") => _instance.Get(key, fallback);

    /// <summary>Alias for <see cref="Get"/> for dynamic runtime string lookups.</summary>
    public static string GetString(string key, string fallback = "") => _instance.Get(key, fallback);

    /// <summary>
    /// Resolves a composite format string from resources and substitutes placeholders.
    /// Placeholder order is left to the caller (matches .resw value's {0} {1} ...).
    /// </summary>
    public static string Format(string key, params object[] args) => _instance.Format(key, args);

    /// <summary>
    /// Resolves a composite format string from resources (or uses <paramref name="fallbackFormat"/> when missing) and substitutes placeholders.
    /// </summary>
    public static string Format(string key, string fallbackFormat, params object[] args) => _instance.Format(key, fallbackFormat, args);

    /// <summary>Plural-form helper. Reads <c>{baseKey}_{suffix}</c> where suffix is one of
    /// <c>Zero</c>, <c>One</c>, <c>Other</c>; falls back to <c>{baseKey}_Other</c> when the
    /// specific key is missing (e.g. only Other is defined for the language).</summary>
    public static string GetPlural(string baseKey, int count, params object[] args) => _instance.GetPlural(baseKey, count, args);
}
