using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

public sealed class EqSettingsService : IEqSettingsService
{
    private readonly AppSettings _settings;
    private readonly PlaybackController? _playback;
    private readonly Action? _saveCallback;
    private readonly Action? _liveApplyCallback;

    public EqSettingsService(AppSettings settings, PlaybackController? playback = null, Action? saveCallback = null, Action? liveApplyCallback = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _playback = playback;
        _saveCallback = saveCallback ?? (() => SettingsWriter.Schedule(_settings));
        _liveApplyCallback = liveApplyCallback ?? (() => _playback?.ApplyEqualizer());
        _settings.Equalizer.EnsureDefaultProfile();
    }

    public EqSettingsService(AppSettings settings, Action? saveCallback, Action? liveApplyCallback)
        : this(settings, null, saveCallback, liveApplyCallback)
    {
    }

    public IReadOnlyList<EqProfile> GetProfiles()
    {
        _settings.Equalizer.EnsureDefaultProfile();
        return _settings.Equalizer.Profiles.Values.Select(p => p.Clone()).ToList();
    }

    public EqProfile? GetProfileById(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return null;
        _settings.Equalizer.EnsureDefaultProfile();
        return _settings.Equalizer.Profiles.TryGetValue(profileId, out var p) ? p.Clone() : null;
    }

    public EqProfile CreateProfile(string name, EqProfile? template = null)
    {
        _settings.Equalizer.EnsureDefaultProfile();
        string cleanName = string.IsNullOrWhiteSpace(name) ? "새 프로필" : name.Trim();
        string newId = Guid.NewGuid().ToString("N");

        var newProfile = template != null ? template.Clone() : new EqProfile();
        newProfile.Id = newId;
        newProfile.Name = cleanName;
        newProfile.Enabled = true;

        var clamped = ClampProfile(newProfile);
        _settings.Equalizer.Profiles[newId] = clamped;

        _saveCallback?.Invoke();
        return clamped.Clone();
    }

    public void RenameProfile(string profileId, string newName)
    {
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrWhiteSpace(newName)) return;
        _settings.Equalizer.EnsureDefaultProfile();

        if (_settings.Equalizer.Profiles.TryGetValue(profileId, out var profile))
        {
            profile.Name = newName.Trim();
            _saveCallback?.Invoke();
        }
    }

    public bool DeleteProfile(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return false;
        _settings.Equalizer.EnsureDefaultProfile();

        // Cannot delete the designated default profile or if it's the only remaining profile
        if (profileId == _settings.Equalizer.DefaultProfileId || _settings.Equalizer.Profiles.Count <= 1)
        {
            return false;
        }

        if (_settings.Equalizer.Profiles.Remove(profileId))
        {
            // Remove any device bindings that were pointing to this deleted profile
            var toRemove = _settings.Equalizer.DeviceBindings
                .Where(kvp => kvp.Value == profileId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _settings.Equalizer.DeviceBindings.Remove(key);
            }

            _saveCallback?.Invoke();
            _liveApplyCallback?.Invoke();
            return true;
        }

        return false;
    }

    public void SaveProfile(EqProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _settings.Equalizer.EnsureDefaultProfile();

        var clamped = ClampProfile(profile);
        _settings.Equalizer.Profiles[clamped.Id] = clamped;

        _saveCallback?.Invoke();
        _liveApplyCallback?.Invoke();
    }

    public string? GetBoundProfileId(AudioDriverType driver, string? deviceId)
    {
        var key = EqualizerProfileResolver.CanonicalKey(driver, deviceId);
        return _settings.Equalizer.DeviceBindings.TryGetValue(key, out var id) ? id : null;
    }

    public void BindDeviceToProfile(AudioDriverType driver, string? deviceId, string? profileId)
    {
        _settings.Equalizer.EnsureDefaultProfile();
        var key = EqualizerProfileResolver.CanonicalKey(driver, deviceId);

        if (string.IsNullOrEmpty(profileId))
        {
            _settings.Equalizer.DeviceBindings.Remove(key);
        }
        else
        {
            _settings.Equalizer.DeviceBindings[key] = profileId;
        }

        _saveCallback?.Invoke();
        _liveApplyCallback?.Invoke();
    }

    public EqProfile GetResolvedProfileForDevice(AudioDriverType driver, string? deviceId)
    {
        return EqualizerProfileResolver.Resolve(_settings.Equalizer, driver, deviceId);
    }

    public bool IsEnabled()
    {
        return _settings.Equalizer.Enabled;
    }

    public void SetEnabled(bool enabled)
    {
        _settings.Equalizer.Enabled = enabled;
        _saveCallback?.Invoke();
        _liveApplyCallback?.Invoke();
    }

    public string GetDefaultProfileId()
    {
        _settings.Equalizer.EnsureDefaultProfile();
        return _settings.Equalizer.DefaultProfileId;
    }

    public void SetDefaultProfileId(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return;
        _settings.Equalizer.EnsureDefaultProfile();
        if (_settings.Equalizer.Profiles.ContainsKey(profileId))
        {
            _settings.Equalizer.DefaultProfileId = profileId;
            _saveCallback?.Invoke();
            _liveApplyCallback?.Invoke();
        }
    }

    public void ApplyToPlayback()
    {
        _liveApplyCallback?.Invoke();
    }

    public static EqProfile ClampProfile(EqProfile input)
    {
        var copy = input.Clone();
        copy.PreampDb = Math.Clamp(copy.PreampDb, -12.0, 12.0);

        var bands = (copy.Bands ?? new List<EqBandSettings>())
            .Take(20)
            .Select(b => new EqBandSettings
            {
                Type = b.Type,
                FrequencyHz = Math.Clamp(b.FrequencyHz, 20.0, 20000.0),
                GainDb = Math.Clamp(b.GainDb, -15.0, 15.0),
                Q = Math.Clamp(b.Q, 0.1, 8.0)
            })
            .ToList();

        copy.Bands = bands;
        return copy;
    }
}
