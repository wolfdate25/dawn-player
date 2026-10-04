using System.Text.RegularExpressions;
using DawnPlayer.App.Localization;
using Xunit;

namespace DawnPlayer.Tests;

public class LocalizationTests
{
    private readonly InMemoryLocalizationService _service;

    public LocalizationTests()
    {
        _service = new InMemoryLocalizationService();
        _service.SetString("en-US", "WelcomeText", "Welcome!");
        _service.SetString("ko-KR", "WelcomeText", "환영합니다!");
        _service.SetString("ja-JP", "WelcomeText", "ようこそ！");

        _service.SetString("en-US", "FileTransferStatus", "Transferred {1} of {0} files (Remaining: {2})");
        _service.SetString("ko-KR", "FileTransferStatus", "{0}개 중 {1}개 파일 전송 완료 (남은 시간: {2})");

        _service.SetString("en-US", "SelectedItems_Zero", "No items selected");
        _service.SetString("en-US", "SelectedItems_One", "1 item selected");
        _service.SetString("en-US", "SelectedItems_Other", "{0} items selected");

        _service.SetString("ko-KR", "SelectedItems_Zero", "선택된 항목 없음");
        _service.SetString("ko-KR", "SelectedItems_Other", "{0}개 항목 선택됨");
    }

    [Fact]
    public void Get_ReturnsCorrectString_ForAppliedLanguage()
    {
        _service.ApplyLanguage("ko-KR");
        Assert.Equal("환영합니다!", _service.Get("WelcomeText"));

        _service.ApplyLanguage("en-US");
        Assert.Equal("Welcome!", _service.Get("WelcomeText"));

        _service.ApplyLanguage("ja-JP");
        Assert.Equal("ようこそ！", _service.Get("WelcomeText"));
    }

    [Fact]
    public void Get_ReturnsFallback_WhenKeyMissing()
    {
        _service.ApplyLanguage("ko-KR");
        Assert.Equal("DefaultValue", _service.Get("NonExistentKey", "DefaultValue"));
        Assert.Equal("", _service.Get("NonExistentKey"));
    }

    [Fact]
    public void Format_SubstitutesCompositePlaceholders_Correctly()
    {
        _service.ApplyLanguage("en-US");
        var resultEn = _service.Format("FileTransferStatus", 100, 45, "5s");
        Assert.Equal("Transferred 45 of 100 files (Remaining: 5s)", resultEn);

        _service.ApplyLanguage("ko-KR");
        var resultKo = _service.Format("FileTransferStatus", 100, 45, "5s");
        Assert.Equal("100개 중 45개 파일 전송 완료 (남은 시간: 5s)", resultKo);
    }

    [Fact]
    public void GetPlural_ResolvesZeroOneOther_AndFallsBack()
    {
        _service.ApplyLanguage("en-US");
        Assert.Equal("No items selected", _service.GetPlural("SelectedItems", 0, 0));
        Assert.Equal("1 item selected", _service.GetPlural("SelectedItems", 1, 1));
        Assert.Equal("5 items selected", _service.GetPlural("SelectedItems", 5, 5));

        _service.ApplyLanguage("ko-KR");
        Assert.Equal("선택된 항목 없음", _service.GetPlural("SelectedItems", 0, 0));
        // In ko-KR, SelectedItems_One is not defined, should fall back to _Other
        Assert.Equal("1개 항목 선택됨", _service.GetPlural("SelectedItems", 1, 1));
        Assert.Equal("10개 항목 선택됨", _service.GetPlural("SelectedItems", 10, 10));
    }

    [Fact]
    public void LanguageChanged_EventFires_OnApplyLanguage()
    {
        var fired = false;
        _service.LanguageChanged += () => fired = true;

        _service.ApplyLanguage("ko-KR");
        Assert.True(fired);
        Assert.Equal("ko-KR", _service.CurrentLanguage);
    }

    [Fact]
    public void AppStrings_FacadeDelegates_ToAssignedService()
    {
        var original = AppStrings.Instance;
        try
        {
            AppStrings.Instance = _service;
            _service.ApplyLanguage("ko-KR");

            Assert.Equal("환영합니다!", AppStrings.Get("WelcomeText"));
            Assert.Equal("선택된 항목 없음", AppStrings.GetPlural("SelectedItems", 0, 0));
        }
        finally
        {
            AppStrings.Instance = original;
        }
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrowOrDeadlock()
    {
        var languages = new[] { "en-US", "ko-KR", "ja-JP" };
        Parallel.For(0, 1000, i =>
        {
            if (i % 50 == 0)
            {
                _service.ApplyLanguage(languages[i % languages.Length]);
            }
            var text = _service.Get("WelcomeText");
            Assert.False(string.IsNullOrEmpty(text));
            var plural = _service.GetPlural("SelectedItems", i % 5, i % 5);
            Assert.False(string.IsNullOrEmpty(plural));
        });
    }

    [Fact]
    public void AppearanceSettingsViewModel_LanguageIndex_SyncsCorrectly()
    {
        var settings = new DawnPlayer.Core.Persistence.AppSettings();
        DawnPlayer.Core.Persistence.UiLanguage? lastChanged = null;
        var vm = new DawnPlayer.App.ViewModels.Settings.AppearanceSettingsViewModel(
            new DawnPlayer.App.Services.AppearanceSettingsService(settings),
            settings,
            onLanguageChanged: lang => lastChanged = lang);

        // Default should be System (0)
        Assert.Equal(0, vm.LanguageIndex);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.System, vm.Language);

        // Select Korean (1)
        vm.LanguageIndex = 1;
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.KoKR, vm.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.KoKR, settings.Ui.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.KoKR, lastChanged);

        // Select English (2)
        vm.LanguageIndex = 2;
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.EnUS, vm.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.EnUS, settings.Ui.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.EnUS, lastChanged);

        // Select Japanese (3)
        vm.LanguageIndex = 3;
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.JaJP, vm.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.JaJP, settings.Ui.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.JaJP, lastChanged);

        // Select System (0)
        vm.LanguageIndex = 0;
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.System, vm.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.System, settings.Ui.Language);
        Assert.Equal(DawnPlayer.Core.Persistence.UiLanguage.System, lastChanged);
    }

    [Fact]
    public void ReswFiles_HaveValidXml_AndIdenticalKeySets()
    {
        var dir = FindRepoRoot();
        // Fail loudly rather than silently skipping: a green-but-skipped parity test is how
        // key drift between languages ships unnoticed. (The old lookup matched "DawnPlayer.sln"
        // and quietly no-op'd after the solution became DawnPlayer.slnx.)
        Assert.True(dir != null,
            "DawnPlayer.slnx not found above the test output directory; resource checks require a source checkout.");

        var koPath = System.IO.Path.Combine(dir!.FullName, "src", "DawnPlayer.App", "Strings", "ko-KR", "Resources.resw");
        var enPath = System.IO.Path.Combine(dir!.FullName, "src", "DawnPlayer.App", "Strings", "en-US", "Resources.resw");
        var jaPath = System.IO.Path.Combine(dir!.FullName, "src", "DawnPlayer.App", "Strings", "ja-JP", "Resources.resw");

        Assert.True(System.IO.File.Exists(koPath), "ko-KR Resources.resw must exist");
        Assert.True(System.IO.File.Exists(enPath), "en-US Resources.resw must exist");
        Assert.True(System.IO.File.Exists(jaPath), "ja-JP Resources.resw must exist");

        var koKeys = LoadReswKeys(koPath);
        var enKeys = LoadReswKeys(enPath);
        var jaKeys = LoadReswKeys(jaPath);

        // Ensure no duplicate keys exist in each file
        Assert.Equal(koKeys.Distinct().Count(), koKeys.Count);
        Assert.Equal(enKeys.Distinct().Count(), enKeys.Count);
        Assert.Equal(jaKeys.Distinct().Count(), jaKeys.Count);

        var koSet = koKeys.ToHashSet();
        var enSet = enKeys.ToHashSet();
        var jaSet = jaKeys.ToHashSet();

        var missingInEn = koSet.Except(enSet).ToList();
        var missingInJa = koSet.Except(jaSet).ToList();
        var extraInEn = enSet.Except(koSet).ToList();
        var extraInJa = jaSet.Except(koSet).ToList();

        Assert.True(missingInEn.Count == 0, $"Keys present in ko-KR but missing in en-US: {string.Join(", ", missingInEn)}");
        Assert.True(missingInJa.Count == 0, $"Keys present in ko-KR but missing in ja-JP: {string.Join(", ", missingInJa)}");
        Assert.True(extraInEn.Count == 0, $"Keys present in en-US but missing in ko-KR: {string.Join(", ", extraInEn)}");
        Assert.True(extraInJa.Count == 0, $"Keys present in ja-JP but missing in ko-KR: {string.Join(", ", extraInJa)}");
    }

    // ---------- x:Uid / lookup-key cross-checks against the resw catalog ----------
    //
    // A missing key is a silent runtime bug: x:Uid quietly no-ops and AppStrings.Get quietly
    // returns the fallback, so drift only surfaces as a stray hard-coded string in the UI.
    // These tests turn that drift red at build time. tools/check_i18n.py stays around for
    // on-demand lint reports (unused keys, non-localized literals) that are too noisy to gate.

    [Fact]
    public void Xaml_XUid_Values_Resolve_ToReswKeys()
    {
        var uidBases = ReswKeySet().Select(k => k.Split('.')[0]).ToHashSet();
        var missing = ScanAppSources(".xaml", XUidPattern)
            .Where(hit => !uidBases.Contains(hit.Value))
            .ToList();

        Assert.True(missing.Count == 0,
            "x:Uid values with no matching resw key base:\n" + FormatHits(missing));
    }

    [Fact]
    public void Xaml_XUid_Values_HaveApplicablePropertyEntries()
    {
        // A bare '<uid>' resw entry is only reachable through ResourceLoader/AppStrings lookups;
        // the x:Uid pipeline applies '<uid>.<Property>' entries exclusively. An element whose uid
        // owns no suffixed entry therefore keeps its hard-coded XAML text in EVERY language —
        // the 2026-10-04 rating-row title/desc, the sleep-timer menu items, the DLNA preparing
        // label and 42 '*_A11yName' automation names all shipped that way: the base-name-only
        // gate above sees the bare key and stays green.
        var applicableBases = ReswKeySet()
            .Where(k => k.Contains('.'))
            .Select(k => k.Split('.')[0])
            .ToHashSet();
        var missing = ScanAppSources(".xaml", XUidPattern)
            .Select(h => h.Value)
            .Distinct()
            .Where(uid => !applicableBases.Contains(uid))
            .ToList();

        Assert.True(missing.Count == 0,
            "x:Uid values with no '<uid>.<Property>' resw entry — the element keeps its hard-coded text in every language.\n"
            + "Rename the key to '<uid>.<Property>', or keep the bare key for AppStrings lookups and add a suffixed duplicate:\n"
            + string.Join("\n", missing.Select(m => "  " + m)));
    }

    [Fact]
    public void CSharp_LiteralLookupKeys_Exist_InResw()
    {
        var keys = ReswKeySet();
        var missing = ScanAppSources(".cs", LiteralKeyPattern)
            .Where(hit => !keys.Contains(hit.Value))
            .ToList();

        Assert.True(missing.Count == 0,
            "AppStrings literal keys missing from the resw catalog:\n" + FormatHits(missing));
    }

    [Fact]
    public void CSharp_InterpolatedPrefixes_HaveReswKeys()
    {
        var keys = ReswKeySet();
        var missing = ScanAppSources(".cs", InterpolatedPrefixPattern)
            .Where(hit => !keys.Any(k => k.StartsWith(hit.Value, StringComparison.Ordinal)))
            .ToList();

        Assert.True(missing.Count == 0,
            "AppStrings interpolated prefixes with no resw key under them:\n" + FormatHits(missing));
    }

    [Fact]
    public void Xaml_WindowRoots_DoNotCarry_XUid()
    {
        // x:Uid on a Window root makes InitializeComponent throw XamlParseException as soon as a
        // resw key (e.g. '<uid>.Title') tries to localize it: Window is not a FrameworkElement
        // and Title is not a dependency property, so the deferred resource pass fails to assign
        // ("Failed to assign to property 'Microsoft.UI.Xaml.Window.Title'"). This made both the
        // lyrics editor and the lyrics search window die on open (v1.1.0) — the exception was
        // swallowed by the global handler, so nothing visible happened. Window titles are set
        // from code-behind via AppStrings instead; keep that convention.
        var root = FindRepoRoot();
        Assert.True(root != null, "Repository root not found; see ReswFiles_HaveValidXml_AndIdenticalKeySets.");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppSourceDir(root!), "*.xaml", SearchOption.AllDirectories))
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}"))
            {
                continue;
            }

            // The root tag spans several lines; match from '<Window' to its closing '>'.
            var openingTag = WindowOpeningTagPattern.Match(System.IO.File.ReadAllText(file));
            if (!openingTag.Success)
            {
                continue;
            }

            var uid = XUidPattern.Match(openingTag.Value);
            if (uid.Success)
            {
                offenders.Add($"  {System.IO.Path.GetRelativePath(root!.FullName, file)} -> {uid.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Window root elements must not carry x:Uid — a resw '<uid>.Title' entry crashes the window at parse time.\n"
            + "Set window titles from code-behind with AppStrings.Get instead.\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Xaml_AutomationNames_CarryReswKeys()
    {
        // M6-5 gate: a hardcoded AutomationProperties.Name is only a compile-time fallback; the
        // screen-reader-facing text must come from the resw pipeline so non-Korean users get
        // localized names. Every element that hardcodes an automation name therefore needs an
        // x:Uid and a matching '<uid>.AutomationProperties.Name' key.
        var root = FindRepoRoot();
        Assert.True(root != null, "Repository root not found; see ReswFiles_HaveValidXml_AndIdenticalKeySets.");
        var keys = ReswKeySet();

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppSourceDir(root!), "*.xaml", SearchOption.AllDirectories))
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            // Opening tags can span lines; attribute values in this codebase never contain '>'.
            foreach (Match tag in ElementTagPattern.Matches(text))
            {
                var uid = UidValuePattern.Match(tag.Value);
                var auto = AutoNameValuePattern.Match(tag.Value);
                // Bindings supply the name at runtime from (already localized) view data.
                if (!auto.Success || auto.Groups[1].Value.TrimStart().StartsWith((char)123))
                {
                    continue;
                }

                int line = 1;
                for (int i = 0; i < tag.Index; i++)
                {
                    if (text[i] == '\n') line++;
                }

                if (!uid.Success)
                {
                    offenders.Add($"  {System.IO.Path.GetRelativePath(root!.FullName, file)}:{line} '{auto.Groups[1].Value}' has no x:Uid");
                }
                else if (!keys.Contains(uid.Groups[1].Value + ".AutomationProperties.Name"))
                {
                    offenders.Add($"  {System.IO.Path.GetRelativePath(root!.FullName, file)}:{line} '{uid.Groups[1].Value}.AutomationProperties.Name' missing from resw");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Automation names must be keyed for localization:\n" + string.Join("\n", offenders));
    }

    private static readonly Regex XUidPattern = new("x:Uid=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ElementTagPattern = new(@"<[A-Za-z][A-Za-z0-9_.:]*(?:\s[^<>]*?)?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex UidValuePattern = new("x:Uid=\"([^\"]+)\"");
    private static readonly Regex AutoNameValuePattern = new("AutomationProperties.Name=\"([^\"]+)\"");
    private static readonly Regex WindowOpeningTagPattern = new(@"<Window\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex LiteralKeyPattern =
        new("AppStrings\\.(?:Get|GetString|Format|GetPlural)\\(\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex InterpolatedPrefixPattern =
        new("AppStrings\\.(?:Get|GetString|Format|GetPlural)\\(\\s*\\$\"([^\"{]+)\\{", RegexOptions.Compiled);

    /// <summary>Walks up from the test output directory to the checkout owning DawnPlayer.slnx.</summary>
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "DawnPlayer.slnx")))
            {
                return dir;
            }
        }

        return null;
    }

    private static string AppSourceDir(DirectoryInfo root) =>
        System.IO.Path.Combine(root.FullName, "src", "DawnPlayer.App");

    /// <summary>Key set of the ko-KR catalog — the reference language every other file must match.</summary>
    private static HashSet<string> ReswKeySet()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "Repository root not found; see ReswFiles_HaveValidXml_AndIdenticalKeySets.");
        return LoadReswKeys(System.IO.Path.Combine(AppSourceDir(root!), "Strings", "ko-KR", "Resources.resw")).ToHashSet();
    }

    private static IEnumerable<(string RelativePath, int Line, string Value)> ScanAppSources(
        string extension, Regex pattern)
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "Repository root not found; see ReswFiles_HaveValidXml_AndIdenticalKeySets.");

        foreach (var file in Directory.EnumerateFiles(AppSourceDir(root!), "*" + extension, SearchOption.AllDirectories))
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}"))
            {
                continue;
            }

            var lines = System.IO.File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pattern.Matches(lines[i]).Cast<Match>())
                {
                    yield return (System.IO.Path.GetRelativePath(root!.FullName, file), i + 1, match.Groups[1].Value);
                }
            }
        }
    }

    private static string FormatHits(List<(string RelativePath, int Line, string Value)> hits) =>
        string.Join("\n", hits.Select(h => $"  {h.RelativePath}:{h.Line} -> {h.Value}"));

    private static List<string> LoadReswKeys(string path)
    {
        var doc = System.Xml.Linq.XDocument.Load(path);
        var keys = new List<string>();
        foreach (var el in doc.Root?.Elements("data") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var name = el.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                keys.Add(name);
            }
        }
        return keys;
    }
}
