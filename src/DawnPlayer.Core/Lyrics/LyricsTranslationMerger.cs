namespace DawnPlayer.Core.Lyrics;

/// <summary>
/// Merges a plugin-provided translated LRC (<see cref="Plugins.LyricsContent.TranslatedLrc"/>)
/// into the main document. Exact timestamp matches only; a map entry is consumed by its first
/// match so shared stamps (unsynced documents) stamp exactly one line; existing translations
/// and the original text are never touched; unmatched translation lines are dropped.
/// </summary>
public static class LyricsTranslationMerger
{
    public static LyricsDocument Merge(LyricsDocument document, string? translatedLrc)
    {
        if (document is not { HasLines: true } || string.IsNullOrWhiteSpace(translatedLrc))
            return document;

        // The translation source's own duplicate lines are its content, not translation pairs.
        var translation = LrcParser.Parse(translatedLrc, sourcePath: null, pairTranslationLines: false);
        if (!translation.HasLines) return document;

        var byTime = new Dictionary<TimeSpan, string>();
        foreach (var line in translation.Lines)
        {
            if (line.Text.Length == 0 || string.Equals(line.Text, "♪", StringComparison.Ordinal)) continue;
            if (!byTime.ContainsKey(line.Time)) byTime[line.Time] = line.Text;
        }
        if (byTime.Count == 0) return document;

        var lines = new List<LrcLine>(document.Lines.Count);
        foreach (var line in document.Lines)
        {
            if (line.Translation is null &&
                byTime.TryGetValue(line.Time, out var text) &&
                !string.Equals(
                    LrcParser.NormalizeForPairing(line.Text),
                    LrcParser.NormalizeForPairing(text),
                    StringComparison.Ordinal))
            {
                byTime.Remove(line.Time);
                lines.Add(line with { Translation = text });
            }
            else
            {
                lines.Add(line);
            }
        }

        return new LyricsDocument
        {
            Title = document.Title,
            Artist = document.Artist,
            Album = document.Album,
            By = document.By,
            SourcePath = document.SourcePath,
            Lines = lines
        };
    }
}
