namespace DawnPlayer.App.Helpers;

/// <summary>
/// WCAG 2.x relative-luminance and contrast-ratio math over sRGB hex colors. Pure logic —
/// no UI dependency — so token gates can assert real contrast instead of trusting palette
/// intent. Used by DesignTokenTests and available to palette-guard code.
/// </summary>
public static class ContrastMath
{
    /// <summary>WCAG relative luminance for sRGB channels 0-255.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    /// <summary>WCAG contrast ratio (1.0 identical … 21.0 black/white) between two hex colors.
    /// Accepts "RRGGBB", "#RRGGBB", "AARRGGBB" and "#AARRGGBB"; alpha is ignored (composited
    /// contrast is a separate question the caller must resolve first).</summary>
    public static double ContrastRatio(string hexA, string hexB)
    {
        var a = ParseRgb(hexA);
        var b = ParseRgb(hexB);
        var la = RelativeLuminance(a.r, a.g, a.b);
        var lb = RelativeLuminance(b.r, b.g, b.b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (byte r, byte g, byte b) ParseRgb(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) throw new ArgumentException("null/empty hex color");
        var s = hex.TrimStart('#');
        // #RRGGBB (6) vs #AARRGGBB (8) — strip a leading alpha byte, never a channel.
        if (s.Length == 8) s = s.Substring(2);
        if (s.Length != 6) throw new FormatException($"Unsupported hex color '{hex}'");
        return (Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
    }

    /// <summary>
    /// Resolves a text-usable variant of an accent color: the color closest to the original
    /// (smallest shift) that still meets <paramref name="targetRatio"/> against the surface it
    /// will be painted on. The shift moves away from the background's luminance — darkening on
    /// light surfaces, lightening on dark ones — by blending toward black/white, which preserves
    /// hue exactly. Already-passing colors come back unchanged, so dark-theme accents (which
    /// pass) never drift while light-theme presets get a readable text variant. Pure logic;
    /// consumed by ThemeService.SetAccentBrushes for the DawnAccentTextBrush resource.
    /// </summary>
    public static (byte r, byte g, byte b) SolveTextVariant(
        byte r, byte g, byte b, byte bgR, byte bgG, byte bgB, double targetRatio = 4.5)
    {
        double RatioOf(byte cr, byte cg, byte cb)
        {
            var la = RelativeLuminance(cr, cg, cb);
            var lb = RelativeLuminance(bgR, bgG, bgB);
            var lighter = Math.Max(la, lb);
            var darker = Math.Min(la, lb);
            return (lighter + 0.05) / (darker + 0.05);
        }

        if (RatioOf(r, g, b) >= targetRatio) return (r, g, b);

        var darken = RelativeLuminance(bgR, bgG, bgB) > 0.5;

        // t is the shift amount from the original toward the blend extreme — t=0 is the original
        // (fails by precondition), t=1 is full black/white. Contrast is monotonic in t, so the
        // passing region is [t*, 1] and the search invariant "lo fails, hi passes" holds for
        // both directions.
        (byte, byte, byte) Blend(double t) => darken
            ? ((byte)Math.Round(r * (1 - t)), (byte)Math.Round(g * (1 - t)), (byte)Math.Round(b * (1 - t)))
            : ((byte)Math.Round(r + (255 - r) * t),
               (byte)Math.Round(g + (255 - g) * t),
               (byte)Math.Round(b + (255 - b) * t));

        var extreme = Blend(1);
        if (RatioOf(extreme.Item1, extreme.Item2, extreme.Item3) < targetRatio)
        {
            return extreme;
        }

        double lo = 0, hi = 1;
        for (var i = 0; i < 20; i++)
        {
            var mid = (lo + hi) / 2;
            var c = Blend(mid);
            if (RatioOf(c.Item1, c.Item2, c.Item3) >= targetRatio) hi = mid; else lo = mid;
        }

        // Byte rounding at the boundary can land an epsilon below the target — step further
        // from the original (toward the extreme, always the safe direction) until it clears.
        var t2 = hi;
        var result = Blend(t2);
        for (var i = 0; i < 256 && RatioOf(result.Item1, result.Item2, result.Item3) < targetRatio; i++)
        {
            t2 = Math.Min(1.0, t2 + 1.0 / 256);
            result = Blend(t2);
        }
        return result;
    }
}
