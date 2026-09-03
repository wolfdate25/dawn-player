using System.Threading;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Pass-through analysis tap: mirrors the most recent post-processing samples (mono-mixed) for
/// the UI spectrum. The render thread fills a single window buffer without locking; the UI copies
/// it out whenever it wants a frame. A torn window is possible while the render thread is
/// mid-copy — harmless here, it is a visualizer, and the worst case is one odd-looking frame.
/// Exposes a publish version so the UI can distinguish "signal unchanged" (paused) from a new
/// window and run its decay animation without recomputing.
/// </summary>
public sealed class SpectrumTapDspEffect : IAudioDspEffect
{
    public const int WindowSamples = 2048;

    private readonly float[] _window = new float[WindowSamples];
    private int _channels = 2;
    private long _version;

    public string Name => "SpectrumTap";
    public bool IsEnabled { get; set; } = true;

    /// <summary>Increments on every published window; unchanged means silence or pause.</summary>
    public long Version => Volatile.Read(ref _version);

    public void Initialize(int sampleRate, int channels)
    {
        if (channels > 0) _channels = channels;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        int channels = _channels;
        if (channels <= 0) return;

        // Mono mix into the window with wraparound; the previous window simply ages out.
        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = offset + f * channels;
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += buffer[baseIdx + c];
            _window[_pos] = sum / channels;
            _pos = (_pos + 1) & (WindowSamples - 1);
        }

        _version++;
    }

    private int _pos;

    /// <summary>Copies the current window, oldest sample first. Returns the window's version.</summary>
    public long CopyTo(float[] destination)
    {
        var w = _window;
        int split = _pos;
        int tail = WindowSamples - split;
        System.Array.Copy(w, split, destination, 0, tail);
        System.Array.Copy(w, 0, destination, tail, split);
        return Volatile.Read(ref _version);
    }

    public void Reset()
    {
        // A window of the previous track decays out naturally; nothing to clear.
    }
}
