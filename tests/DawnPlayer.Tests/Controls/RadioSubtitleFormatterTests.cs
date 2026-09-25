using DawnPlayer.App.Controls;
using Xunit;

namespace DawnPlayer.Tests.Controls;

/// <summary>
/// The subtitle is the only live text a radio track ever shows; every combination of missing
/// station name / missing StreamTitle must degrade instead of showing separators or blanks.
/// </summary>
public sealed class RadioSubtitleFormatterTests
{
    [Theory]
    [InlineData("Jazz FM", "Artist - Song", "Jazz FM · Artist - Song")]
    [InlineData("Jazz FM", "", "Jazz FM")]
    [InlineData("Jazz FM", null, "Jazz FM")]
    [InlineData("", "Artist - Song", "Artist - Song")]
    [InlineData(null, "Artist - Song", "Artist - Song")]
    [InlineData("", "", "")]
    [InlineData(null, null, "")]
    [InlineData("  ", "  ", "")]
    [InlineData(" Jazz FM ", " Song ", "Jazz FM · Song")]
    public void Format_CombinesWithFallbacks(string? station, string? title, string expected)
    {
        Assert.Equal(expected, RadioSubtitleFormatter.Format(station, title));
    }
}
