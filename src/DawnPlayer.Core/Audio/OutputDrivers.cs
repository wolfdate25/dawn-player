using System;
using System.Globalization;
using System.Linq;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Everything an output driver needs to build one session. The factory (composition root)
/// assembles this; drivers stay stateless and pull what they need from the request.
/// </summary>
public sealed record OutputSessionRequest(
    PendingTrack First,
    int Latency,
    AppSettings Settings,
    Func<Track, float> GainProvider,
    Func<Track, float?> ReplayGainProvider,
    Action<SequencerStream> SubscribeSequencer,
    Action<IWavePlayer> SubscribeOutput,
    Action<string> Warn,
    Func<Dsp.Plugins.PluginDspEffect?>? PluginDsp,
    Func<PendingTrack, WaveFormat, bool, EqProfile, SequencerStream> CreateSequencer,
    Func<PendingTrack, PendingTrack> ReopenPending,
    Func<PendingTrack, PendingTrack> ReopenPendingAsPcm);

/// <summary>
/// Output driver seam: opens and starts one output session for <see cref="OutputSessionRequest.First"/>.
/// Implementations are selected by <see cref="DriverType"/> through
/// <see cref="OutputSessionFactory"/>'s registry — a new backend (e.g. ASIO) is a registration,
/// not a factory edit. Must be stateless and thread-safe: sessions are opened from the thread
/// pool under the controller's session lock.
/// </summary>
public interface IOutputDriver
{
    AudioDriverType DriverType { get; }

    /// <summary>Opens a session and starts it playing <see cref="OutputSessionRequest.First"/>.
    /// Throws <see cref="AudioSessionStartException"/> when the failure has already been
    /// explained to the user, or a driver exception otherwise.</summary>
    OutputSession Start(OutputSessionRequest request);
}

/// <summary>WASAPI: exclusive mode with format negotiation and automatic shared fallback.</summary>
public sealed class WasapiOutputDriver : IOutputDriver
{
    public AudioDriverType DriverType => AudioDriverType.Wasapi;

    public OutputSession Start(OutputSessionRequest request)
    {
        var first = request.First;
        var latency = request.Latency;
        var _settings = request.Settings;

        var device = WasapiDeviceService.OpenDevice(_settings.Output.DeviceId);
        if (device == null)
            throw new InvalidOperationException(CoreMessages.Encode(CoreMessageKey.OutputDeviceNotFound));

        bool exclusive = _settings.Output.UseExclusiveMode;

        // DoP gating: packed DSD can only ride an exclusive session whose endpoint accepted the
        // DoP rate. Anything else (shared mode, probe rejection, a previous rejection of this
        // device) must swap to the plain boxcar-PCM reader first — resampling or mixing DoP
        // frames produces garbage, and warning on every track would be noise.
        if (DsdSupport.IsRawDsdReader(first.Reader) && DsdSupport.PlaybackMode == DsdPlaybackMode.DoPPriority)
        {
            bool dopUsable = exclusive && !DsdSupport.IsDoPBlocked
                && WasapiDeviceService.TryNegotiateExclusive(
                    device, first.Reader.SourceFormat, _settings.Output.ExclusiveBitDepth) != null;
            if (!dopUsable)
            {
                if (!DsdSupport.IsDoPBlocked)
                {
                    DsdSupport.BlockDoP();
                    request.Warn(CoreMessages.Encode(CoreMessageKey.DoPUnsupportedFallback));
                }
                first = request.ReopenPendingAsPcm(first);
            }
        }

        WaveFormat? target = null;
        if (exclusive)
            target = WasapiDeviceService.TryNegotiateExclusive(
                device, first.Reader.SourceFormat, _settings.Output.ExclusiveBitDepth);
        if (target == null)
        {
            if (exclusive)
                request.Warn(AudioErrorMessages.BuildExclusiveFailureReason(device, first.Reader.SourceFormat));
            exclusive = false;
            target = WasapiDeviceService.GetSharedTarget(device);
        }

        var applyVolume = !exclusive || _settings.Output.AllowVolumeInExclusive;
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.Wasapi, device.ID);
        var seq = request.CreateSequencer(first, target, applyVolume, eqProfile);
        seq.SwitchTo(first); // initial load

        var output = new WasapiOut(device,
            exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared,
            useEventSync: true, latency);
        request.SubscribeOutput(output);
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
            first = request.ReopenPending(first);

            exclusive = false;
            target = WasapiDeviceService.GetSharedTarget(device);
            applyVolume = true;
            seq = request.CreateSequencer(first, target, applyVolume, eqProfile);
            seq.SwitchTo(first);

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
            request.SubscribeOutput(output);
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
            request.Warn(CoreMessages.Encode(CoreMessageKey.ExclusiveFallbackShared));
        }

        var info = new SessionInfo(
            device.FriendlyName, exclusive, WasapiDeviceService.Describe(target), latency, AudioDriverType.Wasapi,
            IsDop: DsdSupport.IsRawDsdReader(first.Reader));

        return new OutputSession(seq, output, device, exclusive, AudioDriverType.Wasapi, device.ID, info,
            IsDop: DsdSupport.IsRawDsdReader(first.Reader));
    }
}

/// <summary>DirectSound legacy path: float format at the source rate, volume applied in DSP.</summary>
public sealed class DirectSoundOutputDriver : IOutputDriver
{
    public AudioDriverType DriverType => AudioDriverType.DirectSound;

    public OutputSession Start(OutputSessionRequest request)
    {
        var first = request.First;
        var latency = request.Latency;
        var _settings = request.Settings;

        Guid dsGuid = WasapiDeviceService.ResolveDirectSoundDevice(_settings.Output.DeviceId);
        if (!string.IsNullOrEmpty(_settings.Output.DeviceId)
            && (!Guid.TryParse(_settings.Output.DeviceId, out var configuredGuid) || configuredGuid != dsGuid))
        {
            request.Warn(CoreMessages.Encode(CoreMessageKey.DirectSoundDeviceMissing));
        }

        var rate = first.Reader.SourceFormat.SampleRate > 0 ? first.Reader.SourceFormat.SampleRate : 44100;
        var channels = first.Reader.SourceFormat.Channels > 0 ? first.Reader.SourceFormat.Channels : 2;
        var target = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.DirectSound, dsGuid.ToString());

        var seq = request.CreateSequencer(first, target, true, eqProfile);
        seq.SwitchTo(first);

        var dsOutput = new DirectSoundOut(dsGuid, latency);
        request.SubscribeOutput(dsOutput);
        dsOutput.Init(seq);
        dsOutput.Play();

        var devInfo = WasapiDeviceService.EnumerateDirectSoundDevices().FirstOrDefault(d => d.Id == dsGuid.ToString());
        string devName = devInfo?.Name ?? "DirectSound (Windows Audio)";
        var info = new SessionInfo(devName, false, $"DirectSound • {rate / 1000.0:0.#}kHz / 32-bit float", latency, AudioDriverType.DirectSound);

        return new OutputSession(seq, dsOutput, Device: null, Exclusive: false,
            AudioDriverType.DirectSound, dsGuid.ToString(), info);
    }
}

/// <summary>WaveOut legacy path: 16-bit PCM at the source rate, volume applied in DSP.</summary>
public sealed class WaveOutOutputDriver : IOutputDriver
{
    public AudioDriverType DriverType => AudioDriverType.WaveOut;

    public OutputSession Start(OutputSessionRequest request)
    {
        var first = request.First;
        var latency = request.Latency;
        var _settings = request.Settings;

        int devNum = WasapiDeviceService.ResolveWaveOutDeviceNumber(_settings.Output.DeviceId);
        if (!string.IsNullOrEmpty(_settings.Output.DeviceId)
            && (!int.TryParse(_settings.Output.DeviceId, out var configuredNum) || configuredNum != devNum))
        {
            request.Warn(CoreMessages.Encode(CoreMessageKey.WaveOutDeviceMissing));
        }

        var rate = first.Reader.SourceFormat.SampleRate > 0 ? first.Reader.SourceFormat.SampleRate : 44100;
        var channels = first.Reader.SourceFormat.Channels > 0 ? first.Reader.SourceFormat.Channels : 2;
        var target = new WaveFormat(rate, 16, channels);
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.WaveOut, devNum.ToString(CultureInfo.InvariantCulture));

        var seq = request.CreateSequencer(first, target, true, eqProfile);
        seq.SwitchTo(first);

        var waveOut = new WaveOutEvent { DeviceNumber = devNum, DesiredLatency = latency };
        request.SubscribeOutput(waveOut);
        waveOut.Init(seq);
        waveOut.Play();

        var devInfo = WasapiDeviceService.EnumerateWaveOutDevices().FirstOrDefault(d => d.Id == devNum.ToString(CultureInfo.InvariantCulture));
        string devName = devInfo?.Name ?? "WaveOut (Windows Audio)";
        var info = new SessionInfo(devName, false, $"WaveOut • {rate / 1000.0:0.#}kHz / 16-bit", latency, AudioDriverType.WaveOut);

        return new OutputSession(seq, waveOut, Device: null, Exclusive: false,
            AudioDriverType.WaveOut, devNum.ToString(CultureInfo.InvariantCulture), info);
    }
}
