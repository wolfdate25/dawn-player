namespace DawnPlayer.Plugins;

/// <summary>What the player knows about the track it wants lyrics for.</summary>
public sealed record LyricsSearchQuery
{
    /// <summary>트랙 제목, 모르면 null</summary>
    public string? Title { get; init; }

    /// <summary>아티스트, 모르면 null</summary>
    public string? Artist { get; init; }

    /// <summary>앨범명(사용자가 앨범으로만 검색한 경우 이것만 있음)</summary>
    public string? Album { get; init; }

    /// <summary>Track duration in milliseconds when known; 0 otherwise. Great for disambiguation.</summary>
    public int DurationMs { get; init; }
}

/// <summary>One search hit shown to the user / scored by the automatic matcher.</summary>
public sealed record LyricsSearchResult
{
    /// <summary>Plugin-opaque handle passed back to <see cref="ILyricsPlugin.GetAsync"/>.</summary>
    public string ResultId { get; init; } = "";

    /// <summary>트랙 제목</summary>
    public string? Title { get; init; }

    /// <summary>아티스트</summary>
    public string? Artist { get; init; }

    /// <summary>앨범명</summary>
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
    /// <summary>LRC 텍스트 전체</summary>
    public string? SyncedLrc { get; init; }

    /// <summary>타임스탬프 없는 일반 가사</summary>
    public string? PlainText { get; init; }

    /// <summary>둘 중 하나라도 비지 않았는지</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(SyncedLrc) || !string.IsNullOrWhiteSpace(PlainText);
}
