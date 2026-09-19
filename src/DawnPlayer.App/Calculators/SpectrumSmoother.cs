namespace DawnPlayer.App.Calculators;

/// <summary>
/// Analyzer-style envelope for spectrum bars: instant attack (a loud bin jumps to full height
/// immediately) and linear release (bars fall at a constant rate instead of teleporting down).
/// Reuses the caller's input array; allocates only its own state. Pure logic.
/// </summary>
public sealed class SpectrumSmoother
{
    private readonly double[] _smoothed;

    public SpectrumSmoother(int bins)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bins);
        _smoothed = new double[bins];
    }

    public int BinCount => _smoothed.Length;

    /// <summary>
    /// Folds the current frame <paramref name="levels"/> into the envelope. Rise is instant;
    /// fall decays by <paramref name="releasePerSecond"/> × <paramref name="dtSeconds"/>, never
    /// below the current level (so a sustained signal holds) and never above it.
    /// </summary>
    public void Apply(double[] levels, double releasePerSecond, double dtSeconds)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Length != _smoothed.Length)
            throw new ArgumentException($"levels must have {BinCount} bins", nameof(levels));
        ArgumentOutOfRangeException.ThrowIfNegative(releasePerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(dtSeconds);

        var decay = releasePerSecond * dtSeconds;
        for (var i = 0; i < _smoothed.Length; i++)
        {
            var current = levels[i];
            _smoothed[i] = current >= _smoothed[i]
                ? current
                : Math.Max(current, _smoothed[i] - decay);
        }
    }

    /// <summary>Copies the envelope out for rendering.</summary>
    public double[] CopyTo(double[] target)
    {
        Array.Copy(_smoothed, target, _smoothed.Length);
        return target;
    }

    /// <summary>Reduced-motion bypass: seed the envelope with the raw frame so the next Apply
    /// starts from it and the display never animates — it only ever shows current truth.</summary>
    public void Reset(double[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Length != _smoothed.Length)
            throw new ArgumentException($"levels must have {BinCount} bins", nameof(levels));
        Array.Copy(levels, _smoothed, _smoothed.Length);
    }
}
