using System;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Normalized biquad coefficients (a0 divided out), as used by both the runtime filter bank and
/// the frequency-response curve drawn in the settings UI.
/// </summary>
public readonly struct BiquadCoefficients
{
    public double B0 { get; }
    public double B1 { get; }
    public double B2 { get; }
    public double A1 { get; }
    public double A2 { get; }

    public BiquadCoefficients(double b0, double b1, double b2, double a1, double a2)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        A1 = a1;
        A2 = a2;
    }

    /// <summary>Identity filter (passes the signal through untouched).</summary>
    public static BiquadCoefficients Identity => new(1.0, 0.0, 0.0, 0.0, 0.0);
}

/// <summary>
/// RBJ audio-EQ-cookbook coefficient design, shared by <see cref="EqualizerDspEffect"/> and
/// <c>EqFrequencyResponseCalculator</c>.
/// </summary>
/// <remarks>
/// One design routine for both means the curve the user sees and the filter they hear cannot drift
/// apart. They previously came from two independent implementations.
/// </remarks>
public static class BiquadDesign
{
    /// <summary>Frequency clamp shared by every caller, so the curve and the filter agree at the edges.</summary>
    public static double ClampFrequency(double frequencyHz, double sampleRate) =>
        Math.Clamp(frequencyHz, 20.0, Math.Min(20000.0, sampleRate * 0.499));

    /// <summary>Gain clamp in dB, shared by every caller.</summary>
    public static double ClampGainDb(double gainDb) => Math.Clamp(gainDb, -15.0, 15.0);

    /// <summary>Q clamp, shared by every caller.</summary>
    public static double ClampQ(double q) => Math.Clamp(q, 0.1, 8.0);

    /// <summary>
    /// Designs one filter section. <paramref name="sampleRate"/> must be positive; the frequency,
    /// gain and Q are clamped with the helpers above.
    /// </summary>
    public static BiquadCoefficients Create(EqFilterType type, double frequencyHz, double gainDb, double q, double sampleRate)
    {
        if (sampleRate <= 0) return BiquadCoefficients.Identity;

        double f0 = ClampFrequency(frequencyHz, sampleRate);
        double gain = ClampGainDb(gainDb);
        double qq = ClampQ(q);

        double w0 = 2.0 * Math.PI * f0 / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double a = Math.Pow(10.0, gain / 40.0);

        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case EqFilterType.LowShelf:
                {
                    // Shelf slope S = 1 → alpha = sin(w0)/2 * sqrt(2).
                    double alpha = sinW0 / 2.0 * Math.Sqrt(2.0);
                    double beta = 2.0 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + beta);
                    b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0);
                    b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - beta);
                    a0 = (a + 1.0) + (a - 1.0) * cosW0 + beta;
                    a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0);
                    a2 = (a + 1.0) + (a - 1.0) * cosW0 - beta;
                    break;
                }
            case EqFilterType.HighShelf:
                {
                    double alpha = sinW0 / 2.0 * Math.Sqrt(2.0);
                    double beta = 2.0 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + beta);
                    b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
                    b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - beta);
                    a0 = (a + 1.0) - (a - 1.0) * cosW0 + beta;
                    a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
                    a2 = (a + 1.0) - (a - 1.0) * cosW0 - beta;
                    break;
                }
            case EqFilterType.LowPass:
                {
                    double alpha = sinW0 / (2.0 * qq);
                    b0 = (1.0 - cosW0) / 2.0;
                    b1 = 1.0 - cosW0;
                    b2 = (1.0 - cosW0) / 2.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
            case EqFilterType.HighPass:
                {
                    double alpha = sinW0 / (2.0 * qq);
                    b0 = (1.0 + cosW0) / 2.0;
                    b1 = -(1.0 + cosW0);
                    b2 = (1.0 + cosW0) / 2.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
            default: // PeakEq
                {
                    double alpha = sinW0 / (2.0 * qq);
                    b0 = 1.0 + alpha * a;
                    b1 = -2.0 * cosW0;
                    b2 = 1.0 - alpha * a;
                    a0 = 1.0 + alpha / a;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha / a;
                    break;
                }
        }

        if (Math.Abs(a0) < 1e-12 || !double.IsFinite(a0)) return BiquadCoefficients.Identity;

        var coeffs = new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        if (!double.IsFinite(coeffs.B0) || !double.IsFinite(coeffs.B1) || !double.IsFinite(coeffs.B2) ||
            !double.IsFinite(coeffs.A1) || !double.IsFinite(coeffs.A2))
        {
            return BiquadCoefficients.Identity;
        }

        return coeffs;
    }
}
