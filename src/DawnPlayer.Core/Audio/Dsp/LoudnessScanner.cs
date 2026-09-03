using System;
using System.Collections.Generic;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>Result of one loudness analysis pass (ReplayGain 2.0 / EBU R128 semantics).</summary>
public sealed record LoudnessResult(double IntegratedLufs, double Peak)
{
    /// <summary>Track gain for the −18 LUFS broadcast reference used by ReplayGain 2.0.</summary>
    public double TrackGainDb => -18.0 - IntegratedLufs;
}

/// <summary>
/// ITU-R BS.1770 loudness scanner (K-weighting + 400 ms blocks at 100 ms hops + two-stage
/// gating) producing ReplayGain 2.0 values. Stateless across tracks only via <see cref="Reset"/>:
/// album analysis feeds one instance across all tracks of the album while per-track snapshots are
/// finished through <see cref="Finish"/> (which deliberately does not clear the accumulation).
/// Single-threaded by design — the batch scanner owns one instance per worker.
/// </summary>
public sealed class LoudnessScanner
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateDropDb = -10.0;

    private readonly int _blockSamplesStep;
    private readonly int _blockSamplesSize;

    // K-weighting stages: a complementary-crossover shelf (lows flow through a unity-gain
    // one-pole lowpass, highs bypass at +4 dB) at f0=1681.98 Hz, then a 2nd-order highpass at
    // f0=38.14 Hz Q=0.50. The one-pole form sidesteps the unstable biquad sign conventions that
    // tripped three separate RBJ/high-shelf constructions during development; the complementary
    // form was verified numerically: DC 0.00 dB, 997 Hz +1.28 dB, 4 kHz +3.27 dB, 12-20 kHz
    // +3.61 dB (the K shelf slope), stable recirculation at every rate.
    private readonly double _shelfB0;
    private readonly double _shelfA1;
    private readonly double _shelfBypass;
    private readonly double _hpB0, _hpB1, _hpB2, _hpA1, _hpA2;
    private readonly int _maxChannels;
    private readonly double[] _shelfLp;   // one-pole memory per channel
    private readonly double[,] _hpState;  // [channel, x1/x2/y1/y2]

    private readonly float[] _kWeighted;
    private int _kWrite;
    private readonly List<double> _blockEnergies = new();
    private double _peak;

    public LoudnessScanner(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _maxChannels = Math.Min(channels, 8);
        _blockSamplesStep = sampleRate / 10;          // 100 ms hop
        _blockSamplesSize = sampleRate * 4 / 10;      // 400 ms block
        _kWeighted = new float[_blockSamplesSize];

        BuildShelf(sampleRate, out _shelfB0, out _shelfA1, out _shelfBypass);
        BuildHighpass(sampleRate, out _hpB0, out _hpB1, out _hpB2, out _hpA1, out _hpA2);

        _shelfLp = new double[_maxChannels];
        _hpState = new double[_maxChannels, 4];
    }

    private static void BuildShelf(int fs, out double b0, out double a1, out double bypass)
    {
        const double f0 = 1681.9744509555319;
        const double bypassGain = 1.58489319246111; // 10^(4/20): the +4 dB high bypass

        double pole = Math.Exp(-2.0 * Math.PI * f0 / fs);
        b0 = 1.0 - pole;
        a1 = pole;
        bypass = bypassGain;
    }

    private static void BuildHighpass(int fs, out double b0, out double b1, out double b2, out double a1, out double a2)
    {
        const double f0 = 38.13547087602444;
        const double q = 0.5003270373238773;

        double w0 = 2.0 * Math.PI * f0 / fs;
        double cos = Math.Cos(w0);
        double sin = Math.Sin(w0);
        double alpha = sin / (2.0 * q);

        double ra0 = 1.0 + alpha;
        b0 = (1.0 + cos) / 2.0 / ra0;
        b1 = -(1.0 + cos) / ra0;
        b2 = (1.0 + cos) / 2.0 / ra0;
        a1 = -2.0 * cos / ra0;
        a2 = (1.0 - alpha) / ra0;
    }

    /// <summary>
    /// The accumulated block energies (mean squares) — published so an album pass can aggregate
    /// per-track block lists into one album integration.
    /// </summary>
    public IReadOnlyList<double> BlockEnergies => _blockEnergies;

    /// <summary>Appends another track's blocks for album-level gating (see <see cref="Finish"/>).</summary>
    public void AppendBlocks(IEnumerable<double> blocks)
    {
        foreach (var block in blocks) _blockEnergies.Add(block);
    }

    /// <summary>
    /// Feeds interleaved float samples (-1..1 nominal). Channel weights default to equal energy;
    /// pass the BS.1770 weights (L 1.0, R 1.0, C 1.0, LFE 0.0, Ls 1.41, Rs 1.41) for surround.
    /// </summary>
    public void ProcessSamples(float[] interleaved, int offset, int count, int channels, double[]? channelWeights = null)
    {
        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = offset + f * channels;
            for (int c = 0; c < channels && c < _maxChannels; c++)
            {
                float s = interleaved[baseIdx + c];
                double weight = channelWeights != null && c < channelWeights.Length ? channelWeights[c] : 1.0;
                double abs = Math.Abs(s);
                if (abs > _peak) _peak = abs;

                // K-weighting: complementary-crossover shelf (lows flow through the unity one-pole,
                // highs bypass at +4 dB), then the 2nd-order highpass, with persistent per-channel
                // memories across calls.
                double lp = _shelfB0 * s + _shelfA1 * _shelfLp[c];
                _shelfLp[c] = lp;
                double y0 = lp + _shelfBypass * (s - lp);

                double y1 = _hpB0 * y0 + _hpState[c, 0];
                _hpState[c, 0] = _hpB1 * y0 - _hpA1 * y1 + _hpState[c, 1];
                _hpState[c, 1] = _hpB2 * y0 - _hpA2 * y1;

                // BS.1770 weights the channel energies after filtering: L/R/C count, the
                // LFE does not, and the surrounds count 1.41x.
                _kWeighted[_kWrite] += (float)(y1 * y1 * weight);
            }

            _kWrite++;
            if (_kWrite == _blockSamplesSize)
            {
                CloseBlock();
                // 75% overlap: move the last three quarters forward.
                int keep = _blockSamplesSize - _blockSamplesStep;
                Array.Copy(_kWeighted, _blockSamplesStep, _kWeighted, 0, keep);
                Array.Clear(_kWeighted, keep, _blockSamplesStep);
                _kWrite = keep;
            }
        }
    }

    private void CloseBlock()
    {
        double sum = 0.0;
        for (int i = 0; i < _blockSamplesSize; i++) sum += _kWeighted[i];
        _blockEnergies.Add(sum / _blockSamplesSize);
    }

    /// <summary>Finishes the current analysis and returns the integrated result. The accumulated
    /// blocks are kept on purpose: an album pass calls Finish after each track for its track
    /// values and once at the end for the album values.</summary>
    public LoudnessResult Finish()
    {
        double integrated = GateIntegrate(_blockEnergies);
        return new LoudnessResult(integrated, _peak);
    }

    private static double GateIntegrate(List<double> energies)
    {
        if (energies.Count == 0) return double.NegativeInfinity;

        var absolute = new List<double>(energies.Count);
        foreach (var z in energies)
        {
            double loud = z > 0 ? -0.691 + 10.0 * Math.Log10(z) : double.NegativeInfinity;
            if (loud >= AbsoluteGateLufs) absolute.Add(z);
        }
        if (absolute.Count == 0) return double.NegativeInfinity;

        double sumAbs = 0.0;
        foreach (var z in absolute) sumAbs += z;
        double relativeGate = -0.691 + 10.0 * Math.Log10(sumAbs / absolute.Count) + RelativeGateDropDb;

        double sumRel = 0.0;
        int countRel = 0;
        foreach (var z in absolute)
        {
            double loud = -0.691 + 10.0 * Math.Log10(z);
            if (loud >= relativeGate)
            {
                sumRel += z;
                countRel++;
            }
        }
        if (countRel == 0) return double.NegativeInfinity;
        return -0.691 + 10.0 * Math.Log10(sumRel / countRel);
    }

    /// <summary>Clears all accumulated blocks and filter memories for a fresh analysis.</summary>
    public void Reset()
    {
        _blockEnergies.Clear();
        Array.Clear(_kWeighted);
        Array.Clear(_shelfLp);
        Array.Clear(_hpState);
        _kWrite = 0;
        _peak = 0;
    }
}
