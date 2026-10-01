using System.Xml.Linq;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Styles;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// U0 design-token gates (implementation_plan.md 개정 4판, U0). The XAML itself cannot be
/// linked into this project, so the tests parse the source dictionaries off the repository —
/// the same approach as the resw parity gate. Gates:
///   * Default/Light token dictionaries define identical key sets.
///   * Required tokens exist; scale tokens match their C# mirrors in DesignTokenValues.
///   * Semantic color pairs meet WCAG contrast on the real Eole surfaces (parsed from
///     DawnTheme.xaml, so palette drift re-checks itself).
///   * Views/Controls XAML introduce no new hardcoded hex colors (baseline 2).
/// </summary>
public class DesignTokenTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string AppDir(DirectoryInfo root, params string[] parts) =>
        Path.Combine(new[] { root.FullName, "src", "DawnPlayer.App" }.Concat(parts).ToArray());

    private static XElement LoadXaml(DirectoryInfo root, params string[] parts) =>
        XElement.Load(AppDir(root, parts));

    private static readonly XName XKey = "{http://schemas.microsoft.com/winfx/2006/xaml}Key";

    private static Dictionary<string, string> DictionaryKeys(XElement root, string themeKey)
    {
        var dict = root.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary"
                        && (string?)e.Attribute(XKey) == themeKey)
            .SelectMany(d => d.Elements())
            .Where(e => e.Attribute(XKey) != null)
            .ToDictionary(e => (string)e.Attribute(XKey)!, innerTextOf);
        return dict;
    }

    private static string innerTextOf(XElement e) => e.Value.Trim();

    private static Dictionary<string, string> RootDoubles(XElement root) =>
        root.Elements()
            .Where(e => e.Name.LocalName == "Double" && e.Attribute(XKey) != null)
            .ToDictionary(e => (string)e.Attribute(XKey)!, e => e.Value.Trim());

    private const string TokensPath = "Styles/DesignTokens.xaml";

    private static (Dictionary<string, string> dark, Dictionary<string, string> light, Dictionary<string, string> scales, XElement root)
        LoadTokens(DirectoryInfo root_dir)
    {
        var el = LoadXaml(root_dir, TokensPath.Split('/'));
        return (DictionaryKeys(el, "Default"), DictionaryKeys(el, "Light"), RootDoubles(el), el);
    }

    private static string DawnThemeHex(DirectoryInfo root, string colorKey, string themeKey)
    {
        var el = LoadXaml(root, "DawnTheme.xaml");
        var dict = el.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(XKey) == themeKey)
            .First();
        var color = dict.Elements()
            .First(e => e.Name.LocalName == "Color" && (string?)e.Attribute(XKey) == colorKey);
        return color.Value.Trim();
    }

    [Fact]
    public void TokenDictionaries_DefineIdenticalKeySets_InDefaultAndLight()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; token gates need a source checkout");

        var tokens = LoadTokens(root!);
        var darkKeys = tokens.dark.Keys.ToHashSet();
        var lightKeys = tokens.light.Keys.ToHashSet();

        var missingInLight = darkKeys.Except(lightKeys).ToList();
        var missingInDark = lightKeys.Except(darkKeys).ToList();
        Assert.True(missingInLight.Count == 0, $"Keys missing in Light: {string.Join(", ", missingInLight)}");
        Assert.True(missingInDark.Count == 0, $"Keys missing in Default: {string.Join(", ", missingInDark)}");
    }

    [Fact]
    public void RequiredSemanticTokens_Exist_InBothThemes()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var tokens = LoadTokens(root!);

        string[] required =
        {
            "StatusSuccessColor", "StatusDangerColor", "StatusWarningColor", "StatusInfoColor",
            "StatusSuccessBrush", "StatusDangerBrush", "StatusWarningBrush", "StatusInfoBrush",
            "GlassFillColor", "GlassBorderColor", "GlassFillBrush", "GlassBorderBrush",
        };
        foreach (var key in required)
        {
            Assert.True(tokens.dark.ContainsKey(key), $"Default missing token '{key}'");
            Assert.True(tokens.light.ContainsKey(key), $"Light missing token '{key}'");
        }
    }

    [Fact]
    public void ScaleTokens_MatchCSharpMirrors()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var scales = LoadTokens(root!).scales;

        var expected = new Dictionary<string, double>
        {
            ["SpaceXs"] = DesignTokenValues.Space.Xs,
            ["SpaceS"] = DesignTokenValues.Space.S,
            ["SpaceM"] = DesignTokenValues.Space.M,
            ["SpaceL"] = DesignTokenValues.Space.L,
            ["SpaceXl"] = DesignTokenValues.Space.Xl,
            ["SpaceXxl"] = DesignTokenValues.Space.Xxl,
            ["FontCaption"] = DesignTokenValues.Font.Caption,
            ["FontBodySmall"] = DesignTokenValues.Font.BodySmall,
            ["FontBody"] = DesignTokenValues.Font.Body,
            ["FontSubtitle"] = DesignTokenValues.Font.Subtitle,
            ["FontTitle"] = DesignTokenValues.Font.Title,
            ["FontDisplay"] = DesignTokenValues.Font.Display,
            ["MotionDurationFastMs"] = DesignTokenValues.Motion.FastMs,
            ["MotionDurationNormalMs"] = DesignTokenValues.Motion.NormalMs,
            ["MotionDurationSlowMs"] = DesignTokenValues.Motion.SlowMs,
        };

        foreach (var (key, value) in expected)
        {
            Assert.True(scales.TryGetValue(key, out var raw), $"Scale token '{key}' missing from {TokensPath}");
            Assert.True(double.TryParse(raw, out var actual) && Math.Abs(actual - value) < 0.001,
                $"Scale token '{key}' drifted: XAML={raw}, C#={value}");
        }
    }

    [Fact]
    public void SpacingScale_FollowsFourPxBaseGrid()
    {
        var s = new[] { DesignTokenValues.Space.Xs, DesignTokenValues.Space.S, DesignTokenValues.Space.M,
                        DesignTokenValues.Space.L, DesignTokenValues.Space.Xl, DesignTokenValues.Space.Xxl };
        Assert.Equal(new double[] { 4, 8, 12, 16, 24, 32 }, s);
    }

    [Fact]
    public void MotionDurations_StayInsideMicroInteractionBand()
    {
        // The skill's hover micro-interaction band is 150-300ms; Normal must sit inside it,
        // Fast must stay shorter (tick/state flips), Slow must stay a surface transition.
        Assert.True(DesignTokenValues.Motion.FastMs > 0 && DesignTokenValues.Motion.FastMs < 150);
        Assert.True(DesignTokenValues.Motion.NormalMs >= 150 && DesignTokenValues.Motion.NormalMs <= 300);
        Assert.True(DesignTokenValues.Motion.SlowMs > DesignTokenValues.Motion.NormalMs && DesignTokenValues.Motion.SlowMs <= 400);
    }

    [Fact]
    public void StatusColors_MeetTextContrast_OnCardSurface_InBothThemes()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var tokens = LoadTokens(root!);
        var darkCard = DawnThemeHex(root!, "CardColor", "Default");
        var lightCard = DawnThemeHex(root!, "CardColor", "Light");

        foreach (var key in new[] { "StatusSuccessColor", "StatusDangerColor", "StatusWarningColor", "StatusInfoColor" })
        {
            var darkRatio = ContrastMath.ContrastRatio(tokens.dark[key], darkCard);
            Assert.True(darkRatio >= 4.5, $"{key} on dark Card: {darkRatio:F2} < 4.5");

            var lightRatio = ContrastMath.ContrastRatio(tokens.light[key], lightCard);
            Assert.True(lightRatio >= 4.5, $"{key} on light Card: {lightRatio:F2} < 4.5");
        }
    }

    [Fact]
    public void CoreTextPairs_MeetWcagAA_OnEoleSurfaces()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        var primary = DawnThemeHex(root!, "TextPrimaryColor", "Default");
        var secondary = DawnThemeHex(root!, "TextSecondaryColor", "Default");
        var layer = DawnThemeHex(root!, "LayerBgColor", "Default");
        var panel = DawnThemeHex(root!, "PanelColor", "Default");

        Assert.True(ContrastMath.ContrastRatio(primary, layer) >= 7.0, "TextPrimary on LayerBg must be AAA (dark)");
        Assert.True(ContrastMath.ContrastRatio(secondary, panel) >= 4.5, "TextSecondary on Panel must be AA (dark)");

        var primaryLight = DawnThemeHex(root!, "TextPrimaryColor", "Light");
        var layerLight = DawnThemeHex(root!, "LayerBgColor", "Light");
        Assert.True(ContrastMath.ContrastRatio(primaryLight, layerLight) >= 7.0, "TextPrimary on LayerBg must be AAA (light)");
    }

    /// <summary>TextTertiary paints functional small text (track numbers, column headers,
    /// settings descriptions) — it is body text under WCAG, not decoration, so it must clear
    /// 4.5:1 on every rest surface it appears on (2026-09-30 audit PT1-02/PT5-04).</summary>
    [Fact]
    public void TextTertiary_MeetsTextContrast_OnEoleSurfaces_InBothThemes()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        string[] surfaces = { "LayerBgColor", "PanelSubtleColor", "PanelColor", "CardColor" };
        foreach (var theme in new[] { "Default", "Light" })
        {
            var tertiary = DawnThemeHex(root!, "TextTertiaryColor", theme);
            foreach (var surface in surfaces)
            {
                var bg = DawnThemeHex(root!, surface, theme);
                var ratio = ContrastMath.ContrastRatio(tertiary, bg);
                Assert.True(ratio >= 4.5, $"TextTertiary on {surface} ({theme}): {ratio:F2} < 4.5");
            }
        }
    }

    /// <summary>DawnAccentTextBrush is what accent-colored foregrounds actually consume
    /// (toggles, segment tabs, active lyrics, ratings). It must clear AA text contrast on the
    /// panel and card surfaces in both themes — the raw light accent cannot, which is exactly
    /// why this separate token exists (2026-09-30 audit PT1-01/PT5-05).</summary>
    [Fact]
    public void AccentText_MeetsTextContrast_OnPanelAndCard_InBothThemes()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        foreach (var theme in new[] { "Default", "Light" })
        {
            var accentText = DawnThemeHex(root!, "DawnAccentTextColor", theme);
            foreach (var surface in new[] { "PanelColor", "CardColor" })
            {
                var bg = DawnThemeHex(root!, surface, theme);
                var ratio = ContrastMath.ContrastRatio(accentText, bg);
                Assert.True(ratio >= 4.5, $"DawnAccentText on {surface} ({theme}): {ratio:F2} < 4.5");
            }
        }
    }

    [Fact]
    public void Accent_MeetsUiContrast_OnPanel()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        var darkAccent = DawnThemeHex(root!, "DawnAccentColor", "Default");
        var darkPanel = DawnThemeHex(root!, "PanelColor", "Default");
        Assert.True(ContrastMath.ContrastRatio(darkAccent, darkPanel) >= 3.0,
            "Accent must meet the 3:1 UI-component threshold on the dark panel");

        // The light fill accent (#C77F1B family) stays at ~2.6:1 on the warm light panel by
        // design — it is only ever a *fill* (buttons, slider value, muted toggle backgrounds)
        // where its shape carries the affordance. Text/icon foregrounds moved to the separate
        // DawnAccentTextColor token, gated at 4.5:1 above. This floor keeps the fill from
        // degrading further.
        var lightAccent = DawnThemeHex(root!, "DawnAccentColor", "Light");
        var lightPanel = DawnThemeHex(root!, "PanelColor", "Light");
        Assert.True(ContrastMath.ContrastRatio(lightAccent, lightPanel) >= 2.5,
            "Light accent fell below the documented 2.5:1 floor on the light panel");
    }

    [Fact]
    public void PlayGlyph_MeetsTextContrast_OnAccentFill()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var accent = DawnThemeHex(root!, "DawnAccentColor", "Default");
        Assert.True(ContrastMath.ContrastRatio("#141414", accent) >= 4.5,
            "Play/pause glyph (#141414) must stay AA against the accent fill");
    }

    [Fact]
    public void ViewsAndControlsXaml_DoNotIntroduceNewHardcodedHex()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // Gate scope: hand-written UI surfaces. DawnTheme.xaml/DesignTokens.xaml legitimately
        // define palette hexes and are excluded.
        var uiXamlFiles = Directory.EnumerateFiles(AppDir(root!), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}")
                     || p.Contains($"{Path.DirectorySeparatorChar}Controls{Path.DirectorySeparatorChar}")
                     || p.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Baseline at token-layer introduction (2026-09-20): wallpaper dim overlay and one
        // library placeholder. New UI must reference tokens/ThemeResource instead of hex.
        const int baseline = 2;
        var offenders = new List<string>();
        foreach (var file in uiXamlFiles)
        {
            var text = File.ReadAllText(file);
            offenders.AddRange(System.Text.RegularExpressions.Regex.Matches(text, "#[0-9A-Fa-f]{8}")
                .Select(m => $"{Path.GetFileName(file)}:{m.Value}"));
        }

        Assert.True(offenders.Count <= baseline,
            $"Hardcoded 8-hex colors in UI XAML grew beyond baseline {baseline}:\n  {string.Join("\n  ", offenders)}");
    }

    [Fact]
    public void AppXaml_MergesDesignTokens()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var app = File.ReadAllText(AppDir(root!, "App.xaml"));
        Assert.Contains("DesignTokens.xaml", app);
    }
}
