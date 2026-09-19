using System.Collections.ObjectModel;
using System.Globalization;
using DawnPlayer.Core.Playlists;

namespace DawnPlayer.App.Controls;

public sealed class QueueUiEntry : System.ComponentModel.INotifyPropertyChanged
{
    private int _index;
    private string _title = "";
    private string _subtitle = "";

    public int Index { get => _index; set => Set(ref _index, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed class QueuePopupController
{
    public ObservableCollection<QueueUiEntry> Entries { get; } = new();

    public void SyncFromQueue(IReadOnlyList<QueueEntry>? queueEntries)
    {
        // Delta update: reuse the existing rows (patching only what changed) instead of
        // Clear+Add — a queue change used to reset the whole popup list, collapsing the
        // open popup's scroll position and re-realizing every row for a one-track change.
        int count = 0;
        if (queueEntries != null)
        {
            for (int i = 0; i < queueEntries.Count; i++)
            {
                var entry = queueEntries[i];
                if (entry == null) continue;
                var ui = count < Entries.Count ? Entries[count] : null;
                if (ui == null)
                {
                    ui = new QueueUiEntry();
                    Entries.Add(ui);
                }
                ui.Index = count + 1;
                ui.Title = entry.Title ?? string.Empty;
                ui.Subtitle = entry.Subtitle ?? string.Empty;
                count++;
            }
        }
        while (Entries.Count > count)
        {
            Entries.RemoveAt(Entries.Count - 1);
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
