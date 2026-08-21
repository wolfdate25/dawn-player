using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public sealed class EqualizerDspTests
{
    [Fact]
    public void FrequencyResponseCalculator_DisabledProfile_ReturnsZeroDbResponse()
    {
        var profile = new EqProfile
        {
            Enabled = false,
            PreampDb = 4.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 6.0, Q = 1.0 }
            }
        };

        var freqs = new double[] { 100, 1000, 10000 };
        var response = EqFrequencyResponseCalculator.CalculateResponse(profile, freqs);

        Assert.Equal(3, response.Length);
        Assert.All(response, r => Assert.Equal(0.0, r));
    }

    [Fact]
    public void FrequencyResponseCalculator_PeakingFilter_MatchesTheoreticalPeakGain()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = -2.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 6.0, Q = 2.0 }
            }
        };

        var freqs = new double[] { 1000 };
        var response = EqFrequencyResponseCalculator.CalculateResponse(profile, freqs, 48000);

        Assert.Single(response);
        // At 1000 Hz center frequency, total response should be Preamp (-2dB) + Gain (+6dB) = +4dB
        Assert.Equal(4.0, response[0], 1);
    }
}
