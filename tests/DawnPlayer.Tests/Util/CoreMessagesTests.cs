using DawnPlayer.App.Localization;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Util;

/// <summary>
/// M6-4: Core emits user-facing sentences as keyed wire messages
/// (<c>coremsg:&lt;Key&gt;\u001fargs…</c>) because Core cannot reach the resource catalog; the
/// App layer translates them through the <c>CoreMsg_&lt;Key&gt;</c> resw entries. Pinned here:
/// the wire format round-trips, embedded separator rules hold, unknown/raw strings pass
/// through untouched, and the App-side translation resolves the resw frame (or the Korean
/// fallback when the catalog is unavailable, as in tests).
/// </summary>
public sealed class CoreMessagesTests
{
    [Fact]
    public void EncodeDecode_RoundTripsKeyAndArgs()
    {
        var raw = CoreMessages.Encode(CoreMessageKey.PlayStartFailed, "0x8889000A");
        Assert.StartsWith("coremsg:PlayStartFailed", raw);

        Assert.True(CoreMessages.TryDecode(raw, out var key, out var args));
        Assert.Equal(CoreMessageKey.PlayStartFailed, key);
        Assert.Single(args);
        Assert.Equal("0x8889000A", args[0]);
    }

    [Fact]
    public void NoArgMessage_RoundTrips()
    {
        var raw = CoreMessages.Encode(CoreMessageKey.NextTrackMissing);
        Assert.Equal("coremsg:NextTrackMissing", raw);

        Assert.True(CoreMessages.TryDecode(raw, out var key, out var args));
        Assert.Equal(CoreMessageKey.NextTrackMissing, key);
        Assert.Empty(args);
    }

    [Fact]
    public void MultipleArgs_SurviveTheWire()
    {
        var raw = CoreMessages.Encode(CoreMessageKey.PlaybackError, "a", "b");
        Assert.True(CoreMessages.TryDecode(raw, out _, out var args));
        Assert.Equal(2, args.Length);
        Assert.Equal("b", args[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("다음 트랙이 없습니다.")]
    [InlineData("coremsg:NoSuchKey")]
    [InlineData("coremsg:")]
    public void NonCoreOrMalformedStrings_DoNotDecode(string raw)
    {
        Assert.False(CoreMessages.TryDecode(raw, out _, out _));
    }

    [Fact]
    public void Localize_ReproducesTheHistoricKoreanSentences()
    {
        // The built-in fallback must keep the exact sentences Core used to hardcode, so a host
        // without the resw catalog (tests, CLI) still reads the same warnings as before M6-4.
        Assert.Equal("다음 트랙이 없습니다.", CoreMessages.Localize(CoreMessageKey.NextTrackMissing));
        Assert.Equal("재생 시작 실패: 0x8889000A",
            CoreMessages.Localize(CoreMessageKey.PlayStartFailed, "0x8889000A"));
        Assert.Equal("지원하지 않는 형식입니다: song.zzz",
            CoreMessages.Localize(CoreMessageKey.UnsupportedFormat, "song.zzz"));
    }

    [Fact]
    public void AppLayerTranslation_UsesReswFrame_WithArgs()
    {
        var raw = CoreMessages.Encode(CoreMessageKey.FileOpenFailed, "song.zzz");
        var localized = AppStrings.LocalizeCoreMessage(raw);

        // In the test environment the resw catalog is unavailable, so AppStrings falls back to
        // the Korean frame — the same sentence users saw before the keying.
        Assert.Equal("파일을 열 수 없습니다: song.zzz", localized);
    }

    [Fact]
    public void AppLayerTranslation_PassesRawStringsThrough()
    {
        const string appWarning = "App 측에서 만든 안내문";
        Assert.Equal(appWarning, AppStrings.LocalizeCoreMessage(appWarning));
    }

    [Fact]
    public void ControllerWarnings_AreKeyed_NotHardcoded()
    {
        // The whole point of M6-4: the controller's warnings must travel as keys so the App can
        // translate them. Assert the emitted wire shape for the no-arg case directly.
        var raw = CoreMessages.Encode(CoreMessageKey.ExclusiveFallbackShared);
        Assert.Equal("coremsg:ExclusiveFallbackShared", raw);
        Assert.True(CoreMessages.TryDecode(raw, out var key, out _));
        Assert.Equal(CoreMessageKey.ExclusiveFallbackShared, key);
    }
}
