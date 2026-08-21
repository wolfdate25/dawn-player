using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Covers what <see cref="SettingsStore"/> does to the settings file: atomic save under rapid and
/// concurrent writes (no torn file, no stray .tmp), and recovery from a corrupt, empty, or
/// null-sectioned settings.json — with and without a readable .bak.
/// </summary>
[Collection("SettingsStoreCollection")]
public class SettingsStorePersistenceAndRecoveryTests
{
    [Fact]
    public void SettingsStore_RapidSequentialSaves_MaintainsDataIntegrity()
    {
        var originalSettings = SettingsStore.Load();
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var settings = new AppSettings();
                settings.Ui.AlbumCoverSize = 100 + i;
                settings.Ui.LeftSidebarWidth = 200 + i;
                settings.Playback.Volume = (i % 100) / 100.0;
                SettingsStore.Save(settings);
            }

            var loaded = SettingsStore.Load();
            Assert.NotNull(loaded);
            Assert.Equal(199, loaded.Ui.AlbumCoverSize);
            Assert.Equal(299, loaded.Ui.LeftSidebarWidth);
            Assert.Equal(0.99, loaded.Playback.Volume, 2);

            // Verify no stray .tmp files left in settings folder
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null && Directory.Exists(dir))
            {
                var tmpFiles = Directory.EnumerateFiles(dir, "settings.json.tmp.*").ToList();
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            SettingsStore.Save(originalSettings);
        }
    }

    [Fact]
    public void SettingsStore_ConcurrentMultiThreadedSaves_NoCorruptionOrDeadlock()
    {
        var originalSettings = SettingsStore.Load();
        try
        {
            const int threadCount = 10;
            const int iterationsPerThread = 20;
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, threadCount, threadIndex =>
            {
                try
                {
                    for (int j = 0; j < iterationsPerThread; j++)
                    {
                        var settings = new AppSettings();
                        settings.Ui.AlbumCoverSize = 100 + (threadIndex * 10) + j;
                        settings.Playback.Volume = ((threadIndex + j) % 100) / 100.0;
                        SettingsStore.Save(settings);

                        // Interleaved reads
                        var read = SettingsStore.Load();
                        Assert.NotNull(read);
                        Assert.InRange(read.Ui.AlbumCoverSize, 50, 400);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);

            var finalLoaded = SettingsStore.Load();
            Assert.NotNull(finalLoaded);
            Assert.InRange(finalLoaded.Ui.AlbumCoverSize, 100, 300);

            // Verify no leftover .tmp files
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null && Directory.Exists(dir))
            {
                var tmpFiles = Directory.EnumerateFiles(dir, "settings.json.tmp.*").ToList();
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            SettingsStore.Save(originalSettings);
        }
    }

    [Fact]
    public void SettingsStore_CorruptedJsonWithNoBackup_RecoversToDefault()
    {
        var originalSettings = SettingsStore.Load();
        var backupPath = AppPaths.SettingsFile + ".bak";
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null) Directory.CreateDirectory(dir);

            File.WriteAllText(AppPaths.SettingsFile, "{ corrupt json <<< not valid !!!");
            if (File.Exists(backupPath)) File.Delete(backupPath);

            var recovered = SettingsStore.Load();

            Assert.NotNull(recovered);
            Assert.Equal(144, recovered.Ui.AlbumCoverSize);
            Assert.Equal(220, recovered.Ui.LeftSidebarWidth);
        }
        finally
        {
            SettingsStore.Save(originalSettings);
        }
    }

    [Fact]
    public void SettingsStore_CorruptedJsonWithValidBackup_RecoversPreviousGeneration()
    {
        var originalSettings = SettingsStore.Load();
        var backupPath = AppPaths.SettingsFile + ".bak";
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null) Directory.CreateDirectory(dir);

            var previous = AppSettings.CreateDefault();
            previous.Ui.AlbumCoverSize = 211;
            File.WriteAllText(backupPath, SettingsStore.Serialize(previous));
            File.WriteAllText(AppPaths.SettingsFile, "{ corrupt json <<< not valid !!!");

            var recovered = SettingsStore.Load();

            // Silently resetting every preference the user ever set is worse than serving the
            // previous generation, so a readable .bak wins over defaults.
            Assert.Equal(211, recovered.Ui.AlbumCoverSize);
        }
        finally
        {
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
            SettingsStore.Save(originalSettings);
        }
    }

    [Fact]
    public void SettingsStore_NullSectionInJson_NormalizedToDefaults()
    {
        var originalSettings = SettingsStore.Load();
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null) Directory.CreateDirectory(dir);

            // A hand-edited file with a null section deserializes fine and then throws deep in
            // the playback path instead of at load time.
            File.WriteAllText(AppPaths.SettingsFile, "{ \"Playback\": null, \"Ui\": null }");

            var recovered = SettingsStore.Load();

            Assert.NotNull(recovered.Playback);
            Assert.NotNull(recovered.Ui);
            Assert.NotNull(recovered.Output);
            Assert.NotNull(recovered.Shortcuts);
        }
        finally
        {
            SettingsStore.Save(originalSettings);
        }
    }

    [Fact]
    public void SettingsStore_EmptyFile_GracefullyRecoversToDefault()
    {
        var originalSettings = SettingsStore.Load();
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.SettingsFile);
            if (dir != null) Directory.CreateDirectory(dir);

            File.WriteAllText(AppPaths.SettingsFile, "");
            var recovered = SettingsStore.Load();

            Assert.NotNull(recovered);
            Assert.Equal(144, recovered.Ui.AlbumCoverSize);
            Assert.Equal(220, recovered.Ui.LeftSidebarWidth);
        }
        finally
        {
            SettingsStore.Save(originalSettings);
        }
    }
}
