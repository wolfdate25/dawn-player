using DawnPlayer.App.Localization;
using DawnPlayer.App.Shortcuts;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>One rebindable command as a row in the shortcut list.</summary>
public sealed class ShortcutBindingViewModel : ViewModelBase
{
    /// <summary>Shown in place of a chord when the command has been deliberately unbound.</summary>
    public const string UnassignedLabel = "없음";

    private string _chordText = UnassignedLabel;
    private bool _isDefault = true;
    private bool _isUnassigned;

    public ShortcutBindingViewModel(ShortcutCommandInfo info) => Info = info;

    public ShortcutCommandInfo Info { get; }

    public ShortcutCommand Command => Info.Command;

    public string DisplayName => Info.LocalizedName;

    /// <summary>The current chord as a label, or <see cref="UnassignedLabel"/>.</summary>
    public string ChordText
    {
        get => _chordText;
        private set => SetProperty(ref _chordText, value);
    }

    public bool IsUnassigned
    {
        get => _isUnassigned;
        private set
        {
            if (SetProperty(ref _isUnassigned, value)) OnPropertyChanged(nameof(CanClear));
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        private set
        {
            if (SetProperty(ref _isDefault, value)) OnPropertyChanged(nameof(CanReset));
        }
    }

    /// <summary>Inverses for button enablement — x:Bind has no boolean negation.</summary>
    public bool CanReset => !IsDefault;

    public bool CanClear => !IsUnassigned;

    public void Refresh(ShortcutMap map)
    {
        var chord = map.GetChord(Command);
        ChordText = chord?.ToDisplayString() ?? AppStrings.Get("Settings_Shortcuts_Unassigned", UnassignedLabel);
        IsUnassigned = chord == null;
        IsDefault = map.IsDefault(Command);
    }
}
