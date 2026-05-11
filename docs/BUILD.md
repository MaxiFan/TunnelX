# Build and Release

## Local Build

TunnelX currently targets 64-bit Windows only. The project file sets `PlatformTarget` to `x64`, and the bundled native components under `AppTunnel/NativeLibs/x64` are required for the current build.

```powershell
dotnet build AppTunnel.sln -c Release
```

## Standalone Compressed EXE

```powershell
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o publish\TunnelX-standalone-compressed-exe
```

Rename the final executable with the app version:

```powershell
TunnelX-v1.2.21-standalone-compressed.exe
```

## 32-bit Windows

32-bit Windows builds are not supported at this time. Supporting `win-x86` would require a separate compatibility pass, x86-compatible native binaries for every bundled network component, and separate testing for WinDivert/Wintun, Xray/sing-box, packet interception, route management, and the standalone extraction path.

## Before Publishing

- Run the leak test plan in `docs/PUBLISHING_CHECKLIST.md`.
- Confirm third-party license notices are current.
- Confirm the app version in `AppTunnel/AppTunnel.csproj`.
- Attach release artifacts only to GitHub Releases; do not commit generated `publish/` or `Releases/` output.
