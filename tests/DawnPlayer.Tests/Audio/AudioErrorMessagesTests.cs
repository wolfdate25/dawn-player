using System;
using DawnPlayer.Core.Audio;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// A raw "재생 시작 실패: 0x8889000A" tells a user nothing, so each known audio-client HRESULT maps
/// to an explanation with an action in it. These map without touching a device.
/// </summary>
public sealed class AudioErrorMessagesTests
{
    private static Exception WithHResult(int hr) => new InvalidOperationException("driver said no") { HResult = hr };

    [Theory]
    [InlineData(WasapiDeviceService.AudclntDeviceInUse, "배타")]
    [InlineData(WasapiDeviceService.AudclntUnsupportedFormat, "형식")]
    [InlineData(WasapiDeviceService.AudclntBufferSizeNotAligned, "지연")]
    [InlineData(WasapiDeviceService.AudclntDeviceInvalidated, "장치")]
    [InlineData(WasapiDeviceService.AudclntExclusiveModeNotAllowed, "배타")]
    public void DescribeStartFailure_KnownHResult_ExplainsWhatToDo(int hresult, string expectedFragment)
    {
        var message = AudioErrorMessages.DescribeStartFailure(WithHResult(hresult));

        Assert.Contains(expectedFragment, message, StringComparison.Ordinal);
        Assert.DoesNotContain("driver said no", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeStartFailure_UnknownHResult_FallsBackToTheDriverMessage()
    {
        var message = AudioErrorMessages.DescribeStartFailure(WithHResult(unchecked((int)0x80004005)));

        Assert.Equal("driver said no", message);
    }

    [Fact]
    public void DescribeStartFailure_UnknownPrimaryWithKnownOriginal_UsesTheOriginalExplanation()
    {
        // The shared-mode retry throws something generic; the exclusive attempt is what carries the
        // real reason, so both are consulted.
        var message = AudioErrorMessages.DescribeStartFailure(
            WithHResult(unchecked((int)0x80004005)),
            WithHResult(WasapiDeviceService.AudclntDeviceInUse));

        Assert.Contains("배타", message, StringComparison.Ordinal);
        Assert.NotEqual("driver said no", message);
    }

    [Fact]
    public void DescribeStartFailure_KnownPrimary_TakesPrecedenceOverOriginal()
    {
        var message = AudioErrorMessages.DescribeStartFailure(
            WithHResult(WasapiDeviceService.AudclntUnsupportedFormat),
            WithHResult(WasapiDeviceService.AudclntDeviceInUse));

        Assert.Contains("형식", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeStartFailure_NullOriginal_IsAccepted()
    {
        var message = AudioErrorMessages.DescribeStartFailure(WithHResult(0x11111111), original: null);

        Assert.Equal("driver said no", message);
    }
}
