# Publishing Checklist

Use this checklist before making the repository public or attaching a release build.

## Repository

- [ ] Confirm repository URL: `https://github.com/MaxiFan/TunnelX`.
- [ ] Confirm default branch name and branch protection rules.
- [ ] Confirm `.gitignore` excludes `publish/`, `Releases/`, archives, logs, local env files, and build outputs.
- [ ] Confirm no private configs, server URLs, UUIDs, passwords, access tokens, wallet private keys, personal notes, or local test JSON files are committed.
- [ ] Run `git status --short --ignored` and verify ignored local files are intentionally excluded.
- [ ] Confirm README, LICENSE, THIRD_PARTY_NOTICES, SECURITY, SUPPORT, CONTRIBUTING, CHANGELOG, and donation docs are present.

## Legal

- [ ] Confirm project license is acceptable: `GPL-3.0-or-later`.
- [ ] Verify current upstream licenses for Xray-core, sing-box, WinDivert, Wintun, GeoIP/GeoSite data, .NET, and NuGet packages.
- [ ] Keep third-party license files/notices in binary releases.
- [ ] Confirm donation text does not imply paid support unless that support exists.

## App

- [ ] Confirm About/Help shows MaxFan, app version, GitHub link, license, and donation path.
- [ ] Confirm PayPal link works.
- [ ] Verify donation wallet addresses character-by-character before the first public release.
- [ ] Confirm all user-facing text fits at common window sizes.

## Build

- [ ] Run `dotnet restore AppTunnel.sln`.
- [ ] Run `dotnet build AppTunnel.sln -c Release`.
- [ ] Run standalone publish command from `docs/BUILD.md`.
- [ ] Confirm final file name includes version, for example `TunnelX-v1.2.21-standalone-compressed.exe`.

## Network Test Matrix

- [ ] Connect in split route mode.
- [ ] Verify selected Chrome traffic uses tunnel.
- [ ] Verify excluded Iranian/internal domains stay direct.
- [ ] Toggle Telegram off/on while connected.
- [ ] Enable full route, then disable full route.
- [ ] Confirm stats stay `leak=0` and transition packets are logged as suppressed/blocked, not leaks.
- [ ] Confirm DNS redirect behavior is expected.
- [ ] Confirm IPv6 blocked count increases only for blocked IPv6 attempts.
- [ ] Confirm app traffic totals, session totals, direct traffic, and history totals match the same accounting model.

## Release

- [ ] Create a GitHub Release with a clear changelog.
- [ ] Attach the standalone compressed EXE.
- [ ] Attach checksums.
- [ ] Include third-party notice summary in release notes.
