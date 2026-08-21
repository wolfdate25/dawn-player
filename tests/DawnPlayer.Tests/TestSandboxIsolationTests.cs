using System;
using System.IO;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Tripwire: the whole suite runs against a sandboxed AppPaths.BaseDir. If these fail, some other
/// test is writing to the real %AppData%\DawnPlayer and destroying the developer's own settings.
/// </summary>
public class TestSandboxIsolationTests
{
    [Fact]
    public void TestEnvironment_AppPaths_IsIsolatedFromRealAppData()
    {
        // The base directory is process-wide, so read it under the same gate the tests that
        // temporarily redirect it hold.
        lock (AppPaths.BaseDirGate)
        {
            var baseDir = AppPaths.BaseDir;
            var realAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DawnPlayer");
            Assert.False(string.Equals(baseDir, realAppData, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("DawnPlayer_Tests_Sandbox", baseDir);
        }
    }

    [Fact]
    public void MusicLibrary_CustomDbPath_And_Sandbox_Isolation()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"custom_test_lib_{Guid.NewGuid():N}.db");
        try
        {
            using (var customLib = new MusicLibrary(tempDb))
            {
                Assert.Equal(0, customLib.Count);
            }
            Assert.True(File.Exists(tempDb));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }
}
