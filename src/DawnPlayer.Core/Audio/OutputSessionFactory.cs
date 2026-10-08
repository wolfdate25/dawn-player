using System;
using System.Linq;
using System.Globalization;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// A freshly opened output session: the sequencer feeding it, the player, and the endpoint it
/// belongs to. Ownership transfers to the caller, which must dispose all three on teardown.
/// </summary>
public sealed record OutputSession(
    SequencerStream Sequencer,
    IWavePlayer Output,
    MMDevice? Device,
    bool Exclusive,
    AudioDriverType Driver,
    string DeviceKey,
    SessionInfo Info,
    bool IsDop = false);

/// <summary>
/// Opens an output session for the configured driver through the registered
/// <see cref="IOutputDriver"/> chain. The driver set is process-wide: register additional
/// backends once at startup; resolution falls back to the WASAPI driver when a type has none.
/// </summary>
/// <remarks>
/// Separated from the playback controller because this is the slow, driver-facing half — device
/// enumeration, format negotiation and <c>Init</c>/<c>Play</c> — and it needs none of the
/// controller's playback state. The controller supplies the subscription hooks so each event
/// handler can still tell whether the session that raised it is still current.
/// </remarks>
public sealed class OutputSessionFactory
{
    private static readonly object _driverGate = new();
    private static readonly Dictionary<AudioDriverType, IOutputDriver> _builtIns = CreateDefaultDrivers();
    private static Dictionary<AudioDriverType, IOutputDriver> _drivers = new(_builtIns);

    private static Dictionary<AudioDriverType, IOutputDriver> CreateDefaultDrivers() => new()
    {
        [AudioDriverType.Wasapi] = new WasapiOutputDriver(),
        [AudioDriverType.DirectSound] = new DirectSoundOutputDriver(),
        [AudioDriverType.WaveOut] = new WaveOutOutputDriver(),
    };

    private readonly AppSettings _settings;
    // Supplies the pre-chain ReplayGain node gain (source correction). The master fader value is
    // read from settings at session construction — see CreateSequencer.
    private readonly Func<Track, float> _replayGainNodeGainProvider;
    private readonly Func<Track, float?> _replayGainProvider;
    private readonly Action<SequencerStream> _subscribeSequencer;
    private readonly Action<IWavePlayer> _subscribeOutput;
    private readonly Action<string> _warn;
    private readonly Func<Dsp.Plugins.PluginDspEffect?>? _pluginDsp;

    public OutputSessionFactory(
        AppSettings settings,
        Func<Track, float> replayGainNodeGainProvider,
        Func<Track, float?> replayGainProvider,
        Action<SequencerStream> subscribeSequencer,
        Action<IWavePlayer> subscribeOutput,
        Action<string> warn,
        Func<Dsp.Plugins.PluginDspEffect?>? pluginDsp = null)
    {
        _pluginDsp = pluginDsp;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _replayGainNodeGainProvider = replayGainNodeGainProvider ?? throw new ArgumentNullException(nameof(replayGainNodeGainProvider));
        _replayGainProvider = replayGainProvider ?? throw new ArgumentNullException(nameof(replayGainProvider));
        _subscribeSequencer = subscribeSequencer ?? throw new ArgumentNullException(nameof(subscribeSequencer));
        _subscribeOutput = subscribeOutput ?? throw new ArgumentNullException(nameof(subscribeOutput));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>Snapshot of the registered drivers, keyed by the driver type they serve.</summary>
    public static IReadOnlyDictionary<AudioDriverType, IOutputDriver> Drivers
    {
        get { lock (_driverGate) return new Dictionary<AudioDriverType, IOutputDriver>(_drivers); }
    }

    /// <summary>Registers (or replaces) the driver for its <see cref="IOutputDriver.DriverType"/>.</summary>
    public static void RegisterDriver(IOutputDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        lock (_driverGate)
        {
            var next = new Dictionary<AudioDriverType, IOutputDriver>(_drivers)
            {
                [driver.DriverType] = driver,
            };
            _drivers = next;
        }
    }

    /// <summary>Removes the driver registered for <paramref name="type"/>, restoring the
    /// built-in for that type (or the WASAPI fallback when the type has none). Returns false
    /// when nothing was registered beyond the built-in. The WASAPI driver itself cannot be
    /// removed — it is the fallback.</summary>
    public static bool UnregisterDriver(AudioDriverType type)
    {
        lock (_driverGate)
        {
            if (type == AudioDriverType.Wasapi || !_drivers.TryGetValue(type, out var current) ||
                ReferenceEquals(current, _builtIns.GetValueOrDefault(type)))
            {
                return false;
            }

            var next = new Dictionary<AudioDriverType, IOutputDriver>(_drivers);
            next.Remove(type);
            if (_builtIns.TryGetValue(type, out var builtIn)) next[type] = builtIn;
            _drivers = next;
            return true;
        }
    }

    /// <summary>The driver serving <paramref name="type"/>, falling back to WASAPI.</summary>
    public static IOutputDriver ResolveDriver(AudioDriverType type)
    {
        lock (_driverGate)
        {
            return _drivers.GetValueOrDefault(type) ?? _drivers[AudioDriverType.Wasapi];
        }
    }

    /// <summary>
    /// Opens a session and starts it playing <paramref name="first"/>. Throws
    /// <see cref="AudioSessionStartException"/> when the failure has already been explained to the
    /// user, or a driver exception otherwise.
    /// </summary>
    public OutputSession Start(PendingTrack first)
    {
        ArgumentNullException.ThrowIfNull(first);
        var latency = Math.Clamp(_settings.Output.LatencyMs, 20, 1000);

        var driver = ResolveDriver(_settings.Output.DriverType);
        return driver.Start(new OutputSessionRequest(
            first,
            latency,
            _settings,
            _replayGainNodeGainProvider,
            _replayGainProvider,
            _subscribeSequencer,
            _subscribeOutput,
            _warn,
            _pluginDsp,
            (pending, target, applyVolume, eqProfile) => CreateSequencer(pending, target, applyVolume, latency, eqProfile),
            ReopenPending,
            DsdSupport.ReopenAsPcm));
    }

    private SequencerStream CreateSequencer(PendingTrack pending, WaveFormat target, bool applyVolume, int latency, EqProfile eqProfile)
    {
        // DoP containers ride the pipeline as opaque words — volume/EQ arithmetic corrupts the
        // markers, so such sessions run the sequencer in raw-passthrough mode.
        bool rawPassthrough = DsdSupport.IsRawDsdReader(pending.Reader);
        var seq = new SequencerStream(
            target, applyVolume, _replayGainNodeGainProvider, latency, eqProfile, _settings.Normalizer, _replayGainProvider,
            _settings.Crossfeed, _settings.Playback.MonoDownmixEnabled,
            dspChain: null, pluginDsp: _pluginDsp?.Invoke(), rawPassthrough: rawPassthrough,
            initialMasterGain: (float)Math.Clamp(_settings.Playback.Volume, 0.0, 1.0));
        _subscribeSequencer(seq);
        return seq;
    }

    /// <summary>Re-opens the source file for a pending track whose reader a torn-down sequencer
    /// has already consumed and disposed.</summary>
    private static PendingTrack ReopenPending(PendingTrack pending) => new()
    {
        Playlist = pending.Playlist,
        Item = pending.Item,
        Reader = AudioFileReaderFactory.Open(pending.Item.Track.Path),
        StartPosition = pending.StartPosition,
        RequiresRestart = pending.RequiresRestart
    };
}
