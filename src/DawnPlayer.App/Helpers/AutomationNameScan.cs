using System.Text.RegularExpressions;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// U5 scanner for the automation-name override defect: a literal
/// <c>AutomationProperties.Name</c> attribute in XAML overrides the localized name coming from
/// the element's x:Uid, so non-Korean UIs read Korean labels from the screen reader. The rule
/// (post-2026-09-20 sweep): automation names come from resw via x:Uid — never from a literal
/// attribute. Dynamic bindings stay allowed (they are data, not hardcoded copy).
/// </summary>
public static class AutomationNameScan
{
    /// <summary>Literal (non-binding, non-empty) AutomationProperties.Name attribute: the value
    /// must not start with an opening brace (that is a {Binding}/{x:Bind}, i.e. data-driven).</summary>
    private static readonly Regex LiteralNameAttribute = new(
        @"AutomationProperties\.Name=""(?<value>[^""{][^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Returns the literal automation-name attributes in one XAML document as
    /// (lineNumber, value) pairs — line numbers are 1-based for direct test output.</summary>
    public static List<(int Line, string Value)> FindLiterals(string content)
    {
        var hits = new List<(int, string)>();
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in LiteralNameAttribute.Matches(lines[i]))
            {
                hits.Add((i + 1, m.Groups["value"].Value));
            }
        }
        return hits;
    }
}
