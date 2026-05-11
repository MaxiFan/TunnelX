# Contributing

Thanks for helping TunnelX. The project is built by MaxFan and published as free/open-source software.

## Development

1. Use Windows and .NET 8 SDK.
2. Build with `dotnet build AppTunnel.sln -c Release`.
3. Keep changes focused. Avoid unrelated formatting churn.
4. For networking changes, include log samples and explain the tested mode: split route, full route, app toggle, DNS, IPv6, and leak guard.

## Pull Requests

- Describe the problem and the behavior change.
- Mention manual tests and commands you ran.
- Update documentation when behavior, release process, donation information, or legal notices change.
- Do not commit local profiles, logs, generated publish outputs, archives, or private server configs.

## Security

Please do not publish sensitive exploit details in public issues. See `SECURITY.md`.
