# TunnelX

[فارسی](README.fa.md) | English

TunnelX is a free and open-source Windows split-tunneling client built by **MaxFan**. It routes selected apps, selected destinations, or the whole system through supported tunnel cores while keeping local and excluded destinations on the normal network path.

> Status: pre-release. Review the release notes in `docs/BUILD.md` before publishing a public artifact.

## Features

- App-based split tunneling for selected Windows processes
- Full-route mode for whole-system tunneling
- Xray-core / sing-box based V2Ray workflows
- Local SOCKS5 proxy for tools that need `127.0.0.1`
- DNS redirect, IPv6 blocking, leak guard, route diagnostics, and traffic history
- Persian-first Windows desktop UI

## Screenshots

| Connection dashboard                                                                   | Profile and server setup                                                                   |
| -------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| ![TunnelX connection dashboard](docs/ScreenShots/Screenshot%202026-05-12%20115349.png) | ![TunnelX profile and server setup](docs/ScreenShots/Screenshot%202026-05-12%20115544.png) |

| App split tunneling                                                                   | Tunnel settings                                                                   |
| ------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| ![TunnelX app split tunneling](docs/ScreenShots/Screenshot%202026-05-12%20115646.png) | ![TunnelX tunnel settings](docs/ScreenShots/Screenshot%202026-05-12%20115718.png) |

## Download

Public downloads should be attached to GitHub Releases after release validation is complete:

[GitHub project](https://github.com/MaxiFan/TunnelX)

Release assets are built and uploaded by GitHub Actions. Each published standalone executable includes a `.sha256` checksum file, and the release notes link back to the workflow run that produced the artifact.

## Build

End-user requirements for the recommended standalone release:

- Windows 10/11
- 64-bit Windows (`win-x64`). 32-bit Windows is not supported by the current release package.
- Administrator privileges when running the app, because route and packet interception features need elevated access
- No separate .NET Runtime installation is required for the self-contained standalone EXE.

Developer requirements for building from source:

- .NET 10 SDK

```powershell
dotnet build AppTunnel.sln -c Release
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

More release notes are in `docs/BUILD.md`. Future ideas are tracked in `docs/ROADMAP.md`.

## License

TunnelX is licensed under **GPL-3.0-or-later**. Bundled third-party components keep their own licenses. See:

- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- `docs/LEGAL.md`

## Donate

TunnelX is free. Donations are optional and help keep the project maintained.

Use the GitHub funding button or see `docs/DONATE.md` for donation options.

## Safety Notice

TunnelX is a networking and routing tool. Use it only where you are allowed to run VPN, proxy, packet capture, and route-management software. The project does not provide legal advice.

TunnelX is provided as-is, without warranty and without any obligation from the maintainer to provide updates, fixes, support, or continued availability.
