using System.Collections.Generic;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Manages and executes an ordered chain of audio DSP effects.
/// </summary>
public interface IAudioDspChain
{
    /// <summary>Snapshot of all effects currently in the processing chain.</summary>
    IReadOnlyList<IAudioDspEffect> Effects { get; }

    /// <summary>Appends an effect to the end of the DSP chain.</summary>
    void AddEffect(IAudioDspEffect effect);

    /// <summary>Removes an effect by name from the chain.</summary>
    void RemoveEffect(string name);

    /// <summary>Retrieves the first effect matching type <typeparamref name="T"/>, or null if not found.</summary>
    T? GetEffect<T>() where T : class, IAudioDspEffect;

    /// <summary>Initializes all effects in the chain with the specified audio format.</summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>Processes samples sequentially through all enabled effects. Must be lock-free and zero-allocation.</summary>
    void Process(float[] buffer, int offset, int count);

    /// <summary>Resets internal state for all effects in the chain.</summary>
    void Reset();
}
