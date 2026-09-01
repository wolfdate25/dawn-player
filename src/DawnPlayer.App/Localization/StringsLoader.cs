using System.Globalization;
using System.Resources;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Localization;

/// <summary>
/// One-stop access to localized UI strings. ResourceManager finds the right satellite
/// assembly for the current <see cref="CultureInfo.CurrentUICulture"/>, so each shipped
/// <c>Strings.&lt;culture&gt;.resx</c> is its own satellite and the lookup is free at runtime.
/// </summary>
/// <remarks>
/// Keys follow the pattern Area_Element. For example <see cref="App_ProductTitle"/>.
/// On Windows the loader follows the OS display language when the user picked "System";
/// the user's choice wins on every call to <see cref="ApplyLanguage"/>.
/// </remarks>
public static class StringsLoader
{
    private static readonly ResourceManager Resources = new(
        "DawnPlayer.App.Localization.Strings",
        typeof(StringsLoader).Assembly);

    private static CultureInfo? _activeCulture;

    /// <summary>Raised on the caller's thread when <see cref="ApplyLanguage"/> swaps cultures.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Applies the user's setting; call at startup and whenever it changes.</summary>
    public static void ApplyLanguage(UiLanguage language)
    {
        var culture = language switch
        {
            UiLanguage.KoKR => new CultureInfo("ko-KR"),
            UiLanguage.EnUS => new CultureInfo("en-US"),
            UiLanguage.JaJP => new CultureInfo("ja-JP"),
            UiLanguage.ZhCN => new CultureInfo("zh-CN"),
            _ => CultureInfo.CurrentUICulture
        };

        // CurrentCulture affects formatting (numbers, dates) and is what ResourceManager
        // falls back through for neutral-culture lookups. ThreadPool follows the current
        // culture on dispatched work so the change reaches the rest of the app.
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        _activeCulture = culture;

        LanguageChanged?.Invoke();
    }

    public static CultureInfo? CurrentCulture => _activeCulture;

    /// <summary>Localized string for <paramref name="name"/>, or <paramref name="fallback"/> when missing.</summary>
    public static string Get(string name, string fallback = "")
    {
        try
        {
            var value = Resources.GetString(name, CultureInfo.CurrentUICulture);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }
}
