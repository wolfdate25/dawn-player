using DawnPlayer.App.Localization;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Controls;

public static class AudioFormatBadgeFormatter
{
    public static string GetCodec(string? codec, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(codec))
        {
            var upper = codec.Trim().ToUpperInvariant();
            if (upper.Contains("FLAC")) return "FLAC";
            if (upper.Contains("MP3") || upper.Contains("MPEG")) return "MP3";
            if (upper.Contains("AAC")) return "AAC";
            if (upper.Contains("ALAC")) return "ALAC";
            if (upper.Contains("WAV") || upper.Contains("PCM")) return "WAV";
            if (upper.Contains("OGG") || upper.Contains("VORBIS")) return "OGG";
            if (upper.Contains("OPUS")) return "OPUS";
            if (upper.Contains("WMA")) return "WMA";
            if (upper.Contains("APE") || upper.Contains("MONKEY")) return "APE";
            if (upper.Contains("DSD") || upper.Contains("DSF") || upper.Contains("DFF")) return "DSD";
            return upper;
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath).TrimStart('.').Trim().ToUpperInvariant();
            if (ext == "M4A") return "AAC";
            return ext;
        }

        return string.Empty;
    }

    public static string GetDriverLabel(AudioDriverType driver, bool exclusive) => driver switch
    {
        AudioDriverType.DirectSound => "DirectSound",
        AudioDriverType.WaveOut => "WaveOut",
        _ => exclusive
            ? AppStrings.Get("Badge_WasapiExclusive", "WASAPI 배타")
            : AppStrings.Get("Badge_WasapiShared", "WASAPI 공유")
    };

    public static string FormatTrackBadgeText(Track? track)
    {
        if (track == null) return string.Empty;

        var parts = new List<string>();
        var codec = GetCodec(track.Codec, track.Path);
        if (!string.IsNullOrEmpty(codec))
        {
            parts.Add(codec);
        }

        if (track.BitsPerSample > 0 && track.SampleRate > 0)
        {
            double khz = track.SampleRate / 1000.0;
            string sampleRateStr = khz % 1 == 0 ? $"{khz:0}kHz" : $"{khz:0.0}kHz";
            parts.Add($"{track.BitsPerSample}bit/{sampleRateStr}");
        }
        else if (track.SampleRate > 0)
        {
            double khz = track.SampleRate / 1000.0;
            string sampleRateStr = khz % 1 == 0 ? $"{khz:0}kHz" : $"{khz:0.0}kHz";
            parts.Add(sampleRateStr);
        }
        else if (track.BitrateKbps > 0)
        {
            parts.Add($"{track.BitrateKbps}kbps");
        }

        return string.Join(" · ", parts);
    }

    public static string FormatOutputBadgeText(SessionInfo? sessionInfo)
    {
        if (sessionInfo == null) return string.Empty;

        var driverLabel = GetDriverLabel(sessionInfo.Driver, sessionInfo.Exclusive);
        if (!string.IsNullOrWhiteSpace(sessionInfo.DeviceName))
        {
            return $"{driverLabel} · {sessionInfo.DeviceName}";
        }
        return driverLabel;
    }

    public static bool IsBadgeVisible(string? badgeText) => !string.IsNullOrWhiteSpace(badgeText);
}
