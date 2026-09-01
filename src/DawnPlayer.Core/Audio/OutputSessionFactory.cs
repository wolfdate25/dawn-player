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
    SessionInfo Info);

/// <summary>
/// Opens an output session for the configured driver: WASAPI (exclusive with shared fallback),
/// DirectSound, or WaveOut.
/// </summary>
/// <remarks>
/// Separated from the playback controller because this is the slow, driver-facing half — device
/// enumeration, format negotiation and <c>Init</c>/<c>Play</c> — and it needs none of the
/// controller's playback state. The controller supplies the subscription hooks so each event
/// handler can still tell whether the session that raised it is still current.
/// </remarks>
public sealed class OutputSessionFactory
{
    private readonly AppSettings _settings;
    private readonly Func<Track, float> _gainProvider;
    private readonly Func<Track, float?> _replayGainProvider;
    private readonly Action<SequencerStream> _subscribeSequencer;
    private readonly Action<IWavePlayer> _subscribeOutput;
    private readonly Action<string> _warn;

    public OutputSessionFactory(
        AppSettings settings,
        Func<Track, float> gainProvider,
        Func<Track, float?> replayGainProvider,
        Action<SequencerStream> subscribeSequencer,
        Action<IWavePlayer> subscribeOutput,
        Action<string> warn)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gainProvider = gainProvider ?? throw new ArgumentNullException(nameof(gainProvider));
        _replayGainProvider = replayGainProvider ?? throw new ArgumentNullException(nameof(replayGainProvider));
        _subscribeSequencer = subscribeSequencer ?? throw new ArgumentNullException(nameof(subscribeSequencer));
        _subscribeOutput = subscribeOutput ?? throw new ArgumentNullException(nameof(subscribeOutput));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
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

        return _settings.Output.DriverType switch
        {
            AudioDriverType.DirectSound => StartDirectSound(first, latency),
            AudioDriverType.WaveOut => StartWaveOut(first, latency),
            _ => StartWasapi(first, latency)
        };
    }

    private SequencerStream CreateSequencer(WaveFormat target, bool applyVolume, int latency, EqProfile eqProfile)
    {
        var seq = new SequencerStream(
            target, applyVolume, _gainProvider, latency, eqProfile, _settings.Normalizer, _replayGainProvider);
        _subscribeSequencer(seq);
        return seq;
    }

    private OutputSession StartDirectSound(PendingTrack first, int latency)
    {
        Guid dsGuid = WasapiDeviceService.ResolveDirectSoundDevice(_settings.Output.DeviceId);
        if (!string.IsNullOrEmpty(_settings.Output.DeviceId)
            && (!Guid.TryParse(_settings.Output.DeviceId, out var configuredGuid) || configuredGuid != dsGuid))
        {
            _warn("설정된 DirectSound 장치를 찾을 수 없어 기본 장치로 재생합니다.");
        }

        var rate = first.Reader.SourceFormat.SampleRate > 0 ? first.Reader.SourceFormat.SampleRate : 44100;
        var channels = first.Reader.SourceFormat.Channels > 0 ? first.Reader.SourceFormat.Channels : 2;
        var target = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.DirectSound, dsGuid.ToString());

        var seq = CreateSequencer(target, applyVolume: true, latency, eqProfile);
        seq.SwitchTo(first);

        var dsOutput = new DirectSoundOut(dsGuid, latency);
        _subscribeOutput(dsOutput);
        dsOutput.Init(seq);
        dsOutput.Play();

        var devInfo = WasapiDeviceService.EnumerateDirectSoundDevices().FirstOrDefault(d => d.Id == dsGuid.ToString());
        string devName = devInfo?.Name ?? "DirectSound (Windows Audio)";
        var info = new SessionInfo(devName, false, $"DirectSound • {rate / 1000.0:0.#}kHz / 32-bit float", latency, AudioDriverType.DirectSound);

        return new OutputSession(seq, dsOutput, Device: null, Exclusive: false,
            AudioDriverType.DirectSound, dsGuid.ToString(), info);
    }

    private OutputSession StartWaveOut(PendingTrack first, int latency)
    {
        int devNum = WasapiDeviceService.ResolveWaveOutDeviceNumber(_settings.Output.DeviceId);
        if (!string.IsNullOrEmpty(_settings.Output.DeviceId)
            && (!int.TryParse(_settings.Output.DeviceId, out var configuredNum) || configuredNum != devNum))
        {
            _warn("설정된 WaveOut 장치를 찾을 수 없어 기본 사운드 매퍼로 재생합니다.");
        }

        var rate = first.Reader.SourceFormat.SampleRate > 0 ? first.Reader.SourceFormat.SampleRate : 44100;
        var channels = first.Reader.SourceFormat.Channels > 0 ? first.Reader.SourceFormat.Channels : 2;
        var target = new WaveFormat(rate, 16, channels);
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.WaveOut, devNum.ToString(CultureInfo.InvariantCulture));

        var seq = CreateSequencer(target, applyVolume: true, latency, eqProfile);
        seq.SwitchTo(first);

        var waveOut = new WaveOutEvent { DeviceNumber = devNum, DesiredLatency = latency };
        _subscribeOutput(waveOut);
        waveOut.Init(seq);
        waveOut.Play();

        var devInfo = WasapiDeviceService.EnumerateWaveOutDevices().FirstOrDefault(d => d.Id == devNum.ToString(CultureInfo.InvariantCulture));
        string devName = devInfo?.Name ?? "WaveOut (Windows Audio)";
        var info = new SessionInfo(devName, false, $"WaveOut • {rate / 1000.0:0.#}kHz / 16-bit", latency, AudioDriverType.WaveOut);

        return new OutputSession(seq, waveOut, Device: null, Exclusive: false,
            AudioDriverType.WaveOut, devNum.ToString(CultureInfo.InvariantCulture), info);
    }

    private OutputSession StartWasapi(PendingTrack first, int latency)
    {
        var device = WasapiDeviceService.OpenDevice(_settings.Output.DeviceId);
        if (device == null)
            throw new InvalidOperationException("오디오 출력 장치를 찾을 수 없습니다.");

        bool exclusive = _settings.Output.UseExclusiveMode;

        WaveFormat? target = null;
        if (exclusive)
            target = WasapiDeviceService.TryNegotiateExclusive(
                device, first.Reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
        if (target == null)
        {
            if (exclusive)
                _warn(AudioErrorMessages.BuildExclusiveFailureReason(device, first.Reader.SourceFormat));
            exclusive = false;
            target = WasapiDeviceService.GetSharedTarget(device);
        }

        var applyVolume = !exclusive || _settings.Output.AllowVolumeInExclusive;
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.Wasapi, device.ID);
        var seq = CreateSequencer(target, applyVolume, latency, eqProfile);
        seq.SwitchTo(first); // initial load

        var output = new WasapiOut(device,
            exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared,
            useEventSync: true, latency);
        _subscribeOutput(output);
        try
        {
            output.Init(seq);
            output.Play();
        }
        catch (Exception exclusiveFailure) when (exclusive)
        {
            output.Dispose();

            // Cancel() disposes the reader the failed sequencer took ownership of in SwitchTo,
            // so the shared-mode retry needs a freshly opened one — reusing the disposed reader
            // would make the fallback session play silence.
            seq.Cancel();
            first = ReopenPending(first);

            exclusive = false;
            target = WasapiDeviceService.GetSharedTarget(device);
            applyVolume = true;
            seq = CreateSequencer(target, applyVolume, latency, eqProfile);
            seq.SwitchTo(first);

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
            _subscribeOutput(output);
            try
            {
                output.Init(seq);
                output.Play();
            }
            catch (Exception sharedFailure)
            {
                // While another application holds the endpoint in exclusive mode, Windows suspends
                // the shared mixer too, so the fallback cannot succeed either. Announcing "falling
                // back to shared mode" before knowing that, then reporting a bare HRESULT, told the
                // user nothing. Report the real reason once, and only once.
                output.Dispose();
                seq.Cancel();
                throw new AudioSessionStartException(
                    AudioErrorMessages.DescribeStartFailure(sharedFailure, exclusiveFailure), sharedFailure);
            }

            // Only now is the claim true.
            _warn("WASAPI 배타 모드를 열 수 없습니다 (다른 프로그램이 장치 사용 중). 공유 모드로 재생합니다.");
        }

        var info = new SessionInfo(
            device.FriendlyName, exclusive, WasapiDeviceService.Describe(target), latency, AudioDriverType.Wasapi);

        return new OutputSession(seq, output, device, exclusive, AudioDriverType.Wasapi, device.ID, info);
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
