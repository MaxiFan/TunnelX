# TunnelX

[فارسی](README.fa.md) · [English](#english) · [Русский](README.ru.md) · [简体中文](README.zh.md)

<div dir="rtl" align="right">

## فارسی

<span dir="ltr">**TunnelX**</span> یک نرم‌افزار آزاد و رایگان برای ویندوز است که توسط **<span dir="ltr">MaxFan</span>** ساخته شده و برای مدیریت تونل، وی‌پی‌ان و <span dir="ltr">Split Tunneling</span> استفاده می‌شود. ترافیک برنامه‌های انتخاب‌شده، مقصدهای مشخص یا کل سیستم را از تونل عبور می‌دهد و مسیر عادی شبکه را برای مقصدهای محلی یا مستثنی‌شده حفظ می‌کند. رابط دو‌زبانه فارسی/انگلیسی با تشخیص خودکار زبان و چینش <span dir="ltr">RTL/LTR</span> دارد.

**[مستند کامل فارسی ←](README.fa.md)**

</div>

## English

**TunnelX** is a free and open-source Windows split-tunneling client built by **MaxFan**. It routes selected apps, selected destinations, or the whole system through supported tunnel cores while keeping local and excluded destinations on the normal network path. The app supports Persian and English UI modes with automatic system-language detection and correct RTL/LTR layout handling.

*Full English documentation continues below.*

## Русский

**TunnelX** — бесплатный клиент с открытым исходным кодом для Windows (split tunneling) от **MaxFan**. Он направляет через VPN, V2Ray/Xray, OpenVPN или SOCKS5/HTTP Proxy только выбранные приложения, домены/IP или весь системный трафик, сохраняя обычный маршрут для локальных и исключённых назначений. Интерфейс поддерживает персидский и английский языки с автоматическим выбором языка системы и корректным RTL/LTR.

**[Полная документация на русском →](README.ru.md)**

## 简体中文

**TunnelX** 是由 **MaxFan** 构建的免费开源 Windows 分流隧道客户端。它可让选定应用、指定域名/IP 或整个系统通过 VPN、V2Ray/Xray、OpenVPN 或 SOCKS5/HTTP Proxy，同时让本地或排除的目标继续走普通网络。应用支持波斯语和英语界面，可自动检测系统语言并正确处理 RTL/LTR 布局。

**[完整中文文档 →](README.zh.md)**

## Stay Updated on Telegram

Join the official TunnelX Telegram channel for release announcements, update alerts, and project news:

**[Join @tunnelxx on Telegram](https://t.me/tunnelxx)**

If Telegram is installed on Windows, the in-app button opens the channel directly in the Telegram app.

## Features

- App-based split tunneling for selected Windows processes
- Full-route mode for whole-system tunneling
- Windows L2TP/IPsec profile support
- Xray-core / sing-box based V2Ray workflows
- Dedicated SOCKS5/HTTP Proxy profiles with separate server, port, username, and password fields
- OpenVPN Community support via user-provided `.ovpn` files for app-based split tunneling
- WireGuard support via user-provided single-peer `.conf` files and sing-box
- Local SOCKS5 proxy for tools that need `127.0.0.1`
- DNS redirect, IPv6 blocking, leak guard, route diagnostics, and traffic history
- Multiple profiles, duplicate/edit flows, server tests, public exit IP detection, and release update checks
- **Connection health check** before the connected screen: end-to-end TCP probes to `google.com` and `cloudflare.com` through the tunnel SOCKS path, with live per-host latency during the verify step
- Connected dashboard: **exit IP** with country name and flag image (geo and flag PNG fetched through the tunnel, not direct from the local network)
- Windows tray notifications with clearer error guidance, optional release-notes action on update cards, and scheduled update check after connect
- Persian and English desktop UI with automatic language detection, manual language switching, and correct RTL/LTR layout behavior
- Dynamic local port selection for V2Ray/Xray internals to reduce `2080/2081` binding conflicts

## Quick Start

1. Download the latest standalone release from GitHub Releases.
2. Run TunnelX as Administrator. Route management, WinDivert, and packet interception require elevated privileges.
3. Create a new profile or select an existing profile from the connection tab.
4. Choose the connection type: L2TP/IPsec, V2Ray/Xray, SOCKS5/HTTP Proxy, OpenVPN, or WireGuard.
5. Test the server, then enable the Windows apps that should use the tunnel.
6. Add include or exclude destinations when needed, connect, and check the traffic health cards for DNS, IPv6, leaks, and route status.

After you connect, TunnelX runs a short **health verify** step (adapter/route checks plus real tunnel probes). The connected dashboard appears only when at least one end-to-end probe succeeds. Expired or quota-exhausted proxy configs should fail here instead of showing a false “connected” state.

## Exit IP and Country Flag

While connected, TunnelX shows your public exit IP on the dashboard. Country name and flag image are optional enrichments:

- Exit IP is queried through the local SOCKS/mixed proxy so the request exits via the tunnel (`ipv4.icanhazip.com`, `api.ipify.org`, `ifconfig.me`).
- Country lookup uses fallback APIs through the same tunnel path: `ip-api.com`, `ipwho.is`, `ipapi.co`.
- The flag is a small PNG from `flagcdn.com` (`/h20/{country-code}.png`), also downloaded through the tunnel.

These calls are not analytics sent to the TunnelX maintainer; they are on-demand lookups initiated by the app. See `docs/PRIVACY.md` for details.

## Connection Types

### L2TP/IPsec

Enter the server address, username, password, and pre-shared key. TunnelX creates the Windows VPN connection and manages routes according to the selected-app policy or full-route mode.

### V2Ray / Xray

Paste a V2Ray/Xray link or JSON config into the profile. TunnelX uses sing-box for regular configs and switches to Xray-core for configs that require Xray-specific behavior such as `xhttp`.

### SOCKS5/HTTP Proxy

Use a SOCKS5/HTTP Proxy profile when you already have an external proxy endpoint. Enter the proxy server, port, and optional credentials. This is different from the local `127.0.0.1` SOCKS5 proxy, which is exposed after connection for tools that need a local proxy address.

### WireGuard

Select a standard WireGuard `.conf` file or paste its contents into the profile. TunnelX runs WireGuard through sing-box and keeps Windows routing under TunnelX control, so app-based split tunneling, include/exclude rules, DNS redirect, IPv6 leak guard, and full-route mode work through the existing routing engine.

The first WireGuard implementation supports one `[Peer]` section per profile. UDP endpoint tests are best-effort diagnostics, not a guaranteed handshake check, and private keys are stored locally with the profile data.

## OpenVPN

TunnelX can run an installed **OpenVPN Community** `openvpn.exe` with a user-selected `.ovpn` profile, then apply its own split-tunneling policy so only selected apps and included destinations use the OpenVPN tunnel.

OpenVPN is not bundled with TunnelX. Install OpenVPN Community separately, select the `.ovpn` file in TunnelX, and enter the OpenVPN username/password if the server requires credentials. OpenVPN Connect alone is not enough for this mode because it manages routes and DNS through its own client.

For split-tunnel compatibility, TunnelX prepares the OpenVPN config by controlling pushed route and DNS behavior. Recent builds improve stability for multi-`<connection>` profiles: stable remote port ordering (443/80 before 21/53), preserved `tcp-client` blocks, skipping unresolvable remote hostnames, and clearer disconnect insight when the control channel resets. If OpenVPN reconnects and changes the tunnel IP, gateway, interface, or remote endpoint, TunnelX restarts its packet routing with the new values.

To reach the OpenVPN server through another proxy, open the OpenVPN profile and set **Upstream proxy** to HTTP or SOCKS5, then enter the proxy address, port, and username/password if the proxy requires them. TunnelX saves those fields with the profile and writes OpenVPN `http-proxy` / `socks-proxy` (and `http-proxy-retry` / `socks-proxy-retry`) into the prepared config. Leave the type as **No proxy** to connect directly. OpenVPN can use this only when the `.ovpn` profile is TCP (`proto tcp` or `tcp-client`); a UDP-only profile shows an error instead of connecting. If the `.ovpn` already contains `http-proxy` or `socks-proxy`, choose the file again and TunnelX copies the host and port into the profile. A separate auth file path is not read automatically — enter the proxy username and password in TunnelX. Credentials are stored with the profile using the same Windows DPAPI protection as other passwords.

## Routing Notes

Destination include/exclude rules match both the entered domain and its subdomains. For example, adding `githubusercontent.com` also covers `raw.githubusercontent.com` after DNS resolves it. Some HTTPS clients may still fail during certificate revocation checks if their OCSP/CRL hosts are not reachable through the selected route; add the downloader app or the relevant revocation domains to the include list when that happens.

- Excluded destinations stay direct even for selected apps.
- Included destinations use the tunnel even when the matching app is not selected.
- For Store/MSIX, WebView2, or multi-process apps, keep the app open and refresh the app list.
- In full-route mode, the whole system uses the tunnel; direct/exclude rules are still useful for keeping specific destinations on the normal route.

## Local Data and Logs

Profiles, selected apps, include/exclude destinations, connection history, and logs are stored on the user's Windows machine, typically under `%LOCALAPPDATA%\TunnelX` or next to the app depending on the feature. TunnelX does not intentionally send analytics or telemetry to the maintainer. Optional exit-IP and country lookups use third-party HTTPS endpoints **through the tunnel**; see `docs/PRIVACY.md`.

Logs can contain process names, hostnames, IP addresses, ports, and connection state. Before posting logs publicly, remove server credentials, UUIDs, private keys, private endpoints, and other sensitive data.

## Troubleshooting

- If connection fails, check Administrator privileges, firewall rules, config validity, proxy ports, and prerequisites for the selected connection type.
- If an app does not use the tunnel, enable it in the apps tab, keep it running, and refresh the app list.
- If only one site or domain should use the tunnel, add it to include destinations. If it should stay direct, add it to exclusions.
- If DNS or IPv6 status looks wrong, check the health cards after connection and reconnect once to rebuild routes and DNS rules.
- For OpenVPN connection delays, verify the `.ovpn` file, credentials, and OpenVPN Community installation.

## Screenshots

| Connection dashboard | Profile and server setup |
| --- | --- |
| ![TunnelX connection dashboard](docs/ScreenShots/en/connection-dashboard.png) | ![TunnelX app split tunneling](docs/ScreenShots/en/apps.png) |

| Routing rules | Help and troubleshooting |
| --- | --- |
| ![TunnelX routing rules](docs/ScreenShots/en/routing-rules.png) | ![TunnelX help and troubleshooting](docs/ScreenShots/en/help.png) |

## Download

Public downloads are published through GitHub Releases:

[Download the latest release](https://github.com/MaxiFan/TunnelX/releases/latest)

Release assets are built and uploaded by GitHub Actions. Each published standalone executable includes a `.sha256` checksum file, and the release notes link back to the workflow run that produced the artifact.

## Build

End-user requirements for the recommended standalone release:

- Windows 10/11
- 64-bit Windows (`win-x64`). 32-bit Windows is not supported by the current release package.
- Administrator privileges when running the app, because route and packet interception features need elevated access
- No separate .NET Runtime installation is required for the self-contained standalone EXE.

Developer requirements for building from source:

- .NET 8 SDK

```powershell
dotnet build AppTunnel.sln -c Release
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

More release notes are in `CHANGELOG.md` and `docs/BUILD.md`. Privacy details for exit-IP lookups are in `docs/PRIVACY.md`. Future ideas are tracked in `docs/ROADMAP.md`.

## License

TunnelX is licensed under **GPL-3.0-or-later**. Commercial use is allowed under the terms of the GPL. Bundled third-party components keep their own licenses. See:

- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- `docs/LEGAL.md`

## Support, Customization, and Donations

TunnelX is free and open-source. Donations are optional and help keep the project maintained.

For release news and update alerts, join the official Telegram channel: **[t.me/tunnelxx](https://t.me/tunnelxx)**.

For direct contact, support requests, private customization, or paid development work, message MaxFan on Telegram: [t.me/maxifaan](https://t.me/maxifaan).

Paid services may be available separately for private support, deployment help, custom builds, company-specific customization, or development of a similar application. These paid services do not limit the rights granted by the GPL license.

Fixed advertising placements may be available inside TunnelX. Advertising is handled directly with the maintainer, is not served through third-party ad networks or intermediary websites, and is intended to stay simple, static, and safe for users.

Use the GitHub funding button or see `docs/DONATE.md` for donation options.

## Safety Notice

TunnelX is a networking and routing tool. Use it only where you are allowed to run VPN, proxy, packet capture, and route-management software. The project does not provide legal advice.

TunnelX is provided as-is, without warranty and without any obligation from the maintainer to provide updates, fixes, support, or continued availability.
