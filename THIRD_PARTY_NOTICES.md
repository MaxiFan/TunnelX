# Third-Party Notices

TunnelX includes or works with third-party software. These notices are part of the release package and should be reviewed before publishing binaries.

| Component | Project | How TunnelX uses it | License to verify |
| --- | --- | --- | --- |
| Xray-core | https://github.com/XTLS/Xray-core | Bundled executable for Xray/V2Ray configs | MPL-2.0 |
| sing-box | https://github.com/SagerNet/sing-box | Bundled executable / TUN bridge workflows, including WireGuard endpoint support | GPL-3.0-or-later |
| WinDivert | https://github.com/basil00/Divert | Packet interception driver and DLL | LGPL-3.0-or-later or GPL-2.0 |
| Wintun | https://www.wintun.net/ | Windows TUN driver DLL | WireGuard project license terms |
| CommunityToolkit.Mvvm | https://github.com/CommunityToolkit/dotnet | MVVM helpers | MIT |
| Microsoft .NET / WPF | https://github.com/dotnet | Runtime and desktop framework | MIT and Microsoft component licenses |
| Vazirmatn | https://github.com/rastikerdar/vazirmatn | Embedded Persian UI font | SIL Open Font License 1.1 |

## Optional Online Services (while connected)

When the user is connected, TunnelX may call the hosts below **through the local tunnel proxy** for exit-IP display, country lookup, or flag images. These are not bundled binaries; terms and privacy policies belong to each provider.

| Host | Purpose |
| --- | --- |
| `ipv4.icanhazip.com`, `api.ipify.org`, `ifconfig.me` | Public exit IP detection |
| `ip-api.com`, `ipwho.is`, `ipapi.co`, `api.internetdata.io` | Country / region lookup |
| `flagcdn.com` | Country flag PNG images |
| `api.github.com`, `github.com` | Release update checks |

See `docs/PRIVACY.md` for how these requests are routed.

## Redistributing Binaries

- Keep this file with every binary release.
- Keep `AppTunnel/NativeLibs/LICENSE` with WinDivert notices.
- The current repository bundles x64 native components only. Do not advertise 32-bit Windows support unless separate x86 binaries and tests are added.
- Do not remove upstream copyright or license notices.
- Keep the Vazirmatn SIL Open Font License notice when redistributing builds that embed the font.
- If native binaries are updated, refresh this table and the release checklist.
- If crypto, GeoIP, GeoSite, or core binaries are downloaded from upstream releases, record the exact version and source URL in the release notes.
- WireGuard protocol support is provided through the bundled sing-box executable and its WireGuard endpoint support. If future builds switch to official WireGuard/WireGuardNT/Wintun components directly, refresh the notices and redistribution checklist before publishing.

This file is a compliance aid, not legal advice.
