namespace DawnPlayer.Core.Lyrics;

/// <summary>
/// Pure word-timeline math for karaoke rendering. Kept free of UI types so the invariant
/// "the highlighted word is the one the timeline says is active" (plan §10.3, I7) is testable
/// headless.
/// </summary>
public static class KaraokeTimeline
{
    /// <summary>
    /// Index of the word active at <paramref name="position"/> (-1 before the first word).
    /// Equal start times (possible after clamping) resolve to the last of the group, so a
    /// clamped-away word never lights up.
    /// </summary>
    public static int WordIndexAt(IReadOnlyList<LrcWord>? words, TimeSpan position)
    {
        if (words is not { Count: > 0 }) return -1;

        int lo = 0, hi = words.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (words[mid].Start <= position) { res = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return res;
    }
}
