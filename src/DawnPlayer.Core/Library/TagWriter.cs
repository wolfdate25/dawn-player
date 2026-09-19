using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Fields the tag editor applies. Null means "leave unchanged"; strings may be empty to clear.
/// Artwork: <see cref="TagEditorArt.None"/> keeps pictures, <see cref="TagEditorArt.Embed"/>
/// replaces them with the front cover from <see cref="ArtSourcePath"/>, <see cref="TagEditorArt.Remove"/>
/// drops every embedded picture.
/// </summary>
public sealed record TagEdit(
    string? Title = null,
    string? Artist = null,
    string? AlbumArtist = null,
    string? Album = null,
    string? Genre = null,
    int? Year = null,
    int? TrackNo = null,
    int? DiscNo = null,
    TagEditorArt Art = TagEditorArt.None,
    string? ArtSourcePath = null);

/// <summary>What the editor does with the embedded pictures of a file.</summary>
public enum TagEditorArt { None, Embed, Remove }

/// <summary>Result of one file write.</summary>
public enum TagWriteResult { Ok, FileMissing, ReadFailed, SaveFailed }

/// <summary>
/// Writes tags through TagLibSharp — the only writer in the codebase (everything else only
/// reads). Every flow edits a same-volume copy and swaps it into place with
/// <see cref="File.Replace"/>, so a crash or a thrown TagLib exception never leaves a
/// half-written music file behind.
/// </summary>
public static class TagWriter
{
    /// <summary>Same-volume temp path that preserves the extension — TagLib dispatches file
    /// types by extension, so a ".dawn-tmp" name would leave every file unreadable.</summary>
    private static string TempPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".dawn-tmp" + Path.GetExtension(path));
    }

    /// <summary>Applies editor fields and/or artwork to one file atomically.</summary>
    public static TagWriteResult TryApplyAtomic(string path, TagEdit edit)
    {
        if (!File.Exists(path)) return TagWriteResult.FileMissing;

        string temp = TempPath(path);
        try
        {
            File.Copy(path, temp, overwrite: true);

            try
            {
                using (var tf = TagLib.File.Create(temp))
                {
                    ApplyFields(tf.Tag, ResolveWritable(tf), edit, EditArtwork(edit));
                    tf.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[tags] write read-phase failed for '{path}': {ex.Message}");
                return TagWriteResult.ReadFailed;
            }

            File.Replace(temp, path, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"[tags] write save-phase failed for '{path}': {ex.Message}");
            return TagWriteResult.SaveFailed;
        }
        finally
        {
            TryDeleteTemp(temp);
        }
        return TagWriteResult.Ok;
    }

    /// <summary>
    /// Writes ReplayGain values (track always, album when provided) through the same atomic
    /// path. Formatting matches what the reader accepts ("+1.23 dB", "0.987123").
    /// </summary>
    public static bool TrySetReplayGain(string path, double trackGainDb, double trackPeak,
        double? albumGainDb, double? albumPeak)
        => TrySetReplayGain(path, trackGainDb, trackPeak, albumGainDb, albumPeak, writeR128: false);

    /// <summary>
    /// Writes ReplayGain values, optionally also as ReplayGain 2.0 fields
    /// (<c>R128_TRACK_GAIN</c> / <c>R128_ALBUM_GAIN</c>, LU relative to −18 LUFS). RG2 defines no
    /// peak fields, so peaks only land in the RG1 fields.
    /// </summary>
    public static bool TrySetReplayGain(string path, double trackGainDb, double trackPeak,
        double? albumGainDb, double? albumPeak, bool writeR128)
    {
        if (!File.Exists(path)) return false;

        string temp = TempPath(path);
        try
        {
            File.Copy(path, temp, overwrite: true);

            try
            {
                using (var tf = TagLib.File.Create(temp))
                {
                    var container = ResolveWritable(tf);
                    SetReplayField(container, "REPLAYGAIN_TRACK_GAIN", FormatGain(trackGainDb));
                    SetReplayField(container, "REPLAYGAIN_TRACK_PEAK", FormatPeak(trackPeak));
                    if (albumGainDb.HasValue && albumPeak.HasValue)
                    {
                        SetReplayField(container, "REPLAYGAIN_ALBUM_GAIN", FormatGain(albumGainDb.Value));
                        SetReplayField(container, "REPLAYGAIN_ALBUM_PEAK", FormatPeak(albumPeak.Value));
                    }
                    if (writeR128)
                    {
                        SetReplayField(container, "R128_TRACK_GAIN", FormatLU(trackGainDb));
                        if (albumGainDb.HasValue)
                        {
                            SetReplayField(container, "R128_ALBUM_GAIN", FormatLU(albumGainDb.Value));
                        }
                    }
                    tf.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[tags] replaygain write read-phase failed for '{path}': {ex.Message}");
                return false;
            }

            File.Replace(temp, path, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[tags] replaygain write save-phase failed for '{path}': {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteTemp(temp);
        }
    }

    /// <summary>
    /// Writes a 0-5 star rating through the atomic path: ID3v2 POPM (Popularimeter, de facto
    /// anchors 1/64/128/196/255, 0 clears) for MP3, and the free-form <c>RATING</c> field
    /// (1-5 scale, empty clears) for Xiph/Apple/APE containers.
    /// </summary>
    public static bool TrySetRating(string path, int stars)
    {
        if (!File.Exists(path)) return false;
        stars = Math.Clamp(stars, 0, 5);

        string temp = TempPath(path);
        try
        {
            File.Copy(path, temp, overwrite: true);

            try
            {
                using (var tf = TagLib.File.Create(temp))
                {
                    WriteRatingToContainer(ResolveWritable(tf), stars);
                    tf.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[tags] rating write read-phase failed for '{path}': {ex.Message}");
                return false;
            }

            File.Replace(temp, path, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[tags] rating write save-phase failed for '{path}': {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteTemp(temp);
        }
    }

    private static void WriteRatingToContainer(TagLib.Tag container, int stars)
    {
        if (container is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.PopularimeterFrame.Get(id3, user: string.Empty, create: true);
            frame.Rating = StarsToPopm(stars);
        }
        else if (container is TagLib.Ogg.XiphComment xiph)
        {
            if (stars > 0) xiph.SetField("RATING", new[] { stars.ToString(CultureInfo.InvariantCulture) });
            else xiph.RemoveField("RATING");
        }
        else if (container is TagLib.Mpeg4.AppleTag apple)
        {
            if (stars > 0) apple.SetDashBox("com.apple.iTunes", "rating", stars.ToString(CultureInfo.InvariantCulture));
            else apple.SetDashBox("com.apple.iTunes", "rating", string.Empty);
        }
        else if (container is TagLib.Ape.Tag ape)
        {
            if (stars > 0) ape.SetItem(new TagLib.Ape.Item("RATING", stars.ToString(CultureInfo.InvariantCulture)));
            else ape.RemoveItem("RATING");
        }
        // Unknown containers are skipped for the same reason as SetReplayField.
    }

    /// <summary>Star count → POPM counter byte, using the anchors most writers agree on
    /// (1 / 64 / 128 / 196 / 255; 0 clears).</summary>
    public static byte StarsToPopm(int stars) => Math.Clamp(stars, 0, 5) switch
    {
        1 => 1,
        2 => 64,
        3 => 128,
        4 => 196,
        5 => 255,
        _ => 0,
    };

    private static void ApplyFields(TagLib.Tag union, TagLib.Tag container, TagEdit edit, TagLib.IPicture? cover)
    {
        if (edit.Title != null) union.Title = edit.Title;
        if (edit.Artist != null) union.Performers = SplitArtists(edit.Artist);
        if (edit.AlbumArtist != null) union.AlbumArtists = SplitArtists(edit.AlbumArtist);
        if (edit.Album != null) union.Album = edit.Album;
        if (edit.Genre != null) union.Genres = string.IsNullOrEmpty(edit.Genre) ? Array.Empty<string>() : new[] { edit.Genre };
        if (edit.Year.HasValue) union.Year = edit.Year.Value < 0 ? 0 : (uint)edit.Year.Value;
        if (edit.TrackNo.HasValue) union.Track = edit.TrackNo.Value < 0 ? 0 : (uint)edit.TrackNo.Value;
        if (edit.DiscNo.HasValue) union.Disc = edit.DiscNo.Value < 0 ? 0 : (uint)edit.DiscNo.Value;

        if (edit.Art == TagEditorArt.Remove)
        {
            container.Pictures = Array.Empty<TagLib.IPicture>();
        }
        else if (edit.Art == TagEditorArt.Embed && cover != null)
        {
            container.Pictures = new TagLib.IPicture[] { cover };
        }
    }

    /// <summary>Loads the embed source into a front-cover picture, or null when it is unreadable.</summary>
    private static TagLib.Picture? EditArtwork(TagEdit edit)
    {
        if (edit.Art != TagEditorArt.Embed || string.IsNullOrEmpty(edit.ArtSourcePath)) return null;

        try
        {
            return new TagLib.Picture(new TagLib.ByteVector(File.ReadAllBytes(edit.ArtSourcePath)))
            {
                Type = TagLib.PictureType.FrontCover,
                Description = string.Empty,
                MimeType = MimeFromExtension(Path.GetExtension(edit.ArtSourcePath)),
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[tags] artwork load failed for '{edit.ArtSourcePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The concrete container the structured fields land in. <see cref="TagLib.File.Tag"/> is a
    /// union that reads from every subtag, so plain scalar fields go through it for maximum format
    /// coverage while artwork and descriptors target one resolved container.
    /// </summary>
    private static TagLib.Tag ResolveWritable(TagLib.File tf)
    {
        if (tf.Tag is TagLib.CombinedTag combined && combined.Tags.Length > 0)
        {
            return combined.Tags[0];
        }
        return tf.Tag;
    }

    private static readonly string[] ArtistSeparators = new[] { ";", " / ", " & " };

    private static string[] SplitArtists(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        // TagLib carries multiple performers as an array; split on the common separators rather
        // than forcing one blob on the tag.
        var parts = value.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries);
        var trimmed = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var t = part.Trim();
            if (t.Length > 0) trimmed.Add(t);
        }
        return trimmed.Count > 0 ? trimmed.ToArray() : new[] { value.Trim() };
    }

    private static string MimeFromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static string FormatGain(double db) =>
        (db >= 0 ? "+" : string.Empty) + db.ToString("F2", CultureInfo.InvariantCulture) + " dB";

    /// <summary>R128 fields are bare LU values ("-4.25"), no unit and no forced sign.</summary>
    private static string FormatLU(double lu) =>
        lu.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatPeak(double peak) =>
        peak.ToString("F6", CultureInfo.InvariantCulture);

    private static void SetReplayField(TagLib.Tag container, string field, string formatted)
    {
        if (container is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField(field, new[] { formatted });
        }
        else if (container is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, field, true);
            frame.Text = new[] { formatted };
        }
        else if (container is TagLib.Mpeg4.AppleTag apple)
        {
            apple.SetDashBox("com.apple.iTunes", field.ToLowerInvariant(), formatted);
        }
        else if (container is TagLib.Ape.Tag ape)
        {
            ape.SetItem(new TagLib.Ape.Item(field, formatted));
        }
        else if (container is TagLib.Asf.Tag asf)
        {
            asf.SetDescriptorString(formatted, new[] { field });
        }
        // Unknown containers (formats TagLib only partially supports) are skipped: writing
        // through the union would spread the value unpredictably across subtags.
    }

    private static void TryDeleteTemp(string temp)
    {
        if (string.IsNullOrEmpty(temp)) return;
        try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) { Log.Trace($"[tags] temp delete failed '{temp}': {ex.Message}"); }
    }
}
