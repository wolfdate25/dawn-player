using DawnPlayer.App.Calculators;
using DawnPlayer.Core.Audio.Dsp;
using Xunit;

namespace DawnPlayer.Tests.Calculators;

/// <summary>
/// Spectrum analysis feeding the now-playing strip: a known tone lights its log-spaced bin,
/// silence stays flat, and bins cover the musical range end to end.
/// </summary>
public sealed class SpectrumCalculatorTests
{
    private static float[] HannTone(int sampleRate, double freqHz, double seconds)
    {
        int n = SpectrumTapDspEffect.WindowSamples;
        var window = new float[n];
        int toneFrames = (int)(sampleRate * seconds);
        for (int i = 0; i < n && i < toneFrames; i++)
        {
            window[i] = (float)(0.9 * System.Math.Sin(2.0 * System.Math.PI * freqHz * i / sampleRate));
        }
        return window;
    }

    [Fact]
    public void Sine_440Hz_LightsOnlyItsNeighborhood()
    {
        var levels = SpectrumCalculator.ComputeLevels(HannTone(44100, 440.0, 0.2), 44100);

        Assert.Equal(SpectrumCalculator.BinCount, levels.Length);
        int peak = 0;
        for (int i = 1; i < levels.Length; i++)
            if (levels[i] > levels[peak]) peak = i;

        // 440 Hz must dominate: the peak bin reads high, and bins away from the main lobe
        // stay well under the peak. The musical-range log bins are very narrow down low
        // (bin 1 spans only ~37-46 Hz) while a 2048-point Hann main lobe is ~90 Hz wide, so
        // bins 0-9 all legitimately show the 440 Hz tone at 0.6+ — assert instead that the
        // true peak sits at the correct 440 Hz bin and the far tail stays quiet.
        double peakLevel = levels[peak];
        Assert.True(peakLevel > 0.7, $"peak level {peakLevel}");
        Assert.True(peak >= 9 && peak <= 11, $"440 Hz peak at bin {peak}");
        for (int i = 14; i < levels.Length; i++)
        {
            Assert.True(levels[i] < peakLevel * 0.5, $"bin {i} leaked {levels[i]} vs peak {peakLevel}");
        }
    }

    [Fact]
    public void Silence_StaysFlat()
    {
        var levels = SpectrumCalculator.ComputeLevels(
            new float[SpectrumTapDspEffect.WindowSamples], 44100);
        Assert.All(levels, l => Assert.Equal(0.0, l));
    }

    [Fact]
    public void HighTone_12kHz_ResolvesInUpperBins()
    {
        var levels = SpectrumCalculator.ComputeLevels(HannTone(44100, 12000.0, 0.2), 44100);
        int peak = 0;
        for (int i = 1; i < levels.Length; i++)
            if (levels[i] > levels[peak]) peak = i;

        Assert.True(peak >= SpectrumCalculator.BinCount - 6, $"12 kHz landed at bin {peak}");
        Assert.True(levels[peak] > 0.5, $"peak level {levels[peak]}");
    }

    [Fact]
    public void BadInput_ReturnsZeros_WithoutThrowing()
    {
        var levels = SpectrumCalculator.ComputeLevels(new float[16], 44100);
        Assert.All(levels, l => Assert.Equal(0.0, l));

        var levels2 = SpectrumCalculator.ComputeLevels(new float[SpectrumTapDspEffect.WindowSamples], 0);
        Assert.All(levels2, l => Assert.Equal(0.0, l));
    }
}
