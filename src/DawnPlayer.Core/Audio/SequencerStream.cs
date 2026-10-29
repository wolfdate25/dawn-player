using System;
using System.Threading;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DawnPlayer.Core.Audio;

public sealed class PendingTrack
{
    public required Playlist Playlist { get; init; }
    public required PlaylistItem Item { get; init; }
    public required ITrackReader Reader { get; init; }
    /// <summary>Initial position for this track (device/mode switch restarts);
    /// applied atomically with the switch so no audio leaks from position zero.</summary>
    public TimeSpan? StartPosition { get; init; }
    /// <summary>True when the next track needs a different output format in exclusive
    /// mode → the session must be rebuilt at the track boundary instead of chaining.</summary>
    public bool RequiresRestart { get; init; }
}

public enum SequencerEndReason
{
    /// <summary>All sources drained, nothing left to play.</summary>
    NaturalEnd,
    /// <summary>Next track needs a device re-open (exclusive format change).</summary>
    FormatChange
}

/// <summary>
/// Feeds a continuous stream of consecutive tracks to WASAPI so that track
/// boundaries are sample-accurate (gapless). Track transitions, seeking and
/// switching are serialized by <see cref="_gate"/>; events fire on the audio thread.
/// Audio DSP processing is decoupled and routed through <see cref="IAudioDspChain"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locking.</b> <see cref="_gate"/> is held across a whole render pass, which includes decoder
/// I/O, so anything a UI thread calls at interactive rates must not take it. Position, duration,
/// current item and gain are therefore published as volatile state and read without locking, and
/// the prefetch handoff has its own small <see cref="_prefetchLock"/>.
/// </para>
/// <para>
/// Lock order, where both are needed: <see cref="_gate"/> then <see cref="_prefetchLock"/>. Never
/// the reverse.
/// </para>
/// </remarks>
public sealed class SequencerStream : IWaveProvider
{
    private readonly object _gate = new();
    private readonly WaveFormat _outFormat;
    private readonly bool _applyVolume;
    // DoP containers ride the pipeline as opaque 24-bit words: volume or DSP arithmetic would
    // corrupt the 0x05/0xFA markers the DAC resyncs on, so both are skipped entirely.
    private readonly bool _rawPassthrough;
    // Supplies the PRE-CHAIN ReplayGain node gain: source correction only, boost-capable and
    // therefore limiter-protected. The master fader (listening volume) is applied AFTER the DSP
    // chain — see _masterGain — so the AGC can never see, or compensate away, the volume slider.
    private readonly Func<Track, float> _replayGainNodeGainProvider;
    private readonly Func<Track, float?>? _replayGainProvider;
    private readonly int _latencyBytes;
    private readonly IAudioDspChain _dspChain;

    /// <summary>
    /// A track with its sample-provider graph already built. Building the graph allocates
    /// (resampler and channel-converter buffers), so it happens on the thread that queues the
    /// track, never on the render thread at a gapless boundary.
    /// </summary>
    private sealed class PreparedTrack
    {
        public required PendingTrack Track { get; init; }
        public required ISampleProvider Source { get; init; }
        public VolumeSampleProvider? ReplayGainNode { get; init; }
        /// <summary>The gain the ReplayGain node was built with — carried so the boundary swap
        /// (under the gate) can refresh limiter arming without dereferencing the node.</summary>
        public float ReplayGainNodeGain { get; init; } = 1f;
    }

    /// <summary>The per-track facts a UI thread polls. Immutable, published as one reference.</summary>
    private sealed record TrackFacts(PlaylistItem Item, TimeSpan TotalTime);

    private readonly object _prefetchLock = new();

    // Readers retired from the prefetch slot. Disposing them on the thread that invalidated the
    // prefetch raced the render thread's crossfade read — that read copies the reference out of
    // _prefetchLock, releases the lock, and only then decodes, so a concurrent Dispose could free
    // a native decoder underneath it (AccessViolation on MediaFoundation/Vorbis readers).
    // Retirement only parks readers here; actual disposal happens where no render pass can be
    // reading: the render thread itself at the top of Read, or any caller already holding _gate
    // (SwitchTo/Cancel), which by definition waits out any in-flight pass.
    private readonly List<PreparedTrack> _retired = new();

    private PreparedTrack? _current;
    private PreparedTrack? _prefetched;
    private ISampleProvider? _sourceProvider;
    private VolumeSampleProvider? _replayGainNode;
    private TrackFacts? _facts;
    private float[] _floatBuf = Array.Empty<float>();
    private long _bytesServed;
    private bool _trackStartedFired;
    private bool _endFired;
    // Master fader, applied AFTER the DSP chain in Read: attenuation-only, so it can never push
    // the signal over full scale and processing stages never see (or compensate) the slider.
    // Plain float write from the UI thread / read on the audio thread: 32-bit accesses are
    // atomic on all supported runtimes; worst case is one block of the previous value.
    private float _masterGain = 1f;
    // Current ReplayGain node gain — mirrors _current's node so SyncLimiterEnabled can decide
    // arming without chasing the node reference. Written under _gate at the boundary.
    private float _nodeGain = 1f;

    private double _crossfadeDurationSeconds;
    /// <summary>
    /// Configured crossfade duration between consecutive tracks in seconds (0.0 to 10.0s).
    /// Value of 0 disables crossfade and uses pure gapless sequencing.
    /// </summary>
    public double CrossfadeDurationSeconds
    {
        get => _crossfadeDurationSeconds;
        set => _crossfadeDurationSeconds = Math.Clamp(value, 0.0, 10.0);
    }

    private float[] _crossfadeNextBuf = Array.Empty<float>();
    private bool _inCrossfade;
    private long _crossfadeTotalFrames;
    private long _crossfadeElapsedFrames;
    // Post-abort gain recovery: an interrupted fade left the current track partway down its
    // fade-out curve; restoring unity in one block was an audible step. Ramp back instead.
    private bool _fadeRecovering;
    private float _fadeRecoverStartGain = 1f;
    private long _fadeRecoverTotalFrames;
    private long _fadeRecoverElapsedFrames;

    public SequencerStream(
        WaveFormat outFormat,
        bool applyVolume,
        Func<Track, float> replayGainNodeGainProvider,
        int latencyMs,
        EqProfile? initialEqProfile = null,
        NormalizerSettings? initialNormalizerSettings = null,
        Func<Track, float?>? replayGainProvider = null,
        CrossfeedSettings? initialCrossfeed = null,
        bool initialMonoDownmix = false,
        IAudioDspChain? dspChain = null,
        global::DawnPlayer.Core.Audio.Dsp.Plugins.PluginDspEffect? pluginDsp = null,
        bool rawPassthrough = false,
        float initialMasterGain = 1f)
    {
        _rawPassthrough = rawPassthrough;
        _outFormat = outFormat;
        _applyVolume = applyVolume;
        _replayGainNodeGainProvider = replayGainNodeGainProvider;
        _replayGainProvider = replayGainProvider;
        _masterGain = Math.Clamp(initialMasterGain, 0f, 1f);
        _latencyBytes = (int)(outFormat.AverageBytesPerSecond * latencyMs / 1000.0);

        if (dspChain != null)
        {
            _dspChain = dspChain;
        }
        else
        {
            var defaultChain = new AudioDspChain();
            defaultChain.AddEffect(new EqualizerDspEffect(initialEqProfile));
            defaultChain.AddEffect(new DynamicNormalizerDspEffect(initialNormalizerSettings));
            defaultChain.AddEffect(new CrossfeedDspEffect(initialCrossfeed));
            defaultChain.AddEffect(new MonoDownmixDspEffect(initialMonoDownmix));
            defaultChain.AddEffect(new ConvolutionDspEffect());
            if (pluginDsp != null) defaultChain.AddEffect(pluginDsp);
            defaultChain.AddEffect(new SoftLimiterDspEffect(0.90f));
            defaultChain.AddEffect(new SpectrumTapDspEffect());
            _dspChain = defaultChain;
        }

        _dspChain.Initialize(_outFormat.SampleRate, _outFormat.Channels);
        SyncLimiterEnabled();
    }

    public WaveFormat WaveFormat => _outFormat;

    /// <summary>Accesses the active audio DSP chain for dynamic node manipulation.</summary>
    public IAudioDspChain DspChain => _dspChain;

    /// <summary>True when playback is paused; causes Read() to emit silence without advancing track position.</summary>
    /// <remarks>Volatile fields, not properties: these flags are set from the UI thread and polled
    /// on the real-time audio thread, and <c>volatile</c> only applies to fields.</remarks>
#pragma warning disable CA1051
    public volatile bool IsPaused;

    /// <summary>True while the controller is opening the next reader.</summary>
    public volatile bool PrefetchPending;

    /// <summary>
    /// A-B repeat window in output-format bytes; either being 0 disables the loop. Set from the UI
    /// thread via <see cref="PlaybackController"/>, enforced on the audio thread inside the render
    /// pass. Published with <see cref="Volatile"/> rather than <c>volatile</c> because C# does not
    /// allow the modifier on <see langword="long"/> — the same treatment <see cref="_bytesServed"/>
    /// gets. Cleared on every track change: the window is per-track, not per-session.
    /// </summary>
    public long AbLoopStartBytes;
    public long AbLoopEndBytes;
#pragma warning restore CA1051

    public bool HasPrefetched { get { lock (_prefetchLock) return _prefetched != null; } }

    /// <summary>Fires on the audio thread when the first samples of a track leave the sequencer.</summary>
    public event Action<PendingTrack>? TrackStarted;

    /// <summary>
    /// Fires on the audio thread when the sequence cannot continue (see reasons). The argument is
    /// the item that just drained, captured before the sequencer clears it — the controller needs
    /// it to attribute the end to the right track without racing the ThreadPool.
    /// </summary>
    public event Action<SequencerEndReason, PlaylistItem?>? SequenceEnded;

    public event Action<Exception>? ReadError;

    public PlaylistItem? CurrentItem => Volatile.Read(ref _facts)?.Item;

    public TimeSpan TotalTime => Volatile.Read(ref _facts)?.TotalTime ?? TimeSpan.Zero;

    /// <summary>
    /// Estimated audible position of the current track (compensated for device latency).
    /// Lock-free: the seekbar polls this several times a second and must never wait behind a decode.
    /// </summary>
    public TimeSpan GetPosition()
    {
        var facts = Volatile.Read(ref _facts);
        if (facts == null) return TimeSpan.Zero;

        var pos = RawPosition();
        return pos > facts.TotalTime ? facts.TotalTime : pos;
    }

    public TimeSpan RemainingTime
    {
        get
        {
            var facts = Volatile.Read(ref _facts);
            if (facts == null) return TimeSpan.Zero;
            return facts.TotalTime - RawPosition();
        }
    }

    private TimeSpan RawPosition()
    {
        long served = Volatile.Read(ref _bytesServed);
        var pos = TimeSpan.FromSeconds((double)served / _outFormat.AverageBytesPerSecond)
                  - TimeSpan.FromSeconds((double)_latencyBytes / _outFormat.AverageBytesPerSecond);
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate) SeekLocked(position);
    }

    private void SeekLocked(TimeSpan position)
    {
        if (_current == null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        var total = _current.Track.Reader.TotalTime;
        if (position > total) position = total;
        _current.Track.Reader.CurrentTime = position;

        // Trust the decoder over the request. A decoder that cannot seek (a live radio stream's
        // reconnect) or one that clamps reports where it actually is; publishing the requested
        // position anyway made the seekbar flow from a stale offset while the audio served from
        // somewhere else — the output-settings restart mismatch.
        var actual = _current.Track.Reader.CurrentTime;
        if (actual >= TimeSpan.Zero)
        {
            position = actual;
        }

        Volatile.Write(ref _bytesServed, MfTrackReader.TimeToBytes(_outFormat, position));
        // 시크로 페이드를 취소하면 다음 트랙 소스는 이미 일부 소비된 상태다. 되감기지 않으면
        // (크로스페이드를 끈 뒤의) 갭리스 전환에서 다음 트랙 앞부분이 통째로 건너뛰어진다.
        if (_inCrossfade)
        {
            lock (_prefetchLock)
            {
                RewindAbortedCrossfadeSource(_prefetched?.Track);
                BeginCrossfadeAbortLocked();
            }
        }
        // The normalizer's converged gain stays valid across a seek within the track; resetting
        // it made the volume pump after every scrub. Delay-line/convolution/limiter state IS
        // stale after a jump and must clear.
        _dspChain.ResetForSeek();
    }

    /// <summary>Hard switch to another track within the same session (same format).</summary>
    public void SwitchTo(PendingTrack next)
    {
        // Built before taking the gate: a user-initiated switch is not latency-critical, but there
        // is no reason to hold the render lock across the graph construction either.
        var prepared = Prepare(next);

        DiscardPrefetched();

        lock (_gate)
        {
            var old = _current;
            SetCurrentLocked(prepared);
            if (next.StartPosition is { } start) SeekLocked(start);
            old?.Track.Reader.Dispose();
            DrainRetiredLocked();
        }
    }

    /// <summary>
    /// Queues the next track. The sample-provider graph is built here, on the caller's thread, so
    /// the gapless boundary inside <see cref="Read"/> only has to swap references.
    /// </summary>
    public void SetPrefetched(PendingTrack? track)
    {
        var prepared = track == null ? null : Prepare(track);

        PreparedTrack? previous;
        lock (_prefetchLock)
        {
            previous = _prefetched;
            _prefetched = prepared;
            BeginCrossfadeAbortLocked();
        }
        RetireReader(previous);
    }

    /// <summary>Takes the prefetched track (used when rebuilding the session after a format change).</summary>
    public PendingTrack? TakePrefetched()
    {
        lock (_prefetchLock)
        {
            var t = _prefetched;
            _prefetched = null;
            if (_inCrossfade)
            {
                RewindAbortedCrossfadeSource(t?.Track);
                BeginCrossfadeAbortLocked();
            }
            return t?.Track;
        }
    }

    /// <summary>
    /// Rewinds a prefetched source that a cancelled crossfade had partially consumed, so the
    /// track still starts at its beginning when it is later chained or handed off. Only the
    /// reader is repositioned — a resampler wrapper keeps a few samples of history across the
    /// rewind, which is inaudible and confined to this aborted-fade edge case.
    /// </summary>
    private static void RewindAbortedCrossfadeSource(PendingTrack? track)
    {
        if (track == null || track.RequiresRestart) return;
        try
        {
            track.Reader.CurrentTime = TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            Log.Warn($"[sequencer] crossfade-abort rewind failed: {ex.Message}");
        }
    }

    private void DiscardPrefetched()
    {
        PreparedTrack? previous;
        lock (_prefetchLock)
        {
            previous = _prefetched;
            _prefetched = null;
            BeginCrossfadeAbortLocked();
        }
        RetireReader(previous);
    }

    /// <summary>Parks a reader for later disposal on a thread that cannot race the render
    /// pass — see <see cref="_retired"/>.</summary>
    private void RetireReader(PreparedTrack? track)
    {
        if (track == null) return;
        lock (_prefetchLock) _retired.Add(track);
    }

    /// <summary>Disposes retired readers. Caller must hold <see cref="_gate"/>: that excludes any
    /// concurrent render pass, and the render thread is the only reader of prefetch sources, so
    /// nothing can still be decoding them here.</summary>
    private void DrainRetiredLocked()
    {
        PreparedTrack[] toDispose;
        lock (_prefetchLock)
        {
            if (_retired.Count == 0) return;
            toDispose = _retired.ToArray();
            _retired.Clear();
        }
        foreach (var retired in toDispose)
        {
            try { retired.Track.Reader.Dispose(); }
            catch (Exception ex) { Log.Trace($"[sequencer] retired reader dispose: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Clears an in-progress crossfade (caller holds <see cref="_prefetchLock"/>) and starts the
    /// gain-recovery ramp from wherever the fade had reached, so an aborted fade-out does not
    /// step the current track straight back to unity.
    /// </summary>
    private void BeginCrossfadeAbortLocked()
    {
        if (!_inCrossfade) return;
        _inCrossfade = false;
        double t = _crossfadeTotalFrames > 0
            ? Math.Clamp((double)_crossfadeElapsedFrames / _crossfadeTotalFrames, 0.0, 1.0)
            : 0.0;
        _crossfadeElapsedFrames = 0;
        _fadeRecoverStartGain = (float)Math.Cos(t * Math.PI * 0.5);
        _fadeRecoverTotalFrames = Math.Max(1, (long)(0.15 * _outFormat.SampleRate));
        _fadeRecoverElapsedFrames = 0;
        _fadeRecovering = _fadeRecoverStartGain < 0.999f;
    }

    /// <summary>Aborts playback: drops everything without firing events.</summary>
    public void Cancel()
    {
        DiscardPrefetched();

        lock (_gate)
        {
            _current?.Track.Reader.Dispose();
            _current = null;
            _sourceProvider = null;
            Volatile.Write(ref _replayGainNode, null);
            Volatile.Write(ref _facts, null);
            _endFired = false;
            _trackStartedFired = false;
            _fadeRecovering = false;
            Volatile.Write(ref AbLoopStartBytes, 0);
            Volatile.Write(ref AbLoopEndBytes, 0);
            _dspChain.Reset();
            // The session is going away, so no render pass will drain the retirement list.
            DrainRetiredLocked();
        }
    }

    /// <summary>Updates the master fader (listening volume). Applied after the DSP chain, so it
    /// takes effect on the very next rendered block and no processing stage compensates it.</summary>
    public void SetMasterGain(float gain)
    {
        // Lock-free: the volume slider writes this on every pointer move. Plain float field —
        // see the declaration comment for the atomicity/staleness reasoning.
        _masterGain = Math.Clamp(gain, 0f, 1f);
    }

    /// <summary>Updates the pre-chain ReplayGain node gain live (e.g. when the normalizer toggles
    /// and ReplayGain ownership moves between the node and the normalizer effect).</summary>
    public void SetReplayGainNode(float gain)
    {
        // Under the gate: without it, a UI-thread toggle interleaving with a gapless-boundary
        // SetCurrentLocked could stamp the OLD track's gain onto the NEW track's node — wrong
        // ReplayGain level for the whole track — and leave a stale _nodeGain arming the limiter.
        // The call is UI-rate; the worst case waits one render pass.
        lock (_gate)
        {
            _nodeGain = gain;
            var node = Volatile.Read(ref _replayGainNode);
            if (node != null) node.Volume = gain;
            SyncLimiterEnabled();
        }
    }

    /// <summary>Updates the equalizer profile applied to the active and future tracks.</summary>
    public void SetEqualizer(EqProfile? profile)
    {
        _dspChain.GetEffect<EqualizerDspEffect>()?.SetProfile(profile);
        SyncLimiterEnabled();
    }

    /// <summary>
    /// The soft limiter is a memoryless waveshaper, so leaving it armed when nothing upstream can
    /// raise the level meant the configuration the UI calls bit-perfect still reshaped every peak
    /// above 0.90. Arm it only when something can actually push the signal up: a boosting
    /// ReplayGain node, the equalizer, the normalizer, convolution, or a plugin. The master fader
    /// is attenuation-only and never warrants arming.
    /// </summary>
    private void SyncLimiterEnabled()
    {
        var limiter = _dspChain.GetEffect<SoftLimiterDspEffect>();
        if (limiter == null) return;

        bool eqActive = _dspChain.GetEffect<EqualizerDspEffect>()?.CanAlterLevel == true;
        bool normalizerActive = _dspChain.GetEffect<DynamicNormalizerDspEffect>()?.CanAlterLevel == true;
        bool convolutionActive = _dspChain.GetEffect<ConvolutionDspEffect>() is { IsEnabled: true, HasImpulse: true };
        // Plugin DSPs are unknown quantity-wise: treat an armed one as level-altering.
        bool pluginDspActive = _dspChain.GetEffect<global::DawnPlayer.Core.Audio.Dsp.Plugins.PluginDspEffect>() is { IsEnabled: true };
        bool replayGainNodeMayBoost = _applyVolume && _nodeGain > 1.0001f;
        limiter.IsEnabled = replayGainNodeMayBoost || eqActive || normalizerActive || convolutionActive || pluginDspActive;
    }

    /// <summary>The analysis tap at the end of the chain, or null for a custom chain without one.</summary>
    public SpectrumTapDspEffect? SpectrumTap => _dspChain.GetEffect<SpectrumTapDspEffect>();

    /// <summary>Updates crossfeed and mono-downmix live (spatial DSP).</summary>
    public void SetSpatial(CrossfeedSettings? crossfeed, bool monoDownmix)
    {
        _dspChain.GetEffect<CrossfeedDspEffect>()?.ApplySettings(crossfeed);
        _dspChain.GetEffect<MonoDownmixDspEffect>()?.ApplySettings(monoDownmix);
    }

    /// <summary>Live-toggles plugin-provided DSP effects.</summary>
    public void SetPluginDsp(bool enabled)
    {
        var plugin = _dspChain.GetEffect<global::DawnPlayer.Core.Audio.Dsp.Plugins.PluginDspEffect>();
        if (plugin == null) return;
        plugin.IsEnabled = enabled;
        SyncLimiterEnabled();
        if (!enabled) plugin.Reset();
    }

    /// <summary>
    /// Swaps the convolver's impulse (already prepared by the caller, off-thread) and its enable
    /// state. Null or an empty impulse bypasses the effect.
    /// </summary>
    public void SetConvolution(bool enabled, float[]? monoImpulse)
    {
        var conv = _dspChain.GetEffect<ConvolutionDspEffect>();
        if (conv == null) return;

        conv.SetImpulse(monoImpulse is { Length: > 0 } ? monoImpulse : null);
        conv.IsEnabled = enabled && monoImpulse is { Length: > 0 };
        SyncLimiterEnabled();
    }

    /// <summary>Updates the dynamic normalizer settings and active ReplayGain linear multiplier.</summary>
    public void SetNormalizer(NormalizerSettings? settings, float? staticReplayGainLinear = null)
    {
        var norm = _dspChain.GetEffect<DynamicNormalizerDspEffect>();
        if (settings != null)
        {
            norm?.ApplySettings(settings);
        }
        if (staticReplayGainLinear.HasValue || norm != null)
        {
            norm?.SetReplayGain(staticReplayGainLinear);
        }
        SyncLimiterEnabled();
    }

    /// <summary>
    /// Applies normalizer settings and the pre-chain node gain as ONE step under the gate.
    /// Toggling the normalizer moves ReplayGain ownership between the normalizer effect and the
    /// node; doing that in two separate (ungated) calls let the render thread produce a block in
    /// between — RG² right after a toggle-off, or a unity dip right after a toggle-on.
    /// </summary>
    public void SetNormalizerAndNode(NormalizerSettings? settings, float? staticReplayGainLinear, float nodeGain)
    {
        lock (_gate)
        {
            var norm = _dspChain.GetEffect<DynamicNormalizerDspEffect>();
            if (settings != null)
            {
                norm?.ApplySettings(settings);
            }
            if (staticReplayGainLinear.HasValue || norm != null)
            {
                norm?.SetReplayGain(staticReplayGainLinear);
            }
            _nodeGain = nodeGain;
            var node = Volatile.Read(ref _replayGainNode);
            if (node != null) node.Volume = nodeGain;
            SyncLimiterEnabled();
        }
    }

    /// <summary>
    /// Ramps the current track's gain back to unity after an aborted crossfade. The aborted fade
    /// had already attenuated rendered blocks partway down its curve; jumping straight back to
    /// full gain mid-fade-out was an audible step on every queue edit in the final seconds.
    /// </summary>
    private int ApplyFadeRecovery(int floatsRead)
    {
        if (!_fadeRecovering || floatsRead <= 0) return floatsRead;
        int channels = _outFormat.Channels;
        int frames = floatsRead / channels;
        float start = _fadeRecoverStartGain;
        for (int f = 0; f < frames; f++)
        {
            double p = Math.Clamp((double)(_fadeRecoverElapsedFrames + f) / _fadeRecoverTotalFrames, 0.0, 1.0);
            float g = start + (1f - start) * (float)p;
            for (int ch = 0; ch < channels; ch++)
            {
                _floatBuf[f * channels + ch] *= g;
            }
        }
        _fadeRecoverElapsedFrames += frames;
        if (_fadeRecoverElapsedFrames >= _fadeRecoverTotalFrames) _fadeRecovering = false;
        return floatsRead;
    }

    public int Read(Span<byte> buffer)
    {
        try
        {
            if (IsPaused)
            {
                buffer.Clear();
                return buffer.Length;
            }

            int count = buffer.Length;
            int total = 0;
            lock (_gate)
            {
                if (IsPaused)
                {
                    buffer.Clear();
                    return count;
                }

                // Retired readers are disposed here — the render thread itself — because it is the
                // only thread that decodes prefetch sources, so by construction nothing is reading
                // them mid-pass. See _retired.
                DrainRetiredLocked();

                long served = _bytesServed;
                while (total < count)
                {
                    if (_current == null || _sourceProvider == null) break;

                    int blockAlign = _outFormat.BlockAlign;
                    int framesWanted = (count - total) / blockAlign;
                    if (framesWanted == 0) break;

                    int floatsWanted = framesWanted * _outFormat.Channels;
                    if (_floatBuf.Length < floatsWanted) Array.Resize(ref _floatBuf, floatsWanted);

                    // Check if we should enter crossfade mode. Suppressed while an A-B loop is
                    // active: a loop whose B point sits inside the fade window kept fading past
                    // the first bounce, and the elapsed window drained to the next track with
                    // the loop quietly cleared.
                    if (_crossfadeDurationSeconds > 0.05 && !_rawPassthrough && !_inCrossfade
                        && Volatile.Read(ref AbLoopEndBytes) == 0)
                    {
                        var facts = Volatile.Read(ref _facts);
                        if (facts != null && facts.TotalTime > TimeSpan.Zero)
                        {
                            var currentPos = TimeSpan.FromSeconds((double)served / _outFormat.AverageBytesPerSecond);
                            var rem = facts.TotalTime - currentPos;
                            if (rem > TimeSpan.Zero && rem <= TimeSpan.FromSeconds(_crossfadeDurationSeconds))
                            {
                                lock (_prefetchLock)
                                {
                                    if (_prefetched is { Track.RequiresRestart: false } next)
                                    {
                                        _inCrossfade = true;
                                        // Clamp the window to what both sides can actually fill. A
                                        // fade longer than the current track's remainder handed off
                                        // mid-fade (the next track jumping from sin(t) straight to
                                        // unity), and one longer than the NEXT track skipped most
                                        // of that track.
                                        long fade = Math.Max(1, (long)(_crossfadeDurationSeconds * _outFormat.SampleRate));
                                        long remFrames = Math.Max(1, (long)(rem.TotalSeconds * _outFormat.SampleRate));
                                        var nextTotal = next.Track.Reader.TotalTime;
                                        long nextFrames = nextTotal > TimeSpan.Zero
                                            ? Math.Max(1, (long)(nextTotal.TotalSeconds * _outFormat.SampleRate))
                                            : fade;
                                        _crossfadeTotalFrames = Math.Max(1, Math.Min(fade, Math.Min(remFrames, nextFrames)));
                                        _crossfadeElapsedFrames = 0;
                                    }
                                }
                            }
                        }
                    }

                    int floatsRead;
                    if (_inCrossfade)
                    {
                        PreparedTrack? nextTrack;
                        lock (_prefetchLock)
                        {
                            nextTrack = _prefetched;
                        }

                        if (nextTrack == null || nextTrack.Track.RequiresRestart)
                        {
                            // The fade target vanished underneath the fade (queue edit, stop-after-
                            // current) — recover the gain instead of stepping back to unity.
                            lock (_prefetchLock) BeginCrossfadeAbortLocked();
                            floatsRead = _sourceProvider.Read(_floatBuf.AsSpan(0, floatsWanted));
                            floatsRead = ApplyFadeRecovery(floatsRead);
                        }
                        else
                        {
                            if (_crossfadeNextBuf.Length < floatsWanted) Array.Resize(ref _crossfadeNextBuf, floatsWanted);

                            int floats1 = _sourceProvider.Read(_floatBuf.AsSpan(0, floatsWanted));
                            int floats2 = nextTrack.Source.Read(_crossfadeNextBuf.AsSpan(0, floatsWanted));

                            int frames1 = floats1 / _outFormat.Channels;
                            int frames2 = floats2 / _outFormat.Channels;
                            int maxFrames = Math.Max(frames1, frames2);

                            if (maxFrames == 0)
                            {
                                _inCrossfade = false;
                                _crossfadeElapsedFrames = 0;
                                floatsRead = 0;
                            }
                            else
                            {
                                int channels = _outFormat.Channels;
                                for (int f = 0; f < maxFrames; f++)
                                {
                                    double t = Math.Clamp((double)(_crossfadeElapsedFrames + f) / _crossfadeTotalFrames, 0.0, 1.0);
                                    float gain1 = (float)Math.Cos(t * Math.PI * 0.5);
                                    float gain2 = (float)Math.Sin(t * Math.PI * 0.5);

                                    for (int ch = 0; ch < channels; ch++)
                                    {
                                        int idx = f * channels + ch;
                                        float s1 = idx < floats1 ? _floatBuf[idx] : 0f;
                                        float s2 = idx < floats2 ? _crossfadeNextBuf[idx] : 0f;
                                        _floatBuf[idx] = s1 * gain1 + s2 * gain2;
                                    }
                                }

                                _crossfadeElapsedFrames += maxFrames;
                                floatsRead = maxFrames * channels;

                                if (_crossfadeElapsedFrames >= _crossfadeTotalFrames || (floats1 <= 0 && floats2 > 0))
                                {
                                    _inCrossfade = false;
                                    _crossfadeElapsedFrames = 0;
                                    var next = TakeChainablePrefetched(out _);
                                    if (next != null)
                                    {
                                        AdvanceToPrefetchedLocked(next);
                                        // The handoff anchored the clock to the next reader's real
                                        // position — it already carries the fade window this pass
                                        // finished delivering. Subtract the pass so the increment
                                        // below lands exactly on that anchor instead of counting
                                        // the window twice.
                                        served = Volatile.Read(ref _bytesServed) - (long)maxFrames * _outFormat.BlockAlign;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        floatsRead = _sourceProvider.Read(_floatBuf.AsSpan(0, floatsWanted));
                        floatsRead = ApplyFadeRecovery(floatsRead);
                    }

                    if (floatsRead <= 0)
                    {
                        var endedItem = _current.Track.Item;
                        // current track drained → try to chain the prefetched one
                        var next = TakeChainablePrefetched(out bool requiresRestart);
                        if (requiresRestart)
                        {
                            // Dispose the drained track's reader (the prefetched one is untouched
                            // and the rebuilt session takes it via TakePrefetched). Leaving it
                            // alive leaked a decoder and an open file handle at every exclusive-mode
                            // format transition.
                            _current.Track.Reader.Dispose();
                            _current = null;
                            _sourceProvider = null;
                            Volatile.Write(ref _replayGainNode, null);
                            Volatile.Write(ref _facts, null);
                            Volatile.Write(ref AbLoopStartBytes, 0);
                            Volatile.Write(ref AbLoopEndBytes, 0);
                            FireEndedLocked(SequencerEndReason.FormatChange, endedItem);
                            break;
                        }
                        if (next != null)
                        {
                            AdvanceToPrefetchedLocked(next);
                            // The handoff anchored the clock to the new reader's real position.
                            served = Volatile.Read(ref _bytesServed);
                            continue;
                        }
                        _current.Track.Reader.Dispose();
                        _current = null;
                        _sourceProvider = null;
                        Volatile.Write(ref _facts, null);
                        Volatile.Write(ref AbLoopStartBytes, 0);
                        Volatile.Write(ref AbLoopEndBytes, 0);
                        FireEndedLocked(SequencerEndReason.NaturalEnd, endedItem);
                        break;
                    }

                    if (!_trackStartedFired)
                    {
                        _trackStartedFired = true;
                        TrackStarted?.Invoke(_current.Track);
                    }

                    // Process through the decoupled DSP chain
                    if (!_rawPassthrough) _dspChain.Process(_floatBuf, 0, floatsRead);

                    // Master fader, after the chain: the listening volume is the last thing that
                    // touches the signal, so no processing stage (the AGC above all) can see or
                    // compensate it. Attenuation-only — it can never exceed full scale.
                    if (_applyVolume && !_rawPassthrough)
                    {
                        float fader = _masterGain;
                        if (fader != 1f)
                        {
                            for (int i = 0; i < floatsRead; i++)
                            {
                                _floatBuf[i] *= fader;
                            }
                        }
                    }

                    int frames = floatsRead / _outFormat.Channels;
                    PcmConvert.ToBytes(_floatBuf.AsSpan(0, frames * _outFormat.Channels), buffer.Slice(total), _outFormat);
                    total += frames * blockAlign;
                    // A streaming reader whose clock is more truthful than this counter (the
                    // YouTube pipe reader counts only really-heard PCM) can request a one-shot
                    // re-anchor after a seek-restart silence gap — without it the served clock
                    // keeps the gap's silence and drifts ahead of the heard audio forever.
                    if (_current.Track.Reader is IResyncRequestSource resync && resync.TryConsumeResync(out var resyncPos))
                    {
                        served = MfTrackReader.TimeToBytes(_outFormat, resyncPos);
                    }
                    else
                    {
                        served += (long)frames * blockAlign;
                    }
                    // Published so the lock-free position getters see the progress of this pass.
                    Volatile.Write(ref _bytesServed, served);

                    // A-B repeat: bounce back to the loop start on the thread that already owns
                    // the gate, mirroring what SeekLocked does. Overshoot is bounded by this one
                    // render block, so the loop point stays tight without any UI-thread polling.
                    long loopEnd = Volatile.Read(ref AbLoopEndBytes);
                    if (loopEnd > 0 && served >= loopEnd)
                    {
                        long loopStart = Volatile.Read(ref AbLoopStartBytes);
                        if (loopStart <= 0 || loopStart >= loopEnd)
                        {
                            // Degenerate window (A cleared or A >= B): stop looping rather than
                            // spinning the render thread on a no-op seek.
                            Volatile.Write(ref AbLoopEndBytes, 0);
                        }
                        else
                        {
                            _current.Track.Reader.CurrentTime =
                                TimeSpan.FromSeconds((double)loopStart / _outFormat.AverageBytesPerSecond);
                            served = loopStart;
                            Volatile.Write(ref _bytesServed, served);
                            // Same reasoning as SeekLocked: delay lines clear, loudness gain stays.
                            _dspChain.ResetForSeek();
                        }
                    }
                }
            }
            return total;
        }
        catch (Exception ex)
        {
            ReadError?.Invoke(ex);
            return 0;
        }
    }

    /// <summary>
    /// Claims the queued track when it can be chained seamlessly. A track that needs a session
    /// rebuild is left in place for <see cref="TakePrefetched"/>, and
    /// <paramref name="requiresRestart"/> reports that. Deciding and claiming under one lock is
    /// what keeps the drain path from having to re-examine a queue that moved underneath it.
    /// </summary>
    private PreparedTrack? TakeChainablePrefetched(out bool requiresRestart)
    {
        lock (_prefetchLock)
        {
            var queued = _prefetched;
            requiresRestart = queued is { Track.RequiresRestart: true };
            if (queued == null || requiresRestart) return null;

            _prefetched = null;
            return queued;
        }
    }

    private void AdvanceToPrefetchedLocked(PreparedTrack next)
    {
        var old = _current;
        SetCurrentLocked(next, gapless: true);
        old?.Track.Reader.Dispose();
    }

    /// <summary>
    /// Builds the sample-provider graph for a track. Called off the render thread: the resampler
    /// and channel converter each allocate their own buffers, which is not something to do at a
    /// gapless boundary.
    /// </summary>
    private PreparedTrack Prepare(PendingTrack track)
    {
        ISampleProvider sp = track.Reader.Samples;
        VolumeSampleProvider? replayGainNode = null;
        float replayGainNodeGain = 1f;
        if (_applyVolume && !_rawPassthrough)
        {
            replayGainNodeGain = _replayGainNodeGainProvider(track.Item.Track);
            replayGainNode = new VolumeSampleProvider(sp) { Volume = replayGainNodeGain };
            sp = replayGainNode;
        }
        if (sp.WaveFormat.SampleRate != _outFormat.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, _outFormat.SampleRate);
        if (sp.WaveFormat.Channels != _outFormat.Channels)
            sp = new ChannelConverterSampleProvider(sp, _outFormat.Channels);

        return new PreparedTrack
        {
            Track = track,
            Source = sp,
            ReplayGainNode = replayGainNode,
            ReplayGainNodeGain = replayGainNodeGain,
        };
    }

    /// <param name="gapless">
    /// True when this is a seamless continuation of the previous track. The equalizer's delay lines
    /// still get cleared, but the normalizer keeps its converged loudness gain: resetting it snapped
    /// the gain back to unity, so the first tens of milliseconds of every track on a ReplayGain'd
    /// album played several dB hot and then dived — an audible thump at every "gapless" boundary.
    /// </param>
    private void SetCurrentLocked(PreparedTrack prepared, bool gapless = false)
    {
        var track = prepared.Track;
        _current = prepared;
        // Anchor the reported clock to the reader's actual position instead of hard-zero. A
        // crossfaded-in track (or one whose prefix an aborted fade consumed) continues
        // mid-stream; serving 0 made the seekbar trail the heard audio by the whole overlap for
        // the rest of the track, and the crossfade arming check computes its remaining time off
        // this clock. Reader positions are track-relative (CueTrackReader maps the parent
        // timeline), so a normal gapless handoff still anchors to 0. An endless reader (radio:
        // TotalTime 0) reports wall-clock-since-open, which counts the prefetch open and
        // prebuffer the listener never heard — anchor those to 0 instead.
        var handoffPos = track.Reader.TotalTime > TimeSpan.Zero ? track.Reader.CurrentTime : TimeSpan.Zero;
        Volatile.Write(ref _bytesServed, handoffPos > TimeSpan.Zero ? MfTrackReader.TimeToBytes(_outFormat, handoffPos) : 0);
        _trackStartedFired = false;
        _fadeRecovering = false;
        // Without this the one-shot latch survives the switch and a reused sequencer never
        // reports end-of-stream again, so playback simply stops at the next track boundary.
        _endFired = false;
        // The A-B window is per-track: a new track must not inherit the previous one's loop.
        Volatile.Write(ref AbLoopStartBytes, 0);
        Volatile.Write(ref AbLoopEndBytes, 0);

        _sourceProvider = prepared.Source;
        Volatile.Write(ref _replayGainNode, prepared.ReplayGainNode);
        _nodeGain = prepared.ReplayGainNodeGain;
        Volatile.Write(ref _facts, new TrackFacts(track.Item, track.Reader.TotalTime));

        if (_replayGainProvider != null)
        {
            _dspChain.GetEffect<DynamicNormalizerDspEffect>()?.SetReplayGain(_replayGainProvider(track.Item.Track));
        }
        // The node gain is per-track (tag-dependent): re-evaluate limiter arming at the boundary.
        SyncLimiterEnabled();

        if (gapless)
        {
            // A gapless boundary is a sample-continuous signal: the EQ and crossfeed delay lines
            // hold mid-signal values, and zeroing them injected a step exactly where the user
            // hears one junction. They reset only on a real discontinuity (manual switch, seek).
        }
        else
        {
            _dspChain.Reset();
        }
    }

    private void FireEndedLocked(SequencerEndReason reason, PlaylistItem? endedItem)
    {
        if (_endFired) return;
        _endFired = true;
        SequenceEnded?.Invoke(reason, endedItem);
    }
}
