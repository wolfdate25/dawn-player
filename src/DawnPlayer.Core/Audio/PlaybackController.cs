using System.Globalization;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Network.Dlna;
using DawnPlayer.Core.Network.YouTube;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Coarse transport state. <see cref="Buffering"/> is the open window for a stream that has to
/// fetch and prebuffer before anything sounds (radio/YouTube connect, DLNA spool) — no audio is
/// flowing yet, the previous session (if any) is still paused, and every failure path must leave
/// the state via <see cref="RestoreFromBuffering"/> rather than sticking here. Mid-play stream
/// stalls (silence served while the source is alive) are reported separately through
/// <see cref="IsBuffering"/> so "Playing" keeps meaning the sequencer is running.
/// </summary>
public enum PlaybackState { Stopped, Playing, Paused, Buffering }

/// <summary>Why playback left a track — drives play/skip counting in the stats sink.</summary>
public enum PlaybackLeaveReason
{
    /// <summary>The track drained on its own (gapless chain, end of playlist, stop-after-current).</summary>
    NaturalEnd,
    /// <summary>The user switched away (next / previous / double-clicked another item).</summary>
    ManualAdvance,
    /// <summary>The user pressed stop while the track was playing.</summary>
    ManualStop
}

/// <summary>A-B repeat cycle state: Off → WaitingForB (A marked) → Looping (A..B) → Off.</summary>
public enum AbRepeatStage { Off, WaitingForB, Looping }

public sealed record SessionInfo(string DeviceName, bool Exclusive, string FormatDescription, int LatencyMs,
    AudioDriverType Driver = AudioDriverType.Wasapi, bool IsDop = false);

/// <summary>
/// An output session could not be opened, carrying a message that is already fit to show the user.
/// Distinguishes "we already explained this" from a raw driver error that still needs translating.
/// </summary>
public sealed class AudioSessionStartException : Exception
{
    public AudioSessionStartException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Orchestrates decoding, gapless sequencing, WASAPI output (exclusive with shared
/// fallback), the playback queue, shuffle/repeat and playback history.
/// Events may fire on background threads; UI must marshal.
/// </summary>
public sealed partial class PlaybackController : IPlaybackController
{
    private readonly AppSettings _settings;
    private readonly IPlaylistManager _playlists;
    private readonly IPlayOrderStrategy _playOrder;
    private readonly OutputSessionFactory _sessionFactory;

    /// <summary>
    /// The live output session, published as one immutable reference.
    /// </summary>
    /// <remarks>
    /// Mutation is still serialized by <see cref="_sessionLock"/>, which is held across WASAPI
    /// device open/negotiate/start — hundreds of milliseconds. Readers therefore take the
    /// published snapshot instead of the lock, so no UI interaction waits on a session rebuild.
    /// </remarks>
    private sealed record SessionSnapshot(
        SequencerStream Sequencer,
        IWavePlayer Output,
        MMDevice? Device,
        bool Exclusive,
        AudioDriverType Driver,
        string? DeviceKey,
        bool IsDop = false,
        IStreamStallSource? StallSource = null);

    private readonly object _sessionLock = new();
    private SessionSnapshot? _session;

    private readonly System.Threading.Timer _pollTimer;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private long _commandGeneration;
    private long _lastPositionTicks;
    private int _disposed;

    private readonly object _stateLock = new();
    private PlaylistItem? _currentItem;
    private Playlist? _currentPlaylist;
    private readonly Stack<(Playlist Playlist, PlaylistItem Item)> _history = new();

    // Distinguishes a user-command switch (Next/Previous/double-click) from a natural gapless
    // advance when the successor's TrackStarted arrives: manual paths stamp the outgoing item here
    // before switching, so OnTrackStarted attributes the leave correctly. Reference published via
    // Volatile because it is written on command threads and read on a ThreadPool handler.
    private PlaylistItem? _manualLeaveMarker;

    // An enum cannot be volatile, so the backing int is what gets published (same pattern as _state).
    private int _abStage;

    // Lazy art resolution for remote tracks (Track.ArtUrl → ArtPath at session start). The cache
    // dedupes concurrent calls per URL, so a fresh DlnaSection play that already awaited the
    // download makes this a no-op.
    private readonly DlnaArtCache _remoteArt = new();

    // An enum cannot be volatile, so the backing int is what gets published. State is written from
    // command paths (no lock) and from session paths (under _sessionLock) alike.
    private int _state;
    public PlaybackState State
    {
        get => (PlaybackState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
    }

    /// <summary>Buffering feedback for the UI: either an open is in flight from a non-playing
    /// state (<see cref="PlaybackState.Buffering"/>) or the active stream reader is alive but
    /// underrun and serving silence. Cheap volatile reads — safe to poll at UI timer rates.</summary>
    public bool IsBuffering =>
        State == PlaybackState.Buffering ||
        (Volatile.Read(ref _session)?.StallSource?.IsStalled ?? false);

    /// <summary>Cancels an open that is in flight and leaves the state honest (Paused when a
    /// session survives it, Stopped when none does) — without resuming anything. This is the
    /// "want silence now" primitive: the sleep timer's expiry must kill a buffering open but
    /// must NOT un-pause the track the user had paused, which a PlayPause here would do.</summary>
    public void CancelPendingOpen()
    {
        Interlocked.Increment(ref _commandGeneration);
        RestoreFromBuffering();
    }

    /// <summary>Leaves a stuck <see cref="PlaybackState.Buffering"/> honestly. Paused when the
    /// surviving session still holds a track; a drained sequencer (its track ended, the advance
    /// that was in flight just cancelled) has nothing to resume and is torn down instead — the
    /// same contract the PlayPause Buffering arm applies, so the two can never disagree about
    /// what a silent session means. No-op when a newer command already moved the state on.</summary>
    private void RestoreFromBuffering()
    {
        if (State != PlaybackState.Buffering) return;
        lock (_sessionLock)
        {
            // Re-check under the lock: a newer command may have published a session (and set
            // Playing) between our unlocked reads and this acquisition — its state owns now.
            if (State != PlaybackState.Buffering) return;
            if (Sequencer is { CurrentItem: not null })
            {
                State = PlaybackState.Paused;
            }
            else
            {
                TeardownSessionLocked();
                State = PlaybackState.Stopped;
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>Enters <see cref="PlaybackState.Buffering"/> for a command whose open window is
    /// user-visible (nothing is sounding: Stopped or Paused). Already-buffering or playing →
    /// no churn.</summary>
    private void MaybeEnterBuffering()
    {
        if (State is PlaybackState.Playing or PlaybackState.Buffering) return;
        State = PlaybackState.Buffering;
        StateChanged?.Invoke();
    }

    public PlaybackQueue Queue { get; } = new();
    IPlaybackQueue IPlaybackController.Queue => Queue;

    private volatile bool _stopAfterCurrent;
    public bool StopAfterCurrent
    {
        get => _stopAfterCurrent;
        set
        {
            if (_stopAfterCurrent != value)
            {
                _stopAfterCurrent = value;
                if (value)
                {
                    Volatile.Read(ref _session)?.Sequencer.SetPrefetched(null);
                }
                StopAfterCurrentChanged?.Invoke();
            }
        }
    }

    /// <summary>Convenience accessor for the published session's sequencer, or null when stopped.</summary>
    private SequencerStream? Sequencer => Volatile.Read(ref _session)?.Sequencer;

    public event Action<PlaylistItem?>? CurrentChanged;   // null → stopped keeping last track hidden
    public event Action? StateChanged;

    /// <summary>
    /// Raised exactly once per played track when playback leaves it (natural drain, user switch,
    /// user stop), with the position at the moment of leaving. Background thread; UI must marshal.
    /// </summary>
    public event Action<PlaylistItem, TimeSpan, PlaybackLeaveReason>? TrackLeft;

    /// <summary>Raised when the A-B repeat stage changes (user cycle or per-track reset).</summary>
    public event Action? AbRepeatChanged;

    /// <summary>
    /// Raised on the caller's thread when a CycleAbRepeat press was refused (B before A, or a
    /// live/unseekable source) and the stage did not advance. UI relays it so the press fails
    /// with an explanation instead of silently doing nothing.
    /// </summary>
    public event Action<AbRepeatRejectionReason>? AbRepeatRejected;
    public event Action? StopAfterCurrentChanged;
    public event Action<string>? Warning;
    public event Action<SessionInfo>? SessionStarted;

    /// <summary>
    /// Raised on the reader's fill thread when a live source (radio ICY today) reports a new
    /// now-playing title for the item it is playing. UI must marshal.
    /// </summary>
    public event Action<LiveStreamMetadata>? StreamTitleChanged;

    /// <summary>
    /// Raised on a background thread when a remote track's art finished downloading and
    /// <see cref="Models.Track.ArtPath"/> was set (see <see cref="ResolveRemoteArt"/>).
    /// UI must marshal.
    /// </summary>
    public event Action<Models.Track>? RemoteArtResolved;

    public bool IsExclusiveSession => Volatile.Read(ref _session)?.Exclusive ?? false;
    public SessionInfo? CurrentSessionInfo { get; private set; }

    /// <summary>The analysis tap at the end of the active session's DSP chain, or null when no
    /// session is open (U4 fullscreen visualizer reads it; the tap is always in the default chain).</summary>
    public Dsp.SpectrumTapDspEffect? SpectrumTap => Sequencer?.SpectrumTap;

    /// <summary>Output sample rate of the active session, or null when stopped (U4 visualizer).</summary>
    public int? CurrentSampleRate => Sequencer?.WaveFormat.SampleRate;

    public PlaybackController(AppSettings settings, IPlaylistManager playlists, IPlayOrderStrategy? playOrder = null)
    {
        _settings = settings;
        _playlists = playlists;
        // TryGetCurrent, not Current: resolution runs on the thread pool and the creating
        // accessors insert into the UI-bound playlist collection.
        _playOrder = playOrder ?? new PlayOrderResolver(settings, Queue, () => _playlists.TryGetCurrent());
        // One shared effect instance per controller; each new session re-inserts it into the
        // fresh chain and re-applies the persisted enable state.
        _pluginDspEffect = new DawnPlayer.Core.Audio.Dsp.Plugins.PluginDspEffect(() => Volatile.Read(ref _pluginDspHost));
        _sessionFactory = new OutputSessionFactory(
            settings,
            ComputeReplayGainNodeGain,
            ComputeReplayGain,
            SubscribeSequencer,
            SubscribeOutput,
            message => Warning?.Invoke(message),
            pluginDsp: () => Volatile.Read(ref _pluginDspEffect));
        _pollTimer = new System.Threading.Timer(_ => PollPrefetch(), null, 250, 250);

        // A prefetch decided up to 1.2 s before the boundary would otherwise win over a queue
        // change the user made in that window, so "play next" silently did nothing.
        Queue.Changed += InvalidatePrefetch;
    }

    /// <summary>Drops any prefetched track so the next advance re-resolves play order.</summary>
    public void InvalidatePrefetch()
    {
        // Fires on every queue change, so it must never wait on a session rebuild.
        Sequencer?.SetPrefetched(null);
    }

    public PlaylistItem? CurrentItem { get { lock (_stateLock) return _currentItem; } }
    public Playlist? CurrentPlaylist { get { lock (_stateLock) return _currentPlaylist; } }

    public TimeSpan Position => Sequencer?.GetPosition() ?? HeldPosition();
    public TimeSpan Duration => Sequencer?.TotalTime ?? CurrentItem?.Track.Duration ?? TimeSpan.Zero;

    private TimeSpan HeldPosition() =>
        State == PlaybackState.Stopped || (State == PlaybackState.Buffering && Sequencer == null)
            ? TimeSpan.Zero
            : new TimeSpan(Volatile.Read(ref _lastPositionTicks));

    // ---------------- public commands ----------------

    /// <summary>Starts playing a specific playlist item (double-click / Play).</summary>
    public async Task PlayAsync(Playlist playlist, PlaylistItem item)
    {
        long cmdId = Interlocked.Increment(ref _commandGeneration);
        // A stream's open can prebuffer for seconds (radio/YouTube) or spool the whole body
        // (DLNA); say so instead of leaving the last state on screen. Local files open in
        // milliseconds — the extra transitions would be pure churn on the hot double-click path.
        // Playing keeps its state: the old session keeps sounding until the swap.
        if (item.Track.SourceKind != TrackSourceKind.File && State != PlaybackState.Playing)
        {
            State = PlaybackState.Buffering;
            StateChanged?.Invoke();
        }
        PendingTrack pending;
        try
        {
            var reader = await Task.Run(() => AudioFileReaderFactory.Open(item.Track.Path, item.Track.SourceKind));
            if (Volatile.Read(ref _commandGeneration) != cmdId)
            {
                reader.Dispose();
                return;
            }
            pending = BuildPending(playlist, item, reader);
        }
        catch (AudioOpenException ex)
        {
            Warning?.Invoke(ex.Message);
            RestoreFromBuffering();
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _commandGeneration) != cmdId)
            {
                pending.Reader.Dispose();
                return;
            }
            FireManualLeave(PlaybackLeaveReason.ManualAdvance);
            PushHistory();
            StartPending(pending, cmdId);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void PlayPause()
    {
        switch (State)
        {
            case PlaybackState.Playing:
                // Same reason as Stop(): a track still being opened must not start unpaused.
                Interlocked.Increment(ref _commandGeneration);
                {
                    var seq = Sequencer;
                    if (seq != null) seq.IsPaused = true;
                }
                State = PlaybackState.Paused;
                StateChanged?.Invoke();
                break;
            case PlaybackState.Paused:
                Interlocked.Increment(ref _commandGeneration);
                {
                    var seq = Sequencer;
                    if (seq != null) seq.IsPaused = false;
                }
                State = PlaybackState.Playing;
                StateChanged?.Invoke();
                break;
            case PlaybackState.Buffering:
                // Same contract as Playing: a press during the open window must not let the
                // still-opening track start unpaused — cancel it and fall back to the surviving
                // session (resume it) or to a clean stop when none exists. A drained sequencer
                // (its track ended, the advance just cancelled) has nothing to resume: leaving
                // it alive would show "Playing" over silence forever.
                Interlocked.Increment(ref _commandGeneration);
                {
                    var seq = Sequencer;
                    if (seq is { CurrentItem: not null })
                    {
                        seq.IsPaused = false;
                        State = PlaybackState.Playing;
                    }
                    else
                    {
                        lock (_sessionLock) TeardownSessionLocked();
                        State = PlaybackState.Stopped;
                    }
                }
                StateChanged?.Invoke();
                break;
            default:
                var (pl, item) = LastPlayableContext();
                if (item != null && pl != null) _ = PlayAsync(pl, item);
                break;
        }
    }

    public void Stop()
    {
        // Invalidate anything still opening a file: without this a slow Open() completing after
        // Stop would build a session and start playing seconds after the user stopped playback.
        Interlocked.Increment(ref _commandGeneration);
        FireManualLeave(PlaybackLeaveReason.ManualStop);
        SetAbStage(AbRepeatStage.Off);
        lock (_sessionLock)
        {
            TeardownSessionLocked();
        }
        State = PlaybackState.Stopped;
        StateChanged?.Invoke();
    }

    /// <summary>Manual next (respects queue, ignores repeat-one looping).</summary>
    public async Task NextAsync()
    {
        long cmdId = Interlocked.Increment(ref _commandGeneration);
        // ResolveNextTrack opens the next audio file and can renegotiate the WASAPI format, both
        // of which touch the disk/driver. Running that inline froze the UI on every Next click.
        // Nothing sounds while resolving from Stopped/Paused, so that open window is buffering.
        MaybeEnterBuffering();
        var session = Volatile.Read(ref _session);
        PendingTrack? pending = await Task.Run(() => ResolveNextTrack(session, manualAdvance: true))
            .ConfigureAwait(false);
        if (pending == null)
        {
            Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.NextTrackMissing));
            // Unconditional and self-guarding: this command may have inherited Buffering from a
            // superseded one (MaybeEnterBuffering is a no-op then), and the superseded command's
            // ownership evaporated with its generation check — whoever terminates last restores.
            RestoreFromBuffering();
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _commandGeneration) != cmdId)
            {
                pending.Reader.Dispose();
                return;
            }
            FireManualLeave(PlaybackLeaveReason.ManualAdvance);
            await PlayPendingAsync(pending, pushHistory: true, cmdId);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task PreviousAsync()
    {
        long cmdId = Interlocked.Increment(ref _commandGeneration);
        MaybeEnterBuffering();
        (Playlist pl, PlaylistItem item)? target = null;
        lock (_stateLock)
        {
            while (_history.Count > 0)
            {
                var (pl, it) = _history.Pop();
                if (it.Track != null) { target = (pl, it); break; }
            }
            if (target == null && _currentPlaylist != null && _currentItem != null)
            {
                var snap = _currentPlaylist.GetSnapshot();
                var idx = Array.IndexOf(snap, _currentItem);
                if (idx > 0) target = (_currentPlaylist, snap[idx - 1]);
                else if (_settings.Playback.Repeat == RepeatMode.All && snap.Length > 0)
                    target = (_currentPlaylist, snap[^1]);
            }
        }
        if (target == null)
        {
            Seek(TimeSpan.Zero);
            // See NextAsync: unconditional — this command may have inherited a superseded
            // Buffering it never entered.
            RestoreFromBuffering();
            if (State == PlaybackState.Stopped) PlayPause();
            return;
        }

        PendingTrack pending;
        try
        {
            var reader = await Task.Run(() => AudioFileReaderFactory.Open(target.Value.item.Track.Path, target.Value.item.Track.SourceKind));
            if (Volatile.Read(ref _commandGeneration) != cmdId)
            {
                reader.Dispose();
                return;
            }
            pending = BuildPending(target.Value.pl, target.Value.item, reader);
        }
        catch (AudioOpenException ex)
        {
            Warning?.Invoke(ex.Message);
            RestoreFromBuffering();
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _commandGeneration) != cmdId)
            {
                pending.Reader.Dispose();
                return;
            }
            FireManualLeave(PlaybackLeaveReason.ManualAdvance);
            await PlayPendingAsync(pending, pushHistory: false, cmdId);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void Seek(TimeSpan position)
    {
        Sequencer?.Seek(position);
    }

    // ---------------- A-B repeat ----------------

    public AbRepeatStage AbRepeat => (AbRepeatStage)Volatile.Read(ref _abStage);

    /// <summary>
    /// Cycles A-B repeat: first press marks point A at the current position, second marks B and
    /// starts looping between them (the sequencer enforces the loop sample-tight on the audio
    /// thread), third press clears. The window is per-track and resets on every track change.
    /// Returns the resulting stage so callers can refresh their affordances.
    /// </summary>
    public AbRepeatStage CycleAbRepeat()
    {
        var seq = Sequencer;
        if (seq == null || State == PlaybackState.Stopped)
        {
            SetAbStage(AbRepeatStage.Off);
            return AbRepeatStage.Off;
        }

        var stage = AbRepeat;
        // Clearing is always allowed; marking is refused on live sources, whose readers report
        // TotalTime zero and no-op seeks — a marked loop could never be enforced while the UI
        // reported one (RadioStreamReader.CurrentTime's setter is a deliberate no-op).
        if (stage != AbRepeatStage.Looping && !AbRepeatGate.CanMark(Duration))
        {
            AbRepeatRejected?.Invoke(AbRepeatRejectionReason.UnseekableSource);
            return stage;
        }
        // Every loop bounce re-seeks from the render thread, and on YouTube that means tearing
        // down and respawning the whole yt-dlp/ffmpeg chain — seconds of silence per cycle.
        // Refusing with an explanation beats a technically-working, unusable loop.
        if (stage != AbRepeatStage.Looping &&
            CurrentItem?.Track.SourceKind == Models.TrackSourceKind.YouTube)
        {
            AbRepeatRejected?.Invoke(AbRepeatRejectionReason.UnsupportedSource);
            return stage;
        }

        var pos = seq.GetPosition();
        switch (stage)
        {
            case AbRepeatStage.Off:
                Volatile.Write(ref seq.AbLoopEndBytes, 0);
                Volatile.Write(ref seq.AbLoopStartBytes, MfTrackReader.TimeToBytes(seq.WaveFormat, pos));
                SetAbStage(AbRepeatStage.WaitingForB);
                break;

            case AbRepeatStage.WaitingForB:
                // A second press before A has audibly landed just re-marks A.
                if (pos.Ticks - AbBytesToTime(seq, Volatile.Read(ref seq.AbLoopStartBytes)).Ticks > TimeSpan.TicksPerSecond / 5)
                {
                    long start = Volatile.Read(ref seq.AbLoopStartBytes);
                    var end = MfTrackReader.TimeToBytes(seq.WaveFormat, pos);
                    if (end > start)
                    {
                        Volatile.Write(ref seq.AbLoopEndBytes, end);
                        SetAbStage(AbRepeatStage.Looping);
                    }
                    else
                    {
                        // A silent refusal here left the user's press seemingly ignored; report why.
                        AbRepeatRejected?.Invoke(AbRepeatRejectionReason.BBeforeA);
                    }
                }
                else
                {
                    Volatile.Write(ref seq.AbLoopStartBytes, MfTrackReader.TimeToBytes(seq.WaveFormat, pos));
                }
                break;

            default:
                CancelAbRepeat();
                break;
        }

        return AbRepeat;
    }

    /// <summary>
    /// Clears the A-B window unconditionally (button right-click and the cycle's clear step share
    /// this path). Returns true when a window was actually active, so callers can distinguish a
    /// real cancel from a no-op.
    /// </summary>
    public bool CancelAbRepeat()
    {
        if (AbRepeat == AbRepeatStage.Off) return false;
        var seq = Sequencer;
        if (seq != null)
        {
            Volatile.Write(ref seq.AbLoopStartBytes, 0);
            Volatile.Write(ref seq.AbLoopEndBytes, 0);
        }
        SetAbStage(AbRepeatStage.Off);
        return true;
    }

    /// <summary>
    /// Time-domain snapshot of the A-B window for UI affordances (seekbar overlay, tooltip).
    /// Converted with the sequencer's current WaveFormat, so what the UI draws is exactly the
    /// region the audio thread enforces.
    /// </summary>
    public AbRepeatWindow AbRepeatWindow
    {
        get
        {
            var seq = Sequencer;
            var stage = AbRepeat;
            if (seq == null || stage == AbRepeatStage.Off) return AbRepeatWindow.Off;
            var start = AbBytesToTime(seq, Volatile.Read(ref seq.AbLoopStartBytes));
            var end = stage == AbRepeatStage.Looping
                ? AbBytesToTime(seq, Volatile.Read(ref seq.AbLoopEndBytes))
                : TimeSpan.Zero;
            return new AbRepeatWindow(stage, start, end);
        }
    }

    private static TimeSpan AbBytesToTime(SequencerStream seq, long bytes) =>
        TimeSpan.FromSeconds((double)bytes / seq.WaveFormat.AverageBytesPerSecond);

    private void SetAbStage(AbRepeatStage stage)
    {
        if (Interlocked.Exchange(ref _abStage, (int)stage) != (int)stage)
        {
            AbRepeatChanged?.Invoke();
        }
    }

    /// <summary>
    /// Records the outgoing track for statistics and stamps the marker so the successor's
    /// TrackStarted does not also report it as a natural end. Called from the manual command
    /// paths after their generation check, outside every other lock.
    /// </summary>
    private void FireManualLeave(PlaybackLeaveReason reason)
    {
        PlaylistItem? current;
        lock (_stateLock) current = _currentItem;
        if (current?.Track == null) return;

        Volatile.Write(ref _manualLeaveMarker, current);
        TrackLeft?.Invoke(current, Position, reason);
    }

    public double Volume
    {
        get => _settings.Playback.Volume;
        set
        {
            _settings.Playback.Volume = Math.Clamp(value, 0, 1);
            Sequencer?.SetMasterGain((float)_settings.Playback.Volume);
        }
    }

    /// <summary>
    /// Re-evaluates and applies the active device's equalizer profile live to the running stream without restarting playback.
    /// </summary>
    public void ApplyEqualizer()
    {
        var session = Volatile.Read(ref _session);
        session?.Sequencer.SetEqualizer(ResolveActiveProfile(session));
    }

    /// <summary>
    /// Re-applies normalizer settings live to the running stream without restarting playback.
    /// </summary>
    public void ApplyNormalizer()
    {
        var seq = Sequencer;
        if (seq == null) return;
        seq.SetNormalizer(_settings.Normalizer, ComputeReplayGain(CurrentItem?.Track));
        // Toggling the normalizer moves ReplayGain ownership between the pre-chain node and the
        // normalizer effect; the node must follow or the gain would apply twice (or not at all).
        seq.SetReplayGainNode(ComputeReplayGainNodeGain(CurrentItem?.Track));
    }

    /// <summary>Re-applies crossfeed / mono-downmix settings live to the running stream.</summary>
    public void ApplySpatial()
    {
        Sequencer?.SetSpatial(_settings.Crossfeed, _settings.Playback.MonoDownmixEnabled);
    }

    /// <summary>
    /// Re-applies the convolver impulse. Impulse decoding and partition preparation run on the
    /// thread pool (a 2 s IR decode is tens of milliseconds; not render-thread work). The last
    /// request wins, so dragging the IR picker cannot interleave stale loads.
    /// </summary>
    private int _convolutionGeneration;

    public void ApplyConvolution()
    {
        var sequencer = Sequencer;
        if (sequencer == null) return;

        bool enabled = _settings.Convolution.Enabled;
        string? path = _settings.Convolution.ImpulsePath;
        int generation = Interlocked.Increment(ref _convolutionGeneration);

        if (!enabled || string.IsNullOrWhiteSpace(path))
        {
            sequencer.SetConvolution(false, null);
            return;
        }

        Task.Run(() =>
        {
            var impulse = ImpulseResponse.LoadMono(path);
            // A newer request (or a session rebuild) supersedes this one.
            if (Volatile.Read(ref _convolutionGeneration) != generation) return;
            var seq = Sequencer;
            if (seq == null) return;
            seq.SetConvolution(enabled, impulse);
        });
    }

    /// <summary>
    /// Copies the post-DSP analysis window for the spectrum meter, or false when no session is
    /// feeding the tap. The version identifies the window: equal to the last one the caller saw
    /// means no new samples arrived (paused or stopped).
    /// </summary>
    public bool TryCopySpectrumWindow(float[] destination, out int sampleRate, out long version)
    {
        sampleRate = 0;
        version = 0;
        var seq = Sequencer;
        var tap = seq?.SpectrumTap;
        if (tap == null) return false;
        version = tap.CopyTo(destination);
        sampleRate = seq!.WaveFormat.SampleRate;
        return true;
    }

    private EqProfile ResolveActiveProfile(SessionSnapshot? session)
    {
        var driver = session?.Driver ?? _settings.Output.DriverType;
        var devKey = session?.DeviceKey ?? DesiredDeviceKey();
        return EqualizerProfileResolver.Resolve(_settings.Equalizer, driver, devKey);
    }

    /// <summary>Re-applies output settings (call after the user changed device/mode).
    /// Restarts the current track at the same position; a paused player stays paused.</summary>
    public void RestartIfPlaying()
    {
        var item = CurrentItem;
        var pl = CurrentPlaylist;
        // Buffering is excluded on purpose: an open in flight IS the user's newest intent, and a
        // settings change must not kill it to resurrect the stale CurrentItem (pre-diff, the
        // state was Stopped during open-from-stopped, so the restart returned here anyway).
        if (State is PlaybackState.Stopped or PlaybackState.Buffering || item == null || pl == null) return;

        var seq = Sequencer;
        var pos = seq?.GetPosition() ?? TimeSpan.Zero;
        bool resumePaused = State == PlaybackState.Paused || seq?.IsPaused == true;

        // Claim a command id so a Stop or a track change issued while the file is reopening
        // cancels this restart instead of resurrecting the old track over the new one.
        long restartCmdId = Interlocked.Increment(ref _commandGeneration);

        _ = Task.Run(async () =>
        {
            try
            {
                var reader = AudioFileReaderFactory.Open(item.Track.Path, item.Track.SourceKind);
                await PlayPendingAsync(BuildPending(pl, item, reader, startPosition: pos), pushHistory: false, restartCmdId, recordLeave: false);
                if (Volatile.Read(ref _commandGeneration) != restartCmdId) return;
                if (resumePaused)
                {
                    var restarted = Sequencer;
                    if (restarted != null) restarted.IsPaused = true;
                    State = PlaybackState.Paused;
                    StateChanged?.Invoke();
                }
            }
            catch (AudioOpenException ex)
            {
                // The reopen failed before anything was torn down: the old session is alive and
                // keeps playing. Stop would throw away working audio, silence would strand the
                // user — say what happened and continue on the current output.
                Log.Warn($"[playback] restart aborted, keeping the current session: {ex.Message}");
                Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.RestartFailedContinue, ex.Message));
                // The generation bump above also cancelled any open this restart superseded; if
                // that open was the one holding Buffering, nobody else will restore it.
                RestoreFromBuffering();
            }
            catch (Exception ex)
            {
                // A reopen failure AFTER the open (output device contention etc.) must not
                // vanish into an unobserved task: the old session is still alive, so the same
                // keep-playing contract applies.
                Log.Warn($"[playback] restart failed unexpectedly, keeping the current session: {ex}");
                Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.RestartFailedContinue, ex.Message));
                RestoreFromBuffering();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Cancels anything still opening a file, so a late completion cannot rebuild a session
        // after teardown.
        Interlocked.Increment(ref _commandGeneration);
        Queue.Changed -= InvalidatePrefetch;

        // Wait for an in-flight prefetch poll instead of racing it into a torn-down session.
        using (var timerGone = new ManualResetEvent(false))
        {
            if (_pollTimer.Dispose(timerGone)) timerGone.WaitOne(TimeSpan.FromSeconds(2));
        }

        lock (_sessionLock) TeardownSessionLocked();

        // _commandGate is deliberately not disposed: SemaphoreSlim without AvailableWaitHandle
        // holds no unmanaged resource, and disposing it would make any command still awaiting it
        // throw ObjectDisposedException on the way out.
    }

    // ---------------- session management ----------------

    private async Task PlayPendingAsync(PendingTrack pending, bool pushHistory, long cmdId, bool recordLeave = true)
    {
        if (pushHistory) PushHistory();
        // A device/mode restart replays the SAME item at the same position: not a leave, or every
        // output-settings change would inflate the skip counter.
        if (recordLeave) FireManualLeave(PlaybackLeaveReason.ManualAdvance);
        await Task.Run(() => StartPending(pending, cmdId));
    }

    private void StartPending(PendingTrack pending, long cmdId)
    {
        bool started = false;
        lock (_sessionLock)
        {
            // Re-check under the lock: the command may have been superseded (or the user may have
            // pressed Stop) while this task was queued.
            if (cmdId != 0 && Volatile.Read(ref _commandGeneration) != cmdId)
            {
                pending.Reader.Dispose();
                return;
            }

            try
            {
                StartOrSwitchLocked(pending);
                State = PlaybackState.Playing;
                started = true;
            }
            catch (Exception ex)
            {
                TeardownSessionLocked();
                State = PlaybackState.Stopped;
                Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.PlayStartFailed,
                    ex is AudioSessionStartException ? ex.Message : AudioErrorMessages.DescribeStartFailure(ex)));
            }
            finally
            {
                // The session owns the reader only once it has been handed to a sequencer. On a
                // failed start nobody else will, and leaking it holds an OS handle on the file for
                // the rest of the process lifetime.
                if (!started && !ReferenceEquals(Sequencer?.CurrentItem, pending.Item))
                {
                    try { pending.Reader.Dispose(); } catch (Exception ex) { Log.Trace($"[playback] reader dispose after failed start: {ex.Message}"); }
                }
            }
        }
        StateChanged?.Invoke();
        // OnRemoteTrackStarted moved into OnTrackStarted: the TrackStarted hook fires for every
        // reader change (initial start, hot-swap, gapless chain, natural advance), while this
        // method only saw double-click/restart starts. A single attach point also rules out a
        // double-registered death warning.
    }

    /// <summary>
    /// Post-start bookkeeping for remote sources, invoked from OnTrackStarted (every reader
    /// change passes through it). YouTube's -J resolve metadata is copied onto the playing track
    /// (playlists stop showing the bare URL, and the thumbnail rides the existing remote-art
    /// hook), a mid-stream chain death is surfaced as a warning instead of a silent skip, and a
    /// dead radio stream announces itself once instead of decaying into unexplained silence.
    /// </summary>
    private void OnRemoteTrackStarted(PendingTrack pending)
    {
        if (pending.Reader is YouTubeStreamReader youTube)
        {
            youTube.PrematureEnd += detail =>
                Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.YouTubeEndedEarly,
                    string.IsNullOrEmpty(detail) ? pending.Item.Track.Path : detail));
            ApplyResolvedMeta(youTube.Meta, pending.Item.Track);
        }
        if (pending.Reader is RadioStreamReader radioStream)
        {
            // A dead radio fill loop keeps serving silence by design — the buffering badge stays
            // honest — but silence without a word reads as a hung app. One warning says why.
            radioStream.StreamDied += detail =>
                Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.StreamDied,
                    string.IsNullOrEmpty(detail) ? pending.Item.Track.Path : detail));
        }
        ResolveRemoteArt(pending.Item.Track);
    }

    private static void ApplyResolvedMeta(YouTubeTrackMeta meta, Models.Track track)
    {
        if (!string.IsNullOrWhiteSpace(meta.Title) &&
            (string.IsNullOrWhiteSpace(track.Title) || track.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            track.Title = meta.Title;
        }
        if (string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(meta.Uploader))
        {
            track.Artist = meta.Uploader;
        }
        if (meta.DurationMs > 0) track.DurationMs = meta.DurationMs;
        if (!string.IsNullOrWhiteSpace(meta.ThumbnailUrl) && string.IsNullOrEmpty(track.ArtUrl))
        {
            track.ArtUrl = meta.ThumbnailUrl;
        }
    }

    /// <summary>
    /// Restores art for remote tracks whose ArtPath did not survive an M3U8 reload: the albumArtURI
    /// rides along in the directive (<see cref="Models.Track.ArtUrl"/>) and the cache download runs
    /// here — at session start, where the audio spool gives it a head start over the first UI/SMTC
    /// paint. Fresh DlnaSection plays have already awaited the download, so the cache turns the
    /// duplicate into a no-op. Fire-and-forget on purpose: art is optional and must never delay or
    /// fail a start.
    /// </summary>
    private void ResolveRemoteArt(Models.Track? track)
    {
        if (track?.ArtUrl == null || !string.IsNullOrEmpty(track.ArtPath)) return;
        if (!Uri.TryCreate(track.ArtUrl, UriKind.Absolute, out var url)) return;

        var cache = _remoteArt;
        _ = Task.Run(async () =>
        {
            try
            {
                var path = await cache.GetOrDownloadAsync(url);
                if (path == null) return;
                track.ArtPath = path;
                RemoteArtResolved?.Invoke(track);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug($"[dlna-art] lazy resolve failed for '{url}': {ex.Message}"); }
        });
    }

    private void StartOrSwitchLocked(PendingTrack pending)
    {
        var session = _session;
        if (session != null && SessionMatchesSettingsLocked(session, pending))
        {
            // seamless hot-swap within the running session
            session.Sequencer.SetPrefetched(null);
            session.Sequencer.SwitchTo(pending);
            if (session.Output.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                session.Output.Play();
            // The stall observer tracks the reader, and a hot-swap just replaced it — republish
            // the snapshot so IsBuffering never asks a superseded reader about its stall state.
            if (!ReferenceEquals(session.StallSource, pending.Reader as IStreamStallSource))
            {
                PublishSessionLocked(session with { StallSource = pending.Reader as IStreamStallSource });
            }
            return;
        }

        if (pending.StartPosition is not null && session != null
            && ReferenceEquals(session.Sequencer.CurrentItem, pending.Item))
        {
            pending = new PendingTrack
            {
                Playlist = pending.Playlist,
                Item = pending.Item,
                Reader = pending.Reader,
                StartPosition = session.Sequencer.GetPosition(),
                RequiresRestart = pending.RequiresRestart
            };
        }

        TeardownSessionLocked(); // rebuild (driver/device/mode/format changed)
        StartSessionLocked(pending);
    }

    /// <summary>True when the live session already plays through the configured
    /// driver, device and mode with a compatible format, so a track change can
    /// hot-swap without rebuilding the output.</summary>
    private bool SessionMatchesSettingsLocked(SessionSnapshot session, PendingTrack pending)
    {
        if (session.Driver != _settings.Output.DriverType) return false;
        if (session.DeviceKey == null || session.DeviceKey != DesiredDeviceKey()) return false;
        if (session.Driver != AudioDriverType.Wasapi) return true; // DirectSound/WaveOut are always shared
        if (session.Exclusive != _settings.Output.UseExclusiveMode) return false;
        return !session.Exclusive || FormatMatchesSession(session, pending);
    }

    /// <summary>Canonical device key for the current settings, or null when it
    /// cannot be resolved (forces a rebuild so the session is retried).</summary>
    private string? DesiredDeviceKey()
    {
        try
        {
            switch (_settings.Output.DriverType)
            {
                case AudioDriverType.DirectSound:
                    return WasapiDeviceService.ResolveDirectSoundDevice(_settings.Output.DeviceId).ToString();
                case AudioDriverType.WaveOut:
                    return WasapiDeviceService.ResolveWaveOutDeviceNumber(_settings.Output.DeviceId)
                        .ToString(CultureInfo.InvariantCulture);
                default:
                    {
                        if (!string.IsNullOrEmpty(_settings.Output.DeviceId)) return _settings.Output.DeviceId;
                        return DefaultRenderEndpointId();
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[playback] desired device key resolve failed: {ex.Message}");
            return null;
        }
    }

    private string? _cachedDefaultEndpointId;
    private long _cachedDefaultEndpointStamp;

    /// <summary>
    /// The default endpoint's ID, cached briefly. This is asked once per track (and again on every
    /// live equalizer apply), and building an <see cref="MMDeviceEnumerator"/> per call is a COM
    /// round trip each time. A second of staleness only costs one extra session rebuild.
    /// </summary>
    private string? DefaultRenderEndpointId()
    {
        long now = Environment.TickCount64;
        long stamp = Volatile.Read(ref _cachedDefaultEndpointStamp);
        if (stamp != 0 && now - stamp < 1000)
        {
            return Volatile.Read(ref _cachedDefaultEndpointId);
        }

        using var enumerator = new MMDeviceEnumerator();
        using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var id = def?.ID;
        Volatile.Write(ref _cachedDefaultEndpointId, id);
        Volatile.Write(ref _cachedDefaultEndpointStamp, now);
        return id;
    }

    private bool FormatMatchesSession(SessionSnapshot session, PendingTrack pending)
    {
        if (session.Device == null) return false;
        WaveFormat? negotiated = null;
        if (_settings.Output.ExclusiveRateMismatch == ExclusiveRateMismatchPolicy.RestartSession)
        {
            negotiated = WasapiDeviceService.TryNegotiateExclusive(
                session.Device, pending.Reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
        }
        return ExclusiveSessionAcceptsTrack(
            _settings.Output.ExclusiveRateMismatch, negotiated, session.Sequencer.WaveFormat,
            session.IsDop, pending.Reader is DopTrackReader);
    }

    /// <summary>Whether the running exclusive session must be rebuilt for the next track's
    /// format. Pure so the policy matrix is testable without a device. ResampleToCurrent never
    /// restarts for plain PCM: the sequencer's Prepare step inserts a resampler for any rate
    /// mismatch, which is seamless but not bit-perfect (the user's explicit choice). DSD breaks
    /// that escape hatch in both directions — packed DoP cannot survive a resampler, and PCM
    /// cannot ride a DoP session — so a DoP/PCM boundary always restarts.</summary>
    public static bool ExclusiveSessionRestartNeeded(
        ExclusiveRateMismatchPolicy policy, WaveFormat? negotiated, WaveFormat sessionFormat,
        bool sessionIsDop = false, bool nextIsDop = false) =>
        sessionIsDop || nextIsDop
            ? true
            : policy == ExclusiveRateMismatchPolicy.ResampleToCurrent
                ? false
                : negotiated == null || FormatKey(negotiated) != FormatKey(sessionFormat);

    /// <summary>Whether an existing exclusive session can hot-swap to the given track. Pure;
    /// the mirror of <see cref="ExclusiveSessionRestartNeeded"/> for the play-command path.</summary>
    public static bool ExclusiveSessionAcceptsTrack(
        ExclusiveRateMismatchPolicy policy, WaveFormat? negotiated, WaveFormat sessionFormat,
        bool sessionIsDop = false, bool nextIsDop = false) =>
        !sessionIsDop && !nextIsDop && ExclusiveSessionRestartNeeded(
            policy, negotiated, sessionFormat) == false;

    /// <summary>
    /// Opens a session for <paramref name="first"/> and publishes it. Caller must hold
    /// <see cref="_sessionLock"/>; the driver work itself lives in <see cref="OutputSessionFactory"/>.
    /// </summary>
    private void StartSessionLocked(PendingTrack first)
    {
        TeardownSessionLocked();

        var session = _sessionFactory.Start(first);
        PublishSessionLocked(new SessionSnapshot(
            session.Sequencer, session.Output, session.Device,
            session.Exclusive, session.Driver, session.DeviceKey, session.IsDop,
            first.Reader as IStreamStallSource));

        CurrentSessionInfo = session.Info;
        SessionStarted?.Invoke(session.Info);
    }

    /// <summary>Wires sequencer events so each handler can tell whether it is still current.</summary>
    private void SubscribeSequencer(SequencerStream seq)
    {
        seq.TrackStarted += pending => OnTrackStarted(seq, pending);
        seq.SequenceEnded += (reason, endedItem) => OnSequenceEnded(seq, reason, endedItem);
        seq.ReadError += ex => OnReadError(seq, ex);
    }

    /// <summary>
    /// Watches for the output stopping on its own — endpoint unplugged or disabled, driver reset,
    /// or a render-thread failure. NAudio reports all of those only through PlaybackStopped, so
    /// with no handler the controller stayed in Playing forever: frozen position, pause glyph
    /// showing, prefetch timer still opening files, and no message to the user.
    /// </summary>
    private void SubscribeOutput(IWavePlayer output)
    {
        output.PlaybackStopped += (_, args) =>
        {
            // A clean stop is our own teardown or a drained stream, which other paths handle.
            if (args.Exception == null) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_sessionLock)
                {
                    var session = _session;
                    if (session == null || !ReferenceEquals(session.Output, output)) return;

                    // If the stream simply drained, the sequencer has already cleared its current
                    // track and OnSequenceEnded owns the transition to the next one. Only step in
                    // when a track is still loaded, which means the device really did go away.
                    if (session.Sequencer.CurrentItem == null) return;

                    TeardownSessionLocked();
                    State = PlaybackState.Stopped;
                }
                Warning?.Invoke($"오디오 출력이 중단되었습니다: {args.Exception.Message}");
                StateChanged?.Invoke();
            });
        };
    }

    /// <summary>Publishes a new session. Caller must hold <see cref="_sessionLock"/>.</summary>
    private void PublishSessionLocked(SessionSnapshot session) => Volatile.Write(ref _session, session);

    private void TeardownSessionLocked()
    {
        var session = _session;
        if (session != null)
        {
            Volatile.Write(ref _lastPositionTicks, session.Sequencer.GetPosition().Ticks);
        }
        Volatile.Write(ref _session, null);
        CurrentSessionInfo = null;
        if (session == null) return;

        try { session.Sequencer.Cancel(); } catch (Exception ex) { Log.Trace($"[playback] sequencer cancel during teardown: {ex.Message}"); }
        try { session.Output.Dispose(); } catch (Exception ex) { Log.Trace($"[playback] output dispose during teardown: {ex.Message}"); }
        try { session.Device?.Dispose(); } catch (Exception ex) { Log.Trace($"[playback] device dispose during teardown: {ex.Message}"); }
    }

    // ---------------- sequencer events (audio thread → threadpool) ----------------

    private void OnTrackStarted(SequencerStream raiser, PendingTrack pending) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Sequencer events are raised on the render thread and handled here later, so a stale
            // one must not overwrite state that a newer session already published.
            if (!ReferenceEquals(Sequencer, raiser)) return;

            // PT3-11: a gapless chain advance swapped readers entirely inside the sequencer —
            // no StartOrSwitchLocked runs — so the snapshot still points at the previous track's
            // stall observer and every underrun of the new stream would go unnoticed. Track
            // starts are the one hook that fires for every reader change; republish under the
            // session lock, re-guarded, exactly like the other session mutations.
            lock (_sessionLock)
            {
                var session = _session;
                var stall = pending.Reader as IStreamStallSource;
                if (ReferenceEquals(session?.Sequencer, raiser) &&
                    !ReferenceEquals(session.StallSource, stall))
                {
                    PublishSessionLocked(session with { StallSource = stall });
                }
            }

            PlaylistItem? prev;
            lock (_stateLock)
            {
                prev = _currentItem;
                if (prev != null && !ReferenceEquals(prev, pending.Item)) prev.IsPlaying = false;
                pending.Item.IsPlaying = true;
                _currentItem = pending.Item;
                _currentPlaylist = pending.Playlist;
                // History is pushed by the command paths (PushHistory) and by the natural-advance
                // path in OnSequenceEnded. Pushing here as well recorded every manual Next twice
                // and re-recorded the track Previous had just left, which made Previous oscillate
                // between two tracks instead of walking backwards.
            }
            // Consume this item's queue entry wherever it sits. Matching only the head left
            // entries stranded whenever the started track was not the head — an unreadable head
            // gets skipped, and the queue can be reordered while the next track is prefetched —
            // and a dead file at the head then trapped playback on one track forever.
            Queue.Consume(pending.Item);

            // Stats: a predecessor that was NOT left by a user command ended on its own — gapless
            // chain, repeat-one wrap or a format-change rebuild. Its full duration is the position.
            if (prev?.Track != null && !ReferenceEquals(prev, Volatile.Read(ref _manualLeaveMarker)))
            {
                TrackLeft?.Invoke(prev, prev.Track.Duration, PlaybackLeaveReason.NaturalEnd);
            }
            Volatile.Write(ref _manualLeaveMarker, null);

            // The A-B window is per-track; any new track resets it so the UI affordance follows.
            if (Interlocked.Exchange(ref _abStage, (int)AbRepeatStage.Off) != (int)AbRepeatStage.Off)
            {
                AbRepeatChanged?.Invoke();
            }

            // Remote bookkeeping rides here — the one hook every reader change passes through
            // (initial start, hot-swap, gapless chain, natural-advance rebuild) — so a radio
            // station reached via Next or a natural advance still gets its death warning and
            // a YouTube item its resolved metadata, not just double-click starts.
            OnRemoteTrackStarted(pending);

            CurrentChanged?.Invoke(pending.Item);
        });

    private void OnSequenceEnded(SequencerStream raiser, SequencerEndReason reason, PlaylistItem? endedItem) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Guards evaluated before the lock, and again inside it: a user command can land while
            // the next track is being opened, and the advance must not cut off what the user chose.
            var session = Volatile.Read(ref _session);
            if (State == PlaybackState.Stopped) return;
            if (session == null || !ReferenceEquals(session.Sequencer, raiser)) return;
            if (raiser.CurrentItem != null) return;

            // The sequencer has already dropped the drained item, so it arrives as an argument —
            // _currentItem cannot be trusted here, its ThreadPool update can lag this handler.
            var finished = endedItem;

            if (_stopAfterCurrent)
            {
                lock (_sessionLock)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    _stopAfterCurrent = false;
                    if (finished?.Track != null)
                    {
                        TrackLeft?.Invoke(finished, finished.Track.Duration, PlaybackLeaveReason.NaturalEnd);
                    }
                    TeardownSessionLocked();
                    State = PlaybackState.Stopped;
                }
                StateChanged?.Invoke();
                StopAfterCurrentChanged?.Invoke();
                return;
            }

            long gen = Volatile.Read(ref _commandGeneration);

            // Opening the next file (and renegotiating the exclusive format) happens outside
            // _sessionLock. It used to run inside, which meant every track boundary could block
            // any UI interaction that needed the same lock for up to 25 file-open attempts.
            var pending = raiser.TakePrefetched();
            if (pending == null)
            {
                pending = ResolveNextTrack(session, manualAdvance: false);
                if (pending == null)
                {
                    lock (_sessionLock)
                    {
                        if (ReferenceEquals(_session, session))
                        {
                            if (finished?.Track != null)
                            {
                                TrackLeft?.Invoke(finished, finished.Track.Duration, PlaybackLeaveReason.NaturalEnd);
                            }
                            TeardownSessionLocked();
                            State = PlaybackState.Stopped;
                        }
                    }
                    SetAbStage(AbRepeatStage.Off);
                    StateChanged?.Invoke();
                    return;
                }
                // The drained track is already silent, so a stream's connect/spool open is dead
                // air the user can see: say "buffering" until StartSessionLocked flips to Playing.
                // A prefetched reader was opened ahead of time — no window worth reporting.
                if (pending.Item.Track.SourceKind != TrackSourceKind.File && State == PlaybackState.Playing)
                {
                    State = PlaybackState.Buffering;
                    StateChanged?.Invoke();
                }
            }

            lock (_sessionLock)
            {
                // Re-validate: the session may have been replaced or torn down, or a command may
                // have superseded this advance, while the file was being opened.
                if (!ReferenceEquals(_session, session) ||
                    Volatile.Read(ref _commandGeneration) != gen ||
                    State == PlaybackState.Stopped)
                {
                    try { pending.Reader.Dispose(); } catch (Exception ex) { Log.Trace($"[playback] superseded advance reader dispose: {ex.Message}"); }
                    return;
                }

                try
                {
                    // A natural advance leaves the finished track behind, so this is where the
                    // history entry belongs now that OnTrackStarted no longer records one.
                    PushHistory();
                    StartSessionLocked(pending);
                    State = PlaybackState.Playing;
                }
                catch (Exception ex)
                {
                    try { pending.Reader.Dispose(); } catch (Exception dex) { Log.Trace($"[playback] reader dispose after failed advance: {dex.Message}"); }
                    TeardownSessionLocked();
                    State = PlaybackState.Stopped;
                    Log.Warn($"[playback] natural advance failed: {ex}");
                    Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.NextTrackFailed,
                        ex is AudioSessionStartException ? ex.Message : AudioErrorMessages.DescribeStartFailure(ex)));
                }
            }
            StateChanged?.Invoke();
        });

    private void OnReadError(SequencerStream raiser, Exception ex) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!ReferenceEquals(Sequencer, raiser)) return;
            Warning?.Invoke(CoreMessages.Encode(CoreMessageKey.PlaybackError, ex.Message));
            Stop();
        });

    // ---------------- next/previous resolution ----------------

    // Instance method (was static): it wires the live-metadata relay, which raises this
    // controller's events and detaches itself from TrackLeft.
    private PendingTrack BuildPending(Playlist playlist, PlaylistItem item, ITrackReader reader, TimeSpan? startPosition = null)
    {
        var pending = new PendingTrack
        {
            Playlist = playlist,
            Item = item,
            Reader = reader,
            StartPosition = startPosition
        };

        if (reader is ILiveMetadataSource live)
        {
            var detach = LiveMetadataRelay.Attach(live, item, m => StreamTitleChanged?.Invoke(m));

            // Leave is the one funnel every real play passes through (manual switch, stop, natural
            // end). A pending that never starts (superseded while opening) has no live raise left
            // after its reader is disposed — the relay dies with the reader, unhooked or not.
            void OnLeftForDetach(PlaylistItem left, TimeSpan _, PlaybackLeaveReason __)
            {
                if (!ReferenceEquals(left, item)) return;
                TrackLeft -= OnLeftForDetach;
                detach();
            }
            TrackLeft += OnLeftForDetach;
        }

        return pending;
    }

    /// <summary>
    /// Resolves the next item in play order and opens it, skipping unreadable files. Runs without
    /// <see cref="_sessionLock"/>: it only reads <paramref name="session"/>, which the caller
    /// captured, and re-validation happens where the result is installed.
    /// </summary>
    private PendingTrack? ResolveNextTrack(SessionSnapshot? session, bool manualAdvance)
    {
        var skipped = new HashSet<PlaylistItem>();
        for (int attempt = 0; attempt < 25; attempt++)
        {
            var target = _playOrder.PeekNext(CaptureOrderContext(manualAdvance), skipped);
            if (target == null) return null;
            try
            {
                var reader = AudioFileReaderFactory.Open(target.Value.Item.Track.Path, target.Value.Item.Track.SourceKind);
                bool restart = false;
                if (session is { Exclusive: true, Device: not null })
                {
                    // Probing formats is a COM round trip per candidate, so it is skipped
                    // entirely when the policy would ignore the answer anyway.
                    WaveFormat? negotiated = null;
                    if (_settings.Output.ExclusiveRateMismatch == ExclusiveRateMismatchPolicy.RestartSession)
                    {
                        negotiated = WasapiDeviceService.TryNegotiateExclusive(
                            session.Device, reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
                    }
                    restart = ExclusiveSessionRestartNeeded(
                        _settings.Output.ExclusiveRateMismatch, negotiated, session.Sequencer.WaveFormat,
                        session.IsDop, reader is DopTrackReader);
                }
                return new PendingTrack
                {
                    Playlist = target.Value.Playlist,
                    Item = target.Value.Item,
                    Reader = reader,
                    RequiresRestart = restart
                };
            }
            catch (AudioOpenException ex)
            {
                Log.Debug($"[playback] skipping unreadable '{target.Value.Item.Track.Path}': {ex.Message}");
                skipped.Add(target.Value.Item);

                // The skip set is local to this call, so an unplayable entry left in the queue
                // would be retried and skipped again on every advance — with the queue always
                // winning, that pins playback to whatever plays after it. Evict it for good.
                Queue.RemoveItems(new[] { target.Value.Item });
            }
        }
        return null;
    }

    /// <summary>
    /// Snapshots the state a play-order decision depends on. Taken under <see cref="_stateLock"/>
    /// so the resolver itself can run unlocked.
    /// </summary>
    private PlayOrderContext CaptureOrderContext(bool manualAdvance)
    {
        lock (_stateLock)
        {
            return new PlayOrderContext(_currentPlaylist, _currentItem, _stopAfterCurrent, manualAdvance);
        }
    }

    private (Playlist?, PlaylistItem?) LastPlayableContext()
    {
        lock (_stateLock)
        {
            if (_currentPlaylist != null && _currentItem != null)
            {
                var snap = _currentPlaylist.GetSnapshot();
                if (Array.IndexOf(snap, _currentItem) >= 0)
                    return (_currentPlaylist, _currentItem);
            }

            var pl = _currentPlaylist ?? _playlists.TryGetCurrent();
            if (pl != null)
            {
                var snap = pl.GetSnapshot();
                if (snap.Length > 0) return (pl, snap[0]);
            }

            var nonEmpty = _playlists.Playlists.FirstOrDefault(p => p.GetSnapshot().Length > 0);
            if (nonEmpty != null)
            {
                var snap = nonEmpty.GetSnapshot();
                if (snap.Length > 0)
                {
                    _playlists.SelectPlaylist(nonEmpty);
                    return (nonEmpty, snap[0]);
                }
            }

            return (null, null);
        }
    }

    private void PushHistory()
    {
        lock (_stateLock)
        {
            if (_currentPlaylist != null && _currentItem != null)
                _history.Push((_currentPlaylist, _currentItem));
        }
    }

    // ---------------- gain ----------------

    /// <summary>
    /// Gain for the PRE-CHAIN ReplayGain node: source correction only (boost-capable, limiter-
    /// protected). When the normalizer is enabled it owns ReplayGain via its static paths, so the
    /// node passes unity. The master fader is a separate post-chain multiply and carries no
    /// ReplayGain — see <see cref="Volume"/>.
    /// </summary>
    private float ComputeReplayGainNodeGain(Track? track)
    {
        if (_settings.Normalizer.Enabled) return 1f;

        return ReplayGainMath.ComputeReplayGainOnly(
            track,
            _settings.Playback.ReplayGain,
            _settings.Playback.ReplayGainPreampDb,
            _settings.Playback.ReplayGainPreventClipping) ?? 1f;
    }

    private float? ComputeReplayGain(Track? track) =>
        ReplayGainMath.ComputeReplayGainOnly(
            track,
            _settings.Playback.ReplayGain,
            _settings.Playback.ReplayGainPreampDb,
            _settings.Playback.ReplayGainPreventClipping);

    // ---------------- prefetch ----------------

    private void PollPrefetch()
    {
        if (State != PlaybackState.Playing) return;

        var session = Volatile.Read(ref _session);
        var seq = session?.Sequencer;
        if (seq == null || seq.HasPrefetched || seq.PrefetchPending) return;
        if (seq.RemainingTime > TimeSpan.FromSeconds(1.2)) return;

        seq.PrefetchPending = true;
        Task.Run(() =>
        {
            try
            {
                long gen = Volatile.Read(ref _commandGeneration);

                // Resolved without _sessionLock: this opens files (up to 25 attempts when entries
                // are unreadable) and probes the endpoint format, and doing that under the session
                // lock stalled every UI path that needed it.
                var pending = ResolveNextTrack(session, manualAdvance: false);
                if (pending == null) return;

                bool installed = false;
                lock (_sessionLock)
                {
                    if (ReferenceEquals(_session, session) &&
                        Volatile.Read(ref _commandGeneration) == gen &&
                        State == PlaybackState.Playing)
                    {
                        seq.SetPrefetched(pending);
                        installed = true;
                    }
                }

                // Losing the race costs one wasted open, which is why it is safe to resolve first.
                if (!installed)
                {
                    try { pending.Reader.Dispose(); } catch (Exception ex) { Log.Trace($"[playback] lost prefetch race reader dispose: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[playback] prefetch abandoned: {ex.Message}");
                seq.SetPrefetched(null);
            }
            finally
            {
                seq.PrefetchPending = false;
            }
        });
    }

    private static string FormatKey(WaveFormat f) => $"{f.SampleRate}|{f.Channels}|{f.BitsPerSample}";
}
