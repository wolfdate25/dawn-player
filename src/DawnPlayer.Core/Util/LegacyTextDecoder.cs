using System.Text;

namespace DawnPlayer.Core.Util;

/// <summary>
/// Decodes legacy (non-UTF-8) ANSI text files — CUE sheets, LRC lyrics. Choosing the encoding by
/// the SYSTEM's ANSI code page (<c>Encoding.GetEncoding(0)</c>) only worked on a machine whose
/// locale matches the file's: a CP949 CUE decoded on an en-US system (code page 1252) still came
/// out mojibake. Instead, on strict-UTF-8 failure the bytes are decoded with the Korean code
/// page (949) and the system ANSI page: genuine CP949 Korean decodes to a high Hangul-syllable
/// ratio while Western ANSI text (mostly ASCII with a few accents) stays mostly ASCII under
/// CP949, so a Hangul ratio of 0.3+ identifies Korean decisively on every system locale; below
/// that threshold the system code page keeps the long-standing behavior. Latin-1 is the final
/// fallback: it maps every byte losslessly, so text degrades but never destroys.
/// </summary>
public static class LegacyTextDecoder
{
    /// <summary>Hangul ratio at or above which a CP949 decode is accepted as genuinely Korean.</summary>
    private const double KoreanThreshold = 0.3;

    static LegacyTextDecoder()
    {
        // Needed for code pages 949 / the system ANSI page on .NET Core.
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }

    public static string DecodeNonUtf8(byte[] bytes)
    {
        var cp949 = SafeEncoding(949);
        if (cp949 != null)
        {
            var decoded = cp949.GetString(bytes);
            if (HangulRatio(decoded) >= KoreanThreshold) return decoded;
        }

        var systemAnsi = SafeEncoding(0);
        if (systemAnsi != null && systemAnsi.CodePage != Encoding.UTF8.CodePage)
        {
            return systemAnsi.GetString(bytes);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static double HangulRatio(string text)
    {
        int hangul = 0, nonSpace = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            nonSpace++;
            if (c is >= '\uAC00' and <= '\uD7A3') hangul++;
        }
        return nonSpace == 0 ? 0 : (double)hangul / nonSpace;
    }

    private static Encoding? SafeEncoding(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch { return null; }
    }
}
