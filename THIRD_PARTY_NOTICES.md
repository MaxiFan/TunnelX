# Third-Party Notices

TunnelX includes or works with third-party software. These notices are part of the release package and should be reviewed before publishing binaries.

| Component | Project | How TunnelX uses it | License to verify |
| --- | --- | --- | --- |
| Xray-core | https://github.com/XTLS/Xray-core | Bundled executable for Xray/V2Ray configs | MPL-2.0 |
| sing-box | https://github.com/SagerNet/sing-box | Bundled executable / TUN bridge workflows | GPL-3.0-or-later |
| WinDivert | https://github.com/basil00/Divert | Packet interception driver and DLL | LGPL-3.0-or-later or GPL-2.0 |
| Wintun | https://www.wintun.net/ | Windows TUN driver DLL | WireGuard project license terms |
| CommunityToolkit.Mvvm | https://github.com/CommunityToolkit/dotnet | MVVM helpers | MIT |
| Microsoft .NET / WPF | https://github.com/dotnet | Runtime and desktop framework | MIT and Microsoft component licenses |

## Redistributing Binaries

- Keep this file with every binary release.
- Keep `AppTunnel/NativeLibs/LICENSE` with WinDivert notices.
- The current repository bundles x64 native components only. Do not advertise 32-bit Windows support unless separate x86 binaries and tests are added.
- Do not remove upstream copyright or license notices.
- If native binaries are updated, refresh this table and the release checklist.
- If crypto, GeoIP, GeoSite, or core binaries are downloaded from upstream releases, record the exact version and source URL in the release notes.

This file is a compliance aid, not legal advice.
