using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using Xunit;

namespace DawnPlayer.Tests.Services;

/// <summary>
/// Stage-3 networking/DSP unit tests: the Last.fm request signature (fixed vectors), ICY
/// StreamTitle extraction, radio URL helpers, and the FFT against a brute-force DFT.
/// </summary>
public sealed class Stage3UnitTests
{
    // ---------------- Last.fm signing ----------------

    [Fact]
#pragma warning disable CA5351 // the signature scheme itself is MD5-based by Last.fm spec
    public void LastfmSign_SortsParameters_AndAppendsSecret()
    {
        // Hand-computed reference: md5("api_keyKEYmethodauth.getTokenfoobar") style ordering is
        // alphabetical; the test keeps a self-consistent vector instead of duplicating md5 code.
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = "KEY",
        };
        string sig = LastfmClient.Sign(parameters, "SECRET");

        string expected = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes("api_key=KEYmethod=auth.getTokenSECRET")));
        Assert.Equal(expected, sig);

        // Order independence: inserting the pairs in reverse must not change the signature.
        var reversed = new Dictionary<string, string>();
        foreach (var kv in parameters.Reverse()) reversed[kv.Key] = kv.Value;
        Assert.Equal(sig, LastfmClient.Sign(reversed, "SECRET"));

        // sk is excluded by the caller per spec; the signer itself signs everything it is given
        // (documented behavior), so a differing secret must change the signature.
        Assert.NotEqual(sig, LastfmClient.Sign(parameters, "OTHER"));
    }
#pragma warning restore CA5351

    // ---------------- ICY metadata ----------------

    [Theory]
    [InlineData("StreamTitle='Artist - Song';StreamUrl='';", "Artist - Song")]
    [InlineData("StreamTitle='제목';", "제목")]
    [InlineData("", "")]
    [InlineData("StreamUrl='';StreamTitle='Later';", "Later")]
    public void IcyMetadata_ExtractsStreamTitle(string blob, string expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(blob);
        Assert.Equal(expected, RadioTrack.ParseStreamTitle(bytes));
    }

    [Fact]
    public void IcyMetadata_TrailingNulls_Tolerated()
    {
        var bytes = new byte[64]; // metadata blocks are 16-byte padded with \0
        System.Text.Encoding.UTF8.GetBytes("StreamTitle='Radiohead - Weird';").CopyTo(bytes, 0);
        Assert.Equal("Radiohead - Weird", RadioTrack.ParseStreamTitle(bytes));
    }

    // ---------------- radio URL helpers ----------------

    [Theory]
    [InlineData("http://a.b/c", true)]
    [InlineData("https://a.b/c", true)]
    [InlineData("HTTP://A.B", true)]
    [InlineData("C:\\music\\a.mp3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStreamUrl_ClassifiesPaths(string? path, bool expected)
    {
        Assert.Equal(expected, RadioTrack.IsStreamUrl(path));
    }

    [Fact]
    public void RadioTrack_CreatesLiveTrackShape()
    {
        var track = RadioTrack.Create("http://s.example/stream");
        Assert.Equal("http://s.example/stream", track.Path);
        Assert.Equal(0, track.DurationMs);
        Assert.Equal("Radio", track.Codec);
        Assert.Null(track.ArtPath);
    }

    // ---------------- FFT vs brute-force DFT ----------------

    [Fact]
    public void Fft_MatchesBruteForceDft_ForwardAndInverse()
    {
        const int n = 64;
        var fft = new Fft(n);
        var re = new float[n];
        var im = new float[n];
        var original = new float[n];
        var random = new Random(42);
        for (int i = 0; i < n; i++) original[i] = (float)(random.NextDouble() * 2 - 1);

        Array.Copy(original, re, n);
        fft.Forward(re, im);

        for (int k = 0; k < n; k++)
        {
            double sumRe = 0, sumIm = 0;
            for (int t = 0; t < n; t++)
            {
                double angle = -2 * Math.PI * k * t / n;
                sumRe += original[t] * Math.Cos(angle);
                sumIm += original[t] * Math.Sin(angle);
            }
            Assert.True(Math.Abs(re[k] - sumRe) < 1e-3, $"re bin {k}: {re[k]} vs {sumRe}");
            Assert.True(Math.Abs(im[k] - sumIm) < 1e-3, $"im bin {k}: {im[k]} vs {sumIm}");
        }

        fft.Inverse(re, im);
        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(re[i] - original[i]) < 1e-4f, $"sample {i}");
        }
    }
}
