# Local proxy disconnect and reconnect

TunnelX exposes loopback SOCKS/HTTP listeners while a tunnel is up. Telegram agents (Telethon `client.run_until_disconnected()` and similar reconnect loops) sit on those sockets. If the tunnel drops but the local TCP session stays open, the client never notices.

TunnelX now does two things when a listening endpoint goes down, and the opposite when it comes back:

1. **Reset active sessions** on the built-in mixed proxy (`127.0.0.1` plus the internal port from Settings, default `1080`). Each accepted client socket is aborted (TCP reset) before the listener stops. A blocked read in the agent completes, so `run_until_disconnected()` returns.
2. **Announce the endpoint** in the in-app log and in an append-only file. Engine inbounds are announced the same way. sing-box and Xray close their own listen sockets when the process exits; that exit is treated as a tunnel drop so the built-in proxy is reset as well.

Transient sing-box `i/o timeout` lines are still diagnostic only. They do not announce a disconnect.

## Endpoints

| Listener | When it is announced |
| --- | --- |
| Built-in mixed SOCKS5/HTTP proxy, `127.0.0.1:<internal port>` (default `1080`, shown in the app as `127.0.0.1:port`) | Listener start and stop, including an OpenVPN runtime restart of packet routing |
| sing-box mixed inbound, `127.0.0.1:<mixed port>` (V2Ray, SOCKS/HTTP proxy, and Xray bridge) | Tunnel connected, and again after the process is stopped or exits |
| Xray SOCKS inbound, `127.0.0.1:<socks port>` | Same, for Xray profiles only |

The internal Xray HTTP bridge port is not a client endpoint and is not announced.

The endpoint id is `127.0.0.1:port` — the same identifier the app already uses for the listening proxy.

## Announcement format

In-app log (also visible in the log window):

```text
[PROXY] 127.0.0.1:1080 is now disconnected
[PROXY] 127.0.0.1:1080 is now connected
```

File, appended under the existing TunnelX data directory:

`%LOCALAPPDATA%\TunnelX\proxy-lifecycle.log`

```text
2026-09-26T12:00:00.123Z 127.0.0.1:1080 is now disconnected
2026-09-26T12:00:04.456Z 127.0.0.1:1080 is now connected
```

Lines use LF. The timestamp is UTC `yyyy-MM-ddTHH:mm:ss.fffZ`. One endpoint is announced again only when its state changes, so a reconnect is `disconnected` followed later by `connected`.

Match a line with:

```text
^(?<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z) (?<endpoint>\S+) is now (?<state>connected|disconnected)$
```

## Using it from an agent

Point the client at the listener you already use (built-in `127.0.0.1:1080`, or the engine port logged as `[PORT] ...` at connect time).

- **Disconnect:** `run_until_disconnected()` returns because the proxy socket was reset or the engine process exited. The same fact is the `is now disconnected` line for that `127.0.0.1:port`.
- **Reconnect:** wait until that endpoint's `is now connected` line is appended (or until a TCP connect to the port succeeds), then call `connect()` again.

```python
# Illustrative. proxy_port is the listener the client is configured to use.
disconnected = f"127.0.0.1:{proxy_port} is now disconnected"
connected = f"127.0.0.1:{proxy_port} is now connected"

await client.connect()
await client.run_until_disconnected()
wait_for_lifecycle_line(connected)
```

The log file is local diagnostic state on the Windows machine. TunnelX does not upload it.
