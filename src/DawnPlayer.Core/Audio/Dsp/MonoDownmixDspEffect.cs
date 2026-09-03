namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Folds every channel to the average signal — a mono check (phase problems jump out) and a
/// single-ear listening aid. Stateless; the layout is captured at Initialize time, and layouts
/// below two channels pass through (there is nothing to fold).
/// </summary>
public sealed class MonoDownmixDspEffect : IAudioDspEffect
{
    private int _channels = 2;

    public string Name => "MonoDownmix";
    public bool IsEnabled { get; set; }

    public MonoDownmixDspEffect(bool initialEnabled = false)
    {
        IsEnabled = initialEnabled;
    }

    /// <summary>Live toggle; there are no coefficients to rebuild.</summary>
    public void ApplySettings(bool enabled) => IsEnabled = enabled;

    public void Initialize(int sampleRate, int channels)
    {
        if (channels > 0) _channels = channels;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        int channels = _channels;
        if (channels < 2) return;

        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = offset + f * channels;
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += buffer[baseIdx + c];
            float avg = sum / channels;
            for (int c = 0; c < channels; c++) buffer[baseIdx + c] = avg;
        }
    }

    public void Reset()
    {
        // Stateless.
    }
}
