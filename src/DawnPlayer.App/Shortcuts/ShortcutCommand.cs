namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// Every rebindable action. The enum member name is the id persisted in
/// <c>AppSettings.Shortcuts.Bindings</c>, so members must not be renamed without a migration.
/// </summary>
public enum ShortcutCommand
{
    PlayPause,
    Stop,
    Next,
    Previous,
    ShuffleCycle,
    RepeatCycle,
    StopAfterCurrentToggle,
    ABRepeatCycle,
    SeekForward,
    SeekBackward,
    SeekToStart,
    VolumeUp,
    VolumeDown,
    MuteToggle,
    ToggleLyrics,
    FocusSearch,
    OpenPreferences
}

/// <summary>Grouping used to build the section headers in the settings list.</summary>
public enum ShortcutCategory
{
    Playback,
    Seek,
    Volume,
    Navigation
}
