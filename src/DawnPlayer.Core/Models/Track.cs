using System.Runtime.CompilerServices;

namespace DawnPlayer.Core.Models;

/// <summary>A single music file with its metadata snapshot from the last scan.</summary>
public sealed record Track
{
    public string Path { get; set; } = "";

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";        // first performer, falls back to album artist
    public string AlbumArtist { get; set; } = "";   // first album artist
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Year { get; set; }
    public int TrackNo { get; set; }
    public int DiscNo { get; set; }

    public long DurationMs { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public string Codec { get; set; } = "";
    public int BitrateKbps { get; set; }

    public long FileSize { get; set; }
    public long FileModifiedUtcTicks { get; set; }

    public bool HasLrc { get; set; }
    public string? ArtPath { get; set; }

    // ReplayGain (dB / linear peak), null when not tagged
    public double? RgTrackGainDb { get; set; }
    public double? RgTrackPeak { get; set; }
    public double? RgAlbumGainDb { get; set; }
    public double? RgAlbumPeak { get; set; }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    /// <summary>Primary sort artist: album artist if present, otherwise performer.</summary>
    public string SortArtist =>
        string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist;

    /// <summary>Packed "DiscNo-TrackNo" ordinal used for album ordering.</summary>
    public long AlbumSortKey => ((long)DiscNo << 32) | (uint)TrackNo;

    // The properties are settable only because WinUI's XAML type-info generator emits setters for
    // every property of a type used as an x:DataType (LibraryPage.xaml binds core:Track), so
    // init-only accessors fail the App build with CS8852. Nothing in the codebase writes a Track
    // after construction — derived copies go through `with` — and this memo depends on that: a
    // mutated SortArtist / Album / Path would strand the cached key.
    // The table is keyed on instance identity rather than held in an instance field because a
    // record's synthesized Equals and GetHashCode cover every declared instance field, and `with`
    // copies them — a field cache would join value equality and would also survive into a copy
    // whose album differs.
    private static readonly ConditionalWeakTable<Track, string> AlbumKeyCache = new();

    /// <summary>
    /// Stable key identifying this track's album, used both for art-cache file names and for album
    /// grouping / album shuffle: normalized album artist + album. Tracks with neither tag fall back
    /// to a per-file path key, so untagged files do not collapse into one enormous album.
    /// The exact format is persisted in the on-disk art cache and must not change.
    /// </summary>
    public string AlbumKey => AlbumKeyCache.GetValue(this, static t => t.ComputeAlbumKey());

    private string ComputeAlbumKey()
    {
        var artist = SortArtist.Trim().ToLowerInvariant();
        var album = Album.Trim().ToLowerInvariant();
        if (artist.Length == 0 && album.Length == 0)
        {
            return string.IsNullOrWhiteSpace(Path)
                ? "\u0001"
                : ("file:" + Path.Trim().ToLowerInvariant());
        }
        return artist + "\u0001" + album;
    }

    public override string ToString() => $"{Artist} - {Title}";
}
