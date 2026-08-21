using System.Collections.Generic;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Persistence;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Services;

/// <summary>
/// Service responsible for managing theme mode (Light/Dark/OLED/System),
/// system backdrops (Mica/Acrylic/Solid/AlbumArtBlur), dynamic color extraction,
/// title bar synchronization, and generating accent color palette resources.
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// Applies theme mode, system backdrop, and accent color palette according to UiSettings.
    /// </summary>
    public static void ApplyTheme(Window window, UiSettings ui, Panel? rootGrid = null)
    {
        var theme = ui.Theme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.OledBlack => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme;
        }

        // ThemeMode.System must follow the OS: RequestedTheme=Default lets WinUI controls flip,
        // and ActualTheme (resolved after the assignment above) tells us which custom palette
        // matches. Hard-coding dark here painted dark panels under light system controls.
        var isLight = IsEffectiveLight(window, ui);

        // Apply OLED Pure Black or Standard Palette
        if (ui.Theme == ThemeMode.OledBlack)
        {
            ApplyOledPalette();
        }
        else if (isLight)
        {
            ApplyStandardLightPalette();
        }
        else
        {
            ApplyStandardDarkPalette();
        }

        // Apply Backdrop
        window.SystemBackdrop = ui.Backdrop switch
        {
            BackdropMode.Mica => new MicaBackdrop { Kind = MicaKind.Base },
            BackdropMode.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropMode.Acrylic => new DesktopAcrylicBackdrop(),
            _ => null
        };

        if (rootGrid != null)
        {
            if (ui.Backdrop == BackdropMode.Solid)
            {
                rootGrid.Background = Helpers.ThemeResourceHelper.GetBrush("LayerBgBrush");
            }
            else
            {
                rootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        // Apply TitleBar Chrome
        UpdateTitleBar(window, isLight);

        // Apply Accent Color Preset / Custom Color
        ApplyAccentPreset(ui, isLight);
    }

    /// <summary>
    /// Resolves whether the effective visual flavor is light — either an explicit Light theme, or
    /// System mode on a light-mode OS (read from the root element's ActualTheme).
    /// </summary>
    public static bool IsEffectiveLight(Window window, UiSettings ui) =>
        ui.Theme == ThemeMode.Light ||
        (ui.Theme == ThemeMode.System &&
         window.Content is FrameworkElement f && f.ActualTheme == ElementTheme.Light);

    /// <summary>
    /// Synchronizes the Windows 11 AppWindow TitleBar caption buttons with current theme.
    /// </summary>
    public static void UpdateTitleBar(Window window, bool isLight)
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var titleBar = window.AppWindow?.TitleBar;
            if (titleBar == null) return;

            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            if (isLight)
            {
                titleBar.ButtonForegroundColor = ColorFromHex("#FF1A1A20");
                titleBar.ButtonHoverForegroundColor = ColorFromHex("#FF000000");
                titleBar.ButtonHoverBackgroundColor = ColorFromHex("#15000000");
                titleBar.ButtonPressedForegroundColor = ColorFromHex("#FF000000");
                titleBar.ButtonPressedBackgroundColor = ColorFromHex("#25000000");
                titleBar.ButtonInactiveForegroundColor = ColorFromHex("#FF8A8A94");
            }
            else
            {
                titleBar.ButtonForegroundColor = ColorFromHex("#FFF0F0F3");
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = ColorFromHex("#22FFFFFF");
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = ColorFromHex("#35FFFFFF");
                titleBar.ButtonInactiveForegroundColor = ColorFromHex("#FF6E6E7A");
            }
        }
        catch { }
    }

    /// <summary>
    /// Applies the specified accent color preset or custom color to Application resources.
    /// </summary>
    public static void ApplyAccentPreset(UiSettings ui, bool isLight)
    {
        var (cHex, hHex, pHex, mHex, gHex) = ui.AccentColor switch
        {
            AccentColorPreset.ElectricGold => (
                isLight ? "#FFD6A400" : "#FFFFD13B",
                isLight ? "#FFE6B800" : "#FFFFDF6B",
                isLight ? "#FFB88B00" : "#FFF5BE18",
                isLight ? "#26D6A400" : "#33FFD13B",
                isLight ? "#18D6A400" : "#20FFD13B"),

            AccentColorPreset.ForestEmerald => (
                isLight ? "#FF1B9A50" : "#FF2ECC71",
                isLight ? "#FF22B860" : "#FF48DB8A",
                isLight ? "#FF147A3F" : "#FF27AE60",
                isLight ? "#261B9A50" : "#332ECC71",
                isLight ? "#181B9A50" : "#202ECC71"),

            AccentColorPreset.CyanSapphire => (
                isLight ? "#FF0077B6" : "#FF00B4D8",
                isLight ? "#FF0096C7" : "#FF48CAE4",
                isLight ? "#FF023E8A" : "#FF0096C7",
                isLight ? "#260077B6" : "#3300B4D8",
                isLight ? "#180077B6" : "#2000B4D8"),

            AccentColorPreset.CrimsonRed => (
                isLight ? "#FFD63031" : "#FFFF4757",
                isLight ? "#FFE15F5F" : "#FFFF6B81",
                isLight ? "#FFB32021" : "#FFEE3040",
                isLight ? "#26D63031" : "#33FF4757",
                isLight ? "#18D63031" : "#20FF4757"),

            AccentColorPreset.ModernSlate => (
                isLight ? "#FF636E72" : "#FF95A5A6",
                isLight ? "#FF7A888D" : "#FFB2BEC3",
                isLight ? "#FF485255" : "#FF7F8C8D",
                isLight ? "#26636E72" : "#3395A5A6",
                isLight ? "#18636E72" : "#2095A5A6"),

            AccentColorPreset.NordFrost => (
                isLight ? "#FF5E81AC" : "#FF88C0D0",
                isLight ? "#FF81A1C1" : "#FF8FBCBB",
                isLight ? "#FF4C566A" : "#FF5E81AC",
                isLight ? "#265E81AC" : "#3388C0D0",
                isLight ? "#185E81AC" : "#2088C0D0"),

            AccentColorPreset.TokyoNight => (
                isLight ? "#FF3D59A1" : "#FF7AA2F7",
                isLight ? "#FF4F73C9" : "#FF93B4FA",
                isLight ? "#FF2C3E75" : "#FF608DE8",
                isLight ? "#263D59A1" : "#337AA2F7",
                isLight ? "#183D59A1" : "#207AA2F7"),

            AccentColorPreset.CatppuccinMocha => (
                isLight ? "#FF8839EF" : "#FFCBA6F7",
                isLight ? "#FF9A54F2" : "#FFDDB6F8",
                isLight ? "#FF7026D0" : "#FFB48EED",
                isLight ? "#268839EF" : "#33CBA6F7",
                isLight ? "#188839EF" : "#20CBA6F7"),

            AccentColorPreset.RosePine => (
                isLight ? "#FFB4637A" : "#FFEBBCBA",
                isLight ? "#FFC4768D" : "#FFF0CECC",
                isLight ? "#FF9B4F65" : "#FFD4A2A0",
                isLight ? "#26B4637A" : "#33EBBCBA",
                isLight ? "#18B4637A" : "#20EBBCBA"),

            AccentColorPreset.SunsetViolet => (
                isLight ? "#FF7828C8" : "#FFA78BFA",
                isLight ? "#FF9342DE" : "#FFBEA8FC",
                isLight ? "#FF5C1C9E" : "#FF8B5CF6",
                isLight ? "#267828C8" : "#33A78BFA",
                isLight ? "#187828C8" : "#20A78BFA"),

            AccentColorPreset.Custom => DerivePaletteFromCustomHex(ui.CustomAccentHex, isLight),

            _ => ( // EoleAmber (Default)
                isLight ? "#FFC77F1B" : "#FFE8A33D",
                isLight ? "#FFD68D25" : "#FFF0B456",
                isLight ? "#FFAE6C12" : "#FFD4881A",
                isLight ? "#26C77F1B" : "#33E8A33D",
                isLight ? "#18C77F1B" : "#20E8A33D")
        };

        SetAccentBrushes(cHex, hHex, pHex, mHex, gHex);
    }

    /// <summary>
    /// Applies dynamically extracted album artwork colors to the application's accent resources.
    /// </summary>
    public static void ApplyDynamicAlbumPalette(ExtractedAlbumPalette palette)
    {
        SetAccentBrushes(
            palette.AccentHex,
            palette.HoverHex,
            palette.PressedHex,
            palette.MutedHex,
            palette.GlowHex);
    }

    /// <summary>
    /// Sets the accent brushes in Application.Current.Resources.
    /// </summary>
    public static void SetAccentBrushes(string cHex, string hHex, string pHex, string mHex, string gHex)
    {
        var color = ColorFromHex(cHex);
        var hoverColor = ColorFromHex(hHex);
        var pressedColor = ColorFromHex(pHex);
        var mutedColor = ColorFromHex(mHex);
        var glowColor = ColorFromHex(gHex);

        Application.Current.Resources["DawnAccentColor"] = color;
        Application.Current.Resources["DawnAccentHoverColor"] = hoverColor;
        Application.Current.Resources["DawnAccentPressedColor"] = pressedColor;
        Application.Current.Resources["DawnAccentMutedColor"] = mutedColor;
        Application.Current.Resources["DawnAccentGlowColor"] = glowColor;

        SetBrush("DawnAccentBrush", color);
        SetBrush("DawnAccentHoverBrush", hoverColor);
        SetBrush("DawnAccentPressedBrush", pressedColor);
        SetBrush("DawnAccentMutedBrush", mutedColor);
        SetBrush("DawnAccentGlowBrush", glowColor);

        // Control item highlight overrides
        SetBrush("SliderTrackValueFill", color);
        SetBrush("SliderThumbBackground", color);
        SetBrush("SliderTrackValueFillPointerOver", hoverColor);
        SetBrush("SliderThumbBackgroundPointerOver", hoverColor);
        SetBrush("SliderThumbBackgroundPressed", pressedColor);

        // ToggleButton theme overrides
        SetBrush("ToggleButtonBackgroundChecked", mutedColor);
        SetBrush("ToggleButtonBackgroundCheckedPointerOver", mutedColor);
        SetBrush("ToggleButtonBackgroundCheckedPressed", glowColor);
        SetBrush("ToggleButtonForegroundChecked", color);
        SetBrush("ToggleButtonForegroundCheckedPointerOver", hoverColor);
        SetBrush("ToggleButtonForegroundCheckedPressed", pressedColor);
        SetBrush("ToggleButtonBorderBrushChecked", glowColor);
        SetBrush("ToggleButtonBorderBrushCheckedPointerOver", color);
        SetBrush("ToggleButtonBorderBrushCheckedPressed", pressedColor);
    }


    // ---------------- live-updating resource brushes ----------------

    /// <summary>
    /// Brush instances published into Application.Resources, kept so their Color can be mutated in
    /// place. Replacing a resource entry with a new SolidColorBrush does not repaint anything that
    /// already resolved the old instance, which is why accent, OLED and per-track album palettes
    /// only appeared to work after a restart.
    /// </summary>
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

    private static void SetBrush(string key, Windows.UI.Color color)
    {
        var resources = Application.Current.Resources;
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = resources.TryGetValue(key, out var existing) && existing is SolidColorBrush sb
                ? sb
                : new SolidColorBrush();
            BrushCache[key] = brush;
            resources[key] = brush;
        }
        brush.Color = color;
    }

    private static (string cHex, string hHex, string pHex, string mHex, string gHex) DerivePaletteFromCustomHex(string? hex, bool isLight)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return isLight
                ? ("#FFC77F1B", "#FFD68D25", "#FFAE6C12", "#26C77F1B", "#18C77F1B")
                : ("#FFE8A33D", "#FFF0B456", "#FFD4881A", "#33E8A33D", "#20E8A33D");
        }

        var col = ColorFromHex(hex);
        AlbumArtColorExtractor.RgbToHsl(col.R, col.G, col.B, out double h, out double s, out double l);

        double baseS = Math.Clamp(s, 0.4, 0.95);
        double baseL = isLight ? Math.Clamp(l, 0.35, 0.55) : Math.Clamp(l, 0.48, 0.70);

        AlbumArtColorExtractor.HslToRgb(h, baseS, baseL, out int r, out int g, out int b);
        AlbumArtColorExtractor.HslToRgb(h, Math.Min(1.0, baseS * 1.05), Math.Clamp(baseL + (isLight ? -0.06 : 0.08), 0.2, 0.85), out int hr, out int hg, out int hb);
        AlbumArtColorExtractor.HslToRgb(h, baseS, Math.Clamp(baseL - (isLight ? -0.08 : 0.10), 0.15, 0.75), out int pr, out int pg, out int pb);

        string cHex = $"#FF{r:X2}{g:X2}{b:X2}";
        string hHex = $"#FF{hr:X2}{hg:X2}{hb:X2}";
        string pHex = $"#FF{pr:X2}{pg:X2}{pb:X2}";
        string mHex = $"#33{r:X2}{g:X2}{b:X2}";
        string gHex = $"#26{r:X2}{g:X2}{b:X2}";

        return (cHex, hHex, pHex, mHex, gHex);
    }

    private static void ApplyOledPalette()
    {
        // Text colours belong to the palette: without them the light theme kept the
        // dark-theme fallbacks and rendered drawer titles and lyric lines invisible.
        SetResourceColor("TextPrimaryColor", "#FFF3F3F6");
        SetResourceColor("TextSecondaryColor", "#FFAEAEBC");
        SetResourceColor("TextTertiaryColor", "#FF787888");
        SetResourceColor("LayerBgColor", "#FF000000");
        SetResourceColor("PanelColor", "#FF08080A");
        SetResourceColor("PanelSubtleColor", "#FF000000");
        SetResourceColor("CardColor", "#FF111114");
        SetResourceColor("CardHoverColor", "#FF1C1C22");
        SetResourceColor("CardPressedColor", "#FF26262E");
        SetResourceColor("ControlBgColor", "#FF141418");
        SetResourceColor("HoverColor", "#FF1A1A20");
        SetResourceColor("SeparatorColor", "#FF22222A");
        SetResourceColor("SeparatorSubtleColor", "#FF18181E");
        SetResourceColor("BorderSubtleColor", "#44FFFFFF");
        SetResourceColor("BadgeBgColor", "#FF1C1C24");
        SetResourceColor("BadgeTextColor", "#FFD0D0DC");
    }

    private static void ApplyStandardDarkPalette()
    {
        // Text colours belong to the palette: without them the light theme kept the
        // dark-theme fallbacks and rendered drawer titles and lyric lines invisible.
        SetResourceColor("TextPrimaryColor", "#FFF3F3F6");
        SetResourceColor("TextSecondaryColor", "#FFAEAEBC");
        SetResourceColor("TextTertiaryColor", "#FF787888");
        SetResourceColor("LayerBgColor", "#FF18181D");
        SetResourceColor("PanelColor", "#FF1F1F25");
        SetResourceColor("PanelSubtleColor", "#FF19191E");
        SetResourceColor("CardColor", "#FF27272F");
        SetResourceColor("CardHoverColor", "#FF32323C");
        SetResourceColor("CardPressedColor", "#FF3C3C48");
        SetResourceColor("ControlBgColor", "#FF2A2A33");
        SetResourceColor("HoverColor", "#FF30303A");
        SetResourceColor("SeparatorColor", "#FF2F2F39");
        SetResourceColor("SeparatorSubtleColor", "#FF24242D");
        SetResourceColor("BorderSubtleColor", "#33FFFFFF");
        SetResourceColor("BadgeBgColor", "#FF2A2A34");
        SetResourceColor("BadgeTextColor", "#FFBDBDCB");
    }

    private static void ApplyStandardLightPalette()
    {
        // Text colours belong to the palette: without them the light theme kept the
        // dark-theme fallbacks and rendered drawer titles and lyric lines invisible.
        SetResourceColor("TextPrimaryColor", "#FF1A1A20");
        SetResourceColor("TextSecondaryColor", "#FF555562");
        SetResourceColor("TextTertiaryColor", "#FF868694");
        SetResourceColor("LayerBgColor", "#FFF7F6F3");
        SetResourceColor("PanelColor", "#FFF0EFEB");
        SetResourceColor("PanelSubtleColor", "#FFEBEAE5");
        SetResourceColor("CardColor", "#FFE6E4DE");
        SetResourceColor("CardHoverColor", "#FFDCDAD3");
        SetResourceColor("CardPressedColor", "#FFD2D0C8");
        SetResourceColor("ControlBgColor", "#FFE2E0D8");
        SetResourceColor("HoverColor", "#FFDDDCD5");
        SetResourceColor("SeparatorColor", "#FFD5D3CC");
        SetResourceColor("SeparatorSubtleColor", "#FFE0DED8");
        SetResourceColor("BorderSubtleColor", "#22000000");
        SetResourceColor("BadgeBgColor", "#FFE0DED6");
        SetResourceColor("BadgeTextColor", "#FF484854");
    }

    private static void SetResourceColor(string key, string hex)
    {
        var col = ColorFromHex(hex);
        Application.Current.Resources[key] = col;
        SetBrush(key.Replace("Color", "Brush"), col);
    }

    /// <summary>
    /// Parses an ARGB/RGB hex color string (e.g., "#FFC77F1B" or "C77F1B") into Windows.UI.Color.
    /// Malformed input (hand-edited settings.json) falls back to the default amber accent instead
    /// of throwing out of MainWindow's constructor, which used to leave a windowless process.
    /// </summary>
    public static Windows.UI.Color ColorFromHex(string? hex)
    {
        return TryColorFromHex(hex, out var color)
            ? color
            : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xA3, 0x3D); // EoleAmber (dark accent)
    }

    /// <summary>Attempts to parse an ARGB/RGB hex color string. Returns false on malformed input.</summary>
    public static bool TryColorFromHex(string? hex, out Windows.UI.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var h = hex.Trim().TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        if (h.Length != 8) return false;

        if (!byte.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte a) ||
            !byte.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(h[4..6], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(h[6..8], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        color = Windows.UI.Color.FromArgb(a, r, g, b);
        return true;
    }
}
