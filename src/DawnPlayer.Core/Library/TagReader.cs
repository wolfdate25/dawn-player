using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Library;

/// <summary>Reads a <see cref="Track"/> from a file's tags/properties via TagLib#.</summary>
public static class TagReader
{
    public static Track? TryRead(string path) => TryRead(path, out _);

    public static Track? TryRead(string path, out TagLib.IPicture? embeddedArt)
    {
        embeddedArt = null;
        try
        {
            using var tf = TagLib.File.Create(path);
            var tag = tf.Tag;
            var props = tf.Properties;

            var artist = FirstOrNull(tag.Performers) ?? "";
            var albumArtist = FirstOrNull(tag.AlbumArtists) ?? "";
            if (artist.Length == 0) artist = albumArtist;
            if (albumArtist.Length == 0) albumArtist = artist;

            var fi = new FileInfo(path);

            embeddedArt = tag.Pictures?
                .Where(p => p.Data.Count > 0)
                .OrderBy(p => p.Type == TagLib.PictureType.FrontCover ? 0 : 1)
                .FirstOrDefault();

            return new Track
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim(),
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = tag.Album?.Trim() ?? "",
                Genre = FirstOrNull(tag.Genres) ?? "",
                Year = (int)Math.Min(tag.Year, 9999),
                TrackNo = (int)Math.Min(tag.Track, 9999),
                DiscNo = (int)Math.Min(tag.Disc, 99),
                DurationMs = (long)props.Duration.TotalMilliseconds,
                SampleRate = props.AudioSampleRate,
                Channels = props.AudioChannels,
                BitsPerSample = props.BitsPerSample,
                Codec = DetectCodec(path, props),
                BitrateKbps = props.AudioBitrate,
                FileSize = fi.Length,
                FileModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks,
                HasLrc = File.Exists(Path.ChangeExtension(path, ".lrc")),
                RgTrackGainDb = ParseDb(GetField(tf, tag, "REPLAYGAIN_TRACK_GAIN")),
                RgTrackPeak = ParsePeak(GetField(tf, tag, "REPLAYGAIN_TRACK_PEAK")),
                RgAlbumGainDb = ParseDb(GetField(tf, tag, "REPLAYGAIN_ALBUM_GAIN")),
                RgAlbumPeak = ParsePeak(GetField(tf, tag, "REPLAYGAIN_ALBUM_PEAK")),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstOrNull(string[]? arr) =>
        arr is { Length: > 0 } && !string.IsNullOrWhiteSpace(arr[0]) ? arr[0].Trim() : null;

    /// <summary>Reads embedded lyrics from ID3v2 USLT/SYLT, Vorbis comments (FLAC/OGG), or MP4/M4A tags.</summary>
    public static string? ReadEmbeddedLyrics(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var tf = TagLib.File.Create(path);
            var lyrics = tf.Tag?.Lyrics;
            if (!string.IsNullOrWhiteSpace(lyrics)) return lyrics.Trim();

            if (tf.Tag is TagLib.Id3v2.Tag id3)
            {
                var uslt = id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(uslt)) return uslt.Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Computes a stable album cache key.</summary>
    public static string ComputeAlbumKey(Track track) => AlbumArtService.ComputeAlbumKey(track);

    /// <summary>Extracts embedded album art and caches it under %AppData%/DawnPlayer/artcache.</summary>
    public static string? TryExtractArt(Track track, string albumKey, TagLib.IPicture? picture = null) =>
        AlbumArtService.TryExtractArt(track, albumKey, picture);

    /// <summary>Looks for cover art files sitting next to the track.</summary>
    public static string? FindFolderArt(string trackPath) =>
        AlbumArtService.FindFolderArt(trackPath);

    /// <summary>Looks for cover art files sitting next to the track, reusing
    /// <paramref name="folderArtCache"/> so one directory is probed once per scan.</summary>
    public static string? FindFolderArt(string trackPath, ConcurrentDictionary<string, string?>? folderArtCache) =>
        AlbumArtService.FindFolderArt(trackPath, folderArtCache);

    internal static string DetectCodec(string path, TagLib.Properties props)
    {
        var desc = "";
        try
        {
            desc = props.Codecs.FirstOrDefault()?.Description ?? "";
        }
        catch { }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (desc.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("ALAC", StringComparison.OrdinalIgnoreCase)) return "ALAC";
        if (desc.Contains("Vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        if (desc.Contains("FLAC", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (desc.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (desc.Contains("MPEG", StringComparison.OrdinalIgnoreCase) && ext == ".mp3") return "MP3";

        return ext switch
        {
            ".mp3" => "MP3",
            ".flac" => "FLAC",
            ".ogg" or ".oga" => "Vorbis",
            ".wav" => "WAV",
            ".m4a" or ".m4b" or ".mp4" or ".alac" => "ALAC/AAC",
            ".aac" => "AAC",
            _ => ext.TrimStart('.').ToUpperInvariant()
        };
    }

    internal static string? GetField(TagLib.Tag tag, string field) =>
        GetField(null, tag, field);

    internal static string? GetField(TagLib.File? tf, TagLib.Tag tag, string field)
    {
        try
        {
            var val = GetFieldFromTag(tag, field);
            if (!string.IsNullOrWhiteSpace(val)) return val;

            if (tf != null)
            {
                var types = new[]
                {
                    TagLib.TagTypes.Xiph,
                    TagLib.TagTypes.Id3v2,
                    TagLib.TagTypes.Apple,
                    TagLib.TagTypes.Ape,
                    TagLib.TagTypes.Asf
                };

                foreach (var tagType in types)
                {
                    try
                    {
                        var specificTag = tf.GetTag(tagType);
                        if (specificTag != null && !ReferenceEquals(specificTag, tag))
                        {
                            val = GetFieldFromTag(specificTag, field);
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return null;
    }

    private static string? GetFieldFromTag(TagLib.Tag? tag, string field)
    {
        if (tag == null) return null;

        try
        {
            if (tag is TagLib.CombinedTag combined)
            {
                foreach (var subTag in combined.Tags)
                {
                    var val = GetFieldFromTag(subTag, field);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }

            if (tag is TagLib.Ogg.XiphComment xiph)
            {
                var val = xiph.GetField(field)?.FirstOrDefault()
                       ?? xiph.GetField(field.ToLowerInvariant())?.FirstOrDefault()
                       ?? xiph.GetField(field.ToUpperInvariant())?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            if (tag is TagLib.Id3v2.Tag id3)
            {
                var frames = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>();
                foreach (var frame in frames)
                {
                    if (string.Equals(frame.Description, field, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(frame.Description, "replaygain_" + field, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(frame.Description?.Replace("REPLAYGAIN_", ""), field.Replace("REPLAYGAIN_", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        var text = frame.Text?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }

            if (tag is TagLib.Mpeg4.AppleTag appleTag)
            {
                var val = appleTag.GetDashBox("com.apple.iTunes", field.ToLowerInvariant())
                       ?? appleTag.GetDashBox("com.apple.iTunes", field.ToUpperInvariant())
                       ?? appleTag.GetDashBox("com.apple.iTunes", field)
                       ?? appleTag.GetDashBox("com.apple.iTunes", "replaygain_" + field.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            if (tag is TagLib.Ape.Tag ape)
            {
                var item = ape.GetItem(field)
                        ?? ape.GetItem(field.ToUpperInvariant())
                        ?? ape.GetItem(field.ToLowerInvariant());
                var val = item?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            if (tag is TagLib.Asf.Tag asf)
            {
                var desc = asf.GetDescriptorStrings(field)?.FirstOrDefault()
                        ?? asf.GetDescriptorStrings(field.ToUpperInvariant())?.FirstOrDefault()
                        ?? asf.GetDescriptorStrings(field.ToLowerInvariant())?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
        }
        catch { }

        return null;
    }

    internal static double? ParseDb(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd("dB".ToCharArray()).Trim().Replace('−', '-');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    internal static double? ParsePeak(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace('−', '-');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    internal static string Sha1Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
