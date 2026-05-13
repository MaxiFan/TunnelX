# Build and Release

## Local Build

TunnelX currently targets 64-bit Windows only. The project file sets `PlatformTarget` to `x64`, and the bundled native components under `AppTunnel/NativeLibs/x64` are required for the current build.

Building from source requires the .NET 10 SDK. Running a framework-dependent developer build requires the .NET 10 Desktop Runtime or SDK on the machine.

```powershell
dotnet build AppTunnel.sln -c Release
```

## Standalone Compressed EXE

This is the recommended public release format. It is self-contained, so users do not need to install .NET 10 separately. Native components are bundled and extracted by the app at runtime when needed.

```powershell
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o publish\TunnelX-standalone-compressed-exe
```

Rename the final executable with the app version:

```powershell
TunnelX-v1.2.23-standalone-compressed.exe
```

## GitHub Actions Release

Public releases are published by `.github/workflows/release.yml`.

The normal release flow is:

1. Add user-facing changes under `## Unreleased` in `CHANGELOG.md` when there are curated release notes to publish.
2. Run the `release` workflow from the GitHub Actions tab.
3. Either provide an explicit version like `1.2.24`, or leave the version empty and choose `patch`, `minor`, or `major`.

The workflow then:

- updates `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in `AppTunnel/AppTunnel.csproj`;
- moves `CHANGELOG.md` notes from `## Unreleased` into a dated version section;
- generates release notes from recent commit subjects if `## Unreleased` is empty;
- commits the release metadata update;
- creates and pushes the `vMAJOR.MINOR.PATCH` tag;
- builds and publishes the `win-x64` self-contained single-file executable;
- attaches `TunnelX-vX.Y.Z-standalone-compressed.exe` and a `.sha256` checksum to the GitHub Release;
- adds build provenance to the Release notes, including the GitHub Actions run URL, commit, and checksum.

Only repository users with write access can run the manual release workflow.

## 32-bit Windows

32-bit Windows builds are not supported at this time. Supporting `win-x86` would require a separate compatibility pass, x86-compatible native binaries for every bundled network component, and separate testing for WinDivert/Wintun, Xray/sing-box, packet interception, route management, and the standalone extraction path.

## Before Publishing

- Run leak, DNS, full-route, split-route, app toggle, and reconnect tests before attaching a public artifact.
- Confirm third-party license notices are current.
- Confirm the app version in `AppTunnel/AppTunnel.csproj`.
- Attach release artifacts only to GitHub Releases; do not commit generated `publish/` or `Releases/` output.

## Missing Runtime Behavior

The app cannot show a custom .NET missing-runtime message if it is built as framework-dependent and the target machine does not have the required .NET Desktop Runtime, because the .NET host fails before TunnelX code starts. For public releases, publish the self-contained standalone EXE instead of relying on runtime installation. If an installer is added in the future, the installer/bootstrapper can check and install prerequisites before launching the app.
