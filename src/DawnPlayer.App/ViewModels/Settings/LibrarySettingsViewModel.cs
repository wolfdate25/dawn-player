using System;
using System.Collections.ObjectModel;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Sub-panel ViewModel managing music library monitored folders and scan configuration.
/// </summary>
public sealed class LibrarySettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action? _scanStarter;
    private readonly Action<AppSettings>? _settingsSaver;

    private readonly ObservableCollection<string> _folders = new();
    private string? _selectedFolder;

    public LibrarySettingsViewModel(
        AppSettings settings,
        Action? scanStarter = null,
        Action<AppSettings>? settingsSaver = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scanStarter = scanStarter;
        _settingsSaver = settingsSaver ?? (s => SettingsWriter.Schedule(s));

        foreach (var f in _settings.Library.Folders)
        {
            if (!string.IsNullOrWhiteSpace(f))
            {
                _folders.Add(f);
            }
        }
    }

    public ObservableCollection<string> Folders => _folders;

    public string? SelectedFolder
    {
        get => _selectedFolder;
        set => SetProperty(ref _selectedFolder, value);
    }

    public bool HasFolders => _folders.Count > 0;

    public bool ScanOnStartup
    {
        get => _settings.Library.ScanOnStartup;
        set
        {
            if (_settings.Library.ScanOnStartup != value)
            {
                _settings.Library.ScanOnStartup = value;
                OnPropertyChanged();
                _settingsSaver?.Invoke(_settings);
            }
        }
    }

    public bool AddFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string trimmed = path.Trim();
        if (_settings.Library.Folders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _settings.Library.Folders.Add(trimmed);
        _folders.Add(trimmed);
        OnPropertyChanged(nameof(HasFolders));
        _settingsSaver?.Invoke(_settings);
        TriggerScanNow();
        return true;
    }

    public bool RemoveFolder(string? path = null)
    {
        string? target = path ?? _selectedFolder;
        if (string.IsNullOrWhiteSpace(target)) return false;

        bool removedFromSettings = _settings.Library.Folders.Remove(target);
        bool removedFromCollection = _folders.Remove(target);

        if (removedFromSettings || removedFromCollection)
        {
            if (_selectedFolder == target)
            {
                SelectedFolder = null;
            }
            OnPropertyChanged(nameof(HasFolders));
            _settingsSaver?.Invoke(_settings);
            return true;
        }

        return false;
    }

    public void TriggerScanNow()
    {
        _scanStarter?.Invoke();
    }
}
