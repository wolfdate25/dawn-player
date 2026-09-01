using System;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// Modifier flags for a <see cref="KeyChord"/>. The numeric values deliberately mirror
/// <c>Windows.System.VirtualKeyModifiers</c> so the App layer can cast straight across, while this
/// model stays free of WinRT: the test project targets plain <c>net10.0-windows</c> with no Windows
/// SDK projection and links these files in as source.
/// </summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Menu = 2,     // Alt
    Shift = 4,
    Windows = 8
}
