using System.Diagnostics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Audio;

/// <summary>Supported destination audio formats for transcoding.</summary>
public enum OutputAudioFormat
{
    Wav = 0,
    Flac = 1,
    Mp3 = 2,
    Opus = 3,
    Aac = 4
}

/// <summary>Options for one transcode run.</summary>
public sealed record TranscodeOptions
{
    /// <summary>Directory the converted file lands in (created on demand).</summary>
    public required string OutputDirectory { get; init; }
    /// <summary>Target output format (Wav, Flac, Mp3, Opus, Aac).</summary>
    public OutputAudioFormat OutputFormat { get; init; } = OutputAudioFormat.Wav;
    /// <summary>Format-specific quality or bitrate preset (e.g. "320k", "V0", "160k", "5").</summary>
    public string? Quality { get; init; }
    /// <summary>Explicit path to ffmpeg binary. If null, resolves from settings or PATH.</summary>
    public string? FfmpegPath { get; init; }
    /// <summary>Apply the track's ReplayGain (track gain, album fallback) so the converted file
    /// plays at reference level without a ReplayGain-aware player.</summary>
    public bool ApplyReplayGain { get; init; }
    /// <summary>Copy title/artist/album/... and embedded art into the converted file.</summary>
    public bool WriteTags { get; init; } = true;
    /// <summary>Bits per sample for WAV output (16, 24, 32).</summary>
    public short BitsPerSample { get; init; } = 16;
}

/// <summary>Outcome of one conversion.</summary>
public enum TranscodeResult
{
    Ok,
    OpenFailed,
    WriteFailed,
    Cancelled,
    FfmpegMissing,
    UnsupportedTrack
}

/// <summary>
/// Pure CLI arguments builder for ffmpeg transcoding.
/// </summary>
public static class FfmpegTranscodeArgs
{
    /// <summary>
    /// Pipe PCM depth feeding the encoder: lossless containers get s24le (a s16 pipe silently
    /// truncated 24-bit sources and broke the lossless promise), lossy targets get s16le.
    /// </summary>
    public static string PipeFormatFor(OutputAudioFormat format) =>
        format == OutputAudioFormat.Flac ? "s24le" : "s16le";

    public static int PipeBytesPerSample(OutputAudioFormat format) =>
        format == OutputAudioFormat.Flac ? 3 : 2;

    public static string Build(
        OutputAudioFormat format,
        string? quality,
        int sampleRate,
        int channels,
        int pipeBitsPerSample,
        string outputPath)
    {
        string codecArgs = format switch
        {
            OutputAudioFormat.Flac => $"-c:a flac -compression_level {(string.IsNullOrWhiteSpace(quality) ? "5" : quality.Trim())}",
            OutputAudioFormat.Mp3 => string.IsNullOrWhiteSpace(quality) || quality.StartsWith("320", StringComparison.OrdinalIgnoreCase)
                ? "-c:a libmp3lame -b:a 320k"
                : quality.StartsWith("V", StringComparison.OrdinalIgnoreCase)
                    ? $"-c:a libmp3lame -q:a {quality.TrimStart('V', 'v')}"
                    : $"-c:a libmp3lame -b:a {quality.Trim()}",
            OutputAudioFormat.Opus => $"-c:a libopus -b:a {(string.IsNullOrWhiteSpace(quality) ? "160k" : quality.Trim())} -vbr on",
            OutputAudioFormat.Aac => $"-c:a aac -b:a {(string.IsNullOrWhiteSpace(quality) ? "256k" : quality.Trim())}",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

        // -nostats: stderr is drained only after exit, and periodic progress output is
        // version-dependent (modern builds suppress it when stderr is piped, older ones do not)
        // — an undrained pipe filling up stalls ffmpeg mid-encode.
        string pipeFormat = pipeBitsPerSample == 24 ? "s24le" : "s16le";
        return $"-nostats -hide_banner -loglevel error -y -f {pipeFormat} -ar {sampleRate} -ac {channels} -i pipe:0 {codecArgs} \"{outputPath}\"";
    }
}

/// <summary>
/// Decodes any supported track (physical file, cue range, ...) to a PCM WAV file, optionally with
/// ReplayGain baked in and full tag/art carry-over. Cue-sheet tracks convert to their own audio —
/// this is also the album-image splitter. Output naming is "artist - title.wav", uniquified.
/// </summary>
public static class AudioTranscoder
{
    public static string SuggestFileName(Track track, string outputDirectory, OutputAudioFormat format = OutputAudioFormat.Wav)
    {
        string artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
        string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileName(track.Path) : track.Title;
        string ext = format switch
        {
            OutputAudioFormat.Flac => ".flac",
            OutputAudioFormat.Mp3 => ".mp3",
            OutputAudioFormat.Opus => ".opus",
            OutputAudioFormat.Aac => ".m4a",
            _ => ".wav"
        };
        return SuggestUnique(Path.Combine(outputDirectory, Sanitize(artist + " - " + title) + ext));
    }

    /// <summary>Converts one track to WAV (backward-compatible overload). Calls <see cref="Convert"/>.</summary>
    public static string? ConvertToWav(Track track, TranscodeOptions options, out TranscodeResult result,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        return Convert(track, options with { OutputFormat = OutputAudioFormat.Wav }, out result, progress, ct);
    }

    /// <summary>
    /// Converts one track to the format specified in <paramref name="options"/> (WAV natively, or
    /// FLAC/MP3/Opus/AAC via ffmpeg pipe). Returns the output path, or null with <paramref name="result"/>
    /// describing the failure. Blocking with cancellation checks between decode slices.
    /// </summary>
    public static string? Convert(Track track, TranscodeOptions options, out TranscodeResult result,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        result = TranscodeResult.Ok;

        // Invariant: Non-file streams (radio, http URLs) are not transcode targets.
        if (track.SourceKind != TrackSourceKind.File || track.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || track.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            result = TranscodeResult.UnsupportedTrack;
            return null;
        }

        if (options.OutputFormat == OutputAudioFormat.Wav)
        {
            return ConvertToWavInternal(track, options, out result, progress, ct);
        }

        return ConvertWithFfmpeg(track, options, out result, progress, ct);
    }

    private static string? ConvertToWavInternal(Track track, TranscodeOptions options, out TranscodeResult result,
        Action<double>? progress, CancellationToken ct)
    {
        result = TranscodeResult.Ok;
        string? outputPath = null;
        try
        {
            using var reader = AudioFileReaderFactory.Open(track.Path);
            Directory.CreateDirectory(options.OutputDirectory);
            outputPath = SuggestFileName(track, options.OutputDirectory, OutputAudioFormat.Wav);

            // ReplayGain to bake in: track gain, album fallback; clamped like playback does.
            float gain = 1f;
            if (options.ApplyReplayGain)
            {
                double? db = track.RgTrackGainDb ?? track.RgAlbumGainDb;
                if (db.HasValue)
                {
                    float linear = MathF.Pow(10f, (float)db.Value / 20f);
                    gain = Math.Clamp(linear, 0f, 8f);
                }
            }

            var fmt = reader.SourceFormat;
            int bits = options.BitsPerSample is 16 or 24 or 32 ? options.BitsPerSample : 16;
            long totalFrames = track.DurationMs > 0 ? (long)(track.DurationMs / 1000.0 * fmt.SampleRate) : 0;

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteWavHeader(output, fmt.SampleRate, fmt.Channels, bits, dataSize: 0); // patched at the end

            var floatBuf = new float[fmt.SampleRate * fmt.Channels];
            long framesWritten = 0;
            int read;
            while ((read = reader.Samples.Read(floatBuf)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = read / fmt.Channels;

                var pcm = new byte[frames * fmt.Channels * (bits / 8)];
                int byteIdx = 0;
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < fmt.Channels; c++)
                    {
                        double v = floatBuf[f * fmt.Channels + c] * gain;
                        v = Math.Clamp(v, -1.0, 1.0);
                        if (bits == 16)
                        {
                            short s16 = (short)Math.Round(v * 32767.0);
                            pcm[byteIdx++] = (byte)(s16 & 0xFF);
                            pcm[byteIdx++] = (byte)((s16 >> 8) & 0xFF);
                        }
                        else if (bits == 24)
                        {
                            int s24 = (int)Math.Round(v * 8388607.0);
                            pcm[byteIdx++] = (byte)(s24 & 0xFF);
                            pcm[byteIdx++] = (byte)((s24 >> 8) & 0xFF);
                            pcm[byteIdx++] = (byte)((s24 >> 16) & 0xFF);
                        }
                        else
                        {
                            int s32 = (int)Math.Round(v * 2147483647.0);
                            pcm[byteIdx++] = (byte)(s32 & 0xFF);
                            pcm[byteIdx++] = (byte)((s32 >> 8) & 0xFF);
                            pcm[byteIdx++] = (byte)((s32 >> 16) & 0xFF);
                            pcm[byteIdx++] = (byte)((s32 >> 24) & 0xFF);
                        }
                    }
                }
                output.Write(pcm, 0, pcm.Length);
                framesWritten += frames;
                progress?.Invoke(totalFrames > 0 ? Math.Clamp(framesWritten / (double)totalFrames, 0, 1) : 0);
            }

            // Patch the RIFF sizes with the real byte counts.
            long dataBytes = output.Length - 44;
            output.Seek(0, SeekOrigin.Begin);
            WriteWavHeader(output, fmt.SampleRate, fmt.Channels, bits, dataBytes);

            if (options.WriteTags)
            {
                ApplyMetadataAndArt(outputPath, track);
            }
        }
        catch (AudioOpenException)
        {
            result = TranscodeResult.OpenFailed;
            return null;
        }
        catch (OperationCanceledException)
        {
            result = TranscodeResult.Cancelled;
            TryDelete(outputPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[transcode] wav write failed for '{outputPath}': {ex.Message}");
            result = TranscodeResult.WriteFailed;
            TryDelete(outputPath);
            return null;
        }

        result = TranscodeResult.Ok;
        return outputPath;
    }

    private static string? ConvertWithFfmpeg(Track track, TranscodeOptions options, out TranscodeResult result,
        Action<double>? progress, CancellationToken ct)
    {
        result = TranscodeResult.Ok;
        string? ffmpeg = ResolveFfmpegExecutable(options.FfmpegPath);
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
        {
            result = TranscodeResult.FfmpegMissing;
            return null;
        }

        string? outputPath = null;
        Process? process = null;
        try
        {
            using var reader = AudioFileReaderFactory.Open(track.Path);
            Directory.CreateDirectory(options.OutputDirectory);
            outputPath = SuggestFileName(track, options.OutputDirectory, options.OutputFormat);

            float gain = 1f;
            if (options.ApplyReplayGain)
            {
                double? db = track.RgTrackGainDb ?? track.RgAlbumGainDb;
                if (db.HasValue)
                {
                    float linear = MathF.Pow(10f, (float)db.Value / 20f);
                    gain = Math.Clamp(linear, 0f, 8f);
                }
            }

            var fmt = reader.SourceFormat;
            long totalFrames = track.DurationMs > 0 ? (long)(track.DurationMs / 1000.0 * fmt.SampleRate) : 0;
            int pipeBytes = FfmpegTranscodeArgs.PipeBytesPerSample(options.OutputFormat);

            string args = FfmpegTranscodeArgs.Build(options.OutputFormat, options.Quality, fmt.SampleRate, fmt.Channels, pipeBytes * 8, outputPath);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };

            process = Process.Start(psi);
            if (process == null)
            {
                result = TranscodeResult.WriteFailed;
                return null;
            }

            using (var stdin = process.StandardInput.BaseStream)
            {
                var floatBuf = new float[fmt.SampleRate * fmt.Channels];
                long framesWritten = 0;
                int read;
                while ((read = reader.Samples.Read(floatBuf)) > 0)
                {
                    if (ct.IsCancellationRequested)
                    {
                        TryKill(process);
                        ct.ThrowIfCancellationRequested();
                    }

                    int frames = read / fmt.Channels;
                    var pcm = new byte[frames * fmt.Channels * pipeBytes];
                    int byteIdx = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        for (int c = 0; c < fmt.Channels; c++)
                        {
                            double v = Math.Clamp(floatBuf[f * fmt.Channels + c] * gain, -1.0, 1.0);
                            if (pipeBytes == 3)
                            {
                                // float32의 유횩 정밀도(24-bit 가수)와 정확히 대응 — 24-bit 소스가
                                // 무손실 보존된다.
                                int s24 = (int)Math.Round(v * 8388607.0);
                                pcm[byteIdx++] = (byte)(s24 & 0xFF);
                                pcm[byteIdx++] = (byte)((s24 >> 8) & 0xFF);
                                pcm[byteIdx++] = (byte)((s24 >> 16) & 0xFF);
                            }
                            else
                            {
                                short s16 = (short)Math.Round(v * 32767.0);
                                pcm[byteIdx++] = (byte)(s16 & 0xFF);
                                pcm[byteIdx++] = (byte)((s16 >> 8) & 0xFF);
                            }
                        }
                    }

                    stdin.Write(pcm, 0, pcm.Length);
                    framesWritten += frames;
                    progress?.Invoke(totalFrames > 0 ? Math.Clamp(framesWritten / (double)totalFrames, 0, 1) : 0);
                }
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string err = process.StandardError.ReadToEnd();
                Log.Warn($"[transcode] ffmpeg exited with code {process.ExitCode}: {err}");
                result = TranscodeResult.WriteFailed;
                TryDelete(outputPath);
                return null;
            }

            if (options.WriteTags)
            {
                ApplyMetadataAndArt(outputPath, track);
            }
        }
        catch (AudioOpenException)
        {
            result = TranscodeResult.OpenFailed;
            return null;
        }
        catch (OperationCanceledException)
        {
            result = TranscodeResult.Cancelled;
            TryDelete(outputPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[transcode] ffmpeg transcode failed for '{outputPath}': {ex.Message}");
            result = TranscodeResult.WriteFailed;
            TryDelete(outputPath);
            return null;
        }
        finally
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }

        result = TranscodeResult.Ok;
        return outputPath;
    }

    private static void ApplyMetadataAndArt(string outputPath, Track track)
    {
        Library.TagWriter.TryApplyAtomic(outputPath, new Library.TagEdit(
            Title: track.Title,
            Artist: track.Artist,
            AlbumArtist: track.AlbumArtist,
            Album: track.Album,
            Genre: track.Genre,
            Year: track.Year,
            TrackNo: track.TrackNo,
            DiscNo: track.DiscNo,
            Art: string.IsNullOrEmpty(track.ArtPath) ? Library.TagEditorArt.None : Library.TagEditorArt.Embed,
            ArtSourcePath: track.ArtPath));

        if (track.RgTrackGainDb.HasValue)
        {
            Library.TagWriter.TrySetReplayGain(outputPath,
                track.RgTrackGainDb.Value, track.RgTrackPeak ?? 0,
                track.RgAlbumGainDb, track.RgAlbumPeak);
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>True when an ffmpeg binary can be resolved (explicit path, the YouTube runner's
    /// bundled copy, or PATH). The transcode dialog surfaces this up front instead of letting the
    /// whole batch fail with FfmpegMissing one track at a time.</summary>
    public static bool IsFfmpegAvailable(string? configured = null)
    {
        string? ffmpeg = ResolveFfmpegExecutable(configured);
        return !string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg);
    }

    private static string? ResolveFfmpegExecutable(string? configured)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        try
        {
            string runnerBinary = Network.YouTube.YouTubeProcess.Runner.FfmpegBinary;
            if (!string.IsNullOrEmpty(runnerBinary) && File.Exists(runnerBinary))
            {
                return runnerBinary;
            }
        }
        catch { }

        return FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
    }

    private static string? FindOnPath(string binaryName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), binaryName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Trace($"[transcode] output delete failed '{path}': {ex.Message}"); }
    }

    /// <summary>Writes a canonical 44-byte PCM WAV header. Sizes are patched afterwards by
    /// re-seeking when the data length becomes known.</summary>
    private static void WriteWavHeader(Stream stream, int sampleRate, int channels, int bits, long dataSize)
    {
        int blockAlign = channels * bits / 8;
        int byteRate = sampleRate * blockAlign;

        var header = new byte[44];
        var ascii = new Action<int, string>((pos, s) =>
        {
            for (int i = 0; i < s.Length; i++) header[pos + i] = (byte)s[i];
        });
        ascii(0, "RIFF");
        WriteInt(header, 4, (int)(36 + dataSize));
        ascii(8, "WAVE");
        ascii(12, "fmt ");
        WriteInt(header, 16, 16);
        WriteShort(header, 20, 1); // PCM
        WriteShort(header, 22, (short)channels);
        WriteInt(header, 24, sampleRate);
        WriteInt(header, 28, byteRate);
        WriteShort(header, 32, (short)blockAlign);
        WriteShort(header, 34, (short)bits);
        ascii(36, "data");
        WriteInt(header, 40, (int)dataSize);
        stream.Write(header, 0, header.Length);
    }

    private static void WriteInt(byte[] b, int pos, int value)
    {
        b[pos] = (byte)value;
        b[pos + 1] = (byte)(value >> 8);
        b[pos + 2] = (byte)(value >> 16);
        b[pos + 3] = (byte)(value >> 24);
    }

    private static void WriteShort(byte[] b, int pos, int value)
    {
        b[pos] = (byte)value;
        b[pos + 1] = (byte)(value >> 8);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    /// <summary>"name.wav" → "name.wav", "name 2.wav", ... first non-existing wins. The output
    /// directory part of <paramref name="path"/> must already point at the destination folder.</summary>
    private static string SuggestUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{baseName} {i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
