using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// PT1-08 (2026-09-30 audit): the dark/light palettes are declared in three places that must
/// never drift — the theme dictionaries in DawnTheme.xaml, the in-place palette appliers in
/// ThemeService (ApplyStandardDark/Light/Oled), and the root fallback brushes in DawnTheme.xaml.
/// A one-sided edit used to pass every gate while the runtime palette and the static
/// declaration diverged. This gate parses both sources and asserts the hexes agree.
/// </summary>
public class PaletteConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repository root not found");
    }

    private static readonly string[] PaletteKeys =
    {
        "LayerBgColor", "PanelColor", "PanelSubtleColor", "CardColor", "CardHoverColor",
        "CardPressedColor", "ControlBgColor", "HoverColor", "SeparatorColor",
        "SeparatorSubtleColor", "BorderSubtleColor", "TextPrimaryColor", "TextSecondaryColor",
        "TextTertiaryColor", "BadgeBgColor", "BadgeTextColor",
    };

    private static Dictionary<string, string> ThemeDictionaryColors(string xaml, string themeKey)
    {
        // Pull each palette Color declaration from inside the requested ThemeDictionary block.
        var dictStart = xaml.IndexOf($"x:Key=\"{themeKey}\"", StringComparison.Ordinal);
        Assert.True(dictStart >= 0, $"theme dictionary '{themeKey}' not found");
        var dictEnd = xaml.IndexOf("</ResourceDictionary>", dictStart, StringComparison.Ordinal);
        var block = xaml[dictStart..dictEnd];
        var map = new Dictionary<string, string>();
        foreach (var m in Regex.Matches(block, @"<Color x:Key=""(\w+)"" >(#\w{8})</Color>").Cast<Match>())
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        // The regex above tolerates a space; try the compact form too.
        foreach (var m in Regex.Matches(block, @"<Color x:Key=""(\w+)"" >(#\w{8})</Color>").Cast<Match>())
        {
            map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
        foreach (var m in Regex.Matches(block, @"<Color\s+x:Key=""(\w+)""\s*>(#\w{8})</Color>").Cast<Match>())
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return map;
    }

    private static Dictionary<string, string> ServicePalette(string themeSource, string method)
    {
        var start = themeSource.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, $"ThemeService.{method} not found");
        var next = themeSource.IndexOf("private static void", start + 10, StringComparison.Ordinal);
        var body = next >= 0 ? themeSource[start..next] : themeSource[start..];
        var map = new Dictionary<string, string>();
        foreach (var m in Regex.Matches(body, @"SetResourceColor\(""(\w+)"", ""(#\w{8})""\);").Cast<Match>())
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return map;
    }

    private static Dictionary<string, string> RootFallbackBrushes(string xaml)
    {
        // Brushes declared directly under the root dictionary (after the theme dictionaries).
        var map = new Dictionary<string, string>();
        foreach (var m in Regex.Matches(xaml, @"<SolidColorBrush x:Key=""(\w+Brush)"" Color=""(#\w{8})"" />").Cast<Match>())
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return map;
    }

    [Fact]
    public void DarkPalette_ThemeDictionary_Matches_ThemeService()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "DawnTheme.xaml"));
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "Services", "ThemeService.cs"));

        var xamlPalette = ThemeDictionaryColors(xaml, "Default");
        var servicePalette = ServicePalette(service, "private static void ApplyStandardDarkPalette()");

        foreach (var key in PaletteKeys)
        {
            Assert.True(xamlPalette.TryGetValue(key, out var xamlHex), $"DawnTheme Default missing '{key}'");
            Assert.True(servicePalette.TryGetValue(key, out var serviceHex), $"ApplyStandardDarkPalette missing '{key}'");
            Assert.True(string.Equals(xamlHex, serviceHex, StringComparison.OrdinalIgnoreCase),
                $"Dark palette drift for '{key}': XAML={xamlHex}, ThemeService={serviceHex}");
        }
    }

    [Fact]
    public void LightPalette_ThemeDictionary_Matches_ThemeService()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "DawnTheme.xaml"));
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "Services", "ThemeService.cs"));

        var xamlPalette = ThemeDictionaryColors(xaml, "Light");
        var servicePalette = ServicePalette(service, "private static void ApplyStandardLightPalette()");

        foreach (var key in PaletteKeys)
        {
            Assert.True(xamlPalette.TryGetValue(key, out var xamlHex), $"DawnTheme Light missing '{key}'");
            Assert.True(servicePalette.TryGetValue(key, out var serviceHex), $"ApplyStandardLightPalette missing '{key}'");
            Assert.True(string.Equals(xamlHex, serviceHex, StringComparison.OrdinalIgnoreCase),
                $"Light palette drift for '{key}': XAML={xamlHex}, ThemeService={serviceHex}");
        }
    }

    [Fact]
    public void RootFallbackBrushes_Match_DarkThemeDictionary()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "DawnTheme.xaml"));
        var dark = ThemeDictionaryColors(xaml, "Default");
        var root = RootFallbackBrushes(xaml);

        foreach (var (brushKey, hex) in root)
        {
            var colorKey = brushKey.Replace("Brush", "Color");
            if (!dark.TryGetValue(colorKey, out var darkHex)) continue; // not a palette color (e.g. OnAccent)
            Assert.True(string.Equals(hex, darkHex, StringComparison.OrdinalIgnoreCase),
                $"Root fallback '{brushKey}'={hex} drifted from dark '{colorKey}'={darkHex}");
        }
    }
}
