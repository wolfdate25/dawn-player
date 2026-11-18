using System.Text;

namespace DawnPlayer.Tests.Fuzzing;

/// <summary>
/// Deterministic mutation-based fuzz input generator for the text/byte parsers. Coverage-guided
/// fuzzing (libFuzzer/SharpFuzz) needs Linux-centric tooling this Windows-only repo cannot host,
/// so this is the pragmatic middle ground: a seed corpus of valid grammar plus classic mutation
/// operators (bit flips, splices, truncations, numeric smashing, interesting bytes/chars). .NET's
/// seeded <see cref="Random"/> is a fixed algorithm, so a failing (target, seed, iteration) triple
/// IS the minimal reproduction — log it and the failure replays forever.
/// </summary>
internal static class FuzzInputGenerator
{
    /// <summary>Bytes that flip parsers into their edge branches: markup delimiters, line breaks,
    /// the digits runs feed int/double parsing, and high bytes exercise encoding detection.</summary>
    private static readonly byte[] InterestingBytes =
    [
        (byte)'\0', (byte)'\n', (byte)'\r', (byte)'\t', (byte)'[', (byte)']', (byte)'<', (byte)'>',
        (byte)':', (byte)',', (byte)'"', (byte)'#', (byte)'.', (byte)'-', (byte)'+', (byte)'0',
        (byte)'9', 0x7F, 0x80, 0x81, 0xEF, 0xBB, 0xBF, 0xFE, 0xFF
    ];

    /// <summary>Char-level overlay set: the byte set cannot produce surrogates, the BOM char or
    /// Hangul, and those are exactly the shapes the LRC/CUE pipelines see from real files.</summary>
    private static readonly char[] InterestingChars =
    [
        '\0', '\n', '\r', '\t', '\uFEFF', '[', ']', '<', '>', ':', ',', '"', '#', '.', '-', '+',
        '0', '9', '♪', '가', '\u0080', '\uFFFF', '\uD800', '\uDC00', '\uFFFD'
    ];

    /// <summary>Digit strings of these lengths replace numeric fields when smashing numbers —
    /// zero, empty and >int-digits are the interesting extremes for int/double parsing.</summary>
    private static readonly int[] NumberLengths = [0, 1, 2, 5, 10, 12, 19, 30];

    private const int MaxInputLength = 64 * 1024;

    /// <summary>Mutates <paramref name="corpus"/> into <paramref name="count"/> distinct-ish
    /// strings. Byte-level mutations run on the Latin-1 image (lossless byte↔char), then a
    /// char-level overlay injects the Unicode shapes above.</summary>
    public static IReadOnlyList<string> MutateText(string corpus, int count, int seed)
    {
        var rnd = new Random(seed);
        var results = new string[count];
        for (int i = 0; i < count; i++)
            results[i] = MutateTextOnce(corpus, rnd);
        return results;
    }

    /// <summary>Mutates the given byte corpora (raw byte-level only — used for decoders and
    /// whole-file parse paths where the input is bytes, not text).</summary>
    public static IReadOnlyList<byte[]> MutateBytes(IReadOnlyList<byte[]> corpora, int count, int seed)
    {
        var rnd = new Random(seed);
        var results = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var source = corpora[rnd.Next(corpora.Count)];
            results[i] = MutateByteImage([.. source], rnd);
        }
        return results;
    }

    /// <summary>Pure noise: random-length buffers of uniformly random bytes. This is the only
    /// generator the decoders cannot "expect" — BOM guesses, UTF-8 strict validation and
    /// code-page fallbacks all get exercised by it.</summary>
    public static IReadOnlyList<byte[]> RandomBytes(int count, int seed, int maxLength)
    {
        var rnd = new Random(seed);
        var results = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[rnd.Next(maxLength + 1)];
            rnd.NextBytes(bytes);
            results[i] = bytes;
        }
        return results;
    }

    private static string MutateTextOnce(string corpus, Random rnd)
    {
        var bytes = Encoding.Latin1.GetBytes(corpus);
        bytes = MutateByteImage(bytes, rnd);

        var text = Encoding.Latin1.GetString(bytes);
        int overlays = rnd.Next(4);
        for (int i = 0; i < overlays; i++)
            text = SpliceChar(text, rnd);
        return text;
    }

    /// <summary>Applies 1–6 random mutation operators to the byte image. The empty image is a
    /// legal state (TruncateTail produces it) and stays as-is — every operator below requires
    /// at least one byte to mutate.</summary>
    private static byte[] MutateByteImage(byte[] bytes, Random rnd)
    {
        int mutations = 1 + rnd.Next(6);
        for (int m = 0; m < mutations && bytes.Length > 0 && bytes.Length < MaxInputLength; m++)
        {
            switch (rnd.Next(9))
            {
                case 0: FlipBit(bytes, rnd); break;
                case 1: FlipByte(bytes, rnd); break;
                case 2: bytes = InsertInterestingByte(bytes, rnd); break;
                case 3: bytes = DeleteSpan(bytes, rnd); break;
                case 4: bytes = DuplicateSpan(bytes, rnd); break;
                case 5: bytes = TruncateTail(bytes, rnd); break;
                case 6: bytes = SmashNumbers(bytes, rnd); break;
                case 7: bytes = SpliceOtherRegion(bytes, rnd); break;
                case 8: bytes = RepeatSpanToBloat(bytes, rnd); break;
            }
        }
        return bytes;
    }

    private static void FlipBit(byte[] bytes, Random rnd) =>
        bytes[rnd.Next(bytes.Length)] ^= (byte)(1 << rnd.Next(8));

    private static void FlipByte(byte[] bytes, Random rnd) =>
        bytes[rnd.Next(bytes.Length)] ^= 0xFF;

    private static byte[] InsertInterestingByte(byte[] bytes, Random rnd)
    {
        int at = rnd.Next(bytes.Length + 1);
        var grown = new byte[bytes.Length + 1];
        Array.Copy(bytes, 0, grown, 0, at);
        grown[at] = InterestingBytes[rnd.Next(InterestingBytes.Length)];
        Array.Copy(bytes, at, grown, at + 1, bytes.Length - at);
        return grown;
    }

    private static byte[] DeleteSpan(byte[] bytes, Random rnd)
    {
        int at = rnd.Next(bytes.Length);
        int len = Math.Min(1 + rnd.Next(16), bytes.Length - at);
        var shrunk = new byte[bytes.Length - len];
        Array.Copy(bytes, 0, shrunk, 0, at);
        Array.Copy(bytes, at + len, shrunk, at, bytes.Length - at - len);
        return shrunk;
    }

    private static byte[] DuplicateSpan(byte[] bytes, Random rnd)
    {
        int at = rnd.Next(bytes.Length);
        int len = Math.Min(1 + rnd.Next(24), bytes.Length - at);
        var grown = new byte[bytes.Length + len];
        Array.Copy(bytes, 0, grown, 0, at);
        Array.Copy(bytes, at, grown, at, len);
        Array.Copy(bytes, at, grown, at + len, len);
        Array.Copy(bytes, at + len, grown, at + len * 2, bytes.Length - at - len);
        return grown;
    }

    private static byte[] TruncateTail(byte[] bytes, Random rnd)
    {
        int keep = rnd.Next(bytes.Length + 1);
        var shrunk = new byte[keep];
        Array.Copy(bytes, shrunk, keep);
        return shrunk;
    }

    /// <summary>Replaces whole digit runs with extreme-length digit strings — the highest-value
    /// mutation for timestamp/index/duration fields, which all terminate in numeric parsers.</summary>
    private static byte[] SmashNumbers(byte[] bytes, Random rnd)
    {
        int at = rnd.Next(bytes.Length);
        while (at < bytes.Length && (bytes[at] < (byte)'0' || bytes[at] > (byte)'9')) at++;
        if (at >= bytes.Length) return bytes;

        int end = at;
        while (end < bytes.Length && bytes[end] >= (byte)'0' && bytes[end] <= (byte)'9') end++;

        int digits = NumberLengths[rnd.Next(NumberLengths.Length)];
        var firstDigit = (byte)('1' + rnd.Next(9));
        var replacement = digits == 0 ? [] : Enumerable.Range(0, digits)
            .Select(i => i == 0 ? firstDigit : (byte)('0' + rnd.Next(10)))
            .ToArray();

        var grown = new byte[bytes.Length - (end - at) + replacement.Length];
        Array.Copy(bytes, 0, grown, 0, at);
        Array.Copy(replacement, 0, grown, at, replacement.Length);
        Array.Copy(bytes, end, grown, at + replacement.Length, bytes.Length - end);
        return grown;
    }

    private static byte[] SpliceOtherRegion(byte[] bytes, Random rnd)
    {
        if (bytes.Length < 4) return bytes;
        int srcAt = rnd.Next(bytes.Length);
        int srcLen = Math.Min(1 + rnd.Next(32), bytes.Length - srcAt);
        int dstAt = rnd.Next(bytes.Length);
        var spliced = (byte[])bytes.Clone();
        Array.Copy(bytes, srcAt, spliced, dstAt, Math.Min(srcLen, bytes.Length - dstAt));
        return spliced;
    }

    private static byte[] RepeatSpanToBloat(byte[] bytes, Random rnd)
    {
        if (bytes.Length < 2) return bytes;
        int at = rnd.Next(bytes.Length);
        int len = Math.Min(1 + rnd.Next(12), bytes.Length - at);
        int repeats = 2 + rnd.Next(16);
        long total = (long)bytes.Length + (long)len * (repeats - 1);
        if (total > MaxInputLength) return bytes;

        var grown = new byte[total];
        Array.Copy(bytes, grown, at);
        int cursor = at;
        for (int i = 0; i < repeats; i++)
        {
            Array.Copy(bytes, at, grown, cursor, len);
            cursor += len;
        }
        Array.Copy(bytes, at + len, grown, cursor, bytes.Length - at - len);
        return grown;
    }

    private static string SpliceChar(string text, Random rnd)
    {
        int at = rnd.Next(text.Length + 1);
        int insert = 1 + rnd.Next(4);
        var sb = new StringBuilder(text.Length + insert);
        sb.Append(text, 0, at);
        for (int i = 0; i < insert; i++)
            sb.Append(InterestingChars[rnd.Next(InterestingChars.Length)]);
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }
}
