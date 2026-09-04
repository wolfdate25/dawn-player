using System.Threading;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Uniform-partition convolution (overlap-save) over a mono impulse response applied to every
/// channel. The impulse is prepared off-thread into immutable partition spectra and swapped in by
/// reference; the render path only touches preallocated buffers, so steady-state processing is
/// allocation-free. Adds one <see cref="BlockSamples"/>-frame block of latency, which the input
/// FIFO covers by passing dry samples through until the first convolved block is ready.
/// </summary>
public sealed class ConvolutionDspEffect : IAudioDspEffect
{
    public const string EffectName = "Convolution";
    public const int BlockSamples = 512;
    private const int OutputRingFrames = 2 * BlockSamples;

    /// <summary>IRs longer than this are truncated; 2 s covers headphone-to-room use cases.</summary>
    public const int MaxImpulseSeconds = 2;

    string IAudioDspEffect.Name => EffectName;

    public bool IsEnabled { get; set; }

    private int _sampleRate = 44100;
    private int _channels = 2;

    /// <summary>Immutable prepared IR: frequency-domain partitions of the zero-padded impulse,
    /// peak-normalized. Built off-thread, swapped in by volatile reference.</summary>
    private sealed record PreparedIr(int Partitions, float[][] PartitionRe, float[][] PartitionIm);

    private PreparedIr? _pendingIr;
    private PreparedIr? _activeIr;

    // Render-side state (built in Initialize; used only by the render thread).
    private Fft? _fft;
    private float[]? _inputFifo;   // [frame, channel] incoming frames awaiting a full block
    private float[]? _frameIn;     // per-frame capture (emit overwrites the caller's buffer)
    private int _fifoCount;
    private float[]? _outputFifo;  // [frame, channel] convolved samples, read-side latency line
    private int _outCount;
    private int _outRead;
    private float[]? _historyRe;   // [slot, channel, bin] past input-block spectra ring
    private float[]? _historyIm;
    private int _slots;
    private int _historyPos;
    private int _filledSlots;

    private readonly float[] _scratchRe = new float[2 * BlockSamples];
    private readonly float[] _scratchIm = new float[2 * BlockSamples];
    private readonly float[] _accRe = new float[2 * BlockSamples];
    private readonly float[] _accIm = new float[2 * BlockSamples];

    /// <summary>Last block's spectral sum (per channel, samples B..2B) — the "previous block"
    /// term of the partition sum. See ProcessBlock.</summary>
    private float[]? _prevAcc;

    /// <summary>
    /// Prepares and stages a mono impulse (at the current rate, up to <see cref="MaxImpulseSeconds"/>
    /// worth of samples). Safe from any thread; takes effect on the next processed block after
    /// <see cref="AdoptPendingIr"/>. Peak-normalized to 0.5 so extreme IRs stay inside the chain's
    /// limiter headroom. Null clears the impulse (bypass).
    /// </summary>
    public void SetImpulse(float[]? monoImpulse)
    {
        if (monoImpulse == null || monoImpulse.Length == 0)
        {
            _pendingIr = null;
            return;
        }

        int maxLen = _sampleRate * MaxImpulseSeconds;
        int irLen = Math.Min(monoImpulse.Length, maxLen);

        float peak = 0;
        for (int i = 0; i < irLen; i++)
        {
            float a = MathF.Abs(monoImpulse[i]);
            if (a > peak) peak = a;
        }
        float gain = peak > 1e-6f ? 0.5f / peak : 0;

        int partitions = (irLen + BlockSamples - 1) / BlockSamples;
        var fft = new Fft(2 * BlockSamples);
        var reParts = new float[partitions][];
        var imParts = new float[partitions][];

        for (int p = 0; p < partitions; p++)
        {
            var re = new float[2 * BlockSamples];
            var im = new float[2 * BlockSamples];
            int from = p * BlockSamples;
            int len = Math.Min(BlockSamples, irLen - from);
            for (int i = 0; i < len; i++)
            {
                re[i] = monoImpulse[from + i] * gain;
            }
            fft.Forward(re, im);
            reParts[p] = re;
            imParts[p] = im;
        }

        Volatile.Write(ref _pendingIr, new PreparedIr(partitions, reParts, imParts));
    }

    /// <summary>Clears the staged and active impulse (bypass).</summary>
    public void ClearImpulse() => Volatile.Write(ref _pendingIr, null);

    /// <summary>True when an impulse is staged or active — the soft limiter must stay armed.</summary>
    public bool HasImpulse => Volatile.Read(ref _pendingIr) != null || _activeIr != null;

    public void Initialize(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0) return;
        if (_sampleRate == sampleRate && _channels == channels && _fft != null) return;

        _sampleRate = sampleRate;
        _channels = channels;
        _fft = new Fft(2 * BlockSamples);

        _slots = _sampleRate * MaxImpulseSeconds / BlockSamples + 1;
        _inputFifo = new float[channels * BlockSamples];
        _outputFifo = new float[channels * OutputRingFrames];
        _prevAcc = new float[channels * BlockSamples];
        _frameIn = new float[channels];
        _historyRe = new float[_slots * channels * 2 * BlockSamples];
        _historyIm = new float[_slots * channels * 2 * BlockSamples];
        ClearState();

        // The prepared IR was built for the previous rate; a format change invalidates it.
        Volatile.Write(ref _pendingIr, null);
        _activeIr = null;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        var fft = _fft;
        if (!IsEnabled || fft == null || _inputFifo == null || _outputFifo == null || buffer == null || count <= 0)
        {
            return;
        }

        AdoptPendingIr();
        var ir = _activeIr;
        if (ir == null) return;

        int channels = _channels;
        int frames = count / channels;

        for (int f = 0; f < frames; f++)
        {
            // Capture the frame's input BEFORE the emit below overwrites the buffer slot —
            // ingesting after the emit would feed the convolver its own output.
            int inIdx = _fifoCount * channels;
            for (int c = 0; c < channels; c++)
            {
                _frameIn![c] = buffer[offset + f * channels + c];
            }

            // Emit first, ingest second: y[k] becomes readable at frame k + BlockSamples —
            // exactly one block of latency, never interleaved mid-block.
            if (_outRead < _outCount)
            {
                int outIdx = (_outRead % OutputRingFrames) * channels;
                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + f * channels + c] = _outputFifo[outIdx + c];
                }
                _outRead++;
            }

            for (int c = 0; c < channels; c++)
            {
                _inputFifo[inIdx + c] = _frameIn![c];
            }
            _fifoCount++;

            if (_fifoCount == BlockSamples)
            {
                ProcessBlock(fft, ir);
                _fifoCount = 0;
            }
        }
    }

    private void ProcessBlock(Fft fft, PreparedIr ir)
    {
        int channels = _channels;
        var fifo = _inputFifo!;
        int bins = 2 * BlockSamples;
        int slots = _slots;

        var re = _scratchRe;
        var im = _scratchIm;
        var accRe = _accRe;
        var accIm = _accIm;
        var prevAcc = _prevAcc!;

        for (int c = 0; c < channels; c++)
        {
            // Spectrum of this channel's input block → history ring.
            Array.Clear(re);
            Array.Clear(im);
            for (int i = 0; i < BlockSamples; i++) re[i] = fifo[i * channels + c];
            fft.Forward(re, im);

            int writeSlot = _historyPos;
            int writeBase = (writeSlot * channels + c) * bins;
            Array.Copy(re, 0, _historyRe!, writeBase, bins);
            Array.Copy(im, 0, _historyIm!, writeBase, bins);

            // Partition sum over the full 2B inverse transform. Slot 0 is the block written
            // just above, so the usable count is _filledSlots + 1 (first block convolves itself).
            Array.Clear(accRe);
            Array.Clear(accIm);
            int maxP = Math.Min(ir.Partitions, _filledSlots + 1);
            for (int p = 0; p < maxP; p++)
            {
                int histSlot = ((writeSlot - p) % slots + slots) % slots;
                int baseIdx = (histSlot * channels + c) * bins;
                float[] irRe = ir.PartitionRe[p];
                float[] irIm = ir.PartitionIm[p];
                for (int b = 0; b < bins; b++)
                {
                    float hr = _historyRe![baseIdx + b];
                    float hi = _historyIm![baseIdx + b];
                    accRe[b] += hr * irRe[b] - hi * irIm[b];
                    accIm[b] += hr * irIm[b] + hi * irRe[b];
                }
            }

            fft.Inverse(accRe, accIm);

            // Output sample y[i] folds each partition's j>i terms into the PREVIOUS block's
            // inverse: y[i] = S[i] + S_prev[B+i], where S is this block's full 2B sum and
            // S_prev the last block's. (Circular-convolution wraparound; taking only one half
            // of S is the classic overlap-save error that silences the convolver.)
            if (_outCount - _outRead >= OutputRingFrames) return; // consumer starved
            int writeFrame = _outCount % OutputRingFrames;
            for (int i = 0; i < BlockSamples; i++)
            {
                _outputFifo![(writeFrame + i) * channels + c] = accRe[i] + prevAcc[c * BlockSamples + i];
            }
        }

        // Save this block's second half for the next one, then commit the produced block.
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < BlockSamples; i++)
            {
                prevAcc[c * BlockSamples + i] = accRe[BlockSamples + i];
            }
        }
        _outCount += BlockSamples;
        _historyPos = (_historyPos + 1) % slots;
        if (_filledSlots < slots) _filledSlots++;
    }

    public void Reset() => ClearState();

    private void ClearState()
    {
        _fifoCount = 0;
        _outCount = 0;
        _outRead = 0;
        _filledSlots = 0;
        _historyPos = 0;
        if (_outputFifo != null) Array.Clear(_outputFifo);
        if (_prevAcc != null) Array.Clear(_prevAcc);
        if (_historyRe != null) Array.Clear(_historyRe);
        if (_historyIm != null) Array.Clear(_historyIm);
    }

    private void AdoptPendingIr()
    {
        var pending = Volatile.Read(ref _pendingIr);
        if (!ReferenceEquals(pending, _activeIr))
        {
            _activeIr = pending;
            ClearState();
        }
    }
}
