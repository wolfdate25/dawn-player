using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Audio;

/// <summary>Options for one transcode run.</summary>
public sealed record TranscodeOptions
{
    /// <summary>Directory the WAV lands in (created on demand).</summary>
    public required string OutputDirectory { get; init; }
    /// <summary>Apply the track's ReplayGain (track gain, album fallback) so the converted file
    /// plays at reference level without a ReplayGain-aware player.</summary>
    public bool ApplyReplayGain { get; init; }
    /// <summary>Copy title/artist/album/... and embedded art into the converted file.</summary>
    public bool WriteTags { get; init; } = true;
    /// <summary>16-bit PCM output.</summary>
    public short BitsPerSample { get; init; } = 16;
}

/// <summary>Outcome of one conversion.</summary>
public enum TranscodeResult { Ok, OpenFailed, WriteFailed, Cancelled }

/// <summary>
/// Decodes any supported track (physical file, cue range, ...) to a PCM WAV file, optionally with
/// ReplayGain baked in and full tag/art carry-over. Cue-sheet tracks convert to their own audio —
/// this is also the album-image splitter. Output naming is "artist - title.wav", uniquified.
/// </summary>
public static class AudioTranscoder
{
    public static string SuggestFileName(Track track, string outputDirectory)
    {
        string artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
        string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileName(track.Path) : track.Title;
        return SuggestUnique(Path.Combine(outputDirectory, Sanitize(artist + " - " + title) + ".wav"));
    }

    /// <summary>Converts one track. Returns the output path, or null with <paramref name="result"/>
    /// describing the failure. Blocking with cancellation checks between decode slices.</summary>
    public static string? ConvertToWav(Track track, TranscodeOptions options, out TranscodeResult result,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        result = TranscodeResult.Ok;
        string? outputPath = null;
        try
        {
            using var reader = AudioFileReaderFactory.Open(track.Path);
            Directory.CreateDirectory(options.OutputDirectory);
            outputPath = SuggestFileName(track, options.OutputDirectory);

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
            while ((read = reader.Samples.Read(floatBuf, 0, floatBuf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = read / fmt.Channels;

                // float → int PCM with gain
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

            // Carry metadata and art over through the atomic tag writer (operates on the file in
            // place; same-volume temp + File.Replace is safe here).
            if (options.WriteTags)
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
            Log.Warn($"[transcode] write failed for '{outputPath}': {ex.Message}");
            result = TranscodeResult.WriteFailed;
            TryDelete(outputPath);
            return null;
        }

        result = TranscodeResult.Ok;
        return outputPath;
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
