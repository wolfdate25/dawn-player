using System;
using System.Collections.Generic;
using System.Linq;

namespace DawnPlayer.App.Shortcuts;

/// <summary>Static metadata for one rebindable command.</summary>
/// <param name="RequiresTextInputGuard">
/// When true the handler must bail out (leaving the key unhandled) while focus sits in a text input.
/// WinUI raises <c>KeyboardAccelerator.Invoked</c> for chords a focused TextBox does not mark as
/// handled, so without this guard binding a bare letter or Space would swallow typing.
/// <para>
/// This says nothing about a focused <em>list</em>, which is the opposite problem and is settled by
/// <see cref="ShortcutDispatchPolicy"/> for every command alike: a list swallows the key before the
/// accelerator can fire, so the window takes it first. That path never applies to a text input at
/// all, which makes it strictly stricter than this flag rather than a second reading of it.
/// </para>
/// </param>
public sealed record ShortcutCommandInfo(
    ShortcutCommand Command,
    ShortcutCategory Category,
    string DisplayName,
    KeyChord? DefaultChord,
    bool RequiresTextInputGuard);

/// <summary>
/// The single source of truth for what commands exist, what they are called, and how they are bound
/// out of the box. Settings persist only the deltas against <see cref="ShortcutCommandInfo.DefaultChord"/>,
/// so a default can be changed here without touching anyone's settings file.
/// </summary>
public static class ShortcutCommandCatalog
{
    private const int KeySpace = 32;
    private const int KeyLeft = 37;
    private const int KeyUp = 38;
    private const int KeyRight = 39;
    private const int KeyDown = 40;

    private static readonly ShortcutCommandInfo[] Catalog =
    {
        new(ShortcutCommand.PlayPause, ShortcutCategory.Playback, "재생 / 일시정지",
            new KeyChord(ShortcutModifiers.None, KeySpace), true),
        new(ShortcutCommand.Stop, ShortcutCategory.Playback, "정지",
            new KeyChord(ShortcutModifiers.Control, 'S'), false),
        new(ShortcutCommand.Next, ShortcutCategory.Playback, "다음 트랙",
            new KeyChord(ShortcutModifiers.Control, KeyRight), false),
        new(ShortcutCommand.Previous, ShortcutCategory.Playback, "이전 트랙",
            new KeyChord(ShortcutModifiers.Control, KeyLeft), false),
        new(ShortcutCommand.ShuffleCycle, ShortcutCategory.Playback, "셔플 순환 (끔 → 트랙 → 앨범)",
            new KeyChord(ShortcutModifiers.Control, 'H'), false),
        new(ShortcutCommand.RepeatCycle, ShortcutCategory.Playback, "반복 순환 (끔 → 전체 → 한 곡)",
            new KeyChord(ShortcutModifiers.Control, 'R'), false),
        new(ShortcutCommand.StopAfterCurrentToggle, ShortcutCategory.Playback, "현재 곡 재생 후 정지 전환",
            new KeyChord(ShortcutModifiers.Control | ShortcutModifiers.Shift, 'S'), false),

        new(ShortcutCommand.SeekForward, ShortcutCategory.Seek, "5초 앞으로",
            new KeyChord(ShortcutModifiers.None, KeyRight), true),
        new(ShortcutCommand.SeekBackward, ShortcutCategory.Seek, "5초 뒤로",
            new KeyChord(ShortcutModifiers.None, KeyLeft), true),
        // Not a Home chord, despite "seek to start" wanting one. A focused ListView marks Home
        // handled regardless of modifiers — Home, Ctrl+Home, Alt+Home and Ctrl+Alt+Home were all
        // measured swallowed, Alt+Home additionally moving the list selection — and a list holds
        // focus for most of this app's lifetime. ShortcutDispatchPolicy now takes the modified
        // variants back from the list, so Ctrl+Home would work; bare Home stays reserved for the
        // list's own "jump to the first item", and Ctrl+B stays the shipped default so that nobody's
        // existing binding shifts under them.
        new(ShortcutCommand.SeekToStart, ShortcutCategory.Seek, "현재 곡 처음으로",
            new KeyChord(ShortcutModifiers.Control, 'B'), true),

        new(ShortcutCommand.VolumeUp, ShortcutCategory.Volume, "볼륨 올리기",
            new KeyChord(ShortcutModifiers.Control, KeyUp), false),
        new(ShortcutCommand.VolumeDown, ShortcutCategory.Volume, "볼륨 내리기",
            new KeyChord(ShortcutModifiers.Control, KeyDown), false),
        new(ShortcutCommand.MuteToggle, ShortcutCategory.Volume, "음소거 전환",
            new KeyChord(ShortcutModifiers.Control, 'M'), false),

        new(ShortcutCommand.ToggleLyrics, ShortcutCategory.Navigation, "가사 창 열기 / 닫기",
            new KeyChord(ShortcutModifiers.None, 'L'), true),
        new(ShortcutCommand.FocusSearch, ShortcutCategory.Navigation, "라이브러리 검색으로 이동",
            new KeyChord(ShortcutModifiers.Control, 'F'), false),
        new(ShortcutCommand.OpenPreferences, ShortcutCategory.Navigation, "환경설정 열기",
            new KeyChord(ShortcutModifiers.Control, 'P'), true)
    };

    private static readonly Dictionary<ShortcutCommand, ShortcutCommandInfo> ByCommand =
        Catalog.ToDictionary(c => c.Command);

    /// <summary>All commands, grouped in the order the settings page renders them.</summary>
    public static IReadOnlyList<ShortcutCommandInfo> All => Catalog;

    public static ShortcutCommandInfo Get(ShortcutCommand command) => ByCommand[command];

    /// <summary>Resolves a persisted command id. Unknown ids are rejected rather than throwing.</summary>
    public static bool TryGetCommand(string? id, out ShortcutCommand command) =>
        Enum.TryParse(id, ignoreCase: false, out command) && ByCommand.ContainsKey(command);

    public static string GetCategoryName(ShortcutCategory category) => category switch
    {
        ShortcutCategory.Playback => "재생 제어",
        ShortcutCategory.Seek => "탐색",
        ShortcutCategory.Volume => "볼륨",
        _ => "네비게이션 & 창"
    };
}
