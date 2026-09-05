using System;
using System.IO;
using System.Runtime.CompilerServices;
using DawnPlayer.Core.Util;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DawnPlayer.Tests;

public static class TestAssemblyInitializer
{
    private static string? s_testSandboxDir;

    // Parallelism note: tests mutate process-global state (AppPaths.BaseDir redirection). With
    // parallel collections, a base-dir test's mutation window once pointed concurrent playlist
    // tests at the REAL %APPDATA%, polluting it with ~20k .m3u8 files — hence serialization.
    [ModuleInitializer]
    public static void Initialize()
    {
        s_testSandboxDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Tests_Sandbox_" + Guid.NewGuid().ToString("N"));
        AppPaths.SetCustomBaseDir(s_testSandboxDir);

        // Swallow debounced settings writes by default. Without this, a timer scheduled by one
        // test can fire while another test is asserting on the settings file, producing failures
        // that depend on timing. Tests that exercise the writer install their own sink.
        DawnPlayer.Core.Persistence.SettingsWriter.WriteSink = _ => { };

        // Register AppDomain process exit handler to clean up temp test sandbox directory
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(s_testSandboxDir) && Directory.Exists(s_testSandboxDir))
                {
                    Directory.Delete(s_testSandboxDir, recursive: true);
                }
            }
            catch { }
        };
    }
}
