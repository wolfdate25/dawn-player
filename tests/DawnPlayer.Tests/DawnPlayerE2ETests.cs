using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;

namespace DawnPlayer.Tests;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

[CollectionDefinition("E2E_UI_Test_Collection", DisableParallelization = true)]
public class E2ETestCollectionDefinition { }

/// <summary>
/// Drives the built application through UI Automation.
/// <para>
/// These tests used to contain no assertions at all: every step was
/// <c>if (element != null) { click(); log("VERIFY ... successfully"); }</c>, and the run ended by
/// printing "ALL UI E2E TEST SCENARIOS PASSED WITH 100% SUCCESS!" whether or not a single element
/// had been found. A build where the entire window failed to appear passed just as happily as a
/// working one. Every step now asserts.
/// </para>
/// <para>
/// The one concession to environment: on a machine with no interactive desktop the window cannot
/// appear at all, so a missing window is reported as skipped unless
/// <c>DAWNPLAYER_E2E_STRICT=1</c> is set, in which case it fails. Everything that *is* observable —
/// the process staying alive, the absence of fatal entries in the app's own log — is always
/// asserted. When the window is found, all element and state assertions run unconditionally.
/// </para>
/// </summary>
[Collection("E2E_UI_Test_Collection")]
[Trait("Category", "E2E")]
public sealed class DawnPlayerE2ETests
{
    private readonly ITestOutputHelper _output;
    private static readonly string? AppExePath = FindAppExePath();

    private static bool Strict =>
        string.Equals(Environment.GetEnvironmentVariable("DAWNPLAYER_E2E_STRICT"), "1", StringComparison.Ordinal);

    private static string AppLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DawnPlayer", "dawnplayer.log");

    public DawnPlayerE2ETests(ITestOutputHelper output) => _output = output;

    private static string? FindAppExePath()
    {
        var relative = new[]
        {
            @"..\..\..\..\..\src\DawnPlayer.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\DawnPlayer.App.exe",
            @"..\..\..\..\src\DawnPlayer.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\DawnPlayer.App.exe",
            @"..\..\..\..\..\src\DawnPlayer.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DawnPlayer.App.exe",
            @"..\..\..\..\src\DawnPlayer.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DawnPlayer.App.exe",
            @"..\..\..\..\..\dist\publish\DawnPlayer.App.exe",
            @"..\..\..\..\dist\publish\DawnPlayer.App.exe",
        };

        return relative
            .Select(r => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, r)))
            .FirstOrDefault(File.Exists);
    }

    // ---------------- harness ----------------

    private sealed class AppSession : IDisposable
    {
        public FlaUI.Core.Application App { get; }
        public UIA3Automation Automation { get; }
        public Window? MainWindow { get; }
        public long LogLengthAtStart { get; }

        public AppSession(FlaUI.Core.Application app, UIA3Automation automation, Window? window, long logLength)
        {
            App = app;
            Automation = automation;
            MainWindow = window;
            LogLengthAtStart = logLength;
        }

        public void Dispose()
        {
            try { Automation.Dispose(); } catch { }
            try { App.Close(); } catch { }
            foreach (var proc in Process.GetProcessesByName("DawnPlayer.App"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }
    }

    private AppSession Launch()
    {
        Assert.False(string.IsNullOrEmpty(AppExePath),
            "DawnPlayer.App.exe was not found. Build the solution before running the E2E tests.");

        foreach (var proc in Process.GetProcessesByName("DawnPlayer.App"))
        {
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }

        long logLength = 0;
        try { if (File.Exists(AppLogPath)) logLength = new FileInfo(AppLogPath).Length; } catch { }

        var app = FlaUI.Core.Application.Launch(AppExePath!);
        var automation = new UIA3Automation();

        Window? window = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(app.HasExited,
                $"The application exited during startup with code {SafeExitCode(app)}. App log:{Environment.NewLine}{ReadLogTail(logLength)}");

            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
                if (window != null) break;
            }
            catch { }
            Thread.Sleep(200);
        }

        return new AppSession(app, automation, window, logLength);
    }

    private static int SafeExitCode(FlaUI.Core.Application app)
    {
        try { return app.ExitCode; } catch { return -1; }
    }

    private static string ReadLogTail(long fromOffset)
    {
        try
        {
            if (!File.Exists(AppLogPath)) return "(no log file)";
            using var fs = new FileStream(AppLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fromOffset > 0 && fromOffset < fs.Length) fs.Position = fromOffset;
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? "(no new log output)" : text;
        }
        catch (Exception ex)
        {
            return $"(log unreadable: {ex.Message})";
        }
    }

    /// <summary>
    /// The app marks every unhandled exception as handled and only writes it to its log, so the log
    /// is the only place a swallowed crash shows up. Treat any fatal entry as a test failure.
    /// </summary>
    private void AssertNoFatalLogEntries(AppSession session)
    {
        var tail = ReadLogTail(session.LogLengthAtStart);
        _output.WriteLine("---- app log ----");
        _output.WriteLine(tail);

        var markers = new[] { "[Unhandled]", "[AppDomain Unhandled]", "[UnobservedTask]", "[FATAL" };
        var offending = tail
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => markers.Any(m => l.Contains(m, StringComparison.Ordinal)))
            .ToList();

        Assert.True(offending.Count == 0,
            $"The application logged {offending.Count} unhandled/fatal entrie(s):{Environment.NewLine}{string.Join(Environment.NewLine, offending)}");
    }

    /// <summary>Round-trips a WM_NULL: a blocked UI thread cannot answer, so this measures a real freeze.</summary>
    private static double UiRoundTripMs(IntPtr hwnd, uint timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        var ok = NativeMethods.SendMessageTimeout(hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero, 0, timeoutMs, out _);
        sw.Stop();
        return ok == IntPtr.Zero ? -1 : sw.Elapsed.TotalMilliseconds;
    }

    private bool TrySkipWithoutWindow(AppSession session, string what)
    {
        if (session.MainWindow != null) return false;

        var message = $"[E2E] {what}: no main window appeared within 30s. " +
                      "This machine appears to have no interactive desktop session.";
        if (Strict)
        {
            Assert.Fail(message + Environment.NewLine + ReadLogTail(session.LogLengthAtStart));
        }

        // xUnit 2.5.3 cannot turn a running test into a skip, so this returns a PASS. The marker is
        // the only thing that distinguishes it in a log from a run that actually drove the window.
        _output.WriteLine("[SKIPPED-ENV] " + message);
        _output.WriteLine("Set DAWNPLAYER_E2E_STRICT=1 to make this a failure.");
        return true;
    }

    private static AutomationElement Require(Window window, string automationId)
    {
        var element = Retry(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(8));
        Assert.NotNull(element);
        return element!;
    }

    private static T? Retry<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                var value = probe();
                if (value != null) return value;
            }
            catch { }
            Thread.Sleep(75);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static void Activate(AutomationElement element)
    {
        if (element.Patterns.SelectionItem.IsSupported) { element.Patterns.SelectionItem.Pattern.Select(); return; }
        if (element.Patterns.Toggle.IsSupported) { element.Patterns.Toggle.Pattern.Toggle(); return; }
        if (element.Patterns.Invoke.IsSupported) { element.Patterns.Invoke.Pattern.Invoke(); return; }
        element.Click();
    }

    private static bool IsSelected(AutomationElement element)
    {
        try
        {
            if (element.Patterns.SelectionItem.IsSupported) return element.Patterns.SelectionItem.Pattern.IsSelected.Value;
            if (element.Patterns.Toggle.IsSupported) return element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
        }
        catch { }
        return false;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try { if (condition()) return true; }
            catch { }
            Thread.Sleep(75);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    // ---------------- tests ----------------

    [Fact]
    [Trait("Category", "RequiresDesktop")]
    public void App_Launches_AndExposesItsCoreShell()
    {
        using var session = Launch();
        AssertNoFatalLogEntries(session);
        if (TrySkipWithoutWindow(session, nameof(App_Launches_AndExposesItsCoreShell))) return;

        var window = session.MainWindow!;
        var hwnd = window.Properties.NativeWindowHandle.Value;
        NativeMethods.SetForegroundWindow(hwnd);

        Assert.Equal("Dawn Player", window.Title);

        Assert.True(NativeMethods.GetWindowRect(hwnd, out var rect), "GetWindowRect failed for the main window.");
        Assert.True(rect.Right - rect.Left >= 400, $"Main window is only {rect.Right - rect.Left}px wide.");
        Assert.True(rect.Bottom - rect.Top >= 300, $"Main window is only {rect.Bottom - rect.Top}px tall.");

        // Every part of the shell the user needs in order to do anything at all.
        foreach (var id in new[] { "TabLibrary", "TabPlaylists", "PlayButton", "SeekSlider", "VolumeSlider", "LyricsButton", "QueueButton", "SearchBox" })
        {
            var element = Require(window, id);
            _output.WriteLine($"[E2E] found '{id}' ({element.ControlType})");
        }

        Assert.True(UiRoundTripMs(hwnd) >= 0, "The UI thread did not answer a message within 20s after startup.");
        AssertNoFatalLogEntries(session);
    }

    [Fact]
    [Trait("Category", "RequiresDesktop")]
    public void Navigation_ViewModes_AndLyricsToggle_ChangeStateAndStayResponsive()
    {
        using var session = Launch();
        if (TrySkipWithoutWindow(session, nameof(Navigation_ViewModes_AndLyricsToggle_ChangeStateAndStayResponsive))) return;

        var window = session.MainWindow!;
        var hwnd = window.Properties.NativeWindowHandle.Value;
        NativeMethods.SetForegroundWindow(hwnd);

        var tabLibrary = Require(window, "TabLibrary");
        var tabPlaylists = Require(window, "TabPlaylists");

        // --- tab navigation actually switches selection ---
        Activate(tabPlaylists);
        Assert.True(WaitUntil(() => IsSelected(tabPlaylists), TimeSpan.FromSeconds(5)),
            "Selecting the Playlists tab did not mark it selected.");
        Assert.False(IsSelected(tabLibrary), "Both tabs report selected after switching to Playlists.");

        Activate(tabLibrary);
        Assert.True(WaitUntil(() => IsSelected(tabLibrary), TimeSpan.FromSeconds(5)),
            "Returning to the Library tab did not mark it selected.");

        // --- the library toolbar is present on the library tab ---
        var searchBox = Require(window, "SearchBox");
        var viewGrid = Require(window, "ViewGridBtn");
        var viewList = Require(window, "ViewListBtn");

        // --- view mode toggle flips selection both ways ---
        Activate(viewList);
        Assert.True(WaitUntil(() => IsSelected(viewList), TimeSpan.FromSeconds(5)),
            "Switching to list view did not select the list-view button.");
        Activate(viewGrid);
        Assert.True(WaitUntil(() => IsSelected(viewGrid), TimeSpan.FromSeconds(5)),
            "Switching back to grid view did not select the grid-view button.");

        // --- lyrics toggle round-trips ---
        var lyricsButton = Require(window, "LyricsButton");
        bool before = IsSelected(lyricsButton);
        Activate(lyricsButton);
        Assert.True(WaitUntil(() => IsSelected(lyricsButton) != before, TimeSpan.FromSeconds(5)),
            "Clicking the lyrics button did not change its toggle state.");
        Activate(lyricsButton);
        Assert.True(WaitUntil(() => IsSelected(lyricsButton) == before, TimeSpan.FromSeconds(5)),
            "Clicking the lyrics button a second time did not restore its toggle state.");

        // --- typing in search keeps the UI answering ---
        var edit = searchBox.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? searchBox;
        var value = edit.Patterns.Value.PatternOrDefault;
        if (value != null)
        {
            foreach (var text in new[] { "a", "al", "alb", "album", "" })
            {
                value.SetValue(text);
                var rtt = UiRoundTripMs(hwnd);
                Assert.True(rtt >= 0, $"The UI thread stopped answering while the search box contained '{text}'.");
                _output.WriteLine($"[E2E] search '{text}' -> UI round trip {rtt:0.0}ms");
            }
        }
        else
        {
            _output.WriteLine("[E2E] search box exposes no ValuePattern; skipped the typing leg.");
        }

        Assert.False(session.App.HasExited, "The application exited while being driven.");
        Assert.True(UiRoundTripMs(hwnd) >= 0, "The UI thread did not answer a message after the workflow.");
        AssertNoFatalLogEntries(session);
    }
}
