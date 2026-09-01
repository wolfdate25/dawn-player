using System.Collections.ObjectModel;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>One row in the plugin list on the settings page.</summary>
public sealed class LyricsPluginItemVm : ViewModelBase
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public bool IsExternal { get; init; }

    public string Header => $"{Name} v{Version}";
    public string SourceLabel => IsExternal ? "플러그인 폴더" : "직접 등록";

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}

/// <summary>
/// Online lyrics settings: master switches, the plugin list (enable + priority order), and
/// save-to-disk policy. The plugin list is rebuilt from <see cref="ILyricsOnlineService"/> on
/// demand, so it reflects the last folder scan.
/// </summary>
public sealed class LyricsOnlineSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly ILyricsOnlineService? _online;
    private readonly Action? _lyricsChangedNotifier;
    private readonly Action<AppSettings>? _settingsSaver;

    public ObservableCollection<LyricsPluginItemVm> Plugins { get; } = new();

    public LyricsOnlineSettingsViewModel(
        AppSettings settings,
        ILyricsOnlineService? online = null,
        Action? lyricsChangedNotifier = null,
        Action<AppSettings>? settingsSaver = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _online = online;
        _lyricsChangedNotifier = lyricsChangedNotifier;
        _settingsSaver = settingsSaver ?? (s => SettingsWriter.Schedule(s));
        RefreshPlugins();
    }

    public bool EnableOnline
    {
        get => _settings.LyricsOnline.EnableOnline;
        set { if (_settings.LyricsOnline.EnableOnline != value) { _settings.LyricsOnline.EnableOnline = value; OnPropertyChanged(); SaveAndNotify(); } }
    }

    public bool AutoFetchOnPlay
    {
        get => _settings.LyricsOnline.AutoFetchOnPlay;
        set { if (_settings.LyricsOnline.AutoFetchOnPlay != value) { _settings.LyricsOnline.AutoFetchOnPlay = value; OnPropertyChanged(); Save(); } }
    }

    public bool PreferSynced
    {
        get => _settings.LyricsOnline.PreferSynced;
        set { if (_settings.LyricsOnline.PreferSynced != value) { _settings.LyricsOnline.PreferSynced = value; OnPropertyChanged(); Save(); } }
    }

    public bool AutoSave
    {
        get => _settings.LyricsOnline.AutoSave;
        set { if (_settings.LyricsOnline.AutoSave != value) { _settings.LyricsOnline.AutoSave = value; OnPropertyChanged(); Save(); } }
    }

    public bool OverwriteExisting
    {
        get => _settings.LyricsOnline.OverwriteExisting;
        set { if (_settings.LyricsOnline.OverwriteExisting != value) { _settings.LyricsOnline.OverwriteExisting = value; OnPropertyChanged(); Save(); } }
    }

    public int SaveLocationIndex
    {
        get => (int)_settings.LyricsOnline.SaveLocation;
        set
        {
            if (SaveLocationIndex != value && value is >= 0 and <= 1)
            {
                _settings.LyricsOnline.SaveLocation = (LyricsSaveLocation)value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomFolderSelected));
                Save();
            }
        }
    }

    public bool IsCustomFolderSelected => SaveLocationIndex == 1;

    public string CustomSaveFolderLabel => string.IsNullOrWhiteSpace(_settings.LyricsOnline.CustomSaveFolder)
        ? "(지정 안 됨 — 음원 폴더에 저장)"
        : _settings.LyricsOnline.CustomSaveFolder!;

    public void SetCustomSaveFolder(string path)
    {
        _settings.LyricsOnline.CustomSaveFolder = path;
        OnPropertyChanged(nameof(CustomSaveFolderLabel));
        Save();
    }

    public string SaveFileNameTemplate
    {
        get => _settings.LyricsOnline.SaveFileNameTemplate;
        set
        {
            var trimmed = value.Trim();
            if (!string.Equals(_settings.LyricsOnline.SaveFileNameTemplate, trimmed, StringComparison.Ordinal))
            {
                _settings.LyricsOnline.SaveFileNameTemplate = trimmed;
                OnPropertyChanged();
                Save();
            }
        }
    }

    // Instance member because x:Bind ({x:Bind ViewModel.OnlineLyrics.PluginsFolder}) walks
    // instance properties.
#pragma warning disable CA1822
    public string PluginsFolder => AppPaths.PluginsDir;
#pragma warning restore CA1822

    public bool HasPlugins => Plugins.Count > 0;

    public string LoadErrorsText => _online is null || _online.LoadErrors.Count == 0
        ? ""
        : string.Join(System.Environment.NewLine, _online.LoadErrors);

    // ---------------- plugin list ----------------

    /// <summary>Rebuilds the list from the service snapshot, ordered by the saved priority.</summary>
    public void RefreshPlugins()
    {
        Plugins.Clear();
        if (_online != null)
        {
            var online = _settings.LyricsOnline;
            var infos = _online.Plugins
                .OrderBy(PriorityOf)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

            int PriorityOf(LyricsPluginInfo plugin)
            {
                var index = online.PluginOrder.FindIndex(id => id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            }

            foreach (var info in infos)
            {
                Plugins.Add(new LyricsPluginItemVm
                {
                    Id = info.Id,
                    Name = info.Name,
                    Version = info.Version,
                    Author = info.Author,
                    IsExternal = info.IsExternal,
                    IsEnabled = !online.PluginEnabled.TryGetValue(info.Id, out var enabled) || enabled
                });
            }
        }

        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(LoadErrorsText));
    }

    public void Rescan()
    {
        _online?.ReloadPlugins();
        RefreshPlugins();
    }

    /// <summary>Persists a plugin enable toggle coming from the item row.</summary>
    public void SetPluginEnabled(LyricsPluginItemVm item)
    {
        _settings.LyricsOnline.PluginEnabled[item.Id] = item.IsEnabled;
        Save();
    }

    public void MovePluginUp(LyricsPluginItemVm item) => MovePlugin(item, -1);
    public void MovePluginDown(LyricsPluginItemVm item) => MovePlugin(item, +1);

    private void MovePlugin(LyricsPluginItemVm item, int delta)
    {
        var index = Plugins.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Plugins.Count) return;

        Plugins.Move(index, target);

        // Write the visible order back, keeping ids of uninstalled plugins so a reinstall
        // restores the user's ordering.
        var known = Plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = _settings.LyricsOnline.PluginOrder.Where(id => !known.Contains(id)).ToList();
        _settings.LyricsOnline.PluginOrder = Plugins.Select(p => p.Id).Concat(unknown).ToList();
        Save();
    }

    private void Save()
    {
        _settingsSaver?.Invoke(_settings);
    }

    private void SaveAndNotify()
    {
        Save();
        _lyricsChangedNotifier?.Invoke();
    }
}
