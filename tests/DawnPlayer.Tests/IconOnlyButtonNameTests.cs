using System.Xml.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// PT5-19 (2026-09-30 audit): AutomationNameScan guards against resw names being <em>overwritten</em>
/// by XAML literals, but not against names being <em>absent</em>. This gate closes that gap for
/// icon-only buttons: a Button/ToggleButton/AppButton/DropDownButton carrying an x:Uid and no
/// visible text content must have an accessible name — either a resw
/// <c>&lt;uid&gt;.AutomationProperties.Name</c> entry or a literal AutomationProperties.Name
/// attribute. Without it a screen reader announces nothing for the control.
/// </summary>
public class IconOnlyButtonNameTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("repository root not found");
    }

    private static readonly string[] ButtonTypes = { "Button", "ToggleButton", "AppBarButton", "DropDownButton" };

    private static HashSet<string> ReswKeys(string lang)
    {
        var path = Path.Combine(RepoRoot(), "src", "DawnPlayer.App", "Strings", lang, "Resources.resw");
        var doc = XDocument.Load(path);
        return doc.Root!.Elements("data")
            .Select(d => d.Attribute("name")?.Value ?? "")
            .ToHashSet();
    }

    public static IEnumerable<object[]> UiXamlFiles()
    {
        var app = Path.Combine(RepoRoot(), "src", "DawnPlayer.App");
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (file.EndsWith("App.xaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith("DesignTokens.xaml", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(UiXamlFiles))]
    public void IconOnlyButtons_HaveAccessibleNames(string file)
    {
        // en-US is the canonical catalog; parity across languages is another gate's job.
        var keys = ReswKeys("en-US");
        var doc = XDocument.Load(file);
        var offenders = new List<string>();

        void Walk(XElement el)
        {
            foreach (var child in el.Elements())
            {
                var local = child.Name.LocalName;
                if (ButtonTypes.Contains(local))
                {
                    var uid = child.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Uid")?.Value;
                    if (uid != null)
                    {
                        bool hasVisibleText =
                            child.Attribute("Content") is { } content && content.Value.Trim().Length > 0 ||
                            child.Descendants().Any(d => d.Name.LocalName == "TextBlock");
                        bool hasReswName = keys.Contains(uid + ".AutomationProperties.Name");
                        bool hasReswText = keys.Contains(uid + ".Content") || keys.Contains(uid + ".Text");
                        bool hasLiteralName = child.Attributes()
                            .Any(a => a.Name.LocalName == "Name" && a.Name.NamespaceName.Contains("AutomationProperties"));

                        if (!hasVisibleText && !hasReswText && !hasReswName && !hasLiteralName)
                        {
                            offenders.Add($"{Path.GetFileName(file)}: {local} x:Uid={uid}");
                        }
                    }
                }
                Walk(child);
            }
        }
        Walk(doc.Root!);

        Assert.True(offenders.Count == 0,
            "Icon-only buttons without an accessible name (add <uid>.AutomationProperties.Name to all three resw files):\n  "
            + string.Join("\n  ", offenders));
    }
}
