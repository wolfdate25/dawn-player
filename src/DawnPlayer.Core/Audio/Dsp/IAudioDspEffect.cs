namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Uniform contract for real-time audio DSP effect processors operating on interleaved 32-bit float samples.
/// </summary>
public interface IAudioDspEffect
{
    /// <summary>Unique name or identifier for the DSP effect.</summary>
    string Name { get; }

    /// <summary>Gets or sets whether the effect actively processes audio samples. When false, input is passed through unmodified.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Called when audio format initializes or sample rate / channel count changes.</summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>Processes interleaved 32-bit float audio samples in-place. Must not allocate heap memory in steady state.</summary>
    void Process(float[] buffer, int offset, int count);

    /// <summary>Clears internal filter history, delay lines, and accumulators (e.g. on seek or track boundary).</summary>
    void Reset();
}
