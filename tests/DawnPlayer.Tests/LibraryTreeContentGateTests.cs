using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Library-tree content gate (2026-10-03 user report: blank sidebar rows). The PT2-05
/// expansion-state refactor dropped `Content = model` from LibraryTreeBuilder.ToTreeViewNode,
/// leaving every TreeViewNode with null Content — the item template ({Binding
/// Content.Title/Glyph/CountText}) rendered empty 26px rows ("글자가 안 보이는 사이드바") and
/// every Content-pattern-matching consumer (filter match, deferred expansion, selection
/// restore) silently no-oped. LibraryTreeBuilder cannot be linked into this project (it
/// imports WinUI TreeViewNode), so — like <see cref="PaletteConsistencyTests"/> — the gate
/// scans the source: the template's Content.* bindings and the builder's Content assignment
/// must exist together.
/// </summary>
public class LibraryTreeContentGateTests
{
    private static (string Builder, string Page) ReadSources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var builder = File.ReadAllText(Path.Combine(dir.FullName,
                "src", "DawnPlayer.App", "Views", "Library", "LibraryTreeBuilder.cs"));
            var page = File.ReadAllText(Path.Combine(dir.FullName,
                "src", "DawnPlayer.App", "Views", "LibraryPage.xaml"));
            return (builder, page);
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
        return (string.Empty, string.Empty);
    }

    [Fact]
    public void TreeTemplateBindsContent_SoBuilderMustAssignIt()
    {
        var (builder, page) = ReadSources();

        // The template consumes node.Content.* — a builder that stops assigning Content
        // produces blank rows instead of failing loudly.
        Assert.Contains("Binding Content.Title", page);

        // The assignment lives inside ToTreeViewNode (the node factory for every mode).
        var factory = builder.IndexOf("private static TreeViewNode ToTreeViewNode", StringComparison.Ordinal);
        Assert.True(factory >= 0, "ToTreeViewNode factory not found");
        var bodyEnd = builder.IndexOf("        return node;", factory, StringComparison.Ordinal);
        Assert.True(bodyEnd > factory, "ToTreeViewNode body end not found");
        var body = builder[factory..bodyEnd];
        Assert.Contains("Content = model", body);
    }
}
