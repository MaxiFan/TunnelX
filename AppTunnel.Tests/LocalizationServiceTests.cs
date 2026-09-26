using System.Globalization;
using System.Reflection;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class LocalizationServiceTests : IDisposable
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly string _previousLanguage;

    public LocalizationServiceTests()
    {
        _previousLanguage = _loc.LanguageSetting;
    }

    public void Dispose()
    {
        _loc.SetLanguage(_previousLanguage);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void Missing_key_never_throws_and_returns_key_placeholder()
    {
        const string missing = "__tunnelx_missing_key_never_in_tables__";

        _loc.SetLanguage(LocalizationService.EnglishLanguage);
        Assert.Equal(missing, _loc.T(missing));

        _loc.SetLanguage(LocalizationService.RussianLanguage);
        Assert.Equal(missing, _loc.T(missing));

        _loc.SetLanguage(LocalizationService.PersianLanguage);
        Assert.Equal(missing, _loc.T(missing));
    }

    [Fact]
    public void Null_and_empty_source_never_throw()
    {
        _loc.SetLanguage(LocalizationService.EnglishLanguage);
        Assert.Equal(string.Empty, _loc.T(null));
        Assert.Equal(string.Empty, _loc.T(string.Empty));
        Assert.Equal(string.Empty, _loc.Format(null));
        Assert.Equal(string.Empty, _loc.Format(string.Empty, 1));
    }

    [Fact]
    public void Missing_russian_falls_back_to_english_then_persian()
    {
        const string key = "جزئیات";
        var ru = GetLanguageTable(LocalizationService.RussianLanguage);
        Assert.True(ru.Remove(key, out var savedRussian));

        try
        {
            _loc.SetLanguage(LocalizationService.RussianLanguage);
            // EN table still has the key → English first.
            Assert.Equal("Details", _loc.T(key));

            var en = GetLanguageTable(LocalizationService.EnglishLanguage);
            Assert.True(en.Remove(key, out var savedEnglish));
            try
            {
                // No EN either → Persian source key (fa), which is also the final placeholder.
                Assert.Equal(key, _loc.T(key));
            }
            finally
            {
                en[key] = savedEnglish;
            }
        }
        finally
        {
            ru[key] = savedRussian;
        }
    }

    [Fact]
    public void Empty_russian_entry_falls_back_to_english()
    {
        const string key = "برنامه‌ها";
        var ru = GetLanguageTable(LocalizationService.RussianLanguage);
        var saved = ru[key];
        ru[key] = string.Empty;

        try
        {
            _loc.SetLanguage(LocalizationService.RussianLanguage);
            Assert.Equal("Apps", _loc.T(key));
        }
        finally
        {
            ru[key] = saved;
        }
    }

    [Fact]
    public void Format_with_missing_key_never_throws()
    {
        const string missing = "__tunnelx_format_missing_{0}__";
        _loc.SetLanguage(LocalizationService.RussianLanguage);
        Assert.Equal("__tunnelx_format_missing_42__", _loc.Format(missing, 42));
    }

    [Fact]
    public void Format_with_invalid_template_never_throws()
    {
        const string key = "جزئیات";
        var en = GetLanguageTable(LocalizationService.EnglishLanguage);
        var saved = en[key];
        en[key] = "broken {0 {1}";

        try
        {
            _loc.SetLanguage(LocalizationService.EnglishLanguage);
            Assert.Equal("broken {0 {1}", _loc.Format(key, 1, 2));
        }
        finally
        {
            en[key] = saved;
        }
    }

    [Fact]
    public void Auto_language_resolution_unchanged_for_explicit_fa_en_ru()
    {
        _loc.SetLanguage(LocalizationService.AutoLanguage);
        Assert.Equal(LocalizationService.AutoLanguage, _loc.LanguageSetting);

        _loc.SetLanguage("fa");
        Assert.Equal(LocalizationService.PersianLanguage, _loc.LanguageSetting);
        Assert.Equal(LocalizationService.PersianLanguage, _loc.EffectiveLanguage);

        _loc.SetLanguage("en");
        Assert.Equal(LocalizationService.EnglishLanguage, _loc.EffectiveLanguage);

        _loc.SetLanguage("ru");
        Assert.Equal(LocalizationService.RussianLanguage, _loc.EffectiveLanguage);
    }

    [Fact]
    public void Known_ui_keys_resolve_in_english_and_russian()
    {
        const string key = "پروفایل جدید";
        _loc.SetLanguage(LocalizationService.EnglishLanguage);
        Assert.Equal("New profile", _loc.T(key));

        _loc.SetLanguage(LocalizationService.RussianLanguage);
        Assert.Equal("Новый профиль", _loc.T(key));

        _loc.SetLanguage(LocalizationService.PersianLanguage);
        Assert.Equal(key, _loc.T(key));
    }

    private static Dictionary<string, string> GetLanguageTable(string language)
    {
        var field = typeof(LocalizationService).GetField(
            "_translations",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var tables = field!.GetValue(LocalizationService.Instance) as Dictionary<string, Dictionary<string, string>>;
        Assert.NotNull(tables);
        return tables![language];
    }
}
