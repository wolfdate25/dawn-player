using System;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing hybrid dynamic volume normalizer (AGC) and ReplayGain parameters.
/// Employs clamped properties to eliminate slider-numberbox oscillation and boolean re-entrancy flags.
/// </summary>
public sealed class PlaybackSettingsViewModel : ViewModelBase
{
    private readonly IAudioSettingsService _audioSettingsService;
    private readonly AppSettings _settings;

    public PlaybackSettingsViewModel(IAudioSettingsService audioSettingsService, AppSettings settings)
    {
        _audioSettingsService = audioSettingsService ?? throw new ArgumentNullException(nameof(audioSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool NormalizerEnabled
    {
        get => _settings.Normalizer.Enabled;
        set
        {
            if (_settings.Normalizer.Enabled != value)
            {
                _settings.Normalizer.Enabled = value;
                OnPropertyChanged();
                SaveNormalizer();
            }
        }
    }

    public NormalizerMode NormalizerMode
    {
        get => _settings.Normalizer.Mode;
        set
        {
            if (_settings.Normalizer.Mode != value)
            {
                _settings.Normalizer.Mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NormalizerModeIndex));
                SaveNormalizer();
            }
        }
    }

    public int NormalizerModeIndex
    {
        get => _settings.Normalizer.Mode switch
        {
            NormalizerMode.AlwaysDynamic => 1,
            NormalizerMode.ReplayGainOnly => 2,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                1 => NormalizerMode.AlwaysDynamic,
                2 => NormalizerMode.ReplayGainOnly,
                _ => NormalizerMode.Hybrid
            };
            if (_settings.Normalizer.Mode != mode)
            {
                NormalizerMode = mode;
            }
        }
    }

    public double NormalizerTargetDb
    {
        get => _settings.Normalizer.TargetLevelDb;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), -24.0, -6.0);
            if (Math.Abs(_settings.Normalizer.TargetLevelDb - clamped) > 0.01)
            {
                _settings.Normalizer.TargetLevelDb = clamped;
                OnPropertyChanged();
                SaveNormalizer();
            }
        }
    }

    public double NormalizerMaxBoostDb
    {
        get => _settings.Normalizer.MaxBoostDb;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), 0.0, 18.0);
            if (Math.Abs(_settings.Normalizer.MaxBoostDb - clamped) > 0.01)
            {
                _settings.Normalizer.MaxBoostDb = clamped;
                OnPropertyChanged();
                SaveNormalizer();
            }
        }
    }

    public NormalizerSpeed NormalizerSpeed
    {
        get => _settings.Normalizer.Speed;
        set
        {
            if (_settings.Normalizer.Speed != value)
            {
                _settings.Normalizer.Speed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NormalizerSpeedIndex));
                SaveNormalizer();
            }
        }
    }

    public int NormalizerSpeedIndex
    {
        get => _settings.Normalizer.Speed switch
        {
            NormalizerSpeed.Fast => 0,
            NormalizerSpeed.Smooth => 2,
            _ => 1
        };
        set
        {
            var speed = value switch
            {
                0 => NormalizerSpeed.Fast,
                2 => NormalizerSpeed.Smooth,
                _ => NormalizerSpeed.Balanced
            };
            if (_settings.Normalizer.Speed != speed)
            {
                NormalizerSpeed = speed;
            }
        }
    }

    public ReplayGainMode ReplayGainMode
    {
        get => _settings.Playback.ReplayGain;
        set
        {
            if (_settings.Playback.ReplayGain != value)
            {
                _settings.Playback.ReplayGain = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReplayGainModeIndex));
                SaveReplayGain();
            }
        }
    }

    public int ReplayGainModeIndex
    {
        get => _settings.Playback.ReplayGain switch
        {
            ReplayGainMode.Track => 1,
            ReplayGainMode.Album => 2,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                1 => ReplayGainMode.Track,
                2 => ReplayGainMode.Album,
                _ => ReplayGainMode.Off
            };
            if (_settings.Playback.ReplayGain != mode)
            {
                ReplayGainMode = mode;
            }
        }
    }

    public double ReplayGainPreampDb
    {
        get => _settings.Playback.ReplayGainPreampDb;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), -12.0, 12.0);
            if (Math.Abs(_settings.Playback.ReplayGainPreampDb - clamped) > 0.01)
            {
                _settings.Playback.ReplayGainPreampDb = clamped;
                OnPropertyChanged();
                SaveReplayGain();
            }
        }
    }

    public bool ReplayGainPreventClipping
    {
        get => _settings.Playback.ReplayGainPreventClipping;
        set
        {
            if (_settings.Playback.ReplayGainPreventClipping != value)
            {
                _settings.Playback.ReplayGainPreventClipping = value;
                OnPropertyChanged();
                SaveReplayGain();
            }
        }
    }

    public void SaveNormalizer()
    {
        _audioSettingsService.SetNormalizer(
            _settings.Normalizer.Enabled,
            _settings.Normalizer.Mode,
            _settings.Normalizer.TargetLevelDb,
            _settings.Normalizer.MaxBoostDb,
            _settings.Normalizer.Speed);
    }

    public void SaveReplayGain()
    {
        _audioSettingsService.SetReplayGain(
            _settings.Playback.ReplayGain,
            _settings.Playback.ReplayGainPreampDb,
            _settings.Playback.ReplayGainPreventClipping);
    }
}
