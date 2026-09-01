using System.Globalization;
using System.Text.Json;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FormatProbe;

/// <summary>Diagnoses WASAPI exclusive support for the configured device and the
/// user's actual FLAC files (runs with the same code path as the player).</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        // device: from settings, or default
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DawnPlayer", "settings.json");
        string? deviceId = null;
        var libFolders = new List<string>();
        var policy = ExclusiveBitDepth.Source;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("Output", out var o))
            {
                if (o.TryGetProperty("DeviceId", out var d) && d.ValueKind == JsonValueKind.String) deviceId = d.GetString();
                if (o.TryGetProperty("ExclusiveBitDepth", out var b) && b.ValueKind == JsonValueKind.Number)
                    policy = (ExclusiveBitDepth)b.GetInt32();
            }
            if (root.TryGetProperty("Library", out var l) && l.TryGetProperty("Folders", out var fs))
                foreach (var f in fs.EnumerateArray()) libFolders.Add(f.GetString() ?? "");
        }
        catch { }

        using var device = WasapiDeviceService.OpenDevice(deviceId);
        if (device == null)
        {
            Console.WriteLine("오디오 렌더 장치를 찾을 수 없습니다.");
            return 1;
        }
        Console.WriteLine($"장치: {device.FriendlyName}");
        // NAudio >= 2.3.0 returns a NEW IAudioClient from MMDevice.AudioClient on every get, so
        // the per-call `using` below (and the ones in the probe loops) is correct. Under 2.2.x it
        // returned a cached instance and this pattern would dispose the device's only client.
        using (var client = device.AudioClient)
        {
            var mix = client.MixFormat;
            Console.WriteLine($"공유 모드 믹스 포맷: {WasapiDeviceService.Describe(mix)} (encoding={mix.Encoding})");
        }

        // who is holding the endpoint?
        try
        {
            var meter = device.AudioMeterInformation;
            Console.WriteLine($"엔드포인트 피크 레벨: {meter.MasterPeakValue:0.000} ({(meter.MasterPeakValue > 0.0001 ? "★ 활성 오디오 재생 중" : "조용함")})");
            var mgr = device.AudioSessionManager;
            mgr.RefreshSessions();
            int active = 0;
            for (int i = 0; i < mgr.Sessions.Count; i++)
            {
                var s = mgr.Sessions[i];
                if ((int)s.State != 1) continue; // AudioSessionStateActive
                active++;
                string procName = "?";
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID);
                    procName = proc.ProcessName;
                }
                catch { }
                Console.WriteLine($"  활성 세션: [{procName}] {s.DisplayName}");
            }
            if (active == 0) Console.WriteLine("  활성 오디오 세션 없음 → 다른 앱 점유 아님. '독점 제어 허용' 비활성화가 유력");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"세션 조회 실패: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 배타 모드 형식 지원 매트릭스 (2채널) ===");
        int[] rates = { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 };
        int[] bitsList = { 16, 24, 32 };
        Console.Write($"{"",10}");
        foreach (var b in bitsList) Console.Write($"{b,10}bit");
        Console.WriteLine();
        foreach (var rate in rates)
        {
            Console.Write($"{rate / 1000.0,7:N1}k");
            foreach (var bits in bitsList)
            {
                var fmt = bits == 16
                    ? new WaveFormat(rate, 16, 2)
                    : (WaveFormat)new WaveFormatExtensible(rate, bits, 2);
                string result;
                try
                {
                    using var c = device.AudioClient;
                    result = c.IsFormatSupported(AudioClientShareMode.Exclusive, fmt, out _) ? "OK" : "no";
                }
                catch (Exception ex)
                {
                    var hr = ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
                    result = $"EX:{ex.Message.Split('\n')[0]}({hr})";
                }
                Console.Write($"{result,10}");
            }
            Console.WriteLine();
        }

        // Direct Initialize test: does exclusive Initialize actually succeed
        // even when IsFormatSupported reports false? (known UAC2 driver quirk)
        Console.WriteLine();
        Console.WriteLine("=== 배타 모드 직접 Initialize 테스트 ===");
        foreach (var (rate, bits) in new[] { (44100, 16), (44100, 24), (48000, 24), (44100, 32) })
        {
            var fmt = bits == 16
                ? new WaveFormat(rate, 16, 2)
                : (WaveFormat)new WaveFormatExtensible(rate, bits, 2);
            foreach (var ms in new[] { 100 })
            {
                try
                {
                    using var c = device.AudioClient;
                    c.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback,
                        ms * 10000L, 0, fmt, Guid.Empty);
                    Console.WriteLine($"    {rate}Hz {bits}bit 2ch buffer={ms}ms → Initialize 성공!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    {rate}Hz {bits}bit 2ch buffer={ms}ms → 실패: {ex.Message.Split('\n')[0]} (0x{ex.HResult:X8})");
                }
            }
        }

        // actual files
        Console.WriteLine();
        Console.WriteLine($"=== 라이브러리 FLAC/포맷 조사 (정책: {policy}) ===");
        var files = new List<string>();
        foreach (var folder in libFolders.Where(Directory.Exists))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Take(2000));
                if (files.Count == 0)
                    files.AddRange(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                        .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".flac" or ".mp3" or ".wav" or ".m4a").Take(2000));
            }
            catch { }
        }

        if (files.Count == 0)
        {
            Console.WriteLine("라이브러리 폴더에서 파일을 찾지 못했습니다: " + string.Join(", ", libFolders));
            return 0;
        }

        int shown = 0;
        var sourceFormats = new HashSet<string>();
        foreach (var file in files)
        {
            if (shown >= 12) break;
            try
            {
                using var reader = AudioFileReaderFactory.Open(file);
                var src = reader.SourceFormat;
                var key = $"{src.SampleRate}|{src.Channels}|{src.BitsPerSample}|{src.Encoding}";
                var negotiated = WasapiDeviceService.TryNegotiateExclusive(device, src, policy);
                var dup = sourceFormats.Contains(key) ? "  (같은 포맷 반복)" : "";
                sourceFormats.Add(key);
                Console.WriteLine($"{Path.GetFileName(file)}");
                Console.WriteLine($"    소스: {WasapiDeviceService.Describe(src)} encoding={src.Encoding} ch={src.Channels}");
                Console.WriteLine(negotiated != null
                    ? $"    배타 협상 결과: {negotiated.Encoding} {WasapiDeviceService.Describe(negotiated)}{dup}"
                    : $"    배타 협상 결과: NULL → 공유 폴백{dup}");
                shown++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(file)}    열기 실패: {ex.Message}");
                shown++;
            }
        }

        if (args.Contains("--verbose"))
        {
            Console.WriteLine();
            Console.WriteLine("=== 1채널/추가 확인 (첫 FLAC 소스 기반) ===");
            var first = files.FirstOrDefault();
            if (first != null)
            {
                try
                {
                    using var reader = AudioFileReaderFactory.Open(first);
                    var src = reader.SourceFormat;
                    foreach (var ch in new[] { 1, 2 })
                        foreach (var bits in bitsList)
                        {
                            var fmt = bits == 16
                                ? new WaveFormat(src.SampleRate, 16, ch)
                                : (WaveFormat)new WaveFormatExtensible(src.SampleRate, bits, ch);
                            bool ok;
                            try
                            {
                                using var c = device.AudioClient;
                                ok = c.IsFormatSupported(AudioClientShareMode.Exclusive, fmt, out _);
                            }
                            catch { ok = false; }
                            Console.WriteLine($"    {src.SampleRate}Hz {bits}bit {ch}ch → {(ok ? "OK" : "-")}");
                        }
                }
                catch { }
            }
        }

        return 0;
    }
}
