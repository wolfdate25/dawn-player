namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Precomputed radix-2 complex FFT. One instance per transform size; tables are built once so the
/// steady-state transform path allocates nothing. Methods operate in place on separate real and
/// imaginary arrays. Not thread-safe — the convolver owns exactly one instance per render thread.
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;
    private readonly int[] _bitReverse;

    public int Size => _size;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two.");
        }

        _size = size;
        _cosTable = new float[size / 2];
        _sinTable = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cosTable[i] = MathF.Cos(2 * MathF.PI * i / size);
            _sinTable[i] = MathF.Sin(2 * MathF.PI * i / size);
        }

        _bitReverse = new int[size];
        int bits = BitCount(size) - 1;
        for (int i = 0; i < size; i++)
        {
            int rev = 0;
            for (int b = 0; b < bits; b++)
            {
                rev = (rev << 1) | ((i >> b) & 1);
            }
            _bitReverse[i] = rev;
        }
    }

    /// <summary>Forward transform (sign −1 convention).</summary>
    public void Forward(float[] re, float[] im) => Transform(re, im, inverse: false);

    /// <summary>Inverse transform, scaled by 1/N (so Forward→Inverse round-trips).</summary>
    public void Inverse(float[] re, float[] im)
    {
        Transform(re, im, inverse: true);
        float scale = 1f / _size;
        for (int i = 0; i < _size; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private void Transform(float[] re, float[] im, bool inverse)
    {
        int n = _size;
        for (int i = 0; i < n; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int tableStep = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    // Forward DFT uses e^(−iθ) = cos − i·sin; the inverse flips the sign.
                    float twiddleCos = _cosTable[k * tableStep];
                    float twiddleSin = inverse ? _sinTable[k * tableStep] : -_sinTable[k * tableStep];

                    int a = start + k;
                    int b = a + half;
                    float tr = re[b] * twiddleCos - im[b] * twiddleSin;
                    float ti = re[b] * twiddleSin + im[b] * twiddleCos;

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static int BitCount(int v)
    {
        int count = 0;
        while (v > 1)
        {
            v >>= 1;
            count++;
        }
        return count + 1;
    }
}
