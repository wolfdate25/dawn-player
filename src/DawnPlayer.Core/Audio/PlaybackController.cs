using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

public enum PlaybackState { Stopped, Playing, Paused }

public sealed record SessionInfo(string DeviceName, bool Exclusive, string FormatDescription, int LatencyMs);

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
public sealed class PlaybackController : IPlaybackController
{
    private readonly AppSettings _settings;
    private readonly PlaylistManager _playlists;
    private readonly PlayOrderResolver _playOrder;
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
        string? DeviceKey);

    private readonly object _sessionLock = new();
    private SessionSnapshot? _session;

    private readonly System.Threading.Timer _pollTimer;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private long _commandGeneration;
    private int _disposed;

    private readonly object _stateLock = new();
    private PlaylistItem? _currentItem;
    private Playlist? _currentPlaylist;
    private readonly Stack<(Playlist Playlist, PlaylistItem Item)> _history = new();

    // An enum cannot be volatile, so the backing int is what gets published. State is written from
    // command paths (no lock) and from session paths (under _sessionLock) alike.
    private int _state;
    public PlaybackState State
    {
        get => (PlaybackState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
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
    public event Action? StopAfterCurrentChanged;
    public event Action<string>? Warning;
    public event Action<SessionInfo>? SessionStarted;

    public bool IsExclusiveSession => Volatile.Read(ref _session)?.Exclusive ?? false;
    public SessionInfo? CurrentSessionInfo { get; private set; }

    public PlaybackController(AppSettings settings, PlaylistManager playlists)
    {
        _settings = settings;
        _playlists = playlists;
        // TryGetCurrent, not Current: resolution runs on the thread pool and the creating
        // accessors insert into the UI-bound playlist collection.
        _playOrder = new PlayOrderResolver(settings, Queue, () => _playlists.TryGetCurrent());
        _sessionFactory = new OutputSessionFactory(
            settings,
            ComputeGain,
            ComputeReplayGain,
            SubscribeSequencer,
            SubscribeOutput,
            message => Warning?.Invoke(message));
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

    public TimeSpan Position => Sequencer?.GetPosition() ?? TimeSpan.Zero;
    public TimeSpan Duration => Sequencer?.TotalTime ?? CurrentItem?.Track.Duration ?? TimeSpan.Zero;

    // ---------------- public commands ----------------

    /// <summary>Starts playing a specific playlist item (double-click / Play).</summary>
    public async Task PlayAsync(Playlist playlist, PlaylistItem item)
    {
        long cmdId = Interlocked.Increment(ref _commandGeneration);
        PendingTrack pending;
        try
        {
            var reader = await Task.Run(() => AudioFileReaderFactory.Open(item.Track.Path));
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
        var session = Volatile.Read(ref _session);
        PendingTrack? pending = await Task.Run(() => ResolveNextTrack(session, manualAdvance: true))
            .ConfigureAwait(false);
        if (pending == null)
        {
            Warning?.Invoke("다음 트랙이 없습니다.");
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
            if (State == PlaybackState.Stopped) PlayPause();
            return;
        }

        PendingTrack pending;
        try
        {
            var reader = await Task.Run(() => AudioFileReaderFactory.Open(target.Value.item.Track.Path));
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

    public double Volume
    {
        get => _settings.Playback.Volume;
        set
        {
            _settings.Playback.Volume = Math.Clamp(value, 0, 1);
            Sequencer?.SetGain(ComputeGain(CurrentItem?.Track));
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
        seq.SetGain(ComputeGain(CurrentItem?.Track));
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
        if (State == PlaybackState.Stopped || item == null || pl == null) return;

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
                var reader = AudioFileReaderFactory.Open(item.Track.Path);
                await PlayPendingAsync(BuildPending(pl, item, reader, startPosition: pos), pushHistory: false, restartCmdId);
                if (Volatile.Read(ref _commandGeneration) != restartCmdId) return;
                if (resumePaused)
                {
                    var restarted = Sequencer;
                    if (restarted != null) restarted.IsPaused = true;
                    State = PlaybackState.Paused;
                    StateChanged?.Invoke();
                }
            }
            catch (AudioOpenException) { }
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

    private async Task PlayPendingAsync(PendingTrack pending, bool pushHistory, long cmdId)
    {
        if (pushHistory) PushHistory();
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
                Warning?.Invoke($"재생 시작 실패: {(ex is AudioSessionStartException ? ex.Message : AudioErrorMessages.DescribeStartFailure(ex))}");
            }
            finally
            {
                // The session owns the reader only once it has been handed to a sequencer. On a
                // failed start nobody else will, and leaking it holds an OS handle on the file for
                // the rest of the process lifetime.
                if (!started && !ReferenceEquals(Sequencer?.CurrentItem, pending.Item))
                {
                    try { pending.Reader.Dispose(); } catch { }
                }
            }
        }
        StateChanged?.Invoke();
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
            return;
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
                    return !string.IsNullOrEmpty(_settings.Output.DeviceId)
                           && Guid.TryParse(_settings.Output.DeviceId, out var dsGuid)
                        ? dsGuid.ToString()
                        : DirectSoundOut.DSDEVID_DefaultPlayback.ToString();
                case AudioDriverType.WaveOut:
                    return int.TryParse(_settings.Output.DeviceId, out var waveNum)
                        ? waveNum.ToString()
                        : "-1";
                default:
                {
                    if (!string.IsNullOrEmpty(_settings.Output.DeviceId)) return _settings.Output.DeviceId;
                    return DefaultRenderEndpointId();
                }
            }
        }
        catch
        {
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
        var negotiated = WasapiDeviceService.TryNegotiateExclusive(
            session.Device, pending.Reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
        return negotiated != null && FormatKey(negotiated) == FormatKey(session.Sequencer.WaveFormat);
    }

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
            session.Exclusive, session.Driver, session.DeviceKey));

        CurrentSessionInfo = session.Info;
        SessionStarted?.Invoke(session.Info);
    }

    /// <summary>Wires sequencer events so each handler can tell whether it is still current.</summary>
    private void SubscribeSequencer(SequencerStream seq)
    {
        seq.TrackStarted += pending => OnTrackStarted(seq, pending);
        seq.SequenceEnded += reason => OnSequenceEnded(seq, reason);
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
        Volatile.Write(ref _session, null);
        CurrentSessionInfo = null;
        if (session == null) return;

        try { session.Sequencer.Cancel(); } catch { }
        try { session.Output.Dispose(); } catch { }
        try { session.Device?.Dispose(); } catch { }
    }

    // ---------------- sequencer events (audio thread → threadpool) ----------------

    private void OnTrackStarted(SequencerStream raiser, PendingTrack pending) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Sequencer events are raised on the render thread and handled here later, so a stale
            // one must not overwrite state that a newer session already published.
            if (!ReferenceEquals(Sequencer, raiser)) return;

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

            CurrentChanged?.Invoke(pending.Item);
        });

    private void OnSequenceEnded(SequencerStream raiser, SequencerEndReason reason) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Guards evaluated before the lock, and again inside it: a user command can land while
            // the next track is being opened, and the advance must not cut off what the user chose.
            var session = Volatile.Read(ref _session);
            if (State == PlaybackState.Stopped) return;
            if (session == null || !ReferenceEquals(session.Sequencer, raiser)) return;
            if (raiser.CurrentItem != null) return;

            if (_stopAfterCurrent)
            {
                lock (_sessionLock)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    _stopAfterCurrent = false;
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
            var pending = raiser.TakePrefetched() ?? ResolveNextTrack(session, manualAdvance: false);
            if (pending == null)
            {
                lock (_sessionLock)
                {
                    if (ReferenceEquals(_session, session))
                    {
                        TeardownSessionLocked();
                        State = PlaybackState.Stopped;
                    }
                }
                StateChanged?.Invoke();
                return;
            }

            lock (_sessionLock)
            {
                // Re-validate: the session may have been replaced or torn down, or a command may
                // have superseded this advance, while the file was being opened.
                if (!ReferenceEquals(_session, session) ||
                    Volatile.Read(ref _commandGeneration) != gen ||
                    State == PlaybackState.Stopped)
                {
                    try { pending.Reader.Dispose(); } catch { }
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
                    try { pending.Reader.Dispose(); } catch { }
                    TeardownSessionLocked();
                    State = PlaybackState.Stopped;
                    Warning?.Invoke($"다음 트랙 재생 실패: {(ex is AudioSessionStartException ? ex.Message : AudioErrorMessages.DescribeStartFailure(ex))}");
                }
            }
            StateChanged?.Invoke();
        });

    private void OnReadError(SequencerStream raiser, Exception ex) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!ReferenceEquals(Sequencer, raiser)) return;
            Warning?.Invoke($"재생 오류: {ex.Message}");
            Stop();
        });

    // ---------------- next/previous resolution ----------------

    private static PendingTrack BuildPending(Playlist playlist, PlaylistItem item, ITrackReader reader, TimeSpan? startPosition = null) => new()
    {
        Playlist = playlist,
        Item = item,
        Reader = reader,
        StartPosition = startPosition
    };

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
                var reader = AudioFileReaderFactory.Open(target.Value.Item.Track.Path);
                bool restart = false;
                if (session is { Exclusive: true, Device: not null })
                {
                    var negotiated = WasapiDeviceService.TryNegotiateExclusive(
                        session.Device, reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
                    restart = negotiated == null || FormatKey(negotiated) != FormatKey(session.Sequencer.WaveFormat);
                }
                return new PendingTrack
                {
                    Playlist = target.Value.Playlist,
                    Item = target.Value.Item,
                    Reader = reader,
                    RequiresRestart = restart
                };
            }
            catch (AudioOpenException)
            {
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

    private float ComputeGain(Track? track)
    {
        if (_settings.Normalizer.Enabled)
        {
            return (float)_settings.Playback.Volume;
        }

        return ReplayGainMath.ComputeGain(
            track,
            _settings.Playback.Volume,
            _settings.Playback.ReplayGain,
            _settings.Playback.ReplayGainPreampDb,
            _settings.Playback.ReplayGainPreventClipping);
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
                    try { pending.Reader.Dispose(); } catch { }
                }
            }
            catch
            {
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
