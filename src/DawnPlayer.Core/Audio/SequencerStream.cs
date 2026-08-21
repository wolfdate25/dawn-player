using System;
using System.Threading;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
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
    private readonly Func<Track, float> _gainProvider;
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
        public VolumeSampleProvider? Volume { get; init; }
    }

    /// <summary>The per-track facts a UI thread polls. Immutable, published as one reference.</summary>
    private sealed record TrackFacts(PlaylistItem Item, TimeSpan TotalTime);

    private readonly object _prefetchLock = new();

    private PreparedTrack? _current;
    private PreparedTrack? _prefetched;
    private ISampleProvider? _sourceProvider;
    private VolumeSampleProvider? _volumeNode;
    private TrackFacts? _facts;
    private float[] _floatBuf = Array.Empty<float>();
    private long _bytesServed;
    private bool _trackStartedFired;
    private bool _endFired;

    public SequencerStream(
        WaveFormat outFormat,
        bool applyVolume,
        Func<Track, float> gainProvider,
        int latencyMs,
        EqProfile? initialEqProfile = null,
        NormalizerSettings? initialNormalizerSettings = null,
        Func<Track, float?>? replayGainProvider = null,
        IAudioDspChain? dspChain = null)
    {
        _outFormat = outFormat;
        _applyVolume = applyVolume;
        _gainProvider = gainProvider;
        _replayGainProvider = replayGainProvider;
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
            defaultChain.AddEffect(new SoftLimiterDspEffect(0.90f));
            _dspChain = defaultChain;
        }

        _dspChain.Initialize(_outFormat.SampleRate, _outFormat.Channels);
        SyncLimiterEnabled();
    }

    public WaveFormat WaveFormat => _outFormat;

    /// <summary>Accesses the active audio DSP chain for dynamic node manipulation.</summary>
    public IAudioDspChain DspChain => _dspChain;

    /// <summary>True when playback is paused; causes Read() to emit silence without advancing track position.</summary>
    public volatile bool IsPaused;

    /// <summary>True while the controller is opening the next reader.</summary>
    public volatile bool PrefetchPending;

    public bool HasPrefetched { get { lock (_prefetchLock) return _prefetched != null; } }

    /// <summary>Fires on the audio thread when the first samples of a track leave the sequencer.</summary>
    public event Action<PendingTrack>? TrackStarted;

    /// <summary>Fires on the audio thread when the sequence cannot continue (see reasons).</summary>
    public event Action<SequencerEndReason>? SequenceEnded;

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
        Volatile.Write(ref _bytesServed, MfTrackReader.TimeToBytes(_outFormat, position));
        _dspChain.Reset();
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
        }
        previous?.Track.Reader.Dispose();
    }

    /// <summary>Takes the prefetched track (used when rebuilding the session after a format change).</summary>
    public PendingTrack? TakePrefetched()
    {
        lock (_prefetchLock)
        {
            var t = _prefetched;
            _prefetched = null;
            return t?.Track;
        }
    }

    private void DiscardPrefetched()
    {
        PreparedTrack? previous;
        lock (_prefetchLock)
        {
            previous = _prefetched;
            _prefetched = null;
        }
        previous?.Track.Reader.Dispose();
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
            Volatile.Write(ref _volumeNode, null);
            Volatile.Write(ref _facts, null);
            _endFired = false;
            _trackStartedFired = false;
            _dspChain.Reset();
        }
    }

    /// <summary>Updates the gain applied to the current and future tracks.</summary>
    public void SetGain(float gain)
    {
        // Lock-free: the volume slider writes this on every pointer move.
        var node = Volatile.Read(ref _volumeNode);
        if (node != null) node.Volume = gain;
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
    /// above 0.90. Arm it only when something can actually push the signal up: the volume/ReplayGain
    /// node (whose multiplier reaches 8x), the equalizer, or the normalizer.
    /// </summary>
    private void SyncLimiterEnabled()
    {
        var limiter = _dspChain.GetEffect<SoftLimiterDspEffect>();
        if (limiter == null) return;

        bool eqActive = _dspChain.GetEffect<EqualizerDspEffect>()?.CanAlterLevel == true;
        bool normalizerActive = _dspChain.GetEffect<DynamicNormalizerDspEffect>()?.CanAlterLevel == true;
        limiter.IsEnabled = _applyVolume || eqActive || normalizerActive;
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

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            if (IsPaused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int total = 0;
            lock (_gate)
            {
                if (IsPaused)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                long served = _bytesServed;
                while (total < count)
                {
                    if (_current == null || _sourceProvider == null) break;

                    int blockAlign = _outFormat.BlockAlign;
                    int framesWanted = (count - total) / blockAlign;
                    if (framesWanted == 0) break;

                    int floatsWanted = framesWanted * _outFormat.Channels;
                    if (_floatBuf.Length < floatsWanted) Array.Resize(ref _floatBuf, floatsWanted);

                    int floatsRead = _sourceProvider.Read(_floatBuf, 0, floatsWanted);
                    if (floatsRead <= 0)
                    {
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
                            Volatile.Write(ref _volumeNode, null);
                            Volatile.Write(ref _facts, null);
                            FireEndedLocked(SequencerEndReason.FormatChange);
                            break;
                        }
                        if (next != null)
                        {
                            AdvanceToPrefetchedLocked(next);
                            served = 0;
                            continue;
                        }
                        _current.Track.Reader.Dispose();
                        _current = null;
                        _sourceProvider = null;
                        Volatile.Write(ref _facts, null);
                        FireEndedLocked(SequencerEndReason.NaturalEnd);
                        break;
                    }

                    if (!_trackStartedFired)
                    {
                        _trackStartedFired = true;
                        TrackStarted?.Invoke(_current.Track);
                    }

                    // Process through the decoupled DSP chain
                    _dspChain.Process(_floatBuf, 0, floatsRead);

                    int frames = floatsRead / _outFormat.Channels;
                    PcmConvert.ToBytes(_floatBuf, frames * _outFormat.Channels, buffer, offset + total, _outFormat);
                    total += frames * blockAlign;
                    served += (long)frames * blockAlign;
                    // Published so the lock-free position getters see the progress of this pass.
                    Volatile.Write(ref _bytesServed, served);
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
        VolumeSampleProvider? volume = null;
        if (_applyVolume)
        {
            volume = new VolumeSampleProvider(sp) { Volume = _gainProvider(track.Item.Track) };
            sp = volume;
        }
        if (sp.WaveFormat.SampleRate != _outFormat.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, _outFormat.SampleRate);
        if (sp.WaveFormat.Channels != _outFormat.Channels)
            sp = new ChannelConverterSampleProvider(sp, _outFormat.Channels);

        return new PreparedTrack { Track = track, Source = sp, Volume = volume };
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
        Volatile.Write(ref _bytesServed, 0);
        _trackStartedFired = false;
        // Without this the one-shot latch survives the switch and a reused sequencer never
        // reports end-of-stream again, so playback simply stops at the next track boundary.
        _endFired = false;

        _sourceProvider = prepared.Source;
        Volatile.Write(ref _volumeNode, prepared.Volume);
        Volatile.Write(ref _facts, new TrackFacts(track.Item, track.Reader.TotalTime));

        if (_replayGainProvider != null)
        {
            _dspChain.GetEffect<DynamicNormalizerDspEffect>()?.SetReplayGain(_replayGainProvider(track.Item.Track));
        }

        if (gapless)
        {
            _dspChain.GetEffect<EqualizerDspEffect>()?.Reset();
        }
        else
        {
            _dspChain.Reset();
        }
    }

    private void FireEndedLocked(SequencerEndReason reason)
    {
        if (_endFired) return;
        _endFired = true;
        SequenceEnded?.Invoke(reason);
    }
}
