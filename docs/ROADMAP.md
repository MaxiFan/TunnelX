# Roadmap

TunnelX is free and open-source. This roadmap is not a promise of delivery, but a public place to track useful future improvements.

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
- GitHub release/update status

Potential actions:

- re-extract bundled components when possible
- show a clear manual fix guide when automatic repair is not possible
- ask before downloading any external file
- download only from official TunnelX GitHub Releases
- verify downloaded files before use
- avoid silent driver or system-level changes

For public releases, TunnelX should continue to prefer self-contained standalone EXE builds so end users do not need to install the .NET Runtime separately.
