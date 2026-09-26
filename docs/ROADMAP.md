# Roadmap

TunnelX is free and open-source. This roadmap is not a promise of delivery, but a public place to track useful future improvements.

## Recently Shipped (2.1.0)

- **Connection health verify** before the connected screen: end-to-end TCP probes through the tunnel SOCKS path (`google.com`, `cloudflare.com`), with live latency in the progress UI.
- **Exit IP dashboard** with country name and flag PNG (geo APIs and `flagcdn.com` via tunnel).
- **OpenVPN** split-config stability improvements and clearer disconnect insight.
- Tray update cards with optional in-app release notes.

## Planned Ideas

### System preflight and guided repair

Add an in-app system check before connecting that can detect common local problems and guide the user clearly.

Candidate checks:

- Administrator privileges
- Runtime mode: standalone/self-contained vs framework-dependent developer build
- Xray and sing-box availability
- WinDivert and Wintun native component availability
- extraction folder write permissions
- route and packet interception readiness
- deeper release asset verification for future automatic update flows

Potential actions:

- re-extract bundled components when possible
- show a clear manual fix guide when automatic repair is not possible
- ask before downloading any external file
- download only from official TunnelX GitHub Releases
- verify downloaded files before use
- avoid silent driver or system-level changes

For public releases, TunnelX should continue to prefer self-contained standalone EXE builds so end users do not need to install the .NET Runtime separately.

### V2Ray core fallback: sing-box first, Xray when needed

Today, share links such as `vmess://`, `vless`+WebSocket+TLS, trojan, shadowsocks, and Hysteria (`hysteria://`, `hysteria2://`, `hy2://`) are converted and run through **sing-box** only. **Xray-core** is used when the config explicitly requires it (for example `vless` with `type=xhttp` or raw Xray JSON with `streamSettings`).

Planned improvement for the same class of profiles that work on mobile (v2rayNG / Xray) but fail or feel slow on sing-box:

- Keep **sing-box** as the default path (single process, already used for TUN + mixed inbound health checks).
- If connection **fails** (for example repeated upstream errors, health verify failure, or handshake errors after config conversion fixes) **or** is **consistently slow** (for example sustained `dial tcp` timeouts to the proxy server while direct reachability checks pass), optionally retry or switch using **Xray-core** for the outbound:
  - `xray.exe` handles VMess/VLESS/WebSocket/TLS (and `?ed=` early-data compatibility aligned with v2rayNG).
  - **sing-box** remains the TUN bridge only (local SOCKS → existing `XrayTunnelProvider` pattern).
- UX: surface which core is active (`sing-box` vs `Xray-core + bridge`), log the fallback reason, and allow a per-profile or global setting (“try Xray if sing-box fails or is slow”) without forcing two processes for every user.

Scope notes:

- Not a replacement for all V2Ray traffic by default (heavier: two processes, larger footprint).
- Implement VMess (and common WS+TLS) outbound build for Xray where missing; reuse existing xhttp bridge architecture.
- Compare latency on the same health probes before marking fallback successful.

### Profile management

- import and export connection profiles
- clone profiles with clearer naming
- per-profile health and last-test status
- presets for common workflows such as browser-only, messaging, development, and full VPN

### Split tunnel rule clarity

- explicit rule priority between apps, include destinations, and exclude destinations
- wildcard and domain-suffix previews
- conflict detection when the same destination is both included and excluded
- show which rule caused a destination to be routed or bypassed

### Health dashboard

Partially addressed in 2.1.0 (pre-connect tunnel probes and existing DNS/IPv6/leak/route cards). Still planned:

- unified user-facing states: safe, protected, needs attention, and broken
- short explanation for the latest important network event
- suggested action next to DNS, IPv6, route, and leak status

### Kill switch

- optional per-app kill switch when the tunnel core or TUN bridge drops
- keep selected apps blocked until the tunnel is restored or the user disconnects

### Traffic accounting clarity

- keep tunnel, direct, per-app, DNS, and history counters aligned to one accounting model
- expose whether counters are interface-based, rewritten-flow-based, or app-attributed

### Installer and prerequisite checks

- optional installer/bootstrapper for shortcuts, uninstall support, prerequisite checks, and future update flow
