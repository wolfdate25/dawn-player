using DawnPlayer.App.Localization;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>
/// Default implementation of <see cref="IAudioSettingsService"/>.
/// Manages audio output driver and device selection, exclusive mode status queries,
/// buffer latency constraints, ReplayGain DSP configuration, and automated persistence and playback restart.
/// </summary>
public sealed class AudioSettingsService : IAudioSettingsService
{
    private readonly AppSettings _settings;
    private readonly PlaybackController? _playback;

    public AudioSettingsService(AppSettings settings, PlaybackController? playback = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _playback = playback;
    }

    public IReadOnlyList<OutputDeviceInfo> GetDevices(AudioDriverType driverType)
    {
        try
        {
            return WasapiDeviceService.EnumerateDevices(driverType);
        }
        catch
        {
            return Array.Empty<OutputDeviceInfo>();
        }
    }

    public OutputDeviceInfo? GetSelectedDevice(AudioDriverType driverType, string? deviceId)
    {
        var devices = GetDevices(driverType);
        if (devices.Count == 0) return null;

        return devices.FirstOrDefault(d => d.Id == deviceId)
            ?? devices.FirstOrDefault(d => d.IsDefault)
            ?? (devices.Count > 0 ? devices[0] : null);
    }

    public ExclusiveModeStatus GetExclusiveModeStatus(string? deviceId)
    {
        try
        {
            using var dev = WasapiDeviceService.OpenDevice(deviceId);
            if (dev != null)
            {
                bool exclusiveAllowed = WasapiDeviceService.IsExclusiveModeEnabledInWindows(dev);
                bool priorityAllowed = WasapiDeviceService.IsExclusivePriorityEnabledInWindows(dev);

                string exclusiveStr = exclusiveAllowed
                    ? AppStrings.Get("Audio_ExclusiveAllowed", "허용됨")
                    : AppStrings.Get("Audio_ExclusiveDisabled", "꺼짐 (배타 불가)");
                string priorityStr = priorityAllowed
                    ? AppStrings.Get("Audio_PriorityAllowed", "허용됨 (우선권 자동 획득)")
                    : AppStrings.Get("Audio_PriorityDisabled", "꺼짐 (다른 앱 실행 시 실패)");

                string statusText = AppStrings.Format("Audio_ExclusiveStatusFormat", exclusiveStr, priorityStr);
                string detailsText = AppStrings.Format("Audio_ExclusiveDetailsFormat", exclusiveAllowed, priorityAllowed);

                return new ExclusiveModeStatus(exclusiveAllowed, priorityAllowed, statusText, detailsText);
            }
        }
        catch { }

        return new ExclusiveModeStatus(
            false,
            false,
            AppStrings.Get("Audio_DeviceStatusUnknown", "장치 상태를 확인할 수 없습니다."),
            AppStrings.Get("Audio_DeviceOpenFailed", "장치를 열 수 없거나 WASAPI 엔드포인트를 찾을 수 없습니다."));
    }

    public void SetDriverType(AudioDriverType driverType)
    {
        _settings.Output.DriverType = driverType;
        _settings.Output.DeviceId = null;
        SaveAndRestart();
    }

    public void SetDevice(string? deviceId)
    {
        _settings.Output.DeviceId = deviceId;
        SaveAndRestart();
    }

    public void SetUseExclusiveMode(bool useExclusive)
    {
        _settings.Output.UseExclusiveMode = useExclusive;
        SaveAndRestart();
    }

    public void SetExclusiveBitDepth(ExclusiveBitDepth bitDepth)
    {
        _settings.Output.ExclusiveBitDepth = bitDepth;
        SaveAndRestart();
    }

    public void SetLatency(int latencyMs)
    {
        _settings.Output.LatencyMs = Math.Clamp(latencyMs, 30, 500);
        SettingsWriter.Schedule(_settings);
    }

    public void SetAllowVolumeInExclusive(bool allow)
    {
        _settings.Output.AllowVolumeInExclusive = allow;
        SaveAndRestart();
    }

    public void SetReplayGain(ReplayGainMode mode, double preampDb, bool preventClipping)
    {
        _settings.Playback.ReplayGain = mode;
        _settings.Playback.ReplayGainPreampDb = Math.Clamp(preampDb, -12.0, 12.0);
        _settings.Playback.ReplayGainPreventClipping = preventClipping;
        SettingsWriter.Schedule(_settings);
        _playback?.ApplyNormalizer();
    }

    public void SetNormalizer(bool enabled, NormalizerMode mode, double targetLevelDb, double maxBoostDb, NormalizerSpeed speed)
    {
        _settings.Normalizer.Enabled = enabled;
        _settings.Normalizer.Mode = mode;
        _settings.Normalizer.TargetLevelDb = Math.Clamp(targetLevelDb, -24.0, -6.0);
        _settings.Normalizer.MaxBoostDb = Math.Clamp(maxBoostDb, 0.0, 18.0);
        _settings.Normalizer.Speed = speed;
        SettingsWriter.Schedule(_settings);
        _playback?.ApplyNormalizer();
    }

    public void OpenSoundControlPanel()
    {
        WasapiDeviceService.OpenSoundControlPanel();
    }

    private void SaveAndRestart()
    {
        SettingsWriter.Schedule(_settings);
        _playback?.RestartIfPlaying();
    }
}
