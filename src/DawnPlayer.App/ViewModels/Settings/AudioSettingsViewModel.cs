using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing audio driver selection, endpoint enumeration,
/// WASAPI exclusive mode negotiation, exclusive bit depth, buffer latency, and sound control panel.
/// </summary>
public sealed class AudioSettingsViewModel : ViewModelBase
{
    private readonly IAudioSettingsService _audioSettingsService;
    private readonly AppSettings _settings;
    private readonly Action? _onDriverOrDeviceChanged;

    private IReadOnlyList<OutputDeviceInfo> _devices = Array.Empty<OutputDeviceInfo>();
    private OutputDeviceInfo? _selectedDevice;
    private string _windowsExclusiveStatusText = AppStrings.Get("Settings_Audio_Status_Checking", "확인 중...");
    private bool _isRefreshing;

    public AudioSettingsViewModel(
        IAudioSettingsService audioSettingsService,
        AppSettings settings,
        Action? onDriverOrDeviceChanged = null)
    {
        _audioSettingsService = audioSettingsService ?? throw new ArgumentNullException(nameof(audioSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _onDriverOrDeviceChanged = onDriverOrDeviceChanged;

        RefreshDevices(_settings.Output.DeviceId);
    }

    public AudioDriverType DriverType
    {
        get => _settings.Output.DriverType;
        set
        {
            if (_settings.Output.DriverType != value)
            {
                _audioSettingsService.SetDriverType(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DriverTypeIndex));
                OnPropertyChanged(nameof(IsWasapiDriver));
                OnPropertyChanged(nameof(DriverDescriptionText));
                RefreshDevices(null);
                _onDriverOrDeviceChanged?.Invoke();
            }
        }
    }

    public int DriverTypeIndex
    {
        get => (int)DriverType;
        set
        {
            if (value >= 0 && value <= 2 && (int)DriverType != value)
            {
                DriverType = (AudioDriverType)value;
            }
        }
    }

    public bool IsWasapiDriver => DriverType == AudioDriverType.Wasapi;

    public string DriverDescriptionText => DriverType switch
    {
        AudioDriverType.DirectSound => AppStrings.Get("Settings_Audio_DriverDesc_DirectSound", "Windows Audio (DirectSound) 장치 목록 (윈도우 믹서 경유/블루투스/헤드폰 호환)"),
        AudioDriverType.WaveOut => AppStrings.Get("Settings_Audio_DriverDesc_WaveOut", "Windows WaveOut 표준 장치 목록"),
        _ => AppStrings.Get("Settings_Audio_DriverDesc_Wasapi", "WASAPI 엔드포인트 장치 목록 (비트 퍼펙트 지원)")
    };

    public IReadOnlyList<OutputDeviceInfo> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    public OutputDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value) && !_isRefreshing && value != null)
            {
                _audioSettingsService.SetDevice(value.Id);
                UpdateWindowsExclusiveStatus(value.Id);
                _onDriverOrDeviceChanged?.Invoke();
            }
        }
    }

    public bool UseExclusiveMode
    {
        get => _settings.Output.UseExclusiveMode;
        set
        {
            if (_settings.Output.UseExclusiveMode != value)
            {
                _audioSettingsService.SetUseExclusiveMode(value);
                OnPropertyChanged();
            }
        }
    }

    public int ExclusiveBitDepthIndex
    {
        get => _settings.Output.ExclusiveBitDepth switch
        {
            ExclusiveBitDepth.Bits16 => 1,
            ExclusiveBitDepth.Bits24 => 2,
            ExclusiveBitDepth.Bits32 => 3,
            _ => 0
        };
        set
        {
            var bitDepth = value switch
            {
                1 => ExclusiveBitDepth.Bits16,
                2 => ExclusiveBitDepth.Bits24,
                3 => ExclusiveBitDepth.Bits32,
                _ => ExclusiveBitDepth.Source
            };

            if (_settings.Output.ExclusiveBitDepth != bitDepth)
            {
                _audioSettingsService.SetExclusiveBitDepth(bitDepth);
                OnPropertyChanged();
            }
        }
    }

    public int LatencyMs
    {
        get => _settings.Output.LatencyMs;
        set
        {
            int clamped = Math.Clamp(value, 30, 500);
            if (_settings.Output.LatencyMs != clamped)
            {
                _audioSettingsService.SetLatency(clamped);
                OnPropertyChanged();
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    public string LatencyText => $"{LatencyMs}ms";

    public bool AllowVolumeInExclusive
    {
        get => _settings.Output.AllowVolumeInExclusive;
        set
        {
            if (_settings.Output.AllowVolumeInExclusive != value)
            {
                _audioSettingsService.SetAllowVolumeInExclusive(value);
                OnPropertyChanged();
            }
        }
    }

    public string WindowsExclusiveStatusText
    {
        get => _windowsExclusiveStatusText;
        private set => SetProperty(ref _windowsExclusiveStatusText, value);
    }

    public void RefreshDevices(string? selectId = null)
    {
        _isRefreshing = true;
        try
        {
            var driver = _settings.Output.DriverType;
            var deviceList = _audioSettingsService.GetDevices(driver);
            Devices = deviceList;

            var resolved = _audioSettingsService.GetSelectedDevice(driver, selectId ?? _settings.Output.DeviceId);
            _selectedDevice = resolved;
            OnPropertyChanged(nameof(SelectedDevice));

            if (driver == AudioDriverType.Wasapi)
            {
                UpdateWindowsExclusiveStatus(resolved?.Id);
            }
            else
            {
                WindowsExclusiveStatusText = AppStrings.Get("Settings_Audio_Status_WasapiOnly", "WASAPI 드라이버에서만 독점 제어 설정을 확인합니다.");
            }
        }
        catch
        {
            Devices = Array.Empty<OutputDeviceInfo>();
            _selectedDevice = null;
            OnPropertyChanged(nameof(SelectedDevice));
            WindowsExclusiveStatusText = AppStrings.Get("Settings_Audio_Status_ReadError", "장치 목록을 읽는 중 오류가 발생했습니다.");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateWindowsExclusiveStatus(string? deviceId)
    {
        try
        {
            var status = _audioSettingsService.GetExclusiveModeStatus(deviceId);
            WindowsExclusiveStatusText = status.StatusText;
        }
        catch
        {
            WindowsExclusiveStatusText = AppStrings.Get("Settings_Audio_Status_Unknown", "독점 제어 상태를 확인할 수 없습니다.");
        }
    }

    public void OpenSoundControlPanel()
    {
        _audioSettingsService.OpenSoundControlPanel();
    }
}
