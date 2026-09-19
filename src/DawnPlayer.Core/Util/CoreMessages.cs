namespace DawnPlayer.Core.Util;

/// <summary>Stable identifiers for the user-facing sentences Core emits through the warning
/// pipeline and exception messages. Core cannot reach the resource catalog, so it encodes
/// key + arguments and the App layer translates via resw; <see cref="Localize"/> is the
/// built-in Korean fallback that keeps Core self-sufficient (tests, CLI hosts).</summary>
public enum CoreMessageKey
{
    NextTrackMissing,
    PlayStartFailed,
    NextTrackFailed,
    PlaybackError,
    DirectSoundDeviceMissing,
    WaveOutDeviceMissing,
    ExclusiveFallbackShared,
    OutputDeviceNotFound,
    DoPUnsupportedFallback,
    FileOpenFailed,
    UnsupportedFormat,
}

/// <summary>Encoder/decoder for keyed Core messages. Wire format:
/// <c>coremsg:&lt;Key&gt;</c> or <c>coremsg:&lt;Key&gt;\u001f&lt;arg1&gt;\u001f&lt;arg2&gt;</c>.
/// The unit separator never appears in file names or exception texts in practice, and the
/// decoder tolerates embedded separators in trailing args by reading the remainder.</summary>
public static class CoreMessages
{
    public const string Prefix = "coremsg:";
    private const char Sep = '\u001f';

    public static string Encode(CoreMessageKey key, params string?[] args) =>
        Prefix + key + (args.Length == 0 ? string.Empty : Sep + string.Join(Sep, args.Select(a => a ?? string.Empty)));

    public static bool TryDecode(string raw, out CoreMessageKey key, out string[] args)
    {
        key = default;
        args = Array.Empty<string>();
        if (raw is null || !raw.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var body = raw[Prefix.Length..];
        int sep = body.IndexOf(Sep);
        var keyText = sep < 0 ? body : body[..sep];
        if (!Enum.TryParse(keyText, out key)) return false;
        if (!Enum.IsDefined(key)) return false;

        args = sep < 0
            ? Array.Empty<string>()
            : body[(sep + 1)..].Split(Sep);
        return true;
    }

    /// <summary>The built-in Korean rendering — the exact sentences Core used to hardcode.
    /// The App layer's resw catalog is authoritative for UI display; this fallback keeps
    /// decoded messages readable anywhere the catalog is unavailable.</summary>
    public static string Localize(CoreMessageKey key, params string?[] args) => key switch
    {
        CoreMessageKey.NextTrackMissing => "다음 트랙이 없습니다.",
        CoreMessageKey.PlayStartFailed => Format("재생 시작 실패: {0}", args),
        CoreMessageKey.NextTrackFailed => Format("다음 트랙 재생 실패: {0}", args),
        CoreMessageKey.PlaybackError => Format("재생 오류: {0}", args),
        CoreMessageKey.DirectSoundDeviceMissing => "설정된 DirectSound 장치를 찾을 수 없어 기본 장치로 재생합니다.",
        CoreMessageKey.WaveOutDeviceMissing => "설정된 WaveOut 장치를 찾을 수 없어 기본 사운드 매퍼로 재생합니다.",
        CoreMessageKey.ExclusiveFallbackShared => "WASAPI 배타 모드를 열 수 없습니다 (다른 프로그램이 장치 사용 중). 공유 모드로 재생합니다.",
        CoreMessageKey.OutputDeviceNotFound => "오디오 출력 장치를 찾을 수 없습니다.",
        CoreMessageKey.DoPUnsupportedFallback => "이 장치에서 DoP 재생이 불가능해 DSD를 PCM으로 변환합니다 (설정에서 다시 선택 가능).",
        CoreMessageKey.FileOpenFailed => Format("파일을 열 수 없습니다: {0}", args),
        CoreMessageKey.UnsupportedFormat => Format("지원하지 않는 형식입니다: {0}", args),
        _ => key.ToString(),
    };

    private static string Format(string frame, string?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, frame, args.Select(a => (object?)a ?? "").ToArray());
}
