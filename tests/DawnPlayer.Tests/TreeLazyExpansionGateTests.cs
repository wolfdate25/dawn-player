using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// P0 lazy-expansion wiring gate: Folder-mode children must materialize on first
/// expansion instead of all upfront. The TreeView wires Expanding, and the
/// code-behind delegates to the builder's materializer (WinUI node wrapping
/// itself needs on-device verification; see the manual checklist).
/// </summary>
public sealed class TreeLazyExpansionGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string ReadSource(params string[] parts)
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");
        var path = Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void LibraryTree_WiresExpandingHandler()
    {
        var xaml = ReadSource("src", "DawnPlayer.App", "Views", "LibraryPage.xaml");
        Assert.Contains("Expanding=\"OnTreeExpanding\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandingHandler_MaterializesDeferredChildren()
    {
        var cs = ReadSource("src", "DawnPlayer.App", "Views", "LibraryPage.xaml.cs");
        Assert.Contains("OnTreeExpanding", cs, StringComparison.Ordinal);
        Assert.Contains("EnsureChildrenLoaded", cs, StringComparison.Ordinal);
    }
}
