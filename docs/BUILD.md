# Build and Release

## Local Build

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

## Before Publishing

- Run the leak test plan in `docs/PUBLISHING_CHECKLIST.md`.
- Confirm third-party license notices are current.
- Confirm the app version in `AppTunnel/AppTunnel.csproj`.
- Attach release artifacts only to GitHub Releases; do not commit generated `publish/` or `Releases/` output.
