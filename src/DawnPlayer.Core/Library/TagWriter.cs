using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
            catch
            {
                return TagWriteResult.ReadFailed;
            }

            File.Replace(temp, path, null);
        }
        catch
        {
            return TagWriteResult.SaveFailed;
        }
        finally
        {
            TryDeleteTemp(temp);
        }
        return TagWriteResult.Ok;
    }

    /// <summary>
    /// Writes ReplayGain 2.0 values (track always, album when provided) through the same atomic
    /// path. Formatting matches what the reader accepts ("+1.23 dB", "0.987123").
    /// </summary>
    public static bool TrySetReplayGain(string path, double trackGainDb, double trackPeak,
        double? albumGainDb, double? albumPeak)
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
                    tf.Save();
                }
            }
            catch
            {
                return false;
            }

            File.Replace(temp, path, null);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDeleteTemp(temp);
        }
    }

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
        catch
        {
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
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
    }
}
