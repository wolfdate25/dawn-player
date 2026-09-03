using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>
/// Status representation of Windows Exclusive Mode properties for an audio endpoint.
/// </summary>
public sealed record ExclusiveModeStatus(
    bool IsExclusiveAllowed,
    bool IsPriorityAllowed,
    string StatusText,
    string DetailsText);

/// <summary>
/// Service contract for managing audio driver selection, device enumeration,
/// WASAPI exclusive mode negotiation, buffer latency, and ReplayGain settings.
/// </summary>
public interface IAudioSettingsService
{
    /// <summary>
    /// Enumerates output devices for the specified audio driver type.
    /// </summary>
    IReadOnlyList<OutputDeviceInfo> GetDevices(AudioDriverType driverType);

    /// <summary>
    /// Resolves the selected output device, falling back to the default or first device.
    /// </summary>
    OutputDeviceInfo? GetSelectedDevice(AudioDriverType driverType, string? deviceId);

    /// <summary>
    /// Queries the Windows Exclusive Mode permissions and priority settings for the endpoint.
    /// </summary>
    ExclusiveModeStatus GetExclusiveModeStatus(string? deviceId);

    /// <summary>
    /// Switches the active audio driver type, resetting selected device, persisting settings, and restarting playback if active.
    /// </summary>
    void SetDriverType(AudioDriverType driverType);

    /// <summary>
    /// Switches the active audio device endpoint, persisting settings, and restarting playback if active.
    /// </summary>
    void SetDevice(string? deviceId);

    /// <summary>
    /// Sets whether WASAPI Exclusive mode is enabled, persisting settings, and restarting playback if active.
    /// </summary>
    void SetUseExclusiveMode(bool useExclusive);

    /// <summary>
    /// Sets the exclusive mode bit depth policy, persisting settings, and restarting playback if active.
    /// </summary>
    void SetExclusiveBitDepth(ExclusiveBitDepth bitDepth);

    /// <summary>
    /// Sets the output buffer latency in milliseconds (clamped to 30ms-500ms) and persists settings.
    /// </summary>
    void SetLatency(int latencyMs);

    /// <summary>
    /// Sets whether digital volume scaling and ReplayGain are permitted in exclusive mode.
    /// </summary>
    void SetAllowVolumeInExclusive(bool allow);

    /// <summary>
    /// Updates ReplayGain mode, preamp gain in dB (clamped to [-12, +12]), and peak anti-clipping protection.
    /// </summary>
    void SetReplayGain(ReplayGainMode mode, double preampDb, bool preventClipping);

    /// <summary>
    /// Updates normalizer enabled state, mode, target level in dBFS, max boost in dB, and response speed.
    /// </summary>
    void SetNormalizer(bool enabled, NormalizerMode mode, double targetLevelDb, double maxBoostDb, NormalizerSpeed speed);

    /// <summary>
    /// Updates the headphone crossfeed switch and strength preset (applied live).
    /// </summary>
    void SetCrossfeed(bool enabled, CrossfeedStrength strength);

    /// <summary>
    /// Updates the mono-downmix switch (applied live).
    /// </summary>
    void SetMonoDownmix(bool enabled);

    /// <summary>
    /// Opens the Windows Sound Control Panel (mmsys.cpl) for audio hardware configuration.
    /// </summary>
    void OpenSoundControlPanel();
}
