using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Views.Network;

/// <summary>One row of the favorites list: the station plus a live "playing" flag that highlights
/// whichever entry the controller is currently streaming.</summary>
public sealed class RadioRow : INotifyPropertyChanged
{
    private bool _isPlaying;

    public RadioRow(RadioStation station) => Station = station;

    public RadioStation Station { get; }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayingBadgeVisibility)));
        }
    }

    /// <summary>UI thread only — the playing badge and accent bar bind to this state.</summary>
    public void SetPlaying(bool playing) => IsPlaying = playing;

    public Visibility PlayingBadgeVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Radio section of the Network tab: the station favorites list and the "open stream URL" flow
/// that used to live only in the title menu.
/// </summary>
public sealed partial class RadioSection : UserControl
{
    public RadioSection()
    {
        InitializeComponent();
        AppServices.PlaybackStateChanged += UpdatePlayingHighlight;
        AppServices.CurrentTrackChanged += _ => UpdatePlayingHighlight();
    }

    /// <summary>First activation: the list is static afterwards and reloaded after each edit.</summary>
    public void Activate() => ReloadStations();

    /// <summary>Entry point for the title-menu "open network stream" item: the menu now routes
    /// here instead of owning a duplicate play flow.</summary>
    public async Task OpenAddDialogAsync()
    {
        var (ok, name, url, genre) = await ShowStationDialogAsync(existing: null);
        if (!ok) return;
        if (!SaveStation(existingUrl: null, name, url, genre)) return;
        ReloadStations();
    }

    private async void OnAddStationClick(object sender, RoutedEventArgs e) => await OpenAddDialogAsync();

    private List<RadioRow> _rows = [];

    private void ReloadStations()
    {
        _rows = AppServices.Stations.Stations.Select(station => new RadioRow(station)).ToList();
        UpdatePlayingHighlight();
        StationList.ItemsSource = _rows;
        StationCountText.Text = _rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StationList.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Marks the row whose URL matches the currently streaming radio track (the playing
    /// track's Path IS the station URL). Runs on the UI thread — both relayed events guarantee it.</summary>
    private void UpdatePlayingHighlight()
    {
        var playingPath = AppServices.Playback?.CurrentItem?.Track?.Path;
        foreach (var row in _rows)
        {
            var playing = playingPath != null &&
                string.Equals(playingPath, row.Station.Url, StringComparison.OrdinalIgnoreCase);
            row.SetPlaying(playing);
        }
    }

    private async void OnEditStationClick(object sender, RoutedEventArgs e)
    {
        if (StationFromSender(sender) is not { } station) return;
        var (ok, name, url, genre) = await ShowStationDialogAsync(station);
        if (!ok) return;
        if (url != station.Url) AppServices.Stations.Remove(station.Url);
        if (!SaveStation(existingUrl: station.Url, name, url, genre)) return;
        ReloadStations();
    }

    private void OnRemoveStationClick(object sender, RoutedEventArgs e)
    {
        if (StationFromSender(sender) is not { } station) return;
        AppServices.Stations.Remove(station.Url);
        AppServices.Stations.Save();
        ReloadStations();
    }

    private static bool SaveStation(string? existingUrl, string name, string url, string genre)
    {
        if (!RadioStationStore.IsValidUrl(url))
        {
            AppServices.RaiseWarning(AppStrings.Get("Network_Radio_InvalidUrl", "http:// 또는 https:// 스트림 URL을 입력하세요."));
            return false;
        }

        // Editing keeps the original added-ticks unless the URL changed (then it is a new entry).
        long addedTicks = DateTime.UtcNow.Ticks;
        if (existingUrl != null)
        {
            var previous = AppServices.Stations.Stations.FirstOrDefault(
                s => string.Equals(s.Url, existingUrl, StringComparison.OrdinalIgnoreCase));
            if (previous != null) addedTicks = previous.AddedUtcTicks;
        }

        var changed = AppServices.Stations.AddOrReplace(new RadioStation(
            name, url, string.IsNullOrWhiteSpace(genre) ? null : genre, addedTicks, null));
        if (changed) AppServices.Stations.Save();
        return true;
    }

    private static RadioStation? StationFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as RadioStation;

    private async void OnStationDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (StationList.SelectedItem is RadioRow { } row)
            await PlayStationAsync(row.Station);
    }

    private async void OnPlayStationClick(object sender, RoutedEventArgs e)
    {
        if (StationFromSender(sender) is { } station)
            await PlayStationAsync(station);
    }

    /// <summary>Connects and starts the stream (~2 s: connect + 1.5 s prebuffer) with visible
    /// feedback, since a silent gap on double-click reads as "nothing happened".</summary>
    private async Task PlayStationAsync(RadioStation station)
    {
        ConnectStatusRow.Visibility = Visibility.Visible;
        ConnectRing.IsActive = true;
        ConnectStatusText.Text = AppStrings.Format("Network_Radio_Connecting", "연결 중: {0}", station.Name);
        try
        {
            var playlists = AppServices.Playlists;
            var playlist = playlists.NowPlaying;
            var item = playlists.AddTracks(playlist, new[] { RadioStationCommands.ToTrack(station) }).FirstOrDefault();
            if (item != null)
            {
                await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
                AppServices.Stations.TouchLastPlayed(station.Url, DateTime.UtcNow.Ticks);
                UpdatePlayingHighlight();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[radio-station] play failed for '{station.Url}': {ex.Message}");
            AppServices.RaiseWarning(ex.Message);
        }
        finally
        {
            ConnectRing.IsActive = false;
            ConnectStatusRow.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<(bool Ok, string Name, string Url, string Genre)> ShowStationDialogAsync(RadioStation? existing)
    {
        var nameBox = new TextBox
        {
            Header = AppStrings.Get("Network_Radio_Dialog_Name", "이름"),
            Text = existing?.Name ?? "",
        };
        var urlBox = new TextBox
        {
            Header = AppStrings.Get("Network_Radio_Dialog_Url", "스트림 URL (Icecast/Shoutcast MP3)"),
            PlaceholderText = "http://stream.example.com:8000/stream",
            Text = existing?.Url ?? "",
        };
        var genreBox = new TextBox
        {
            Header = AppStrings.Get("Network_Radio_Dialog_Genre", "장르 (선택)"),
            Text = existing?.Genre ?? "",
        };
        var errorText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            // The system critical brush when the theme ships it, a plain red otherwise.
            Foreground = Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var critical)
                && critical is Brush criticalBrush
                ? criticalBrush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71)),
        };
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(genreBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get(existing is null ? "Network_Radio_Dialog_AddTitle" : "Network_Radio_Dialog_EditTitle",
                existing is null ? "방송국 추가" : "방송국 편집"),
            Content = panel,
            PrimaryButtonText = AppStrings.Get("Common_OK", "확인"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        // Inline validation: an invalid URL keeps the dialog open with the reason beside the
        // field, instead of closing first and surfacing a warning afterwards.
        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) return;
            if (!RadioStationStore.IsValidUrl(urlBox.Text.Trim()))
            {
                args.Cancel = true;
                errorText.Text = AppStrings.Get("Network_Radio_InvalidUrl", "http:// 또는 https:// 스트림 URL을 입력하세요.");
                errorText.Visibility = Visibility.Visible;
            }
        };

        bool ok = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return (ok, nameBox.Text.Trim(), urlBox.Text.Trim(), genreBox.Text.Trim());
    }
}
