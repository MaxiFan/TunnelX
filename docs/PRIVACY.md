# Privacy

TunnelX is designed as a local desktop app.

## Data Stored Locally

<<<<<<< HEAD
The app may store profiles, selected apps, include/exclude destinations, connection history, subscription URLs, and logs on the user's Windows machine, typically under `%LOCALAPPDATA%\TunnelX` or the app directory depending on the feature. Subscription URLs are stored in `subscriptions.json` and can contain access tokens.
=======
The app may store profiles, selected apps, include/exclude destinations, connection history, and logs (including `proxy-lifecycle.log`) on the user's Windows machine, typically under `%LOCALAPPDATA%\TunnelX` or the app directory depending on the feature.
>>>>>>> origin/cursor/proxy-disconnect-announcements-1a9d

## Network Data

TunnelX handles local routing and packet metadata to decide what should use the tunnel. It does not intentionally send analytics or telemetry to the maintainer.

## Exit IP and Country Display (while connected)

When the connected dashboard shows your public exit IP, optional country name, or flag image, TunnelX performs on-demand HTTPS requests **through the local SOCKS/mixed proxy** so traffic exits via the active tunnel (not as a separate direct lookup from your normal network path).

| Purpose | Third-party hosts | Notes |
| --- | --- | --- |
| Public exit IP | `ipv4.icanhazip.com`, `api.ipify.org`, `ifconfig.me` | Fallback chain; retries while connected |
| Country / region lookup | `ip-api.com`, `ipwho.is`, `ipapi.co` | Tried in order until one succeeds |
| Flag image (PNG) | `flagcdn.com` | Path pattern `/h20/{country-code}.png` |

These services receive requests that appear to come from your tunnel exit IP. They are not used for maintainer-side tracking. If you disconnect, these lookups stop for that session.

## Subscription links

When you add or refresh a subscription, TunnelX sends an HTTP GET directly to the URL you entered. That host receives the request, including any token embedded in the URL. The response is parsed locally into connection profiles. TunnelX does not relay subscription contents to the maintainer.

## Update Checks

Scheduled or manual update checks contact **GitHub Releases** (`api.github.com` / `github.com`) to compare the installed version with the latest published release. Release notes may be shown in-app from the same source.

## Logs

Logs can contain hostnames, IP addresses, process names, ports, and connection state. Users should sanitize logs before posting them publicly.

## Donations

Donation links open external services such as PayPal. Those services have their own privacy policies.

Crypto wallet addresses are public receiving addresses. On-chain transactions may be publicly visible and can connect the project identity with wallet activity. For stronger maintainer privacy, use wallets created specifically for TunnelX.
