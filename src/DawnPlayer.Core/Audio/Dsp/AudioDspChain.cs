using System;
using System.Collections.Generic;
using System.Threading;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Thread-safe audio DSP processing chain supporting dynamic effect manipulation via
/// copy-on-write snapshot publication. Audio rendering in <see cref="Process"/> is 100% lock-free
/// and zero-allocation in steady state.
/// </summary>
public sealed class AudioDspChain : IAudioDspChain
{
    private readonly object _mutationLock = new();
    private readonly List<IAudioDspEffect> _effects = new();
    private IAudioDspEffect[] _snapshot = Array.Empty<IAudioDspEffect>();

    private int _sampleRate;
    private volatile int _channels;

    /// <summary>
    /// Gets a thread-safe snapshot of all effects currently configured in the chain.
    /// </summary>
    public IReadOnlyList<IAudioDspEffect> Effects => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Appends an effect to the end of the DSP processing pipeline.
    /// </summary>
    public void AddEffect(IAudioDspEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        lock (_mutationLock)
        {
            if (_sampleRate > 0 && _channels > 0)
            {
                effect.Initialize(_sampleRate, _channels);
            }

            _effects.Add(effect);
            Volatile.Write(ref _snapshot, _effects.ToArray());
        }
    }

    /// <summary>
    /// Inserts an effect at the specified index in the DSP chain.
    /// </summary>
    public void InsertEffect(int index, IAudioDspEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        lock (_mutationLock)
        {
            if (_sampleRate > 0 && _channels > 0)
            {
                effect.Initialize(_sampleRate, _channels);
            }

            index = Math.Clamp(index, 0, _effects.Count);
            _effects.Insert(index, effect);
            Volatile.Write(ref _snapshot, _effects.ToArray());
        }
    }

    /// <summary>
    /// Removes the first effect matching the specified name from the chain.
    /// </summary>
    public void RemoveEffect(string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        lock (_mutationLock)
        {
            int index = _effects.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _effects.RemoveAt(index);
                Volatile.Write(ref _snapshot, _effects.ToArray());
            }
        }
    }

    /// <summary>
    /// Removes a specific effect instance from the chain.
    /// </summary>
    public bool RemoveEffect(IAudioDspEffect effect)
    {
        if (effect == null) return false;

        lock (_mutationLock)
        {
            if (_effects.Remove(effect))
            {
                Volatile.Write(ref _snapshot, _effects.ToArray());
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes all effects from the chain.
    /// </summary>
    public void Clear()
    {
        lock (_mutationLock)
        {
            _effects.Clear();
            Volatile.Write(ref _snapshot, Array.Empty<IAudioDspEffect>());
        }
    }

    /// <summary>
    /// Retrieves the first effect matching the requested type, or null if not found.
    /// </summary>
    public T? GetEffect<T>() where T : class, IAudioDspEffect
    {
        var snapshot = Volatile.Read(ref _snapshot);
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i] is T match)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>
    /// Initializes all effects in the chain with the specified audio sample rate and channel count.
    /// </summary>
    public void Initialize(int sampleRate, int channels)
    {
        lock (_mutationLock)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            var snapshot = _snapshot;
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].Initialize(sampleRate, channels);
            }
        }
    }

    /// <summary>
    /// Executes the DSP processing pipeline sequentially across all active effects.
    /// Operates lock-free on an immutable snapshot reference with zero heap allocation.
    /// </summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer == null || count <= 0) return;

        // The chain consumes whole frames. Truncating here rather than in each effect is what
        // keeps the stages in step: they used to disagree about the trailing partial frame, so
        // the equalizer passed it through unfiltered while the normalizer scaled it.
        int channels = _channels;
        if (channels > 1)
        {
            count -= count % channels;
            if (count <= 0) return;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        for (int i = 0; i < snapshot.Length; i++)
        {
            var effect = snapshot[i];
            if (effect.IsEnabled)
            {
                effect.Process(buffer, offset, count);
            }
        }
    }

    /// <summary>
    /// Resets internal state for all effects in the chain (e.g. on seek or track change).
    /// </summary>
    public void Reset()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i].Reset();
        }
    }
}
