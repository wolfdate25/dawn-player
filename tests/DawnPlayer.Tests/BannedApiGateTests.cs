using System.Text;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Banned-API gate — the deterministic side of the "every agent correction becomes a check"
/// principle (potato pyramid, 2026-11-18; plan §15). Each rule below pins a defect this repo
/// has actually hit, which until now lived only in comments and memory: a comment can explain
/// a trap but cannot stop the next change from reintroducing it. New violations fail the build
/// instead of waiting for a reviewer to remember the lesson.
///
/// Rules:
///   R1 <c>Encoding.Default</c> is banned in App/Core. It is ALWAYS UTF-8 on .NET — the "ANSI
///      fallback" needs GetEncoding(0) + CodePagesEncodingProvider (+ a Latin-1 guard); the
///      shared decoder is <c>LegacyTextDecoder</c> (CP949 mojibake lesson).
///   R2 <c>TimeSpan.FromSeconds</c> is banned in Core/Lyrics — lyric timestamps must assemble
///      integer milliseconds into FromTicks. FromSeconds(0.087) is 869,999 ticks and the
///      written stamp drifted −1 ms per parse/format roundtrip (plan §14.3-5).
///   R3 in-place <c>.Sort(</c> is banned in Core — List/Array.Sort is unstable and reordered
///      equal-timestamp lyric lines run to run; ordering goes through OrderBy (LrcParser
///      lesson). Register deliberate primitive-sort exceptions in <see cref="SortAllowlist"/>.
/// </summary>
public class BannedApiGateTests
{
    /// <summary>Files (repo-relative, case-insensitive) allowed to use in-place Sort — for
    /// deliberate primitive-array sorts where instability cannot matter. Keep empty by default.</summary>
    private static readonly string[] SortAllowlist =
    {
        // Library sort keys break ties with the original index (a.Index.CompareTo) — a total
        // order, so List.Sort instability cannot reorder anything. Deliberate by design.
        @"src\DawnPlayer.Core\Library\MusicLibrary.cs",

        // Legacy tie-unstable sorts (equal-comparing entries may swap run to run — cosmetic
        // ordering only). Blocked for NEW code; existing sites are cleanup candidates (§15).
        @"src\DawnPlayer.Core\Playlist\PlaylistManager.cs",
    };

    /// <summary>Files allowed to use TimeSpan.FromSeconds for WHOLE-SECOND durations (exact in
    /// double). Fractional/derived timestamp math stays banned everywhere in Core/Lyrics.</summary>
    private static readonly string[] FromSecondsAllowlist =
    {
        // PluginTimeoutSeconds is an integer constant — a whole-second network timeout, not a
        // lyric timestamp; FromSeconds on integers is tick-exact.
        @"src\DawnPlayer.Core\Lyrics\Online\OnlineLyricsService.cs",
    };

    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static List<string> SourceFiles(DirectoryInfo root, params string[] projectDirs)
    {
        var result = new List<string>();
        foreach (var project in projectDirs)
        {
            var dir = Path.Combine(root.FullName, "src", project);
            result.AddRange(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories));
        }
        return result
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    /// <summary>Strips line and block comments so rules scan code only — the traps are all
    /// documented in comments, and those mentions must not trip the gate. Known limitation:
    /// "//" inside a string literal (URLs) hides the rest of that line; under-detection is the
    /// safe direction for a ban gate.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inLine = false, inBlock = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); }
            }
            else if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
            }
            else if (c == '/' && next == '/') { inLine = true; i++; }
            else if (c == '/' && next == '*') { inBlock = true; i++; }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AssertNoViolations(string rule, string replacement, IEnumerable<string> violations)
    {
        var list = violations.ToList();
        Assert.True(list.Count == 0,
            $"{rule} ({list.Count} violation(s)) — {replacement}\n" +
            string.Join("\n", list.Select(v => $"  {v}")));
    }

    [Fact]
    public void AppAndCore_NeverUseEncodingDefault()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");

        var violations = new List<string>();
        foreach (var file in SourceFiles(root!, "DawnPlayer.App", "DawnPlayer.Core"))
        {
            var code = StripComments(File.ReadAllText(file));
            if (code.Contains("Encoding.Default", StringComparison.Ordinal))
            {
                int line = 1 + code[..code.IndexOf("Encoding.Default", StringComparison.Ordinal)]
                    .Count(ch => ch == '\n');
                violations.Add($"{Path.GetRelativePath(root!.FullName, file)}:{line}");
            }
        }
        AssertNoViolations(
            "R1: Encoding.Default is banned",
            "it is always UTF-8 on .NET; the ANSI fallback is GetEncoding(0) + CodePagesEncodingProvider (or LegacyTextDecoder for content-based detection)",
            violations);
    }

    [Fact]
    public void CoreLyrics_NeverUseTimeSpanFromSeconds()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");

        var lyricsDir = Path.Combine(root!.FullName, "src", "DawnPlayer.Core", "Lyrics");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(lyricsDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file);
            if (FromSecondsAllowlist.Any(a => string.Equals(a, relative, StringComparison.OrdinalIgnoreCase))) continue;

            var code = StripComments(File.ReadAllText(file));
            if (code.Contains("TimeSpan.FromSeconds", StringComparison.Ordinal))
            {
                violations.Add(relative);
            }
        }
        AssertNoViolations(
            "R2: TimeSpan.FromSeconds is banned in Core/Lyrics",
            "assemble integer milliseconds and use FromTicks — FromSeconds loses sub-tick precision and the written stamp drifted −1 ms per roundtrip (plan §14.3-5)",
            violations);
    }

    [Fact]
    public void Core_NeverUseInPlaceSort()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");

        var violations = new List<string>();
        foreach (var file in SourceFiles(root!, "DawnPlayer.Core"))
        {
            var relative = Path.GetRelativePath(root!.FullName, file);
            if (SortAllowlist.Any(a => string.Equals(a, relative, StringComparison.OrdinalIgnoreCase))) continue;

            var code = StripComments(File.ReadAllText(file));
            if (code.Contains(".Sort(", StringComparison.Ordinal))
            {
                violations.Add(relative);
            }
        }
        AssertNoViolations(
            "R3: in-place .Sort( is banned in Core",
            "List/Array.Sort is unstable and reorders equal keys run to run — order through OrderBy, or add a deliberate exception to SortAllowlist",
            violations);
    }
}
