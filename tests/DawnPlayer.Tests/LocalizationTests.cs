using System.Globalization;
using System.Resources;
using DawnPlayer.App.Localization;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

// The five tests all mutate the process-wide CurrentUICulture; running them in
// parallel inside one collection races itself (Test 4 fires while Test 3's
// ApplyLanguage has only half-set the culture).
[Collection("Localization")]
public class LocalizationTests
{
    private static readonly ResourceManager Resources = new(
        "DawnPlayer.App.Localization.Strings",
        typeof(StringsLoader).Assembly);

    [Fact]
    public void NeutralResourceManager_ContainsAppProductTitle()
    {
        var value = Resources.GetString("App_ProductTitle", CultureInfo.InvariantCulture);
        Assert.Equal("Dawn Player", value);
    }

    [Fact]
    public void Get_ReturnsNeutralValueForNeutralCulture()
    {
        using var scope = new CultureScope(enUS: true);
        Assert.Equal("Dawn Player", StringsLoader.Get("App_ProductTitle"));
    }

    [Fact]
    public void Get_FallsBackWhenKeyMissing()
    {
        using var scope = new CultureScope(enUS: true);
        Assert.Equal("없음", StringsLoader.Get("DoesNotExist", "없음"));
    }

    [Fact]
    public void ApplyLanguage_SwitchesCurrentCultureAndRaisesEvent()
    {
        var original = CultureInfo.CurrentUICulture;
        var raised = 0;
        StringsLoader.LanguageChanged += () => raised++;
        try
        {
            StringsLoader.ApplyLanguage(UiLanguage.JaJP);

            Assert.Equal(new CultureInfo("ja-JP"), CultureInfo.CurrentUICulture);
            Assert.Equal(1, raised);
            Assert.Equal(new CultureInfo("ja-JP"), StringsLoader.CurrentCulture);
        }
        finally
        {
            StringsLoader.LanguageChanged -= () => raised++;
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ApplyLanguage_SystemFollowsCurrentOsCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ko-KR");
            StringsLoader.ApplyLanguage(UiLanguage.System);

            Assert.Equal(new CultureInfo("ko-KR"), CultureInfo.CurrentUICulture);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _original;
        public CultureScope(bool enUS)
        {
            _original = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = enUS ? new CultureInfo("en-US") : new CultureInfo("ko-KR");
        }
        public void Dispose() => CultureInfo.CurrentUICulture = _original;
    }
}
