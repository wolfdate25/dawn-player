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
                RgTrackGainDb = ParseDb(GetField(tf, tag, "REPLAYGAIN_TRACK_GAIN"))
                                ?? ParseR128LU(GetField(tf, tag, "R128_TRACK_GAIN")),
                RgTrackPeak = ParsePeak(GetField(tf, tag, "REPLAYGAIN_TRACK_PEAK")),
                RgAlbumGainDb = ParseDb(GetField(tf, tag, "REPLAYGAIN_ALBUM_GAIN"))
                                ?? ParseR128LU(GetField(tf, tag, "R128_ALBUM_GAIN")),
                RgAlbumPeak = ParsePeak(GetField(tf, tag, "REPLAYGAIN_ALBUM_PEAK")),
                Rating = ReadRating(tf, tag),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstOrNull(string[]? arr) =>
        arr is { Length: > 0 } && !string.IsNullOrWhiteSpace(arr[0]) ? arr[0].Trim() : null;

    /// <summary>
    /// Reads the star rating a file already carries: ID3v2 POPM (Popularimeter) for MP3, the
    /// free-form <c>RATING</c> field for Xiph/Apple/APE. Returns 0 (unrated) when nothing usable
    /// is stored. Vorbis-style ratings come in several de facto scales (1-5, 0-10, 0-100), so the
    /// numeric value is adapted by magnitude.
    /// </summary>
    internal static int ReadRating(TagLib.File tf, TagLib.Tag tag)
    {
        try
        {
            foreach (var subTag in EnumerateConcreteTags(tf, tag))
            {
                if (subTag is TagLib.Id3v2.Tag id3)
                {
                    // POPM is a frame, not a text field: the first frame with a non-zero counter
                    // byte wins (writers disagree on the account; unrated is 0).
                    var frame = id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>()
                        .FirstOrDefault(f => f.Rating > 0);
                    if (frame != null) return PopmToStars(frame.Rating);
                }

                string? raw = null;
                if (subTag is TagLib.Ogg.XiphComment xiph)
                {
                    raw = xiph.GetField("RATING")?.FirstOrDefault()
                          ?? xiph.GetField("rating")?.FirstOrDefault();
                }
                else if (subTag is TagLib.Mpeg4.AppleTag apple)
                {
                    raw = apple.GetDashBox("com.apple.iTunes", "RATING")
                          ?? apple.GetDashBox("com.apple.iTunes", "rating");
                }
                else if (subTag is TagLib.Ape.Tag ape)
                {
                    raw = ape.GetItem("RATING")?.ToString() ?? ape.GetItem("rating")?.ToString();
                }

                if (!string.IsNullOrWhiteSpace(raw) &&
                    int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scaled))
                {
                    return ScaleRating(scaled);
                }
            }
        }
        catch { }
        return 0;
    }

    private static IEnumerable<TagLib.Tag> EnumerateConcreteTags(TagLib.File tf, TagLib.Tag tag)
    {
        // Collected eagerly (no yield) because the per-tag type probing below must stay inside
        // try/catch, and C# forbids yielding from a try with a catch.
        var result = new List<TagLib.Tag>();
        if (tag is TagLib.CombinedTag combined)
        {
            result.AddRange(combined.Tags);
        }
        else
        {
            result.Add(tag);
        }

        foreach (var type in new[]
                 {
                     TagLib.TagTypes.Xiph, TagLib.TagTypes.Id3v2, TagLib.TagTypes.Apple,
                     TagLib.TagTypes.Ape, TagLib.TagTypes.Asf
                 })
        {
            try
            {
                var specific = tf.GetTag(type);
                if (specific != null && !ReferenceEquals(specific, tag) &&
                    !(tag is TagLib.CombinedTag c && c.Tags.Contains(specific)))
                {
                    result.Add(specific);
                }
            }
            catch { }
        }
        return result;
    }

    /// <summary>POPM counter byte → stars. The byte has no standard scale; this quantizes it into
    /// five equal bands, which lands on the de facto anchors 1 / 64 / 128 / 196 / 255.</summary>
    public static int PopmToStars(byte popm)
    {
        if (popm == 0) return 0;
        return Math.Clamp((int)Math.Ceiling(popm / 51.0), 1, 5);
    }

    /// <summary>Adapts a numeric rating on one of the common Vorbis/Apple scales to 0-5 stars.</summary>
    public static int ScaleRating(int value)
    {
        if (value <= 0) return 0;
        if (value <= 5) return value;
        if (value <= 10) return value / 2;
        return Math.Clamp((int)Math.Round(value / 20.0), 0, 5);
    }

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

    /// <summary>
    /// Parses a ReplayGain 2.0 value (<c>R128_TRACK_GAIN</c> / <c>R128_ALBUM_GAIN</c>): an LU offset
    /// from the −18 LUFS reference, written as a bare number such as "-4.25". 1 LU ≡ 1 dB, so the
    /// value is used as-is; a stray "LU"/"dB" suffix is tolerated.
    /// </summary>
    internal static double? ParseR128LU(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd("LUdB".ToCharArray()).Trim().Replace('−', '-');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    internal static string Sha1Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
