# Changelog

## Unreleased

## 2.2.2 - 2026-09-30

### English

- When sing-box/Xray TUN fails with Wintun “device is not ready” or “open interface take too much time”, the UI no longer blames a bad profile. It shows a localized TUN/Wintun readiness message (Administrator, stale adapters, antivirus, other Wintun apps, reboot) and logs leftover adapters (`Fixes #67`).
- Pre-connect now inventories Wintun/TunnelX-V2Ray adapters, bounces a leftover TunnelX adapter if one is still present, and retries TUN open once after a short backoff.

### فارسی

<div dir="rtl" align="right">

- اگر sing-box/Xray با خطای Wintun («device is not ready» یا «open interface take too much time») خارج شود، رابط دیگر کانفیگ را مقصر نشان نمی‌دهد؛ پیام محلی‌شده آماده‌نبودن آداپتر TUN و لاگ آداپترهای گیرکرده نمایش داده می‌شود (رفع #67).
- قبل از اتصال، آداپترهای Wintun/TunnelX-V2Ray فهرست می‌شوند؛ آداپتر باقی‌مانده TunnelX در صورت وجود bounce می‌شود و ساخت TUN یک‌بار با تأخیر کوتاه تکرار می‌شود.

</div>

### Русский

- Если sing-box/Xray падает на TUN с «device is not ready» или «open interface take too much time», интерфейс больше не винит конфиг. Показывается локализованное сообщение о готовности TUN/Wintun и в лог пишутся оставшиеся адаптеры (`Fixes #67`).
- Перед подключением TunnelX перечисляет адаптеры Wintun/TunnelX-V2Ray, при необходимости сбрасывает зависший адаптер TunnelX и один раз повторяет открытие TUN после короткой паузы.

## 2.2.1 - 2026-09-30

### English

- Bulk “Test all ping” for ready V2Ray/Xray/Hysteria configs now runs real-delay probes in parallel, with a Settings control for how many tests run at once (default 4, range 1–8). Cancel still stops in-flight tests; each row updates as it finishes.
- Failed real-delay probes no longer show raw `SOCKS5 connect failed` text. Dead configs use the same localized “ping target did not respond” message as other outbound failures.
- Config list rows ellipsize long names and ping/test results so ping/edit/delete stay inside the window (Fixes #68).
- Split mode no longer treats generic WireGuard install errors (`unknown` / `unrecognized` anywhere) as “Table=off unsupported”. That false fallback could install `AllowedIPs 0.0.0.0/0` as a Windows default route while the UI still showed Split (#60).
- While Split is selected, TunnelX watches `0.0.0.0/0` on the VPN interface (OpenVPN IPCP, WireGuard, TAP/Wintun) and strips it. A banner, header chip, and tray label show Full Route when you turn it on, and a warning if a system-wide default route appears unexpectedly. Logs include `[ROUTE]` / `vpnIfDefault` / `defaultGateways` details.

### فارسی

<div dir="rtl" align="right">

- دکمه «تست پینگ همه» برای کانفیگ‌های آماده V2Ray/Xray/Hysteria حالا چند تست تأخیر واقعی را همزمان اجرا می‌کند. تعداد همزمان در تنظیمات قابل انتخاب است (پیش‌فرض ۴، بین ۱ تا ۸). توقف، تست‌های در حال اجرا را قطع می‌کند و نتیجه هر ردیف جداگانه به‌روز می‌شود.
- خطاهای تست واقعی دیگر متن خام `SOCKS5 connect failed` را نشان نمی‌دهند؛ کانفیگ مرده همان پیام «پاسخی از مقصد پینگ نیامد» را می‌گیرد.
- ردیف کانفیگ‌ها نام و نتیجه پینگ/تست طولانی را با ellipsis کوتاه می‌کند تا دکمه‌ها داخل پنجره بمانند (رفع #68).
- در حالت Split، خطاهای عمومی نصب WireGuard دیگر به‌اشتباه «Table=off پشتیبانی نمی‌شود» تفسیر نمی‌شوند؛ آن مسیر می‌توانست `0.0.0.0/0` را روی ویندوز نصب کند در حالی که رابط Splits نشان می‌داد (#60).
- وقتی Split انتخاب است، TunnelX مسیر پیش‌فرض `0.0.0.0/0` روی آداپتر VPN را می‌پاید و برمی‌دارد. بنر، نشان هدر و متن سینی وقتی Full Route روشن است واضح می‌گویند کل سیستم تونل است؛ اگر مسیر سراسری ناخواسته بیاید هشدار می‌دهند. لاگ‌ها جزئیات `[ROUTE]` و `vpnIfDefault` دارند.

</div>

### Русский

- Кнопка «Пинг всех» для готовых конфигов V2Ray/Xray/Hysteria запускает Real Delay параллельно. Число одновременных проверок задаётся в настройках (по умолчанию 4, диапазон 1–8). Отмена останавливает текущие тесты; строки списка обновляются по мере завершения.
- Ошибки Real Delay больше не показывают сырой текст `SOCKS5 connect failed`. Мёртвые конфиги получают то же локализованное «цель пинга не ответила».
- Длинные имена конфигов и строки ping/теста в списке обрезаются с многоточием, чтобы кнопки не выходили за окно (Fixes #68).
- В split-режиме общие ошибки установки WireGuard больше не считаются «Table=off не поддерживается». Ложный fallback мог поставить `AllowedIPs 0.0.0.0/0` как маршрут по умолчанию Windows, пока UI показывал Split (#60).
- Пока выбран Split, TunnelX следит за `0.0.0.0/0` на VPN-интерфейсе и снимает его. Баннер, чип в шапке и текст в трее явно показывают Full Route; при неожиданном системном маршруте появляется предупреждение. В логах есть `[ROUTE]` / `vpnIfDefault`.

## 2.2.0 - 2026-09-26

### English

- Fixed SOCKS5 and HTTP proxy connects that failed while sing-box decoded the config (`outbounds[0].users: unknown field "users"`). Proxy credentials are now written as outbound `username` and `password`.
- Fixed per-app / split-route sessions marking Wi-Fi as "Connected, no internet" after a long run. Windows connectivity probes stay on the physical NIC, and route-table updates no longer run on the packet path that every outbound packet has to pass through.
- Pre-connect ping for V2Ray/Xray configs no longer succeeds just because the server port accepts TCP. Like v2rayN "test real delay", a latency value is shown only when a request through the config reaches the target. A closed connection is a failure, not a successful ping.
- Delete many configs at once from the list, with multi-select, Select all, and one confirmation that states how many will be removed.
- Custom connection health-check targets in Settings (URL, hostname, or IP). With nothing configured, `google.com` and `cloudflare.com` are still used. The default public targets can be turned off.
- Subscription (sub) links: add an http/https URL, import the returned configs as profiles, and refresh them later.
- Russian UI, with Auto, Persian, English, or Russian selectable in Settings.
- Local proxy disconnect and reconnect are announced as `127.0.0.1:port is now disconnected` / `connected`. Active sessions on the built-in listener are reset so clients such as Telethon `run_until_disconnected()` wake up. See `docs/PROXY_LIFECYCLE.md`.
- Upstream HTTP or SOCKS5 proxy for OpenVPN connections. Host, port, and optional username/password are saved on the profile and written into the OpenVPN config as `http-proxy` or `socks-proxy` when connecting.
- Hysteria 1 (`hysteria://`) and Hysteria 2 (`hysteria2://` / `hy2://`) on the bundled sing-box path, including bare outbound JSON wrapping.
- Localization lookup never throws on missing or empty keys: falls back English → Persian → key-name placeholder. Filled remaining EN/RU gaps for RC UI strings (new profile, WireGuard IPv4 Address, VPN adapter ifIdx).
- Language is chosen from a dropdown (Settings and footer): Auto, Persian, English, or Russian — no more multi-press cycle button.
- Settings can store default local SOCKS/HTTP proxy username and password (DPAPI-encrypted). The built-in listener uses them when a profile has no MixedProxy credentials; empty username keeps no-auth.
- In-app Help covers WireGuard, subscription import, pre-connect latency, and local proxy auth defaults; connection troubleshooting mentions WireGuard install; ping tooltips include Hysteria. FA/EN/RU help strings completed for those guides.

- Wider window layouts on large screens, clearer RTL packing for settings rows, and smoother mouse-wheel scrolling (GentleWheelScroll).

### فارسی

<div dir="rtl" align="right">

- اتصال پراکسی SOCKS5 و HTTP که هنگام خواندن کانفیگ sing-box با خطای `unknown field "users"` قطع می‌شد اصلاح شد. نام کاربری و رمز اکنون در فیلدهای خروجی `username` و `password` نوشته می‌شوند.
- رفع مشکلی که در حالت per-app / اسپلیت، بعد از مدتی طولانی وای‌فای را «Connected - No Internet» نشان می‌داد. بررسی اتصال ویندوز روی کارت شبکه فیزیکی می‌ماند و تغییر جدول مسیر دیگر روی مسیر بسته‌ها انجام نمی‌شود.
- پینگ قبل از اتصال برای کانفیگ‌های V2Ray/Xray دیگر با باز بودن پورت سرور موفق حساب نمی‌شود. مثل «test real delay» در v2rayN، عدد پینگ فقط وقتی نشان داده می‌شود که درخواست واقعاً از داخل کانفیگ به مقصد برسد. قطع شدن اتصال به‌جای پینگ موفق ثبت نمی‌شود.
- حذف چند کانفیگ با هم از لیست: تیک چندتایی، انتخاب همه، و یک تأیید که تعداد حذف را می‌گوید.
- مقصدهای سفارشی برای بررسی سلامت اتصال در تنظیمات (URL، دامنه یا IP). اگر چیزی تنظیم نشود، `google.com` و `cloudflare.com` مثل قبل بررسی می‌شوند. می‌توان مقصدهای عمومی پیش‌فرض را خاموش کرد.
- لینک اشتراک (sub): افزودن آدرس http/https، ساخت پروفایل از کانفیگ‌های دریافتی، و به‌روزرسانی بعدی.
- رابط کاربری روسی و انتخاب زبان در تنظیمات: خودکار، فارسی، انگلیسی یا روسی.
- قطع و وصل پراکسی محلی (`127.0.0.1:port`) اعلام می‌شود و اتصال‌های فعال آن ریست می‌شوند تا کلاینت‌هایی مثل Telethon از `run_until_disconnected()` باخبر شوند. جزئیات در `docs/PROXY_LIFECYCLE.md`.
- پراکسی بالادستی HTTP یا SOCKS5 برای اتصال OpenVPN؛ آدرس، پورت و نام کاربری/رمز اختیاری در پروفایل ذخیره می‌شود و هنگام اتصال در کانفیگ OpenVPN نوشته می‌شود.
- پشتیبانی از Hysteria 1 (`hysteria://`) و Hysteria 2 (`hysteria2://` / `hy2://`) روی مسیر sing-box همراه برنامه، شامل wrapping برای JSON outbound خام.
- جستجوی ترجمه دیگر با کلید خالی یا غایب خطا نمی‌دهد؛ ترتیب پشتیبان: انگلیسی → فارسی → نام کلید. کلیدهای EN/RU باقی‌مانده برای رشته‌های RC تکمیل شد.
- انتخاب زبان از لیست بازشو (تنظیمات و فوتر): خودکار، فارسی، انگلیسی یا روسی — دیگر نیازی به چندبار فشردن دکمه چرخش زبان نیست.
- در تنظیمات می‌توان نام کاربری و رمز پیش‌فرض پروکسی محلی SOCKS/HTTP را ذخیره کرد (رمز با DPAPI). اگر پروفایل اعتبارنامه جدا نداشته باشد، هنگام گوش‌دادن همین پیش‌فرض‌ها اعمال می‌شوند؛ نام کاربری خالی یعنی بدون احراز هویت.
- راهنمای داخل برنامه برای WireGuard، اشتراک، تست تأخیر قبل از اتصال و احراز هویت پروکسی محلی تکمیل شد؛ عیب‌یابی اتصال به نصب WireGuard اشاره می‌کند و tooltipهای پینگ Hysteria را هم پوشش می‌دهند. متن‌های راهنما برای FA/EN/RU پر شد.

- پنجره‌های عریض‌تر روی نمایشگرهای بزرگ، چیدمان راست‌به‌چپ مرتب‌تر برای ردیف‌های تنظیمات، و اسکرول نرم‌تر با چرخ ماوس (GentleWheelScroll).

</div>

### Русский

- Исправлены сбои SOCKS5/HTTP при разборе конфига sing-box (`outbounds[0].users: unknown field "users"`). Учётные данные прокси пишутся как `username` и `password` outbound.
- Исправлено ложное «Connected, no internet» для Wi‑Fi при длительных сессиях per-app / split-route. Проверки связности Windows остаются на физическом NIC; обновления таблицы маршрутов больше не выполняются на пути каждого пакета.
- Предварительный ping для V2Ray/Xray больше не считается успешным только из‑за открытого TCP-порта. Как «test real delay» в v2rayN, задержка показывается только если запрос через конфиг доходит до цели. Обрыв соединения — это ошибка, а не успешный ping.
- Удаление нескольких конфигов сразу: мультивыбор, «Выбрать все» и одно подтверждение с числом удаляемых.
- Пользовательские цели проверки здоровья соединения в настройках (URL, хост или IP). Без настроек по-прежнему используются `google.com` и `cloudflare.com`. Публичные цели по умолчанию можно отключить.
- Subscription (sub) ссылки: добавить http/https URL, импортировать конфиги как профили и обновлять позже.
- Русский интерфейс; в настройках можно выбрать Auto, персидский, английский или русский.
- Отключение и повторное подключение локального прокси объявляются как `127.0.0.1:port is now disconnected` / `connected`. Активные сессии на встроенном listener сбрасываются, чтобы клиенты вроде Telethon `run_until_disconnected()` просыпались. См. `docs/PROXY_LIFECYCLE.md`.
- Upstream HTTP или SOCKS5 прокси для OpenVPN. Хост, порт и опциональные логин/пароль сохраняются в профиле и записываются в конфиг OpenVPN как `http-proxy` или `socks-proxy` при подключении.
- Hysteria 1 (`hysteria://`) и Hysteria 2 (`hysteria2://` / `hy2://`) на встроенном пути sing-box, включая обёртку сырого outbound JSON.
- Поиск локализации не падает на отсутствующих или пустых ключах: запасной путь English → Persian → имя ключа. Дозаполнены оставшиеся EN/RU строки RC UI.
- Язык выбирается из выпадающего списка (Настройки и футер): Auto, персидский, английский или русский — без многократного нажатия кнопки цикла.
- В настройках можно сохранить логин/пароль локального SOCKS/HTTP прокси по умолчанию (шифрование DPAPI). Встроенный listener использует их, если у профиля нет MixedProxy credentials; пустой логин означает без аутентификации.
- Встроенная справка охватывает WireGuard, импорт подписки, latency перед подключением и defaults auth локального прокси; troubleshooting упоминает установку WireGuard; подсказки ping включают Hysteria. Строки FA/EN/RU для этих разделов заполнены.
- UX: более широкие окна на больших экранах, аккуратнее RTL-ряды настроек, мягкая прокрутка колёсиком (GentleWheelScroll).

## 2.1.2 - 2026-05-30

### فارسی

<div dir="rtl" align="right">

#### قابلیت‌های جدید
- افزودن سریع کانفیگ از کلیپ‌بورد با Ctrl+V
- تست تأخیر و پینگ قبل از اتصال برای مقایسه سرورها
- پنجره راهنما هنگام خطای اتصال با توضیح ساده‌تر
- پینگ سرور در حالت متصل
- نمایش مصرف ترافیک هر برنامه هنگام اتصال (ارسال و دریافت)
- مرتب‌سازی خودکار برنامه‌ها بر اساس بیشترین مصرف

#### رفع باگ‌ها
- اصلاح جهت و چیدمان متن در رابط دوزبانه
- نمایش درست پنجره برنامه پس از اجرا
- پیام‌های خطای اتصال واضح‌تر و کوتاه‌تر
- بهبود پایداری اتصال OpenVPN و V2Ray

#### بهبودها
- بازطراحی تب اتصال و لیست کانفیگ‌ها
- نمایش خواناتر حجم ترافیک (KB، MB، GB)
- نمایش مجموع مصرف برنامه‌های تونل و سایر ترافیک تونل
- روانی و سرعت بیشتر رابط کاربری
- گسترش متن‌های فارسی و انگلیسی

</div>

### English

#### Added
- Quick config import from clipboard with Ctrl+V
- Latency and ping test before connecting to compare servers
- Helpful connection error dialog with clearer guidance
- Server ping while connected
- Per-app tunnel traffic while connected (upload and download)
- Apps automatically sorted by highest usage

#### Fixed
- Text direction and layout in the bilingual interface
- Window visibility on startup
- Clearer, shorter connection error messages
- Better OpenVPN and V2Ray connection stability

#### Improved
- Redesigned connection tab and config list
- Clearer traffic size display (KB, MB, GB)
- Separate totals for tunnel apps and other tunnel traffic
- Smoother, faster user interface
- Expanded Persian and English translations

## 2.1.1 - 2026-05-22

### فارسی

<div dir="rtl" align="right">

- اگر مطمئن نیستید، فایل EXE مستقل را از صفحه Releases دانلود کنید؛ این نسخه به نصب جداگانه .NET نیاز ندارد.
- فایل ZIP فقط برای کاربرانی است که .NET 8 Desktop Runtime نسخه x64 را از قبل نصب کرده‌اند.
- بررسی سلامت اتصال دقیق‌تر شد: صفحه «متصل» فقط وقتی نمایش داده می‌شود که دسترسی اینترنت از داخل تونل واقعا تایید شده باشد.
- پیام خطا برای سرورهای بسته یا کند شفاف‌تر شد؛ سرور کند دیگر زودتر از موعد ناموفق حساب نمی‌شود.
- پرچم کشور IP خروجی از مسیر تونل بارگذاری می‌شود و در صورت خطا دوباره تلاش می‌کند.
- پشتیبانی از لینک‌های رایج V2Ray، مخصوصا VMess و VLESS روی WebSocket، بهتر شد.
- چند ناهماهنگی راست‌به‌چپ و چپ‌به‌راست در تب اتصال و ویرایش پروفایل اصلاح شد.

</div>

### English

- If you are not sure which file to use, download the standalone EXE from Releases. It does not need a separate .NET installation.
- Use the ZIP package only if .NET 8 Desktop Runtime x64 is already installed on your PC.
- Connection health checks are stricter: the connected screen appears only after internet access through the tunnel is verified.
- Error messages now better distinguish closed servers from slow servers, so slow servers are not rejected too early.
- The exit IP country flag is loaded through the tunnel and retries automatically when a source fails.
- Common V2Ray links work better, especially VMess and VLESS over WebSocket.
- Several RTL/LTR layout issues were fixed in the connection tab and profile editor.

## 2.1.0 - 2026-05-21

### English

- **Connection health check** before showing the connected screen: end-to-end TCP probes to `google.com` and `cloudflare.com` through the tunnel SOCKS path; expired or quota-exhausted configs no longer appear as “connected” when only the local adapter is up.
- Live **ping results** during the health-check step (per-host latency in the connection progress UI).
- **OpenVPN** split-config improvements: stable remote port ordering (443/80 before 21/53), preserve `<connection>` blocks and `tcp-client` for multi-connection profiles, skip unresolvable remote hostnames, tuned `connect-retry` / timeouts.
- **OpenVPN disconnect insight**: clearer messages when control channel resets or auth fails after unstable sessions.
- **OpenVPN profile analyzer** refactor; validation and credential handling centralized.
- Connected dashboard: **exit IP country flag** (PNG from [flagcdn.com](https://flagcdn.com) via tunnel), geo lookup via tunnel (`ip-api.com`, `ipwho.is`, `ipapi.co`), release-notes window, scheduled update check after connect.
- Tray notifications: richer error guidance, optional **release notes** action on update cards, improved dismiss/overflow layout.
- Routing/DNS: WireGuard ingress wait before health probe; connectivity checks and INCLUDE refresh stability tweaks.
- Expanded **Persian/English** strings for health check, OpenVPN, notifications, and profile editor.

### فارسی

<div dir="rtl" align="right">

- **بررسی سلامت اتصال** قبل از نمایش صفحه متصل: پینگ TCP واقعی به `google.com` و `cloudflare.com` از مسیر SOCKS تونل؛ کانفیگ منقضی یا تمام‌شده دیگر فقط با بالا آمدن آداپتر «متصل» نشان داده نمی‌شود.
- نمایش **نتیجه پینگ** در مرحله بررسی سلامت (تأخیر هر مقصد در UI مراحل اتصال).
- بهبود کانفیگ split **OpenVPN**: اولویت پورت پایدار (۴۴۳/۸۰ قبل از ۲۱/۵۳)، حفظ بلوک‌های `<connection>` و `tcp-client` برای پروفایل‌های چند اتصال، رد hostnameهای غیرقابل resolve، تنظیم `connect-retry` و timeout.
- **تحلیل قطع OpenVPN**: پیام‌های واضح‌تر هنگام reset کانال کنترل یا خطای auth پس از ناپایداری.
- بازآرایی **تحلیلگر پروفایل OpenVPN**؛ اعتبارسنجی و مدیریت اعتبارنامه متمرکز شد.
- داشبورد متصل: **پرچم کشور IP خروجی** (تصویر PNG از [flagcdn.com](https://flagcdn.com) از داخل تونل)، geo از داخل تونل (`ip-api.com`، `ipwho.is`، `ipapi.co`)، پنجره یادداشت انتشار، بررسی بروزرسانی زمان‌بندی‌شده پس از اتصال.
- اعلان tray: راهنمای بهتر برای خطا، دکمه **یادداشت انتشار** روی کارت بروزرسانی، چیدمان dismiss/overflow بهتر.
- مسیریابی/DNS: انتظار برای ingress در WireGuard قبل از probe؛ بهبودهای پایداری connectivity check و INCLUDE.
- گسترش متن‌های **فارسی/انگلیسی** برای بررسی سلامت، OpenVPN، اعلان‌ها و ویرایشگر پروفایل.

</div>

## 2.0.1 - 2026-05-21

- fix: OpenVPN reliability, secret field, and notification controls

## 2.0.0 - 2026-05-20

### English

- Added **WireGuard** profile support (single-peer `.conf` via sing-box) alongside L2TP, V2Ray/Xray, OpenVPN, and SOCKS/HTTP proxy modes.
- Added **Windows tray notifications** (Telegram-style cards): connection errors with guidance, new-version alerts, and optional Telegram channel promo after a successful connect.
- Added a **connection progress** flow with step-by-step status while connecting or canceling.
- Redesigned the **Help** tab: project/update and support cards side by side, bilingual copy, crypto wallet list with per-address copy, and clearer update-status localization.
- Improved the **connected dashboard**: route ping field UX (I-beam cursor, compact Ping button), traffic/health cards, and footer polish.
- Expanded **Persian/English UI** coverage (notifications, Help, footer, donation dialog) and fixed update-status text not switching language correctly.
- Routing/DNS/engine work: app discovery refresh, split-tunnel rule learning, WireGuard/TunnelX host lifecycle cleanup, and related stability fixes.
- Donation: PayPal support via personal-account link (`_xclick`, optional PayPal.Me) to `gallafan@gmail.com` (supporter enters amount on PayPal).

### فارسی

<div dir="rtl" align="right">

- پشتیبانی **WireGuard** (فایل `.conf` تک-peer از مسیر sing-box) به‌همراه L2TP، V2Ray/Xray، OpenVPN و پراکسی SOCKS/HTTP اضافه شد.
- **اعلان‌های tray ویندوز** (کارت شبیه تلگرام): خطای اتصال با راهنما، اعلان نسخه جدید، و پیشنهاد عضویت کانال تلگرام پس از اتصال موفق.
- **پیشرفت مرحله‌ای اتصال** هنگام وصل شدن یا لغو اتصال اضافه شد.
- تب **راهنما** بازطراحی شد: کارت پروژه/بروزرسانی و حمایت در دو ستون، متن دوزبانه، لیست کیف پول رمزارز با دکمه کپی، و رفع ناهماهنگی وضعیت بروزرسانی بین زبان‌ها.
- **داشبورد حالت متصل** بهبود یافت: فیلد پینگ مسیر (نشانگر تایپ، دکمه فشرده Ping)، کارت‌های ترافیک/سلامت، و فوتر مرتب‌تر.
- پوشش **فارسی/انگلیسی** گسترده‌تر (ناتیف، راهنما، فوتر، دیالوگ حمایت) و اصلاح متن «به‌روز است» هنگام تعویض زبان.
- بهبودهای مسیریابی/DNS/موتور: بروزرسانی لیست برنامه‌ها، یادگیری قوانین DNS، پاک‌سازی فرایند TunnelX/WireGuard و رفع پایداری مرتبط.
- حمایت مالی: پی‌پل با لینک حساب شخصی (`_xclick` و PayPal.Me اختیاری) به `gallafan@gmail.com` (حامی مبلغ را در PayPal وارد می‌کند).

</div>

## 1.2.32 - 2026-05-18

### English

- Fixed VLESS REALITY configs in the sing-box path by enabling uTLS with the configured fingerprint, preventing startup failures that reported `uTLS is required by reality client`.
- Replaced icon-only log-panel actions with compact localized text buttons for clearing logs, copying the latest error, and copying all logs.

### فارسی

<div dir="rtl" align="right">

- مشکل کانفیگ‌های VLESS REALITY در مسیر sing-box اصلاح شد؛ uTLS با fingerprint کانفیگ فعال می‌شود تا خطای `uTLS is required by reality client` هنگام شروع اتصال رخ ندهد.
- دکمه‌های فقط‌آیکونی پنل لاگ با دکمه‌های متنی و فشرده جایگزین شدند تا پاک کردن لاگ، کپی آخرین خطا و کپی همه لاگ‌ها در فارسی و انگلیسی واضح باشد.

</div>

## 1.2.31 - 2026-05-18

- Update release packaging and connection UX
- Update README contact and localization notes

## 1.2.30 - 2026-05-18

### English

- Added bilingual Persian/English UI switching with automatic system-language detection, persisted language selection, RTL/LTR layout handling, and localized dialogs, tray text, runtime status messages, help content, and profile/app/routing views.
- Refined the desktop UI with adaptive window sizing, disabled maximize/system-menu fullscreen paths, smoother log-panel animation, polished dialogs, refreshed TunnelX branding, updated app icon, compact app-list rows, footer donation modal, and header update notification.
- Improved V2Ray/Xray reliability by dynamically reserving free local ports for Xray SOCKS and sing-box mixed proxy inbounds instead of relying on fixed `2080/2081` ports.
- Improved connection UX with more reliable exit-IP detection retries, consistent text-field behavior, better log window direction/localization, and localized README screenshots for Persian and English documentation.

### فارسی

<div dir="rtl" align="right">

- تغییر زبان فارسی/انگلیسی به برنامه اضافه شد؛ شامل تشخیص زبان سیستم، ذخیره زبان انتخاب‌شده، رعایت RTL/LTR، و ترجمه دیالوگ‌ها، متن tray، وضعیت‌های runtime، راهنما، پروفایل‌ها، برنامه‌ها و قوانین مسیر.
- رابط کاربری دسکتاپ بهبود یافت؛ شامل اندازه‌گیری تطبیقی پنجره، جلوگیری از maximize/fullscreen و منوی Alt+Space، انیمیشن نرم پنل لاگ، دیالوگ‌های تمیزتر، برندینگ و آیکون جدید TunnelX، ردیف‌های فشرده‌تر برنامه‌ها، مودال حمایت مالی و دکمه اعلان بروزرسانی در هدر.
- پایداری V2Ray/Xray بهتر شد؛ پورت‌های داخلی Xray SOCKS و sing-box mixed proxy دیگر ثابت نیستند و به‌صورت آزاد از سیستم رزرو می‌شوند تا خطای اشغال بودن `2080/2081` تکرار نشود.
- تجربه اتصال بهتر شد؛ شامل تلاش دوباره برای دریافت IP خروجی، رفتار یکدست فیلدهای متنی، جهت و ترجمه بهتر پنجره لاگ، و استفاده از اسکرین‌شات‌های فارسی/انگلیسی در READMEهای مربوطه.

</div>

## 1.2.29 - 2026-05-17

### English

- Expanded the GitHub README with Russian and Simplified Chinese summaries for international users.
- Expanded the in-app Persian Help tab with fuller guidance for profiles, connection types, routing rules, logs, updates, and troubleshooting.

### فارسی

<div dir="rtl" align="right">

- توضیح‌های روسی و چینی ساده‌شده به README گیت‌هاب اضافه شد تا کاربران بین‌المللی سریع‌تر با کاربرد برنامه آشنا شوند.
- تب راهنمای فارسی داخل برنامه با توضیح کامل‌تر درباره پروفایل‌ها، نوع‌های اتصال، قوانین مسیر، لاگ‌ها، بروزرسانی و عیب‌یابی گسترش پیدا کرد.

</div>

## 1.2.28 - 2026-05-17

### English

- Fixed Full Route default-route installation by preferring the VPN gateway, retrying with an on-link gateway when needed, and cleaning up the pinned physical route to the tunnel server when Full Route is disabled.
- Updated English/Persian README and in-app Help content for the new SOCKS/Proxy profile flow, connection types, routing notes, local data, and troubleshooting guidance.

### فارسی

<div dir="rtl" align="right">

- نصب default route در حالت Full Route اصلاح شد؛ ابتدا gateway تونل استفاده می‌شود، در صورت نیاز با gateway روی‌لینک دوباره تلاش می‌شود، و route فیزیکی ثابت‌شده برای سرور تونل هنگام خاموش شدن Full Route پاک‌سازی می‌شود.
- README فارسی/انگلیسی و محتوای راهنمای داخل برنامه برای جریان جدید SOCKS/Proxy، نوع‌های اتصال، نکته‌های مسیر، داده‌های محلی و عیب‌یابی به‌روز شد.

</div>

## 1.2.27 - 2026-05-17

### English

- Added a dedicated SOCKS5/HTTP Proxy profile type with separate server, port, username, and password fields, encrypted proxy password persistence, validation hints, and proxy-specific connection handling through sing-box.
- Reworked profile management into a compact profile list with separate add/edit dialogs, clearer active profile selection, and improved Persian-first profile cards.
- Improved the connected dashboard with public exit IP detection, shorter ping results, clearer tunnel/direct traffic cards, manual proxy guidance, full-route controls, and a dedicated disconnect action.
- Refined Persian font rendering, global WPF text settings, tab headers, footer, connection controls, route rules, app selection, history, and traffic views for a cleaner desktop UI.
- Improved routing diagnostics and split-tunnel handling for V2Ray, SOCKS/Proxy, OpenVPN, include/exclude destination rules, DNS rule learning, and tunnel server health checks.
- Fixed OpenVPN internal reconnect handling by detecting runtime tunnel IP, gateway, interface, or remote endpoint changes and restarting TunnelX packet routing with the new values.

### فارسی

<div dir="rtl" align="right">

- نوع پروفایل اختصاصی SOCKS5/HTTP Proxy اضافه شد؛ شامل فیلدهای جداگانه سرور، پورت، نام کاربری و رمز عبور، ذخیره امن رمز پراکسی، راهنمای اعتبارسنجی و اتصال از طریق sing-box.
- مدیریت پروفایل‌ها به لیست فشرده کانفیگ‌ها با پنجره جدا برای افزودن/ویرایش، انتخاب واضح پروفایل فعال و کارت‌های فارسی‌محور بهتر بازطراحی شد.
- داشبورد بعد از اتصال بهبود یافت؛ نمایش IP خروجی عمومی، نتیجه کوتاه پینگ، کارت‌های واضح‌تر مصرف تونل/خارج تونل، راهنمای پراکسی دستی، کنترل Full Route و دکمه اختصاصی قطع اتصال اضافه شد.
- رندر فونت فارسی، تنظیمات عمومی متن در WPF، تب‌ها، فوتر، کنترل‌های اتصال، قوانین مسیر، انتخاب برنامه‌ها، تاریخچه و نمای مصرف ترافیک برای رابط کاربری تمیزتر اصلاح شد.
- عیب‌یابی مسیر و Split Tunneling برای V2Ray، SOCKS/Proxy، OpenVPN، قوانین include/exclude، یادگیری قوانین DNS و health check سرور تونل بهبود پیدا کرد.
- مشکل reconnect داخلی OpenVPN اصلاح شد؛ اگر هنگام اتصال طولانی IP تونل، gateway، interface یا سرور مقصد عوض شود، TunnelX مسیر‌دهی ترافیک را با مقادیر جدید دوباره راه‌اندازی می‌کند.

</div>

## 1.2.26 - 2026-05-17

### English

- Added OpenVPN Community support as an external tunnel provider for split tunneling.
- Added `.ovpn` file selection, OpenVPN username/password fields, install detection, and clearer Persian guidance in the connection and help screens.
- Added split-compatible OpenVPN config preparation with route/DNS push filtering, credential file handling without UTF-8 BOM, remote candidate filtering, and faster retry behavior.
- Fixed OpenVPN split routing by capturing the real connected remote, assigned tunnel IP, and route gateway before starting packet routing.
- Added OpenVPN stale-process cleanup for TunnelX-started OpenVPN processes and prevented stale TAP adapters from being treated as a fresh connection.
- Improved server testing and post-connect ping behavior for OpenVPN profiles.

### فارسی

<div dir="rtl" align="right">

- پشتیبانی از OpenVPN Community به‌عنوان ارائه‌دهنده خارجی تونل برای Split Tunneling اضافه شد.
- انتخاب فایل `.ovpn`، فیلدهای نام کاربری و رمز عبور OpenVPN، تشخیص نصب بودن OpenVPN Community و راهنمای فارسی واضح‌تر در صفحه اتصال و راهنما اضافه شد.
- آماده‌سازی کانفیگ OpenVPN سازگار با Split Tunnel اضافه شد؛ شامل نادیده گرفتن route/DNSهای push شده، ذخیره فایل credential بدون UTF-8 BOM، فیلتر کردن remoteهای نامعتبر و retry سریع‌تر.
- مسیر‌دهی Split Tunnel در OpenVPN با ثبت remote واقعی متصل‌شده، IP اختصاص داده‌شده به تونل و route gateway قبل از شروع packet routing اصلاح شد.
- پاک‌سازی پردازش‌های قدیمی OpenVPN که توسط TunnelX اجرا شده‌اند اضافه شد و از شناسایی آداپترهای TAP خراب یا قدیمی به‌عنوان اتصال جدید جلوگیری شد.
- تست سرور و پینگ بعد از اتصال برای پروفایل‌های OpenVPN بهبود پیدا کرد.

</div>

## 1.2.25 - 2026-05-16

- Merge pull request #13 from BlacKSnowDot0/pr-clean
- Merge pull request #16 from mohammad-parvizi-dev/main
- Improve tab headers, theme styling, and tray notifications
- Add startup and auto-connect app settings
- feat(proxy): SOCKS5/HTTP via V2Ray/sing-box, add MixedProxyServer, remove standalone proxy types and local auth

## 1.2.24 - 2026-05-12

- Added README screenshots in English and Persian.
- Added automated GitHub Actions release publishing with version bumping, changelog-based release notes, checksums, and build provenance.

## 1.2.23

- Added GitHub release checking from the Help tab.
- Added automatic tray notification when a newer release is available.
- Added tray notifications for connection, disconnection, and connection errors.
- Added a tray menu action for checking updates.
- Moved remaining future VPN-manager improvements into the public roadmap.

## 1.2.22

- Fixed Help page data binding so GitHub and donation buttons work.
- Localized Help page action buttons.
- Added in-app copy action for donation information.
- Removed internal privacy review and publishing checklist documents from the public repository history.

## 1.2.21

- Prepared open-source repository documentation and release guidance.
- Added in-app GitHub and donation links.
- Added project metadata for MaxFan and GPL-3.0-or-later licensing.
- Improved leak logging and traffic accounting in recent internal builds.












