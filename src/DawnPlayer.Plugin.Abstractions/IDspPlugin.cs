namespace DawnPlayer.Plugins;

/// <summary>
/// A DSP effect plugin. Implementations provide one audio effect that the player inserts at the
/// end of its real-time DSP chain (after EQ / normalizer / crossfeed / convolution, before the
/// soft limiter).
/// </summary>
/// <remarks>
/// <see cref="CreateEffect"/> may be called multiple times (once per output format / session) and
/// must return independent instances. Instances are used exclusively by the real-time render
/// thread: <see cref="IDspEffectInstance.Process"/> must not allocate heap memory or take locks in
/// steady state.
/// </remarks>
public interface IDspPlugin
{
    /// <summary>Stable unique identifier, e.g. "mono-sample". Never change after release.</summary>
    string Id { get; }

    /// <summary>Display name shown in the UI, e.g. "Mono Downmix (Sample)".</summary>
    string DisplayName { get; }

    /// <summary>Creates a fresh effect instance for one playback session.</summary>
    IDspEffectInstance CreateEffect();
}

/// <summary>
/// One live DSP effect instance. The contract mirrors the player's built-in effect processor.
/// </summary>
public interface IDspEffectInstance
{
    /// <summary>Called when audio format initializes or sample rate / channel count changes.</summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>Processes interleaved 32-bit float audio samples in place, count samples long.</summary>
    void Process(float[] buffer, int offset, int count);

    /// <summary>Clears internal state (seek, track boundary).</summary>
    void Reset();
}

/// <summary>
/// Marks a class as a Dawn Player DSP plugin and carries the metadata the host shows. Apply it to
/// exactly one class implementing <see cref="IDspPlugin"/> with a public parameterless constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DspPluginAttribute : Attribute
{
    /// <param name="id">Stable unique identifier, e.g. "mono-sample".</param>
    /// <param name="name">Display name shown in the UI.</param>
    /// <param name="version">Plugin version, e.g. "1.0.0".</param>
    /// <param name="author">Plugin author shown in the UI.</param>
    public DspPluginAttribute(string id, string name, string version, string author)
    {
        Id = id;
        Name = name;
        Version = version;
        Author = author;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Author { get; }
}
