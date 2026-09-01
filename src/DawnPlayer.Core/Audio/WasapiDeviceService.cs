using System.Globalization;
using System.Linq;
using DawnPlayer.Core.Persistence;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

public sealed record OutputDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>WASAPI endpoint enumeration, shared mix-format query and exclusive
/// format negotiation.</summary>
public static class WasapiDeviceService
{
    public static List<OutputDeviceInfo> EnumerateDevices(AudioDriverType driverType = AudioDriverType.Wasapi) =>
        driverType switch
        {
            AudioDriverType.DirectSound => EnumerateDirectSoundDevices(),
            AudioDriverType.WaveOut => EnumerateWaveOutDevices(),
            _ => EnumerateWasapiDevices()
        };

    public static List<OutputDeviceInfo> EnumerateDevices() => EnumerateWasapiDevices();

    public static List<OutputDeviceInfo> EnumerateWasapiDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<OutputDeviceInfo>();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def?.ID;
        }
        catch { /* no device */ }

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using var _ = d;
            bool isDef = d.ID == defaultId;
            string name = isDef ? $"{d.FriendlyName} (기본값)" : d.FriendlyName;
            devices.Add(new OutputDeviceInfo(d.ID, name, isDef));
        }
        return devices;
    }

    public static List<OutputDeviceInfo> EnumerateDirectSoundDevices()
    {
        var list = new List<OutputDeviceInfo>();
        try
        {
            bool first = true;
            foreach (var dev in DirectSoundOut.Devices)
            {
                bool isDefault = dev.Guid == DirectSoundOut.DSDEVID_DefaultPlayback || dev.Guid == Guid.Empty || first;
                string name = isDefault ? $"{dev.Description} (기본값)" : dev.Description;
                list.Add(new OutputDeviceInfo(dev.Guid.ToString(), name, isDefault));
                first = false;
            }
        }
        catch { }
        if (list.Count == 0)
        {
            list.Add(new OutputDeviceInfo(DirectSoundOut.DSDEVID_DefaultPlayback.ToString(), "기본 사운드 드라이버 (Primary Sound Driver) (기본값)", true));
        }
        return list;
    }

    public static List<OutputDeviceInfo> EnumerateWaveOutDevices()
    {
        var list = new List<OutputDeviceInfo>
        {
            new OutputDeviceInfo("-1", "Windows 기본 사운드 매퍼 (Default WaveOut)", true)
        };
        try
        {
            int count = WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                list.Add(new OutputDeviceInfo(i.ToString(CultureInfo.InvariantCulture), caps.ProductName, false));
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// The configured DirectSound device when it still exists, else the default primary driver.
    /// A stale DeviceId (unplugged device, settings copied from another machine) must not kill
    /// playback — the session factory and the controller's DesiredDeviceKey both resolve through
    /// here so their answers agree and a fallback session is not rebuilt on every track.
    /// </summary>
    public static Guid ResolveDirectSoundDevice(string? configuredDeviceId)
    {
        if (!string.IsNullOrEmpty(configuredDeviceId)
            && Guid.TryParse(configuredDeviceId, out var guid)
            && guid != DirectSoundOut.DSDEVID_DefaultPlayback
            && guid != Guid.Empty)
        {
            try
            {
                if (EnumerateDirectSoundDevices().Any(d => d.Id == guid.ToString()))
                    return guid;
            }
            catch { }
        }
        return DirectSoundOut.DSDEVID_DefaultPlayback;
    }

    /// <summary>The configured WaveOut device number when it exists, else -1 (the default mapper).</summary>
    public static int ResolveWaveOutDeviceNumber(string? configuredDeviceId)
    {
        if (!string.IsNullOrEmpty(configuredDeviceId)
            && int.TryParse(configuredDeviceId, out var number)
            && number >= 0)
        {
            try
            {
                if (number < WaveOut.DeviceCount)
                    return number;
            }
            catch { }
        }
        return -1;
    }

    /// <summary>Opens the configured device, or the default render device when null.</summary>
    public static MMDevice? OpenDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { return enumerator.GetDevice(deviceId); }
                catch { /* configured device vanished → default */ }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The device's shared-mode mix format (usually float32 / 2ch / 48kHz).</summary>
    public static WaveFormat GetSharedTarget(MMDevice device)
    {
        using var client = device.AudioClient;
        return client.MixFormat;
    }

    /// <summary>
    /// Picks an exclusive-mode format at the source's sample rate / channel count,
    /// walking bit-depth candidates per policy. Returns null when the endpoint
    /// cannot do exclusive with this format.
    /// </summary>
    public static WaveFormat? TryNegotiateExclusive(MMDevice device, WaveFormat source, ExclusiveBitDepth policy)
    {
        int rate = source.SampleRate;
        int channels = source.Channels;

        IEnumerable<int> bitCandidates = policy switch
        {
            ExclusiveBitDepth.Bits16 => new[] { 16, 24, 32 },
            ExclusiveBitDepth.Bits24 => new[] { 24, 32, 16 },
            ExclusiveBitDepth.Bits32 => new[] { 32, 24, 16 },
            _ => SourceBitsThenFallback(source.BitsPerSample)
        };

        foreach (var bits in bitCandidates.Distinct())
        {
            if (bits is not (16 or 24 or 32)) continue;
            foreach (var fmt in GetFormatVariants(rate, bits, channels))
            {
                if (IsExclusiveSupported(device, fmt)) return fmt;
            }
        }
        return null;
    }

    public static IEnumerable<WaveFormat> GetFormatVariants(int rate, int bits, int channels)
    {
        // 1. Standard PCM (WAVE_FORMAT_PCM)
        yield return new WaveFormat(rate, bits, channels);

        // 2. Extensible format (WAVE_FORMAT_EXTENSIBLE)
        yield return new WaveFormatExtensible(rate, bits, channels);

        // 3. IEEE Float for 32-bit
        if (bits == 32)
        {
            yield return WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }
    }

    private static int[] SourceBitsThenFallback(int sourceBits) =>
        sourceBits switch
        {
            16 => new[] { 16, 24, 32 },
            24 => new[] { 24, 32, 16 },
            32 => new[] { 32, 24, 16 },
            _ => new[] { 24, 16, 32 } // lossy sources → prefer 24
        };

    private static bool IsExclusiveSupported(MMDevice device, WaveFormat format)
    {
        try
        {
            using var client = device.AudioClient;
            return client.IsFormatSupported(AudioClientShareMode.Exclusive, format, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Human-readable summary, e.g. "44.1 kHz / 24-bit / 2ch".</summary>
    public static string Describe(WaveFormat f) =>
        $"{(f.SampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} kHz / {f.BitsPerSample}-bit / {f.Channels}ch";

    // WASAPI error codes of interest for exclusive-mode diagnostics.
    // These were previously wrong, which mattered: the exclusive-failure message is chosen by
    // comparing against them, so a real "device in use" (0x8889000A) matched nothing and fell
    // through to a bare HRESULT, while a genuine unsupported-format (0x88890008) was reported as
    // "another program is using the device" — the opposite of the truth. Values per audioclient.h.
    public const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntInvalidSize = unchecked((int)0x88890009);
    public const int AudclntDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntBufferSizeNotAligned = unchecked((int)0x88890019);
    public const int AudclntExclusiveModeNotAllowed = unchecked((int)0x8889001A);

    /// <summary>
    /// Diagnostic: actually attempts an exclusive Initialize (never starts audio) to get
    /// the real failure reason. Some drivers report IsFormatSupported=false across the
    /// board while the endpoint is busy, hiding the true cause (device in use).
    /// </summary>
    public static int ProbeExclusiveInitialize(MMDevice device, WaveFormat format)
    {
        try
        {
            using var client = device.AudioClient;
            client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback,
                100 * 10000L, 0, format, Guid.Empty);
            return 0; // succeeded (was never started; disposed immediately)
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    /// <summary>Checks if "Allow applications to take exclusive control of this device" is checked in Windows.</summary>
    public static bool IsExclusiveModeEnabledInWindows(MMDevice device)
    {
        try
        {
            var key = new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 3);
            if (device.Properties.Contains(key))
            {
                var val = device.Properties[key].Value;
                if (val is uint u) return u != 0;
                if (val is int i) return i != 0;
            }
        }
        catch { }
        return true; // Assume enabled if we can't read it
    }

    /// <summary>Checks if "Give exclusive mode applications priority" is checked in Windows.</summary>
    public static bool IsExclusivePriorityEnabledInWindows(MMDevice device)
    {
        try
        {
            var key = new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 4);
            if (device.Properties.Contains(key))
            {
                var val = device.Properties[key].Value;
                if (val is uint u) return u != 0;
                if (val is int i) return i != 0;
            }
        }
        catch { }
        return true; // Assume enabled if we can't read it
    }

    /// <summary>Opens the classic Windows Sound Control Panel (mmsys.cpl).</summary>
    public static void OpenSoundControlPanel()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "control",
                Arguments = "mmsys.cpl sounds",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mmsys.cpl",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    /// <summary>Process names with active audio sessions on the endpoint (who holds it).</summary>
    public static List<string> GetActiveSessionHolders(MMDevice device)
    {
        var result = new List<string>();
        try
        {
            var mgr = device.AudioSessionManager;
            mgr.RefreshSessions();
            for (int i = 0; i < mgr.Sessions.Count; i++)
            {
                var s = mgr.Sessions[i];
                if ((int)s.State != 1) continue; // active
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID);
                    var name = proc.ProcessName;
                    if (!result.Contains(name)) result.Add(name);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }
}
