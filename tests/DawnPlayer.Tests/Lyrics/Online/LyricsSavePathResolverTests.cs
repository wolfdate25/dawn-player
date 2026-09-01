using System.Text.Json;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Lyrics.Online;

public class LyricsSavePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DawnResolverTests_" + Guid.NewGuid().ToString("N"));

    private Track Track(string fileName = "01_song.flac") => new()
    {
        Path = Path.Combine(_root, "music", fileName),
        Title = "Song",
        Artist = "Artist",
        AlbumArtist = "Album Artist",
        Album = "Album",
        Year = 2024,
        TrackNo = 3
    };

    private static LyricsOnlineSettings Online(Action<LyricsOnlineSettings>? mutate = null)
    {
        var s = new LyricsOnlineSettings();
        mutate?.Invoke(s);
        return s;
    }

    [Fact]
    public void ResolveSavePath_DefaultTemplate_SitsNextToTrack()
    {
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online());

        Assert.Equal(Path.Combine(_root, "music", "01_song.lrc"), path);
    }

    [Fact]
    public void ResolveSavePath_AllTokens_ExpandFromTrack()
    {
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online(s =>
            s.SaveFileNameTemplate = "%albumartist% - %album% (%year%) [%trackno%] %title%.lrc"));

        Assert.Equal(Path.Combine(_root, "music", "Album Artist - Album (2024) [3] Song.lrc"), path);
    }

    [Fact]
    public void ResolveSavePath_SubfolderTemplate_CreatesRelativeSubPath()
    {
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online(s =>
            s.SaveFileNameTemplate = @"%album%\%trackno%. %title%.lrc"));

        Assert.Equal(Path.Combine(_root, "music", "Album", "3. Song.lrc"), path);
    }

    [Fact]
    public void ResolveSavePath_CustomFolder_UsesFolderAsRoot()
    {
        var custom = Path.Combine(_root, "lyrics");
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online(s =>
        {
            s.SaveLocation = LyricsSaveLocation.CustomFolder;
            s.CustomSaveFolder = custom;
        }));

        Assert.Equal(Path.Combine(custom, "01_song.lrc"), path);
    }

    [Fact]
    public void ResolveSavePath_CustomFolderUnset_FallsBackToMusicFolder()
    {
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online(s =>
            s.SaveLocation = LyricsSaveLocation.CustomFolder));

        Assert.Equal(Path.Combine(_root, "music", "01_song.lrc"), path);
    }

    [Theory]
    [InlineData(@"..\..\evil.lrc")]
    [InlineData(@"..\elsewhere\evil.lrc")]
    [InlineData(@"c:\absolute\evil.lrc")]
    public void ResolveSavePath_TraversalAndAbsoluteTemplates_CannotEscapeRoot(string template)
    {
        var path = LyricsSavePathResolver.ResolveSavePath(Track(), Online(s => s.SaveFileNameTemplate = template));

        Assert.StartsWith(Path.Combine(_root, "music"), path);
        Assert.DoesNotContain("..", path);
    }

    [Fact]
    public void ResolveSavePath_InvalidCharacters_AreSanitizedPerSegment()
    {
        var track = new Track
        {
            Path = Path.Combine(_root, "music", "a.flac"),
            Title = "a/b: c",
            Artist = "",
            Album = ""
        };

        var path = LyricsSavePathResolver.ResolveSavePath(track, Online(s => s.SaveFileNameTemplate = "%title%.lrc"));

        Assert.EndsWith("a_b_ c.lrc", Path.GetFileName(path));
    }

    [Fact]
    public void Save_WritesUtf8BomLrc_AndCanSkipExisting()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        var track = Track();
        var doc = LrcParser.Parse("[00:01.00]hello");

        var saved = LyricsSavePathResolver.Save(track, doc, Online());
        Assert.Equal(LyricsSaveResult.Saved, saved.Result);
        Assert.True(File.Exists(saved.Path));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(saved.Path!).Take(3).ToArray());
        Assert.Contains("[00:01.000] hello", File.ReadAllText(saved.Path!));

        var skipped = LyricsSavePathResolver.Save(track, doc, Online());
        Assert.Equal(LyricsSaveResult.SkippedExisting, skipped.Result);

        var overwritten = LyricsSavePathResolver.Save(track, doc, Online(s => s.OverwriteExisting = true));
        Assert.Equal(LyricsSaveResult.Saved, overwritten.Result);
    }

    [Fact]
    public void Save_CreatesSubfoldersFromTemplate()
    {
        var track = Track();
        var doc = LrcParser.Parse("[00:01.00]hello");

        var saved = LyricsSavePathResolver.Save(track, doc, Online(s => s.SaveFileNameTemplate = @"%album%\%title%.lrc"));

        Assert.Equal(LyricsSaveResult.Saved, saved.Result);
        Assert.True(File.Exists(Path.Combine(_root, "music", "Album", "Song.lrc")));
    }

    [Fact]
    public void Save_FailingRoot_ReportsFailure()
    {
        var track = Track();
        var doc = LrcParser.Parse("[00:01.00]hello");

        // A file where the save root should be makes directory creation impossible.
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        var blockedRoot = Path.Combine(_root, "lyrics");
        File.WriteAllText(blockedRoot, "in the way");
        var outcome = LyricsSavePathResolver.Save(track, doc, Online(s =>
        {
            s.SaveLocation = LyricsSaveLocation.CustomFolder;
            s.CustomSaveFolder = blockedRoot;
        }));

        Assert.Equal(LyricsSaveResult.Failed, outcome.Result);
        Assert.NotNull(outcome.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
