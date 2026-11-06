using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

/// <summary>§12 A5/A6/A7 gates: newly exposed lyrics settings must write through to
/// AppSettings with the same notify/save contract as the older toggles.</summary>
public class LyricsSettingsViewModelTests
{
    private static (LyricsSettingsViewModel Vm, AppSettings Settings, int Saved) Create()
    {
        var settings = new AppSettings();
        int saved = 0;
        var vm = new LyricsSettingsViewModel(settings, settingsSaver: _ => saved++);
        return (vm, settings, saved);
    }

    [Fact]
    public void BoldActiveLine_WritesThroughAndNotifies()
    {
        var (vm, settings, _) = Create();
        Assert.True(settings.Lyrics.BoldActiveLine); // default

        bool notified = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.BoldActiveLine)) notified = true; };
        vm.BoldActiveLine = false;

        Assert.False(settings.Lyrics.BoldActiveLine);
        Assert.True(notified);
    }

    [Fact]
    public void OffsetStepMs_WritesThroughAndClamps()
    {
        var (vm, settings, _) = Create();

        vm.OffsetStepMs = 50;
        Assert.Equal(50, settings.Lyrics.DefaultOffsetStepMs, 10);

        vm.OffsetStepMs = 50000; // beyond the clamp ceiling
        Assert.Equal(1000, settings.Lyrics.DefaultOffsetStepMs, 10);

        vm.OffsetStepMs = 0; // below the clamp floor
        Assert.Equal(0.1, settings.Lyrics.DefaultOffsetStepMs, 10);
    }

    [Fact]
    public void SearchFolders_FiltersBlankLinesAndWritesThrough()
    {
        var (vm, settings, _) = Create();
        vm.SearchFoldersText = "D:\\lyrics\r\n\r\n   \r\nE:\\가사\r\nD:\\lyrics";

        vm.SaveSearchFolders(vm.SearchFoldersText);

        Assert.Equal(new[] { "D:\\lyrics", "E:\\가사", "D:\\lyrics" }, settings.Lyrics.SearchFolders);
    }

    [Fact]
    public void SearchFolders_EmptyTextClearsTheList()
    {
        var settings = new AppSettings();
        settings.Lyrics.SearchFolders.Add("D:\\old");
        int saved = 0;
        var vm = new LyricsSettingsViewModel(settings, settingsSaver: _ => saved++);
        vm.SearchFoldersText = " \r\n\r\n";

        vm.SaveSearchFolders(vm.SearchFoldersText);

        Assert.Empty(settings.Lyrics.SearchFolders);
        Assert.Equal(1, saved);
    }
}
