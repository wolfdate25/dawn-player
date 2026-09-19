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
}
