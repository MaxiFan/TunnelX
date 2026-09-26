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
    public void SetLanguage_persists_setting_codes_for_all_options()
    {
        foreach (var code in new[]
                 {
                     LocalizationService.AutoLanguage,
                     LocalizationService.PersianLanguage,
                     LocalizationService.EnglishLanguage,
                     LocalizationService.RussianLanguage
                 })
        {
            _loc.SetLanguage(code);
            Assert.Equal(code, _loc.LanguageSetting);
        }
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

    [Theory]
    [InlineData("کانفیگ بسازید، از لینک اشتراک بگیرید، یا از کلیپ‌بورد پیست کنید.", "Create a config, import a subscription link, or paste from the clipboard.", "Создайте конфиг, импортируйте ссылку подписки или вставьте из буфера обмена.")]
    [InlineData("پروفایل فعال، اشتراک (sub)، تست تأخیر قبل از اتصال، اتصال/قطع، IP خروجی، پینگ، مصرف و راهنمای پراکسی دستی اینجاست.", "Active profile, subscription (sub), pre-connect latency test, connect/disconnect, exit IP, ping, usage, and manual proxy guidance are here.", "Здесь активный профиль, подписка (sub), проверка задержки до подключения, подключение и отключение, выходной IP, пинг, расход и подсказка по ручному прокси.")]
    [InlineData("TunnelX کانفیگ WireGuard را با WireGuard رسمی ویندوز به‌صورت adapter واقعی اجرا می‌کند و سپس اسپلیت‌تانلینگ برنامه‌ها را مثل OpenVPN/L2TP مدیریت می‌کند. نسخه فعلی فقط کانفیگ تک-peer را پشتیبانی می‌کند.", "TunnelX runs WireGuard configs through the official WireGuard for Windows adapter, then manages app split-tunneling like OpenVPN/L2TP. The current version supports single-peer configs only.", "TunnelX запускает конфиг WireGuard через официальный адаптер WireGuard для Windows, а затем управляет раздельным туннелированием приложений так же, как для OpenVPN/L2TP. Сейчас поддерживаются только конфиги с одним peer.")]
    [InlineData("برنامه را با Administrator اجرا کنید. فایروال، آنتی‌ویروس، آدرس سرور، پورت، رمزها، PSK، نصب OpenVPN Community یا WireGuard رسمی ویندوز، و اعتبار کانفیگ را بررسی کنید.", "Run the app as Administrator. Check firewall, antivirus, server address, port, credentials, PSK, OpenVPN Community or official WireGuard for Windows installation, and config validity.", "Запустите программу от имени администратора. Проверьте брандмауэр, антивирус, адрес сервера, порт, учётные данные, PSK, установку OpenVPN Community или официального WireGuard для Windows и сам конфиг.")]
    [InlineData("پینگ = تأخیر واقعی V2Ray/Xray/Hysteria، یا رسیدن به سرور برای بقیه. دکمه سرور فقط IP/پورت است و سالم بودن کانفیگ را نشان نمی‌دهد.", "Ping = real delay for V2Ray/Xray/Hysteria, or server reachability otherwise. The server button only checks IP/port and does not mean the config works.", "Пинг = реальная задержка V2Ray/Xray/Hysteria или доступность сервера для остальных. Кнопка сервера проверяет только IP/порт и не означает, что конфиг работает.")]
    [InlineData("پورت پراکسی محلی، نام کاربری/رمز پیش‌فرض پروکسی محلی، مقصدهای بررسی سلامت اتصال، MTU خودکار، DNS Optimization، Game Mode، اعلان‌های وضعیت، اجرای خودکار ویندوز و اتصال خودکار اینجاست.", "Local proxy port, default local proxy username/password, connection health-check targets, automatic MTU, DNS Optimization, Game Mode, status notifications, Windows startup, and auto-connect are here.", "Здесь локальный порт прокси, имя и пароль локального прокси по умолчанию, цели проверки состояния соединения, автоматический MTU, DNS Optimization, Game Mode, уведомления, автозапуск Windows и автоподключение.")]
    public void Help_guide_keys_resolve_in_english_and_russian(string fa, string en, string ru)
    {
        _loc.SetLanguage(LocalizationService.EnglishLanguage);
        Assert.Equal(en, _loc.T(fa));

        _loc.SetLanguage(LocalizationService.RussianLanguage);
        Assert.Equal(ru, _loc.T(fa));

        _loc.SetLanguage(LocalizationService.PersianLanguage);
        Assert.Equal(fa, _loc.T(fa));
    }

    [Fact]
    public void Russian_translations_do_not_contain_persian_letters()
    {
        var ru = GetLanguageTable(LocalizationService.RussianLanguage);
        foreach (var (key, value) in ru)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                Assert.False(
                    c is >= '\u0600' and <= '\u06FF',
                    $"Russian value for '{key[..Math.Min(40, key.Length)]}' contains Persian letter U+{(int)c:X4} at index {i}");
            }
        }
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
