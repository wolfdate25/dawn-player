using System;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Turns audio-client failures into messages a user can act on.
/// </summary>
/// <remarks>
/// Pure functions apart from the endpoint probe in
/// <see cref="BuildExclusiveFailureReason"/>: extracted from the playback controller so the
/// HRESULT-to-explanation mapping can be read and tested without an output session.
/// </remarks>
public static class AudioErrorMessages
{
    /// <summary>
    /// Turns an audio-client failure into something a user can act on. The raw
    /// "재생 시작 실패: 0x8889000A" told nobody anything.
    /// </summary>
    public static string DescribeStartFailure(Exception primary, Exception? original = null)
    {
        foreach (var ex in new[] { primary, original })
        {
            if (ex == null) continue;
            int hr = ex.HResult;
            if (hr == WasapiDeviceService.AudclntDeviceInUse)
            {
                return "다른 프로그램이 오디오 장치를 배타(독점) 모드로 사용 중입니다. " +
                       "해당 프로그램을 종료하거나, 설정에서 배타 모드를 끄고 다시 시도하세요.";
            }
            if (hr == WasapiDeviceService.AudclntUnsupportedFormat)
            {
                return "장치가 이 트랙의 오디오 형식을 지원하지 않습니다. 설정에서 배타 모드를 끄거나 비트 심도를 변경해 보세요.";
            }
            if (hr == WasapiDeviceService.AudclntBufferSizeNotAligned)
            {
                return "오디오 드라이버가 요청한 버퍼 크기를 거부했습니다. 설정에서 지연 시간(latency)을 조정해 보세요.";
            }
            if (hr == WasapiDeviceService.AudclntDeviceInvalidated)
            {
                return "오디오 장치를 사용할 수 없습니다 (제거되었거나 비활성화됨). 설정에서 다른 출력 장치를 선택하세요.";
            }
            if (hr == WasapiDeviceService.AudclntExclusiveModeNotAllowed)
            {
                return "Windows 소리 설정에서 이 장치의 배타(독점) 모드가 허용되지 않았습니다. " +
                       "소리 제어판에서 허용하거나, 설정에서 배타 모드를 끄세요.";
            }
        }

        return primary.Message;
    }

    /// <summary>Distinguishes "another app holds the device" from a genuinely unsupported
    /// format by probing a real exclusive Initialize.</summary>
    public static string BuildExclusiveFailureReason(MMDevice device, WaveFormat source)
    {
        int hr = 0;
        int bits = source.BitsPerSample > 0 ? source.BitsPerSample : 16;
        foreach (var fmt in WasapiDeviceService.GetFormatVariants(source.SampleRate, bits, source.Channels))
        {
            hr = WasapiDeviceService.ProbeExclusiveInitialize(device, fmt);
            if (hr == 0) break;
        }

        if (hr == WasapiDeviceService.AudclntDeviceInUse)
        {
            var holders = WasapiDeviceService.GetActiveSessionHolders(device);
            if (holders.Count == 0 && !WasapiDeviceService.IsExclusiveModeEnabledInWindows(device))
            {
                return "배타 모드 불가: Windows 소리 제어판에서 장치의 '애플리케이션의 독점 제어 허용' 설정이 꺼져 있습니다. 설정을 켜면 배타 모드가 가능합니다. 지금은 공유 모드로 재생합니다.";
            }

            if (holders.Count > 0 && !WasapiDeviceService.IsExclusivePriorityEnabledInWindows(device))
            {
                var who = $"({string.Join(", ", holders)})";
                return $"배타 모드 불가: 다른 프로그램{who}이(가) 실행 중이며, Windows의 '독점 모드 응용 프로그램에 우선 순위 부여' 설정이 꺼져 있습니다. 소리 제어판에서 해당 옵션을 켜면 다른 프로그램 재생 중에도 우선권을 가져올 수 있습니다. 지금은 공유 모드로 재생합니다.";
            }

            var whoStr = holders.Count > 0 ? $"({string.Join(", ", holders)})" : "";
            return $"배타 모드 불가: 다른 프로그램{whoStr}이(가) 장치를 사용 중입니다. 해당 프로그램의 소리를 끄면 배타 모드가 가능합니다. 지금은 공유 모드로 재생합니다.";
        }
        if (hr == WasapiDeviceService.AudclntUnsupportedFormat)
        {
            return $"장치가 이 형식({WasapiDeviceService.Describe(source)})의 배타 출력을 지원하지 않습니다. 공유 모드로 재생합니다.";
        }
        if (hr == 0 || hr == WasapiDeviceService.AudclntBufferSizeNotAligned)
        {
            return $"드라이버가 배타 형식 쿼리에 실패를 보고하지만 초기화는 허용합니다({WasapiDeviceService.Describe(source)}). 공유 모드로 재생합니다.";
        }
        return $"배타 모드 협상 실패(0x{hr:X8}, {WasapiDeviceService.Describe(source)}). 공유 모드로 재생합니다.";
    }
}
