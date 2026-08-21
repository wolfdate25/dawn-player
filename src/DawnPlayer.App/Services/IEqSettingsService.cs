using System.Collections.Generic;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

public interface IEqSettingsService
{
    /// <summary>
    /// Returns all available equalizer profiles.
    /// </summary>
    IReadOnlyList<EqProfile> GetProfiles();

    /// <summary>
    /// Gets a profile by its unique ID, or null if not found.
    /// </summary>
    EqProfile? GetProfileById(string profileId);

    /// <summary>
    /// Creates a new named profile (optionally cloned from a template) and saves it.
    /// </summary>
    EqProfile CreateProfile(string name, EqProfile? template = null);

    /// <summary>
    /// Renames an existing profile.
    /// </summary>
    void RenameProfile(string profileId, string newName);

    /// <summary>
    /// Deletes a profile. If devices are bound to this profile, their bindings are removed (falling back to default).
    /// Returns false if the profile cannot be deleted (e.g. it is the default profile or the only profile).
    /// </summary>
    bool DeleteProfile(string profileId);

    /// <summary>
    /// Saves changes to an existing profile with parameter clamping and live playback push.
    /// </summary>
    void SaveProfile(EqProfile profile);

    /// <summary>
    /// Gets the profile ID explicitly bound to a device, or null if following default.
    /// </summary>
    string? GetBoundProfileId(AudioDriverType driver, string? deviceId);

    /// <summary>
    /// Binds an audio output device to a specific profile ID (or pass null/empty to follow default).
    /// </summary>
    void BindDeviceToProfile(AudioDriverType driver, string? deviceId, string? profileId);

    /// <summary>
    /// Gets the resolved active profile for a device (via binding or default fallback).
    /// </summary>
    EqProfile GetResolvedProfileForDevice(AudioDriverType driver, string? deviceId);

    /// <summary>
    /// Gets whether the global master equalizer is enabled.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// Sets the global master equalizer enabled state, saves settings, and applies to live playback.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Gets the ID of the default profile.
    /// </summary>
    string GetDefaultProfileId();

    /// <summary>
    /// Sets which profile ID serves as the global default.
    /// </summary>
    void SetDefaultProfileId(string profileId);

    /// <summary>
    /// Pushes the latest equalizer profile to the active audio playback session without restarting.
    /// </summary>
    void ApplyToPlayback();
}
