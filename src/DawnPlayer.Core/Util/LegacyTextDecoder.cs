using System.Text;

namespace DawnPlayer.Core.Util;

/// <summary>
/// Decodes legacy (non-UTF-8) ANSI text files — CUE sheets, LRC lyrics. Choosing the encoding by
/// the SYSTEM's ANSI code page (<c>Encoding.GetEncoding(0)</c>) only worked on a machine whose
/// locale matches the file's: a CP949 CUE decoded on an en-US system (code page 1252) still came
/// out mojibake, while Korean-system users decoding Western ANSI files face the mirror problem.
/// The candidates (CP949 and the system ANSI page) are therefore compared RELATIVELY: genuine
/// CP949 Korean decodes to a Hangul-syllable presence that the 1252 interpretation of the same
/// bytes can never produce (it yields Latin garbage), so "CP949 shows Hangul and the other does
/// not" identifies Korean decisively on every system locale. LRC/CUE markup is mostly ASCII, so
/// the signal is the DELTA between candidates, not an absolute Hangul ratio — a short lyric line
/// is legitimately ~80% markup. Below-delta files fall back to the system code page (the
/// long-standing behavior), and Latin-1 last: it maps every byte losslessly, so text degrades
/// but never destroys.
/// </summary>
public static class LegacyTextDecoder
{
    /// <summary>Minimal Hangul presence for a CP949 decode to be called Korean — one or two
    /// syllables in a short line already beat the zero the 1252 reading offers.</summary>
    private const double KoreanThreshold = 0.05;

    static LegacyTextDecoder()
    {
        // Needed for code pages 949 / the system ANSI page on .NET Core.
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }

    public static string DecodeNonUtf8(byte[] bytes)
    {
        var cp949 = SafeEncoding(949);
        if (cp949 == null) return FinalFallback(bytes);

        var cp949Text = cp949.GetString(bytes);

        var systemAnsi = SafeEncoding(0);
        if (systemAnsi == null || systemAnsi.CodePage == 949 || systemAnsi.CodePage == Encoding.UTF8.CodePage)
        {
            // No second candidate to weigh: accept CP949 when it shows Korean, else Latin-1.
            return HangulRatio(cp949Text) >= KoreanThreshold ? cp949Text : Encoding.Latin1.GetString(bytes);
        }

        double cp949Ratio = HangulRatio(cp949Text);
        double systemRatio = HangulRatio(systemAnsi.GetString(bytes));
        if (cp949Ratio >= KoreanThreshold && cp949Ratio > systemRatio)
        {
            return cp949Text;
        }
        return systemAnsi.GetString(bytes);
    }

    private static string FinalFallback(byte[] bytes)
    {
        try { return Encoding.GetEncoding(0).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
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
