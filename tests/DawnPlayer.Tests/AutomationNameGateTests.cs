using DawnPlayer.App.Helpers;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// U5 automation-name gate. M6's gate checked that x:Uid keys exist; it missed the override
/// defect — a literal AutomationProperties.Name attribute in XAML beats the localized resw
/// name, so screen readers read Korean even on English/Japanese UIs (31 occurrences swept on
/// 2026-09-20). The rule now: literal automation names are forbidden in UI XAML; names come
/// from resw via x:Uid.
/// </summary>
public class AutomationNameGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    [Fact]
    public void UiXaml_HasNoLiteralAutomationNames()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");

        var appDir = Path.Combine(root!.FullName, "src", "DawnPlayer.App");
        var files = Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}")
                     || p.Contains($"{Path.DirectorySeparatorChar}Controls{Path.DirectorySeparatorChar}")
                     || p.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(files.Count > 3, "gate found no UI XAML files — scan scope broken");

        var violations = new List<string>();
        foreach (var file in files)
        {
            foreach (var (line, value) in AutomationNameScan.FindLiterals(File.ReadAllText(file)))
            {
                violations.Add($"{Path.GetFileName(file)}:{line} → {value}");
            }
        }

        Assert.True(violations.Count == 0,
            "Literal AutomationProperties.Name overrides the localized resw name — move the " +
            "string to <uid>.AutomationProperties.Name in all three resw files:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_Detects_Literals_ButNotBindings()
    {
        var xaml = """
            <Button x:Uid="A" AutomationProperties.Name="하드코딩"/>
            <TextBlock AutomationProperties.Name="{Binding Title}"/>
            <Button x:Uid="B" AutomationProperties.Name="{x:Bind Label, Mode=OneWay}"/>
            """;
        var hits = AutomationNameScan.FindLiterals(xaml);
        Assert.Single(hits);
        Assert.Equal(1, hits[0].Line);
        Assert.Equal("하드코딩", hits[0].Value);
    }
}
