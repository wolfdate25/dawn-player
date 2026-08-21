using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Controls;
using DawnPlayer.App.Shortcuts;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public class ShortcutBindingTests
{
    private const int KeyJ = 'J';
    private const int KeyS = 'S';
    private const int KeyHome = 36;
    private const int KeyTab = 9;
    private const int KeyEscape = 27;

    // =========================================================================
    // 1. KeyChord parsing and formatting
    // =========================================================================

    [Theory]
    [InlineData("Ctrl+S")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+F4")]
    [InlineData("Space")]
    [InlineData("Ctrl+Home")]
    [InlineData("Win+Left")]
    [InlineData("Ctrl+Alt+Shift+NumPadAdd")]
    public void KeyChordRoundTripsThroughItsCanonicalToken(string token)
    {
        Assert.True(KeyChord.TryParse(token, out var chord));
        Assert.True(chord.IsValid);
        Assert.Equal(token, chord.ToToken());
    }

    [Fact]
    public void ModifierOrderIsNormalizedSoConflictDetectionIsAPlainLookup()
    {
        Assert.True(KeyChord.TryParse("Shift+Ctrl+S", out var a));
        Assert.True(KeyChord.TryParse("Ctrl+Shift+S", out var b));

        Assert.Equal(b, a);
        Assert.Equal("Ctrl+Shift+S", a.ToToken());
    }

    [Theory]
    [InlineData("control+s")]
    [InlineData("CTRL+S")]
    [InlineData(" Ctrl + S ")]
    public void ModifierAliasesAndCasingAreAccepted(string token)
    {
        Assert.True(KeyChord.TryParse(token, out var chord));
        Assert.Equal("Ctrl+S", chord.ToToken());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+S")]
    [InlineData("Hyper+S")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("S+Ctrl")]
    public void UnusableTokensAreRejectedRatherThanGuessedAt(string? token)
    {
        Assert.False(KeyChord.TryParse(token, out _));
    }

    [Fact]
    public void AnInvalidChordFormatsAsEmptyRatherThanAMisleadingLabel()
    {
        var bogus = new KeyChord(ShortcutModifiers.Control, KeyTab);

        Assert.False(bogus.IsValid);
        Assert.Equal(string.Empty, bogus.ToToken());
        Assert.Equal(string.Empty, bogus.ToDisplayString());
    }

    [Fact]
    public void UnknownModifierBitsMakeAChordInvalid()
    {
        Assert.False(new KeyChord((ShortcutModifiers)64, KeyS).IsValid);
    }

    // =========================================================================
    // 2. Key allow-list
    // =========================================================================

    [Theory]
    [InlineData(KeyTab)]
    [InlineData(KeyEscape)]
    [InlineData(8)]   // Backspace
    [InlineData(16)]  // Shift
    [InlineData(17)]  // Control
    [InlineData(18)]  // Alt
    [InlineData(91)]  // Left Windows
    public void FocusAndDismissalKeysAreNotBindable(int keyCode)
    {
        Assert.False(ShortcutKeyNames.IsAllowedKey(keyCode));
        Assert.Null(ShortcutKeyNames.GetToken(keyCode));
        Assert.Null(ShortcutKeyNames.GetDisplay(keyCode));
    }

    [Fact]
    public void EveryAllowedKeyHasAUniqueTokenAndResolvesBackToItsCode()
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ShortcutKeyNames.All)
        {
            Assert.True(tokens.Add(entry.Token), $"duplicate key token: {entry.Token}");
            Assert.False(string.IsNullOrWhiteSpace(entry.Display));
            Assert.True(ShortcutKeyNames.TryGetKeyCode(entry.Token, out var resolved));
            Assert.Equal(entry.Code, resolved);
        }
    }

    [Fact]
    public void NoKeyTokenContainsTheChordSeparator()
    {
        // Tokens are split on the separator, so a key literally named for it would be ambiguous.
        Assert.All(ShortcutKeyNames.All, entry => Assert.DoesNotContain("+", entry.Token));
    }

    // =========================================================================
    // 3. Catalog
    // =========================================================================

    [Fact]
    public void EveryCommandHasCatalogMetadata()
    {
        foreach (var command in Enum.GetValues<ShortcutCommand>())
        {
            var info = ShortcutCommandCatalog.Get(command);
            Assert.Equal(command, info.Command);
            Assert.False(string.IsNullOrWhiteSpace(info.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(ShortcutCommandCatalog.GetCategoryName(info.Category)));
        }

        Assert.Equal(Enum.GetValues<ShortcutCommand>().Length, ShortcutCommandCatalog.All.Count);
    }

    [Fact]
    public void DefaultChordsAreValidAndNoTwoCommandsShipWithTheSameOne()
    {
        var seen = new Dictionary<KeyChord, ShortcutCommand>();

        foreach (var info in ShortcutCommandCatalog.All)
        {
            if (info.DefaultChord is not { } chord) continue;

            Assert.True(chord.IsValid, $"{info.Command} ships with an unbindable default");
            Assert.False(seen.ContainsKey(chord), $"{info.Command} collides with a default of another command");
            seen[chord] = info.Command;
        }
    }

    [Fact]
    public void DefaultsWithoutAModifierGuardTextInput()
    {
        // An unmodified default would otherwise swallow typing in a TextBox.
        foreach (var info in ShortcutCommandCatalog.All)
        {
            if (info.DefaultChord is not { } chord) continue;
            if (chord.Modifiers != ShortcutModifiers.None) continue;

            Assert.True(info.RequiresTextInputGuard,
                $"{info.Command} is bound to an unmodified key but does not guard text input");
        }
    }

    [Theory]
    [InlineData("PlayPause", true)]
    [InlineData("OpenPreferences", true)]
    [InlineData("playpause", false)]
    [InlineData("NotACommand", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CommandIdsResolveExactlyOrNotAtAll(string? id, bool expected)
    {
        Assert.Equal(expected, ShortcutCommandCatalog.TryGetCommand(id, out _));
    }

    // =========================================================================
    // 4. ShortcutMap
    // =========================================================================

    [Fact]
    public void AFreshMapMatchesTheCatalogDefaults()
    {
        var map = new ShortcutMap();

        foreach (var info in ShortcutCommandCatalog.All)
        {
            Assert.Equal(info.DefaultChord, map.GetChord(info.Command));
            Assert.True(map.IsDefault(info.Command));
        }
    }

    [Fact]
    public void AnOverrideWinsOverTheDefault()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            [nameof(ShortcutCommand.Stop)] = "Alt+F4"
        });

        Assert.Equal("Alt+F4", map.GetChord(ShortcutCommand.Stop)?.ToToken());
        Assert.False(map.IsDefault(ShortcutCommand.Stop));
        Assert.True(map.IsDefault(ShortcutCommand.PlayPause));
    }

    [Fact]
    public void AnEmptyTokenMeansDeliberatelyUnassigned()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            [nameof(ShortcutCommand.PlayPause)] = string.Empty
        });

        Assert.Null(map.GetChord(ShortcutCommand.PlayPause));
        Assert.True(map.IsUnassigned(ShortcutCommand.PlayPause));
        Assert.False(map.IsDefault(ShortcutCommand.PlayPause));
    }

    [Fact]
    public void UnknownCommandIdsAndBrokenTokensFallBackToTheShippedDefault()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            ["ThisCommandWasRemoved"] = "Ctrl+J",
            [nameof(ShortcutCommand.Stop)] = "Ctrl+Tab",
            [nameof(ShortcutCommand.MuteToggle)] = "!!!garbage!!!"
        });

        Assert.Equal(ShortcutCommandCatalog.Get(ShortcutCommand.Stop).DefaultChord, map.GetChord(ShortcutCommand.Stop));
        Assert.Equal(ShortcutCommandCatalog.Get(ShortcutCommand.MuteToggle).DefaultChord, map.GetChord(ShortcutCommand.MuteToggle));
    }

    [Fact]
    public void AnOverrideStealsAChordFromWhicheverCommandHeldItByDefault()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            [nameof(ShortcutCommand.Stop)] = "Ctrl+F"
        });

        Assert.Equal("Ctrl+F", map.GetChord(ShortcutCommand.Stop)?.ToToken());
        Assert.Null(map.GetChord(ShortcutCommand.FocusSearch));
    }

    [Fact]
    public void LoadingNeverLeavesTwoCommandsOnTheSameChord()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            [nameof(ShortcutCommand.Stop)] = "Ctrl+J",
            [nameof(ShortcutCommand.MuteToggle)] = "Ctrl+J"
        });

        var assigned = map.Effective.Values.Where(chord => chord != null).ToList();
        Assert.Equal(assigned.Count, assigned.Distinct().Count());
    }

    [Fact]
    public void TryAssignReportsTheConflictAndChangesNothing()
    {
        var map = new ShortcutMap();
        var searchChord = map.GetChord(ShortcutCommand.FocusSearch)!.Value;

        var result = map.TryAssign(ShortcutCommand.Stop, searchChord, out var conflicting);

        Assert.Equal(ShortcutAssignResult.Conflict, result);
        Assert.Equal(ShortcutCommand.FocusSearch, conflicting);
        Assert.Equal(searchChord, map.GetChord(ShortcutCommand.FocusSearch));
        Assert.True(map.IsDefault(ShortcutCommand.Stop));
    }

    [Fact]
    public void ReassigningACommandToTheChordItAlreadyHoldsIsNotAConflict()
    {
        var map = new ShortcutMap();
        var stop = map.GetChord(ShortcutCommand.Stop)!.Value;

        Assert.Equal(ShortcutAssignResult.Assigned, map.TryAssign(ShortcutCommand.Stop, stop, out _));
    }

    [Fact]
    public void TryAssignRefusesAnUnbindableChord()
    {
        var map = new ShortcutMap();

        var result = map.TryAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyTab), out _);

        Assert.Equal(ShortcutAssignResult.InvalidChord, result);
        Assert.True(map.IsDefault(ShortcutCommand.Stop));
    }

    [Fact]
    public void ForceAssignUnbindsThePreviousHolder()
    {
        var map = new ShortcutMap();
        var searchChord = map.GetChord(ShortcutCommand.FocusSearch)!.Value;

        map.ForceAssign(ShortcutCommand.Stop, searchChord);

        Assert.Equal(searchChord, map.GetChord(ShortcutCommand.Stop));
        Assert.Null(map.GetChord(ShortcutCommand.FocusSearch));
    }

    [Fact]
    public void TryFindCommandResolvesAPressedChordBackToItsCommand()
    {
        var map = new ShortcutMap();
        var chord = map.GetChord(ShortcutCommand.SeekToStart)!.Value;

        Assert.True(map.TryFindCommand(chord, out var command));
        Assert.Equal(ShortcutCommand.SeekToStart, command);
        Assert.False(map.TryFindCommand(new KeyChord(ShortcutModifiers.Control, KeyJ), out _));
    }

    [Fact]
    public void ResetToDefaultRestoresOneCommandAndResetAllRestoresEverything()
    {
        var map = new ShortcutMap();
        map.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyJ));
        map.Clear(ShortcutCommand.PlayPause);

        map.ResetToDefault(ShortcutCommand.Stop);

        Assert.True(map.IsDefault(ShortcutCommand.Stop));
        Assert.True(map.IsUnassigned(ShortcutCommand.PlayPause));

        map.ResetAll();

        Assert.All(ShortcutCommandCatalog.All, info => Assert.True(map.IsDefault(info.Command)));
    }

    [Fact]
    public void ResetToDefaultStealsItsDefaultBackFromAnyoneHoldingIt()
    {
        var map = new ShortcutMap();
        var stopDefault = ShortcutCommandCatalog.Get(ShortcutCommand.Stop).DefaultChord!.Value;

        map.ForceAssign(ShortcutCommand.MuteToggle, stopDefault);
        Assert.Null(map.GetChord(ShortcutCommand.Stop));

        map.ResetToDefault(ShortcutCommand.Stop);

        Assert.Equal(stopDefault, map.GetChord(ShortcutCommand.Stop));
        Assert.Null(map.GetChord(ShortcutCommand.MuteToggle));
    }

    // =========================================================================
    // 5. Persisting only the deltas
    // =========================================================================

    [Fact]
    public void AnUntouchedMapPersistsNothing()
    {
        Assert.Empty(new ShortcutMap().ToBindingsDictionary());
    }

    [Fact]
    public void OnlyChangedCommandsArePersisted()
    {
        var map = new ShortcutMap();
        map.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyJ));
        map.Clear(ShortcutCommand.ToggleLyrics);

        var bindings = map.ToBindingsDictionary();

        Assert.Equal("Ctrl+J", bindings[nameof(ShortcutCommand.Stop)]);
        Assert.Equal(string.Empty, bindings[nameof(ShortcutCommand.ToggleLyrics)]);
        Assert.DoesNotContain(nameof(ShortcutCommand.PlayPause), bindings.Keys);
    }

    [Fact]
    public void PersistedBindingsReloadToTheSameEffectiveMap()
    {
        var original = new ShortcutMap();
        original.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control | ShortcutModifiers.Shift, KeyHome));
        original.Clear(ShortcutCommand.SeekForward);

        var reloaded = new ShortcutMap(original.ToBindingsDictionary());

        Assert.Equal(original.Effective, reloaded.Effective);
    }

    // =========================================================================
    // 6. Settings round trip
    // =========================================================================

    [Fact]
    public void ShortcutOverridesSurviveASettingsRoundTrip()
    {
        var settings = new AppSettings();
        var map = new ShortcutMap();
        map.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control | ShortcutModifiers.Shift, KeyJ));
        map.Clear(ShortcutCommand.ToggleLyrics);
        settings.Shortcuts.Bindings = map.ToBindingsDictionary();

        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.Equal(map.Effective, new ShortcutMap(loaded!.Shortcuts.Bindings).Effective);
    }

    [Fact]
    public void ASettingsFileWithNoShortcutSectionLoadsTheDefaults()
    {
        // Everyone upgrading from a build before this feature has exactly this JSON.
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Ui\":{\"WindowWidth\":1280}}");

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Shortcuts.Bindings);
        Assert.All(ShortcutCommandCatalog.All,
            info => Assert.Equal(info.DefaultChord, new ShortcutMap(loaded.Shortcuts.Bindings).GetChord(info.Command)));
    }

    // =========================================================================
    // 7. ShortcutMapStore
    // =========================================================================

    [Fact]
    public void TheStoreCommitsAndNotifiesOnEveryRealChange()
    {
        var commits = 0;
        var notifications = 0;
        var store = new ShortcutMapStore(new ShortcutMap(), () => commits++);
        store.ShortcutsChanged += () => notifications++;

        store.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyJ));
        store.Clear(ShortcutCommand.PlayPause);
        store.ResetToDefault(ShortcutCommand.Stop);
        store.ResetAll();

        Assert.Equal(4, commits);
        Assert.Equal(4, notifications);
    }

    [Fact]
    public void ARefusedAssignmentDoesNotCommit()
    {
        var commits = 0;
        var store = new ShortcutMapStore(new ShortcutMap(), () => commits++);
        var searchChord = store.Map.GetChord(ShortcutCommand.FocusSearch)!.Value;

        Assert.Equal(ShortcutAssignResult.Conflict, store.TryAssign(ShortcutCommand.Stop, searchChord, out _));
        Assert.Equal(ShortcutAssignResult.InvalidChord,
            store.TryAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.None, KeyTab), out _));

        Assert.Equal(0, commits);
    }

    // =========================================================================
    // 8. ShortcutSettingsViewModel
    // =========================================================================

    [Fact]
    public void TheViewModelExposesEveryCommandExactlyOnceGroupedByCategory()
    {
        var vm = new ShortcutSettingsViewModel(new ShortcutMapStore());

        Assert.Equal(ShortcutCommandCatalog.All.Count, vm.Rows.Count);
        Assert.Equal(ShortcutCommandCatalog.All.Count, vm.Groups.Sum(group => group.Items.Count));
        Assert.Equal(vm.Rows.Count, vm.Rows.Select(row => row.Command).Distinct().Count());
        Assert.All(vm.Groups, group => Assert.False(string.IsNullOrWhiteSpace(group.Name)));
    }

    [Fact]
    public void RowsTrackTheChordAndTheDefaultAndUnassignedState()
    {
        var vm = new ShortcutSettingsViewModel(new ShortcutMapStore());
        var row = vm.Rows.First(r => r.Command == ShortcutCommand.Stop);

        Assert.True(row.IsDefault);
        Assert.False(row.CanReset);
        Assert.True(row.CanClear);
        Assert.Equal(ShortcutCommandCatalog.Get(ShortcutCommand.Stop).DefaultChord!.Value.ToDisplayString(), row.ChordText);

        vm.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyJ));
        Assert.False(row.IsDefault);
        Assert.True(row.CanReset);
        Assert.Equal("Ctrl+J", row.ChordText);

        vm.Clear(ShortcutCommand.Stop);
        Assert.True(row.IsUnassigned);
        Assert.False(row.CanClear);
        Assert.Equal(ShortcutBindingViewModel.UnassignedLabel, row.ChordText);

        vm.ResetToDefault(ShortcutCommand.Stop);
        Assert.True(row.IsDefault);
    }

    [Fact]
    public void AConflictLeavesBothRowsUntouchedUntilTheUserForces()
    {
        var vm = new ShortcutSettingsViewModel(new ShortcutMapStore());
        var searchRow = vm.Rows.First(row => row.Command == ShortcutCommand.FocusSearch);
        var searchChord = ShortcutCommandCatalog.Get(ShortcutCommand.FocusSearch).DefaultChord!.Value;

        Assert.Equal(ShortcutAssignResult.Conflict, vm.TryAssign(ShortcutCommand.Stop, searchChord, out var conflicting));
        Assert.Equal(ShortcutCommand.FocusSearch, conflicting);
        Assert.Equal(searchChord.ToDisplayString(), searchRow.ChordText);

        vm.ForceAssign(ShortcutCommand.Stop, searchChord);

        Assert.Equal(ShortcutBindingViewModel.UnassignedLabel, searchRow.ChordText);
    }

    [Fact]
    public void ResetAllRefreshesEveryRow()
    {
        var vm = new ShortcutSettingsViewModel(new ShortcutMapStore());
        vm.ForceAssign(ShortcutCommand.Stop, new KeyChord(ShortcutModifiers.Control, KeyJ));
        vm.Clear(ShortcutCommand.PlayPause);

        vm.ResetAll();

        Assert.All(vm.Rows, row => Assert.True(row.IsDefault));
    }

    [Fact]
    public void CommandDisplayNamesAreAvailableForConflictMessages()
    {
        Assert.Equal(ShortcutCommandCatalog.Get(ShortcutCommand.FocusSearch).DisplayName,
            ShortcutSettingsViewModel.GetCommandDisplayName(ShortcutCommand.FocusSearch));
    }

    // =========================================================================
    // 9. Reaching the player while a list has focus (ShortcutDispatchPolicy)
    // =========================================================================

    [Fact]
    public void SpaceReachesPlayPauseWhileAListHasFocus()
    {
        // The bug this policy exists for: a focused ListView marks Space handled for item activation,
        // WinUI then never raises the accelerator, and the app's most conventional shortcut was dead
        // wherever the library grid or a playlist held focus - which is most of the time.
        var space = ShortcutCommandCatalog.Get(ShortcutCommand.PlayPause).DefaultChord!.Value;

        Assert.True(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, space));
        Assert.False(ShortcutDispatchPolicy.IsListNavigationChord(space));
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("PageUp")]
    [InlineData("PageDown")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Shift+Home")]
    [InlineData("Shift+End")]
    [InlineData("Shift+Up")]
    [InlineData("Shift+Down")]
    [InlineData("Shift+Left")]
    [InlineData("Shift+Right")]
    public void AListKeepsTheKeysItNavigatesWith(string token)
    {
        // Unmodified these move the selection or invoke the item; with Shift they extend it. Taking
        // them would leave a focused list unusable from the keyboard, which is a worse bug than a
        // shortcut that only works elsewhere.
        Assert.True(KeyChord.TryParse(token, out var chord));

        Assert.True(ShortcutDispatchPolicy.IsListNavigationChord(chord));
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, chord));
    }

    [Theory]
    [InlineData("Ctrl+Home")]
    [InlineData("Ctrl+End")]
    [InlineData("Ctrl+Left")]
    [InlineData("Ctrl+Right")]
    [InlineData("Ctrl+Up")]
    [InlineData("Ctrl+Down")]
    [InlineData("Alt+Home")]
    [InlineData("Ctrl+Alt+Home")]
    [InlineData("Ctrl+Shift+End")]
    [InlineData("Ctrl+Enter")]
    public void AModifiedNavigationKeyBelongsToTheShortcutInstead(string token)
    {
        // A ListView does claim these - it moves focus without selecting, or extends to the end - but
        // those variants are obscure and a deliberately bound shortcut that silently does nothing is
        // the worse outcome. Ctrl+Left / Ctrl+Right ship as previous/next track, so this is also what
        // keeps track skipping alive inside a list.
        Assert.True(KeyChord.TryParse(token, out var chord));

        Assert.False(ShortcutDispatchPolicy.IsListNavigationChord(chord));
        Assert.True(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, chord));
    }

    [Theory]
    [InlineData(ShortcutFocusContext.TextInput)]
    [InlineData(ShortcutFocusContext.Other)]
    [InlineData(ShortcutFocusContext.Unknown)]
    public void NothingIsTakenFromATextInputOrAnyOtherControl(ShortcutFocusContext context)
    {
        // Only a list gets preempted. A TextBox is typing; a Button, a Slider or a ComboBox each need
        // their own Space and arrows, and an album card is a Button - so Space still activates the
        // focused card rather than toggling playback behind it.
        foreach (var info in ShortcutCommandCatalog.All)
        {
            if (info.DefaultChord is not { } chord) continue;

            Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(context, chord),
                $"{info.Command} would be taken from {context}");
        }
    }

    [Fact]
    public void AnUnbindableChordIsNeverPreempted()
    {
        // Reached for real: PreviewKeyDown fires for Tab, Esc and the bare modifiers too, and none of
        // them is on the allow-list.
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(
            ShortcutFocusContext.ItemsList, new KeyChord(ShortcutModifiers.None, KeyTab)));
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(
            ShortcutFocusContext.ItemsList, new KeyChord(ShortcutModifiers.None, KeyEscape)));
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(
            ShortcutFocusContext.ItemsList, default));
    }

    [Fact]
    public void EveryReservedNavigationTokenResolvesToARealKey()
    {
        // The reserved set is built from tokens, so a typo would silently reserve nothing at all.
        Assert.Equal(9, ShortcutDispatchPolicy.ListNavigationKeyCodes.Count);
        Assert.All(ShortcutDispatchPolicy.ListNavigationKeyCodes,
            code => Assert.True(ShortcutKeyNames.IsAllowedKey(code)));
        Assert.Contains(KeyHome, ShortcutDispatchPolicy.ListNavigationKeyCodes);
    }

    [Fact]
    public void EveryShippedDefaultReachesThePlayerFromAFocusedListExceptTheBareArrowSeekPair()
    {
        // The one knowing exception. A vertical ListView leaves bare Left/Right alone, so 5-second
        // seek already worked there and still does through the accelerator; a TreeView uses them to
        // collapse and expand, and the library tree needs that more than seek does. Anything else
        // that ends up reserved is a shipped default gone dead, so this fails rather than shrugging.
        var expectedReserved = new[] { ShortcutCommand.SeekForward, ShortcutCommand.SeekBackward };

        var reserved = ShortcutCommandCatalog.All
            .Where(info => info.DefaultChord is { } chord
                && !ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, chord))
            .Select(info => info.Command)
            .ToList();

        Assert.Equal(expectedReserved, reserved);
    }

    [Fact]
    public void TheTextInputGuardStillMeansWhatItMeantAndIsNotWhatDecidesTheListCase()
    {
        // RequiresTextInputGuard is unchanged: it gates the accelerator path, which is why every
        // unmodified default sets it. The list case is settled without consulting the flag - the
        // preempt path simply never applies to a text input, guard or no guard.
        Assert.True(ShortcutCommandCatalog.Get(ShortcutCommand.PlayPause).RequiresTextInputGuard);
        Assert.False(ShortcutCommandCatalog.Get(ShortcutCommand.Stop).RequiresTextInputGuard);

        var guarded = ShortcutCommandCatalog.Get(ShortcutCommand.PlayPause).DefaultChord!.Value;
        var unguarded = ShortcutCommandCatalog.Get(ShortcutCommand.Stop).DefaultChord!.Value;

        Assert.True(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, guarded));
        Assert.True(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.ItemsList, unguarded));
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.TextInput, guarded));
        Assert.False(ShortcutDispatchPolicy.ShouldPreemptFocusedElement(ShortcutFocusContext.TextInput, unguarded));
    }

    // =========================================================================
    // 10. TransportToggleCalculator
    // =========================================================================

    [Fact]
    public void ShuffleCyclesOffTracksAlbumsAndBack()
    {
        Assert.Equal(ShuffleMode.Tracks, TransportToggleCalculator.NextShuffleMode(ShuffleMode.Off));
        Assert.Equal(ShuffleMode.Albums, TransportToggleCalculator.NextShuffleMode(ShuffleMode.Tracks));
        Assert.Equal(ShuffleMode.Off, TransportToggleCalculator.NextShuffleMode(ShuffleMode.Albums));
    }

    [Fact]
    public void RepeatCyclesOffAllOneAndBack()
    {
        Assert.Equal(RepeatMode.All, TransportToggleCalculator.NextRepeatMode(RepeatMode.Off));
        Assert.Equal(RepeatMode.One, TransportToggleCalculator.NextRepeatMode(RepeatMode.All));
        Assert.Equal(RepeatMode.Off, TransportToggleCalculator.NextRepeatMode(RepeatMode.One));
    }

    [Theory]
    [InlineData(50, 5, 55)]
    [InlineData(98, 5, 100)]
    [InlineData(2, -5, 0)]
    [InlineData(0, -5, 0)]
    [InlineData(100, 5, 100)]
    [InlineData(double.NaN, 5, 5)]
    public void VolumeStepsStayInsideTheSliderRange(double current, double delta, double expected)
    {
        Assert.Equal(expected, TransportToggleCalculator.StepVolumePercent(current, delta));
    }

    [Fact]
    public void MutingRemembersTheLevelAndUnmutingRestoresIt()
    {
        var (muted, remembered) = TransportToggleCalculator.ComputeMuteToggle(65, 80);
        Assert.Equal(0, muted);
        Assert.Equal(65, remembered);

        var (restored, stillRemembered) = TransportToggleCalculator.ComputeMuteToggle(muted, remembered);
        Assert.Equal(65, restored);
        Assert.Equal(65, stillRemembered);
    }

    [Fact]
    public void UnmutingWithNothingRememberedDoesNotStaySilent()
    {
        var (restored, remembered) = TransportToggleCalculator.ComputeMuteToggle(0, 0);

        Assert.Equal(TransportToggleCalculator.DefaultRestorePercent, restored);
        Assert.Equal(TransportToggleCalculator.DefaultRestorePercent, remembered);
    }

    [Fact]
    public void MuteToggleClampsHostileInput()
    {
        var (volume, remembered) = TransportToggleCalculator.ComputeMuteToggle(double.NaN, 500);

        Assert.Equal(100, volume);
        Assert.Equal(100, remembered);
    }
}
