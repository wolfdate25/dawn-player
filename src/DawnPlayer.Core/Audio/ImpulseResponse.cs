namespace DawnPlayer.Core.Audio;

/// <summary>
/// Loads a mono impulse response from any supported audio file (WAV/FLAC/MP3 — whatever the
/// decoder factory opens) for the convolver. Blocking; call off the render and UI threads.
/// </summary>
public static class ImpulseResponse
{
    /// <summary>Decoded impulse plus the sample rate it was decoded at. The convolver partitions
    /// are rate-specific: applying a 44.1 kHz IR in a 48 kHz session stretched the whole filter
    /// ~8.8% in time (every spectral feature slid down) with no warning.</summary>
    public sealed record LoadedImpulse(float[] Samples, int SampleRate, bool Resampled, bool Truncated);

    /// <summary>IRs longer than this are cut (matching the convolver's own partition budget) and
    /// reported so the truncation is not silent.</summary>
    public const double MaxSeconds = 2.0;

    /// <summary>Decodes a file to mono float samples at the given target rate (resampling when
    /// the file's own rate differs). Null when unreadable.</summary>
    public static LoadedImpulse? LoadMono(string path, int targetSampleRate)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var reader = AudioFileReaderFactory.Open(path);
            var fmt = reader.SourceFormat;
            int channels = Math.Max(1, fmt.Channels);
            bool resampled = fmt.SampleRate != targetSampleRate;

            var mono = new List<float>((int)Math.Min(reader.TotalTime.TotalSeconds * targetSampleRate, 44100 * 60));
            var buffer = new float[fmt.SampleRate * channels];
            int read;
            while ((read = reader.Samples.Read(buffer)) > 0)
            {
                int frames = read / channels;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++) sum += buffer[baseIdx + c];
                    mono.Add(sum / channels);
                }
            }
            if (mono.Count == 0) return null;

            var samples = mono.ToArray();
            if (resampled)
            {
                samples = ResampleLinear(samples, fmt.SampleRate, targetSampleRate);
            }

            bool truncated = false;
            int maxSamples = (int)(MaxSeconds * targetSampleRate);
            if (samples.Length > maxSamples)
            {
                Array.Resize(ref samples, maxSamples);
                truncated = true;
            }

            return new LoadedImpulse(samples, targetSampleRate, resampled, truncated);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Back-compat overload: decodes at the FILE's own rate (no resample target known).</summary>
    public static float[]? LoadMono(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var probe = AudioFileReaderFactory.Open(path);
            var rate = probe.SourceFormat.SampleRate;
        }
        catch { }
        return LoadMono(path, 44100)?.Samples;
    }

    /// <summary>
    /// Linear-interpolation resampler for IR correction curves. IRs are smooth energy-decay
    /// shapes (not tone sources), so the HF error of linear interpolation lands well below what
    /// a room/headphone correction expresses; a windowed-sinc here would cost more than the
    /// correction gains.
    /// </summary>
    public static float[] ResampleLinear(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0 || fromRate <= 0 || toRate <= 0) return input;
        double ratio = (double)toRate / fromRate;
        var output = new float[(int)((long)input.Length * ratio)];
        if (output.Length == 0) return input;
        for (int i = 0; i < output.Length; i++)
        {
            double srcPos = i / ratio;
            int i0 = (int)srcPos;
            double frac = srcPos - i0;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            if (i0 >= input.Length) i0 = input.Length - 1;
            output[i] = (float)(input[i0] * (1.0 - frac) + input[i1] * frac);
        }
        return output;
    }
}
