namespace AlsongLyricsPlugin;

/// <summary>One Alsong candidate with its full lyric text (the search response carries it).</summary>
internal sealed class AlsongRecord
{
    public AlsongRecord(string infoId, string? title, string? artist, string? album, string lyricText, bool isSynced)
    {
        InfoId = infoId;
        Title = title;
        Artist = artist;
        Album = album;
        LyricText = lyricText;
        IsSynced = isSynced;
    }

    public string InfoId { get; }

    public string? Title { get; }

    public string? Artist { get; }

    public string? Album { get; }

    public string LyricText { get; }

    public bool IsSynced { get; }
}
