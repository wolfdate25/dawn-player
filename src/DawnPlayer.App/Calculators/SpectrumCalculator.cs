using System;
using DawnPlayer.Core.Audio.Dsp;

namespace DawnPlayer.App.Calculators;

/// <summary>
/// Turns a mono sample window (from <see cref="SpectrumTapDspEffect"/>) into normalized
/// log-spaced spectrum levels for the now-playing bar. Pure logic, no UI dependency.
/// </summary>
public static class SpectrumCalculator
{
    public const int BinCount = 28;
    public const double MinHz = 30.0;
    public const double MaxHz = 16000.0;
    public const double FloorDb = -60.0;
    public const double CeilDb = 0.0;

    private static readonly int FftM = (int)Math.Log2(SpectrumTapDspEffect.WindowSamples);
    private static readonly int FftBins = SpectrumTapDspEffect.WindowSamples / 2;

    /// <summary>Geometric bin edges: edge[i]..edge[i+1] is bin i's frequency range.</summary>
    private static readonly double[] BinEdges = BuildEdges();

    private static double[] BuildEdges()
    {
        var edges = new double[BinCount + 1];
        for (int i = 0; i <= BinCount; i++)
        {
            edges[i] = MinHz * Math.Pow(MaxHz / MinHz, (double)i / BinCount);
        }
        return edges;
    }

    /// <summary>
    /// Computes <see cref="BinCount"/> levels in 0..1. Reuses <paramref name="levels"/> when
    /// provided (the caller renders per-tick and wants no allocations).
    /// </summary>
    public static double[] ComputeLevels(float[] window, int sampleRate, double[]? levels = null)
    {
        levels ??= new double[BinCount];
        // Seed in raw dB space. Do NOT normalize here: the bins accumulate raw dB peaks below
        // (e.g. −88 dB for silence), and comparing those against an already-normalized 0..1
        // array means no bin ever updates — every input then reads as full scale.
        for (int i = 0; i < levels.Length; i++) levels[i] = FloorDb;

        // The FFT size is fixed at WindowSamples; a short window would read past the array.
        // Normalize on this path too so invalid input reports flat zero like silence does.
        if (window.Length < SpectrumTapDspEffect.WindowSamples || sampleRate <= 0)
        {
            Normalize(levels);
            return levels;
        }

        int n = SpectrumTapDspEffect.WindowSamples;
        var data = new NAudio.Dsp.Complex[n];
        for (int i = 0; i < n; i++)
        {
            // Hann window (NAudio.Dsp.Complex stores float components).
            float w = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / (n - 1));
            data[i].X = window[i] * w;
            data[i].Y = 0f;
        }

        // NAudio's FFT(true, …) is the 1/N-normalized forward DFT (its source applies
        // scale = 1/n when forward — verified: a 0.5 DC input reads 0.5 at k=0, and a
        // 0.9-amplitude Hann-windowed tone reads ~0.4 at its bin, exactly the conventional
        // full-scale magnitude). The inverse direction FFT(false, …) is the same butterflies
        // WITHOUT the 1/n — 2048x larger here — so using it would saturate the dB mapping.
        NAudio.Dsp.FastFourierTransform.FFT(true, FftM, data);

        double hzPerBin = sampleRate / 2.0 / FftBins;
        for (int k = 1; k < FftBins; k++)
        {
            double hz = k * hzPerBin;
            if (hz < MinHz || hz > MaxHz) continue;

            int bin = FindBin(hz);
            if (bin < 0 || bin >= BinCount) continue;

            double magnitude = 2.0 * Math.Sqrt(data[k].X * data[k].X + data[k].Y * data[k].Y);
            double db = magnitude > 1e-9 ? 20.0 * Math.Log10(magnitude) : FloorDb;
            if (db > levels[bin]) levels[bin] = db;
        }

        Normalize(levels);
        return levels;
    }

    /// <summary>Maps raw accumulated dB peaks to the 0..1 display range.</summary>
    private static void Normalize(double[] levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = Math.Clamp((levels[i] - FloorDb) / (CeilDb - FloorDb), 0.0, 1.0);
        }
    }

    private static int FindBin(double hz)
    {
        int lo = 0, hi = BinCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (hz < BinEdges[mid]) hi = mid - 1;
            else if (hz >= BinEdges[mid + 1]) lo = mid + 1;
            else return mid;
        }
        return -1;
    }
}
