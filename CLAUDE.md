# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TunnelX is a Windows split-tunneling desktop client (WPF/.NET 10, x64 only) that routes selected apps or the entire system through Xray/sing-box tunnel cores using WinDivert packet interception. It requires administrator privileges at runtime.

## Build & Run

```bash
# Debug build
dotnet build AppTunnel.sln -c Release

# Self-contained single-EXE publish (for distribution)
dotnet publish AppTunnel/AppTunnel.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false
```

There are no automated tests — all testing is manual. Before submitting networking changes, manually test: split route, full route, app toggle, DNS redirection, IPv6 blocking, leak guard, and reconnect scenarios. Include log samples with PRs that touch networking code.

## Architecture

**Pattern**: MVVM with `CommunityToolkit.Mvvm` v8.4.2.  
**Single project**: `AppTunnel/AppTunnel.csproj` (net10.0-windows, WPF + WinForms for tray icon).

### Key layers

| Layer      | Location                      | Role                                                             |
| ---------- | ----------------------------- | ---------------------------------------------------------------- |
| Views      | `AppTunnel/Views/`            | XAML tabs: Connection, Apps, Settings, History, Help             |
| ViewModels | `AppTunnel/ViewModels/`       | `MainViewModel` split across 4 partial files; `AppItemViewModel` |
| Models     | `AppTunnel/Models/`           | `ConnectionProfile`, `TunnelApp`, `ConnectionHistoryEntry`, etc. |
| Services   | `AppTunnel/Services/`         | All business logic (~7,700 LOC)                                  |
| Interop    | `AppTunnel/Services/Interop/` | P/Invoke for WinDivert, IP Helper, routing table APIs            |

### Services of note

- **`TrafficRouterService`** — split across 9 partial `.cs` files; handles WinDivert packet interception, per-app routing, IPv6 blocking, flow tracking, traffic accounting, and leak guard.
- **`V2RayTunnelProvider` / `XrayTunnelProvider`** — launch and manage the bundled `sing-box.exe` / `xray.exe` child processes and write their JSON configs at runtime.
- **`L2tpTunnelProvider`** — L2TP/IPSec VPN via Windows RAS APIs.
- **`TunnelProviderFactory`** — factory that selects the correct `ITunnelProvider` implementation.
- **`AppDiscoveryService`** — enumerates running Windows processes for the per-app split-tunnel list.
- **`Socks5Server`** — custom local SOCKS5 proxy used internally.
- **`ProfileService`** — persists `ConnectionProfile` objects to `%LOCALAPPDATA%\TunnelX\`.

### Native libraries (bundled, `AppTunnel/NativeLibs/x64/`)

`WinDivert.dll` + `WinDivert64.sys`, `sing-box.exe`, `xray.exe`, `wintun.dll`, `geoip.dat`, `geosite.dat` — all extracted to `%LOCALAPPDATA%\TunnelX\` at first run.

## Release Process

Releases are fully automated via `.github/workflows/release.yml` (manual workflow dispatch):

1. Add user-facing changes under `## Unreleased` in `CHANGELOG.md`.
2. Trigger the workflow — it bumps the version in `AppTunnel.csproj`, moves the changelog entry, commits, tags, builds, and publishes the GitHub Release.

Do **not** commit publish artifacts or manually update the version number.

## Notable Conventions

- `TrafficRouterService` uses partial classes for organization — changes to routing logic are spread across files named `TrafficRouterService.*.cs`.
- `MainViewModel` is also split into partial files by concern (`.Core`, `.Connection`, `.AppManagement`, `.ProfileManagement`).
- Thread-safe collections (`ConcurrentDictionary`) are used throughout the caching and flow-tracking code.
- The app enforces single-instance via a named `Mutex`; a second launch brings the existing window to front.
- UI language is Persian-first; the embedded `Vazirmatn-Regular.ttf` font and RTL layout are intentional.
