using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DawnPlayer.App.Calculators;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

public sealed record EqDeviceComboItem(string? Id, string Name);
public sealed record EqProfileBindingComboItem(string? ProfileId, string Name, bool IsFollowDefault);

/// <summary>
/// Sub-panel ViewModel managing parametric equalizer profiles (CRUD), master bypass toggle,
/// preamp attenuation, filter band collections, device-to-profile bindings, and pure visualizer calculation data.
/// </summary>
public sealed class EqualizerSettingsViewModel : ViewModelBase
{
    private readonly IEqSettingsService _eqSettingsService;
    private readonly IAudioSettingsService _audioSettingsService;
    private readonly AppSettings _settings;
    private readonly Func<bool>? _isExclusiveSessionGetter;

    private IReadOnlyList<EqProfile> _profiles = Array.Empty<EqProfile>();
    private EqProfile? _selectedProfile;
    private readonly ObservableCollection<EqBandViewModel> _bands = new();
    private IReadOnlyList<EqDeviceComboItem> _devices = Array.Empty<EqDeviceComboItem>();
    private EqDeviceComboItem? _selectedDevice;
    private IReadOnlyList<EqProfileBindingComboItem> _bindingOptions = Array.Empty<EqProfileBindingComboItem>();
    private EqProfileBindingComboItem? _selectedBindingOption;
    private string _bindingDescriptionText = "";
    private EqVisualizerData? _visualizerData;
    private double _visualizerWidth = 700.0;
    private double _visualizerHeight = 190.0;
    private bool _isExclusiveSession;
    private bool _isUpdating;

    public EqualizerSettingsViewModel(
        IEqSettingsService eqSettingsService,
        IAudioSettingsService audioSettingsService,
        AppSettings settings,
        Func<bool>? isExclusiveSessionGetter = null)
    {
        _eqSettingsService = eqSettingsService ?? throw new ArgumentNullException(nameof(eqSettingsService));
        _audioSettingsService = audioSettingsService ?? throw new ArgumentNullException(nameof(audioSettingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _isExclusiveSessionGetter = isExclusiveSessionGetter;

        RefreshProfiles();
        RefreshDevicesAndBindings(_settings.Output.DeviceId);
    }

    public bool IsMasterEnabled
    {
        get => _eqSettingsService.IsEnabled();
        set
        {
            if (_eqSettingsService.IsEnabled() != value)
            {
                _eqSettingsService.SetEnabled(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsExclusiveWarningVisible));
                RecalculateVisualizer();
            }
        }
    }

    public bool IsExclusiveWarningVisible
    {
        get
        {
            bool isExclusive = _isExclusiveSession || (_isExclusiveSessionGetter != null && _isExclusiveSessionGetter());
            return isExclusive && IsMasterEnabled;
        }
    }

    public IReadOnlyList<EqProfile> Profiles
    {
        get => _profiles;
        private set
        {
            if (SetProperty(ref _profiles, value))
            {
                OnPropertyChanged(nameof(CanDeleteProfile));
            }
        }
    }

    public EqProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value != null && (_selectedProfile == null || _selectedProfile.Id != value.Id))
            {
                _selectedProfile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProfileName));
                OnPropertyChanged(nameof(PreampDb));
                OnPropertyChanged(nameof(CanDeleteProfile));
                LoadBandsForSelectedProfile();
                RecalculateVisualizer();
            }
        }
    }

    public string ProfileName
    {
        get => _selectedProfile?.Name ?? "";
        set
        {
            if (_selectedProfile != null && !string.IsNullOrWhiteSpace(value) && _selectedProfile.Name != value)
            {
                RenameProfile(value.Trim());
            }
        }
    }

    public double PreampDb
    {
        get => _selectedProfile?.PreampDb ?? 0.0;
        set
        {
            if (_selectedProfile == null) return;
            double clamped = Math.Clamp(Math.Round(value, 1), -12.0, 12.0);
            if (Math.Abs(_selectedProfile.PreampDb - clamped) > 0.01)
            {
                _selectedProfile.PreampDb = clamped;
                OnPropertyChanged();
                SaveCurrentProfile();
            }
        }
    }

    public ObservableCollection<EqBandViewModel> Bands => _bands;

    public int BandCount => _bands.Count;

    public string BandCountText => $"({_bands.Count} / 20)";

    public bool CanAddBand => _bands.Count < 20;

    public bool HasEmptyBands => _bands.Count == 0;

    public bool CanDeleteProfile
    {
        get
        {
            if (_selectedProfile == null) return false;
            string defaultId = _eqSettingsService.GetDefaultProfileId();
            return _selectedProfile.Id != defaultId && _profiles.Count > 1;
        }
    }

    public IReadOnlyList<EqDeviceComboItem> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    public EqDeviceComboItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value) && !_isUpdating)
            {
                RefreshDeviceBindingOptions();
            }
        }
    }

    public IReadOnlyList<EqProfileBindingComboItem> BindingOptions
    {
        get => _bindingOptions;
        private set => SetProperty(ref _bindingOptions, value);
    }

    public EqProfileBindingComboItem? SelectedBindingOption
    {
        get => _selectedBindingOption;
        set
        {
            if (SetProperty(ref _selectedBindingOption, value) && !_isUpdating && value != null)
            {
                ApplyDeviceBinding(value);
            }
        }
    }

    public string BindingDescriptionText
    {
        get => _bindingDescriptionText;
        private set => SetProperty(ref _bindingDescriptionText, value);
    }

    public EqVisualizerData? VisualizerData
    {
        get => _visualizerData;
        private set => SetProperty(ref _visualizerData, value);
    }

    public double VisualizerWidth
    {
        get => _visualizerWidth;
        set
        {
            if (double.IsFinite(value) && value > 20 && Math.Abs(_visualizerWidth - value) > 1.0)
            {
                _visualizerWidth = value;
                RecalculateVisualizer();
            }
        }
    }

    public double VisualizerHeight
    {
        get => _visualizerHeight;
        set
        {
            if (double.IsFinite(value) && value > 20 && Math.Abs(_visualizerHeight - value) > 1.0)
            {
                _visualizerHeight = value;
                RecalculateVisualizer();
            }
        }
    }

    public void RefreshProfiles(string? selectProfileId = null)
    {
        var profileList = _eqSettingsService.GetProfiles();
        Profiles = profileList;

        var defaultId = _eqSettingsService.GetDefaultProfileId();
        var target = (!string.IsNullOrEmpty(selectProfileId)
            ? profileList.FirstOrDefault(p => p.Id == selectProfileId)
            : null)
            ?? profileList.FirstOrDefault(p => p.Id == defaultId)
            ?? (profileList.Count > 0 ? profileList[0] : null);

        _selectedProfile = target;
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(PreampDb));
        OnPropertyChanged(nameof(CanDeleteProfile));

        LoadBandsForSelectedProfile();
        RecalculateVisualizer();
    }

    public void RefreshDevicesAndBindings(string? selectDeviceId = null)
    {
        _isUpdating = true;
        try
        {
            var driver = _settings.Output.DriverType;
            var outputDevices = _audioSettingsService.GetDevices(driver);

            var items = new List<EqDeviceComboItem>(outputDevices.Count);
            foreach (var d in outputDevices)
            {
                string suffix = d.IsDefault ? AppStrings.Get("Settings_Eq_Device_SystemDefaultSuffix", " (시스템 기본)") : "";
                items.Add(new EqDeviceComboItem(d.Id, $"{d.Name}{suffix}"));
            }
            Devices = items;

            string? targetId = selectDeviceId ?? _settings.Output.DeviceId;
            var match = targetId != null ? items.FirstOrDefault(i => i.Id == targetId) : null;
            _selectedDevice = match ?? items.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDevice));

            RefreshDeviceBindingOptions();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void RefreshDeviceBindingOptions()
    {
        var driver = _settings.Output.DriverType;
        string? devId = _selectedDevice?.Id;

        var profileList = _eqSettingsService.GetProfiles();
        var defaultId = _eqSettingsService.GetDefaultProfileId();
        var defaultProfile = profileList.FirstOrDefault(p => p.Id == defaultId);

        string defaultProfileName = defaultProfile?.Name ?? AppStrings.Get("Settings_Eq_DefaultProfileFallback", "기본");
        var list = new List<EqProfileBindingComboItem>
        {
            new(null, AppStrings.Format("Settings_Eq_FollowDefaultProfileFormat", "기본 프로필 따르기 ({0})", defaultProfileName), true)
        };

        foreach (var p in profileList)
        {
            list.Add(new EqProfileBindingComboItem(p.Id, p.Name, false));
        }

        BindingOptions = list;

        var boundProfileId = _eqSettingsService.GetBoundProfileId(driver, devId);
        if (!string.IsNullOrEmpty(boundProfileId))
        {
            var match = list.FirstOrDefault(i => !i.IsFollowDefault && i.ProfileId == boundProfileId);
            _selectedBindingOption = match ?? list[0];
            var boundProf = profileList.FirstOrDefault(p => p.Id == boundProfileId);
            string boundProfName = boundProf?.Name ?? AppStrings.Get("Settings_Eq_DedicatedProfileFallback", "전용");
            BindingDescriptionText = AppStrings.Format("Settings_Eq_DeviceBoundFormat", "이 장치는 '{0}' 프로필에 바인딩되어 있습니다.", boundProfName);
        }
        else
        {
            _selectedBindingOption = list[0];
            BindingDescriptionText = AppStrings.Format("Settings_Eq_UsingDefaultProfileFormat", "기본 프로필('{0}')을 사용 중입니다.", defaultProfileName);
        }

        OnPropertyChanged(nameof(SelectedBindingOption));
    }

    private void ApplyDeviceBinding(EqProfileBindingComboItem item)
    {
        var driver = _settings.Output.DriverType;
        string? devId = _selectedDevice?.Id;

        _eqSettingsService.BindDeviceToProfile(driver, devId, item.IsFollowDefault ? null : item.ProfileId);

        if (item.IsFollowDefault)
        {
            var defaultProfile = _eqSettingsService.GetProfileById(_eqSettingsService.GetDefaultProfileId());
            string defaultProfileName = defaultProfile?.Name ?? AppStrings.Get("Settings_Eq_DefaultProfileFallback", "기본");
            BindingDescriptionText = AppStrings.Format("Settings_Eq_UsingDefaultProfileFormat", "기본 프로필('{0}')을 사용 중입니다.", defaultProfileName);
        }
        else
        {
            BindingDescriptionText = AppStrings.Format("Settings_Eq_DeviceBoundFormat", "이 장치는 '{0}' 프로필에 바인딩되었습니다.", item.Name);
            var prof = _eqSettingsService.GetProfileById(item.ProfileId!);
            if (prof != null)
            {
                SelectedProfile = prof;
            }
        }
    }

    private void LoadBandsForSelectedProfile()
    {
        _bands.Clear();
        if (_selectedProfile?.Bands != null)
        {
            for (int i = 0; i < _selectedProfile.Bands.Count; i++)
            {
                var bandModel = _selectedProfile.Bands[i];
                // SaveCurrentProfile already recalculates the visualizer; calling it again here
                // doubled the compute and canvas rebuild on every slider tick.
                var vm = new EqBandViewModel(bandModel, i, SaveCurrentProfile);
                _bands.Add(vm);
            }
        }

        OnPropertyChanged(nameof(BandCount));
        OnPropertyChanged(nameof(BandCountText));
        OnPropertyChanged(nameof(CanAddBand));
        OnPropertyChanged(nameof(HasEmptyBands));
    }

    public EqProfile CreateProfile(string name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? AppStrings.Format("Settings_Eq_DefaultProfileNameFormat", "프로필 {0}", _profiles.Count + 1) : name.Trim();
        var created = _eqSettingsService.CreateProfile(trimmed);
        RefreshProfiles(created.Id);
        RefreshDeviceBindingOptions();
        return created;
    }

    public EqProfile? DuplicateProfile()
    {
        if (_selectedProfile == null) return null;
        var dup = _eqSettingsService.CreateProfile(AppStrings.Format("Settings_Eq_DuplicateProfileFormat", "{0} (복사본)", _selectedProfile.Name), _selectedProfile);
        RefreshProfiles(dup.Id);
        RefreshDeviceBindingOptions();
        return dup;
    }

    public void RenameProfile(string newName)
    {
        if (_selectedProfile == null || string.IsNullOrWhiteSpace(newName)) return;
        string trimmed = newName.Trim();
        _eqSettingsService.RenameProfile(_selectedProfile.Id, trimmed);
        _selectedProfile.Name = trimmed;
        OnPropertyChanged(nameof(ProfileName));
        RefreshProfiles(_selectedProfile.Id);
        RefreshDeviceBindingOptions();
    }

    public bool DeleteCurrentProfile()
    {
        if (_selectedProfile == null) return false;
        if (!CanDeleteProfile) return false;

        bool success = _eqSettingsService.DeleteProfile(_selectedProfile.Id);
        if (success)
        {
            RefreshProfiles(null);
            RefreshDeviceBindingOptions();
        }
        return success;
    }

    public bool AddBand(EqFilterType type = EqFilterType.PeakEq, double freq = 1000, double gain = 0, double q = 1.0)
    {
        if (_selectedProfile == null || _bands.Count >= 20) return false;

        var bandModel = new EqBandSettings
        {
            Type = type,
            FrequencyHz = Math.Clamp(freq, 20.0, 20000.0),
            GainDb = Math.Clamp(gain, -15.0, 15.0),
            Q = Math.Clamp(q, 0.1, 8.0)
        };

        _selectedProfile.Bands.Add(bandModel);

        int newIndex = _bands.Count;
        var vm = new EqBandViewModel(bandModel, newIndex, SaveCurrentProfile);
        _bands.Add(vm);

        OnPropertyChanged(nameof(BandCount));
        OnPropertyChanged(nameof(BandCountText));
        OnPropertyChanged(nameof(CanAddBand));
        OnPropertyChanged(nameof(HasEmptyBands));

        SaveCurrentProfile();
        return true;
    }

    public bool RemoveBand(EqBandViewModel band)
    {
        if (_selectedProfile == null || band == null) return false;
        int idx = _bands.IndexOf(band);
        if (idx < 0) return false;

        return RemoveBandAt(idx);
    }

    public bool RemoveBandAt(int index)
    {
        if (_selectedProfile == null || index < 0 || index >= _bands.Count) return false;

        _selectedProfile.Bands.RemoveAt(index);
        _bands.RemoveAt(index);

        for (int i = index; i < _bands.Count; i++)
        {
            _bands[i].Index = i;
        }

        OnPropertyChanged(nameof(BandCount));
        OnPropertyChanged(nameof(BandCountText));
        OnPropertyChanged(nameof(CanAddBand));
        OnPropertyChanged(nameof(HasEmptyBands));

        SaveCurrentProfile();
        return true;
    }

    public void SaveCurrentProfile()
    {
        if (_selectedProfile == null) return;
        _eqSettingsService.SaveProfile(_selectedProfile);
        RecalculateVisualizer();
    }

    public void RecalculateVisualizer(double width = 0, double height = 0)
    {
        double w = double.IsFinite(width) && width > 20 ? width : _visualizerWidth;
        double h = double.IsFinite(height) && height > 20 ? height : _visualizerHeight;
        _visualizerWidth = w;
        _visualizerHeight = h;

        if (_selectedProfile == null)
        {
            VisualizerData = EqVisualizerCalculator.Calculate(null, w, h);
            return;
        }

        var profileForVisualizer = _selectedProfile.Clone();
        profileForVisualizer.Enabled = IsMasterEnabled;

        VisualizerData = EqVisualizerCalculator.Calculate(profileForVisualizer, w, h);
    }

    public void SetExclusiveSessionState(bool isExclusive)
    {
        if (_isExclusiveSession != isExclusive)
        {
            _isExclusiveSession = isExclusive;
            OnPropertyChanged(nameof(IsExclusiveWarningVisible));
        }
    }
}
