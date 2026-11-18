using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// 가사 UI 통일성 게이트 (2026-11 가사 패널·편집기 정리). The four lyrics surfaces used to
/// hardcode FontSize literals next to sibling controls that used design tokens — the same
/// pair of buttons could render at two different sizes. Gates:
///   * No numeric FontSize literals in the lyrics XAML — sizes come from DesignTokens only.
///   * Every x:Uid in the lyrics XAML has at least one matching resw key in ko-KR, so a new
///     control cannot ship unlocalizable (the key-set parity test only proves the three
///     languages agree with each other, not that a uid actually resolves).
/// </summary>
public class LyricsUiTypographyGateTests
{
    private static readonly string[] LyricsXamlFiles =
    {
        "Controls\\LyricsPane.xaml",
        "Controls\\FullscreenLyricsView.xaml",
        "Views\\LyricsEditorWindow.xaml",
        "Views\\LyricsSearchWindow.xaml",
    };

    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    public static IEnumerable<object[]> LyricsXamlPaths()
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("repo root not found");
        foreach (var rel in LyricsXamlFiles)
        {
            yield return new object[] { Path.Combine(root.FullName, "src", "DawnPlayer.App", rel) };
        }
    }

    [Theory]
    [MemberData(nameof(LyricsXamlPaths))]
    public void LyricsXaml_UsesFontTokens_NotNumericLiterals(string xamlPath)
    {
        Assert.True(File.Exists(xamlPath), $"missing lyrics XAML: {xamlPath}");
        var xaml = File.ReadAllText(xamlPath);

        // Text-scale literals are banned outright — the token scale is 11..17. Sizes above 20
        // are hero/decorative icons in empty states, which have no token by design.
        var literals = Regex.Matches(xaml, """FontSize\s*=\s*"(\d+)""")
            .Select(m => (text: m.Groups[1].Value, value: int.Parse(m.Groups[1].ValueSpan)))
            .Where(m => m.value <= 20)
            .Select(m => m.text)
            .Distinct()
            .ToArray();

        Assert.True(literals.Length == 0,
            $"FontSize literal(s) {string.Join(", ", literals)} in {Path.GetFileName(xamlPath)} — use a DesignTokens size (FontCaption/FontBodySmall/FontBody/FontSubtitle/FontTitle)");
    }

    [Fact]
    public void LyricsXaml_EveryUid_HasAReswKey()
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("repo root not found");
        var reswPath = Path.Combine(root.FullName, "src", "DawnPlayer.App", "Strings", "ko-KR", "Resources.resw");
        Assert.True(File.Exists(reswPath), "ko-KR Resources.resw must exist");

        var reswNames = XDocument.Load(reswPath)
            .Descendants("data")
            .Select(d => d.Attribute("name")?.Value ?? "")
            .Where(n => n.Length > 0)
            .ToHashSet();

        var uidRegex = new Regex("""x:Uid\s*=\s*"([A-Za-z0-9_]+)""");

        foreach (var rel in LyricsXamlFiles)
        {
            var xamlPath = Path.Combine(root.FullName, "src", "DawnPlayer.App", rel);
            var xaml = File.ReadAllText(xamlPath);

            foreach (Match m in uidRegex.Matches(xaml))
            {
                var uid = m.Groups[1].Value;
                Assert.True(reswNames.Any(n => n == uid || n.StartsWith(uid + ".", StringComparison.Ordinal)),
                    $"x:Uid '{uid}' in {rel} has no resw key — the control ships unlocalizable");
            }
        }
    }
}
