using System;
using System.Globalization;
using System.Linq;
using System.Threading;
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

        var output = CreatePlayer(device, exclusive, latency);
        request.SubscribeOutput(output);
        try
        {
            output.Init(seq);
            // WasapiPlayer adapts bit depth/channels in exclusive mode when the device rejects the
            // negotiated format (a probe/init race). The negotiated format must reach the DAC
            // untouched — silent conversion would break bit-perfect output and corrupt DoP marker
            // bytes — so an adapted session counts as a failed exclusive open and takes the shared
            // fallback below.
            if (exclusive && !output.OutputWaveFormat.Equals(seq.WaveFormat))
                throw new NotSupportedException(
                    $"exclusive session opened at {output.OutputWaveFormat} instead of the negotiated {seq.WaveFormat}");
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

            output = CreatePlayer(device, exclusive: false, latency);
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

    /// <summary>
    /// Builds the NAudio 3 <see cref="WasapiPlayer"/> for one session (WasapiOut's replacement,
    /// which implements the same <see cref="IWavePlayer"/> seam): event-driven callbacks at the
    /// user's latency, exact-format exclusive when requested.
    /// </summary>
    private static WasapiPlayer CreatePlayer(MMDevice device, bool exclusive, int latency)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(latency);
        return (exclusive ? builder.WithExclusiveMode() : builder.WithSharedMode()).Build();
    }
}

/// <summary>DirectSound legacy path: 16-bit PCM at the source rate (NAudio 3.1 rejects float
/// secondary buffers); volume is applied in the float DSP chain before conversion.</summary>
public sealed class DirectSoundOutputDriver : IOutputDriver
{
    public AudioDriverType DriverType => AudioDriverType.DirectSound;

    // 0 unknown, 1 usable, -1 broken. NAudio 3.1's DirectSoundOut (GeneratedComInterface
    // rewrite) dies at startup with DSERR_UNSUPPORTED/E_NOTIMPL on machines where its new COM
    // bridging fails — deterministically per process, and Init/Play throw nothing (the death is
    // async on the playback thread). A one-time play probe detects that reliably and cheaply.
    private static int _probeState;

    /// <summary>One-time per process: plays ~0.5s of tone through DirectSoundOut and reports
    /// whether the playback thread survived. First call costs up to ~500ms; the result is cached.</summary>
    internal static bool IsDirectSoundPlaybackUsable()
    {
        int state = Volatile.Read(ref _probeState);
        if (state != 0) return state == 1;

        bool usable;
        try
        {
            var fmt = new WaveFormat(44100, 16, 2);
            var probe = new BufferedWaveProvider(fmt);
            byte[] pcm = new byte[44100 * 4]; // ~0.5s stereo 16-bit tone
            for (int i = 0; i < 44100; i++)
            {
                short v = (short)(Math.Sin(i * 0.05) * 6000);
                pcm[i * 4] = (byte)v;
                pcm[i * 4 + 1] = (byte)(v >> 8);
                pcm[i * 4 + 2] = (byte)v;
                pcm[i * 4 + 3] = (byte)(v >> 8);
            }
            probe.AddSamples(pcm, 0, pcm.Length);

            using var ds = new DirectSoundOut(40);
            ds.Init(probe);
            ds.Play();
            usable = true;
            for (int i = 0; i < 25 && usable; i++)
            {
                Thread.Sleep(20);
                // NAudio 3 PlaybackState is a value type without operator!= overloads.
                if (!ds.PlaybackState.Equals(PlaybackState.Playing)) usable = false;
            }
            ds.Stop();
        }
        catch
        {
            usable = false;
        }

        Volatile.Write(ref _probeState, usable ? 1 : -1);
        return usable;
    }

    public OutputSession Start(OutputSessionRequest request)
    {
        if (!IsDirectSoundPlaybackUsable())
        {
            request.Warn(CoreMessages.Encode(CoreMessageKey.DirectSoundUnavailableWaveOutFallback));
            return new WaveOutOutputDriver().Start(request);
        }

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
        // 16-bit PCM, not float: NAudio 3.1's DirectSoundOut (GeneratedComInterface rewrite)
        // fails secondary-buffer creation with 32-bit IEEE float (E_NOTIMPL), and float was
        // never an officially supported DirectSound buffer format anyway. Volume/DSP still run
        // in the float chain before the sequencer converts to PCM at this target.
        var target = new WaveFormat(rate, 16, channels);
        var eqProfile = EqualizerProfileResolver.Resolve(_settings.Equalizer, AudioDriverType.DirectSound, dsGuid.ToString());

        var seq = request.CreateSequencer(first, target, true, eqProfile);
        seq.SwitchTo(first);

        var dsOutput = new DirectSoundOut(dsGuid, latency);
        request.SubscribeOutput(dsOutput);
        dsOutput.Init(seq);
        dsOutput.Play();

        var devInfo = WasapiDeviceService.EnumerateDirectSoundDevices().FirstOrDefault(d => d.Id == dsGuid.ToString());
        string devName = devInfo?.Name ?? "DirectSound (Windows Audio)";
        var info = new SessionInfo(devName, false, $"DirectSound • {rate / 1000.0:0.#}kHz / 16-bit PCM", latency, AudioDriverType.DirectSound);

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

        // NAudio 3 merged WaveOutEvent into WaveOut (event-driven). DesiredLatency sized the total
        // across all buffers; BufferMilliseconds sizes each one, so three ⅓ buffers keep the same
        // scheduling for the user's latency setting.
        var waveOut = new WaveOut { DeviceNumber = devNum, BufferMilliseconds = latency / 3, NumberOfBuffers = 3 };
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
