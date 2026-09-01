using System.Collections.ObjectModel;
using System.Globalization;
using DawnPlayer.Core.Playlists;

namespace DawnPlayer.App.Controls;

public sealed class QueueUiEntry
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public sealed class QueuePopupController
{
    public ObservableCollection<QueueUiEntry> Entries { get; } = new();

    public void SyncFromQueue(IReadOnlyList<QueueEntry>? queueEntries)
    {
        Entries.Clear();
        if (queueEntries == null || queueEntries.Count == 0) return;

        for (int i = 0; i < queueEntries.Count; i++)
        {
            var entry = queueEntries[i];
            if (entry == null) continue;
            Entries.Add(new QueueUiEntry
            {
                Index = i + 1,
                Title = entry.Title ?? string.Empty,
                Subtitle = entry.Subtitle ?? string.Empty
            });
        }
    }

    public static string FormatBadgeText(int count)
    {
        if (count <= 0) return string.Empty;
        if (count > 99) return "99+";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    public static bool ShouldShowBadge(int count) => count > 0;

    public static void RequestClear(IPlaybackQueue? queue)
    {
        queue?.Clear();
    }

    public static void RequestRemoveAt(IPlaybackQueue? queue, int oneBasedIndex)
    {
        if (queue == null || oneBasedIndex < 1 || oneBasedIndex > queue.Count)
            return;

        queue.RemoveAt(oneBasedIndex - 1);
    }
}
