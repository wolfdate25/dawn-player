namespace DawnPlayer.Plugins;

/// <summary>What the player knows about the track it wants lyrics for.</summary>
public sealed record LyricsSearchQuery
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    /// <summary>Track duration in milliseconds when known; 0 otherwise. Great for disambiguation.</summary>
    public int DurationMs { get; init; }
}

/// <summary>One search hit shown to the user / scored by the automatic matcher.</summary>
public sealed record LyricsSearchResult
{
    /// <summary>Plugin-opaque handle passed back to <see cref="ILyricsPlugin.GetAsync"/>.</summary>
    public string ResultId { get; init; } = "";
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    /// <summary>Candidate duration in milliseconds when the site reports one; 0 otherwise.</summary>
    public int DurationMs { get; init; }
    /// <summary>True when the site reports time-synced (LRC) lyrics for this result.</summary>
    public bool IsSynced { get; init; }
    /// <summary>Optional human-visible source link shown in the search window.</summary>
    public string? SourceUrl { get; init; }
}

/// <summary>Downloaded lyrics. Provide either or both forms; synced is preferred by the player.</summary>
public sealed record LyricsContent
{
    public string? SyncedLrc { get; init; }
    public string? PlainText { get; init; }
    public bool HasContent => !string.IsNullOrWhiteSpace(SyncedLrc) || !string.IsNullOrWhiteSpace(PlainText);
}
