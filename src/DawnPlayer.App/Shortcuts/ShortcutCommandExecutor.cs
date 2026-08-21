using System;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Services;
using Microsoft.UI.Xaml;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// Runs a shortcut command against the live app services. This is the only place a command turns
/// into an action, so a rebound key and the original hard-coded accelerator behave identically.
/// </summary>
internal static class ShortcutCommandExecutor
{
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Executes <paramref name="command"/>. Returns false when the command declined because focus is
    /// in a text input; the caller must then leave the key unhandled so the character reaches the
    /// TextBox instead of being swallowed by the accelerator.
    /// </summary>
    public static bool Execute(ShortcutCommand command, XamlRoot? xamlRoot)
    {
        var info = ShortcutCommandCatalog.Get(command);
        if (info.RequiresTextInputGuard && KeyboardHelper.FocusIsInTextInput(xamlRoot))
        {
            return false;
        }

        var playback = AppServices.Playback;
        var window = App.MainWin;

        switch (command)
        {
            case ShortcutCommand.PlayPause:
                playback?.PlayPause();
                break;

            case ShortcutCommand.Stop:
                playback?.Stop();
                break;

            case ShortcutCommand.Next:
                if (playback != null) _ = playback.NextAsync();
                break;

            case ShortcutCommand.Previous:
                if (playback != null) _ = playback.PreviousAsync();
                break;

            case ShortcutCommand.ShuffleCycle:
                window?.Player.CycleShuffle();
                break;

            case ShortcutCommand.RepeatCycle:
                window?.Player.CycleRepeat();
                break;

            case ShortcutCommand.StopAfterCurrentToggle:
                if (playback != null) playback.StopAfterCurrent = !playback.StopAfterCurrent;
                break;

            case ShortcutCommand.SeekForward:
                if (playback != null) playback.Seek(playback.Position + SeekStep);
                break;

            case ShortcutCommand.SeekBackward:
                if (playback != null)
                {
                    var target = playback.Position - SeekStep;
                    playback.Seek(target < TimeSpan.Zero ? TimeSpan.Zero : target);
                }
                break;

            case ShortcutCommand.SeekToStart:
                playback?.Seek(TimeSpan.Zero);
                break;

            case ShortcutCommand.VolumeUp:
                window?.Player.StepVolume(Controls.TransportToggleCalculator.VolumeStepPercent);
                break;

            case ShortcutCommand.VolumeDown:
                window?.Player.StepVolume(-Controls.TransportToggleCalculator.VolumeStepPercent);
                break;

            case ShortcutCommand.MuteToggle:
                window?.Player.ToggleMute();
                break;

            case ShortcutCommand.ToggleLyrics:
                window?.ToggleLyrics();
                break;

            case ShortcutCommand.FocusSearch:
                window?.FocusLibrarySearch();
                break;

            case ShortcutCommand.OpenPreferences:
                window?.NavigateToSettings();
                break;
        }

        return true;
    }
}
