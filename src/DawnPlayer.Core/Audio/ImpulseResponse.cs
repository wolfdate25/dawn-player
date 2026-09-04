namespace DawnPlayer.Core.Audio;

/// <summary>
/// Loads a mono impulse response from any supported audio file (WAV/FLAC/MP3 — whatever the
/// decoder factory opens) for the convolver. Blocking; call off the render and UI threads.
/// </summary>
public static class ImpulseResponse
{
    /// <summary>Decodes a file to mono float samples. Null when unreadable.</summary>
    public static float[]? LoadMono(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var reader = AudioFileReaderFactory.Open(path);
            var fmt = reader.SourceFormat;
            int channels = Math.Max(1, fmt.Channels);

            var mono = new List<float>((int)Math.Min(reader.TotalTime.TotalSeconds * fmt.SampleRate, 44100 * 60));
            var buffer = new float[fmt.SampleRate * channels];
            int read;
            while ((read = reader.Samples.Read(buffer, 0, buffer.Length)) > 0)
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
            return mono.Count > 0 ? mono.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
