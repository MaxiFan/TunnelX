# TunnelX

[فارسی](README.fa.md) · [English](README.md) · [Русский](README.ru.md) · 简体中文

**TunnelX** 是由 **MaxFan** 构建的免费开源 Windows 分流隧道客户端。它可让选定应用、指定域名/IP 或整个系统通过 VPN、V2Ray/Xray、OpenVPN 或 SOCKS5/HTTP Proxy，同时让本地或排除的目标继续走普通网络。应用支持波斯语、英语和俄语界面，可自动检测系统语言并正确处理 RTL/LTR 布局。

## 在 Telegram 获取更新

加入 TunnelX 官方 Telegram 频道，获取版本发布、更新提醒和项目新闻：

**[加入 Telegram @tunnelxx](https://t.me/tunnelxx)**

若在 Windows 上已安装 Telegram，应用内按钮可直接在 Telegram 客户端中打开频道。

## 功能

- 基于所选 Windows 进程的应用分流（split tunneling）
- 全系统隧道模式（Full Route）
- Windows L2TP/IPsec 配置文件支持
- 基于 Xray-core / sing-box 的 V2Ray 工作流
- 独立的 SOCKS5/HTTP Proxy 配置（服务器、端口、用户名、密码字段分离）
- 通过用户提供的 `.ovpn` 文件支持 OpenVPN Community 应用分流
- 通过单 peer `.conf` 文件和 sing-box 支持 WireGuard
- 为需要 `127.0.0.1` 的工具提供本地 SOCKS5 代理
- DNS 重定向、IPv6 阻断、泄漏防护、路由诊断与流量历史
- 多配置、复制/编辑、服务器测试、公网出口 IP 检测与版本更新检查
- **连接健康检查**：在“已连接”界面前，经隧道 SOCKS 路径做端到端 TCP 探测，验证阶段显示各主机延迟。默认目标为 `google.com` 和 `cloudflare.com`；可在设置中添加自定义 URL、主机名或 IP，并可关闭这些公共目标
- 已连接面板：**出口 IP** 及国家名称与旗帜（地理信息与旗帜 PNG 经隧道获取，非本地网络直连）
- Windows 托盘通知（更清晰的错误指引）、更新卡片上的可选发布说明，以及连接后的定时更新检查
- 波斯语/英语/俄语桌面界面，自动语言检测、在设置中手动切换及正确的 RTL/LTR 布局
- V2Ray/Xray 内部组件动态选择本地端口，减少 `2080/2081` 绑定冲突

## 快速开始

1. 从 GitHub Releases 下载最新 standalone 版本。
2. 以 Administrator 权限运行 TunnelX。路由管理、WinDivert 与数据包拦截需要提升权限。
3. 在连接选项卡创建新配置或选择已有配置。
4. 选择连接类型：L2TP/IPsec、V2Ray/Xray、SOCKS5/HTTP Proxy、OpenVPN 或 WireGuard。
5. 测试服务器，然后启用应走隧道的 Windows 应用。
6. 按需添加 include/exclude 目标，连接后检查流量健康卡片（DNS、IPv6、泄漏、路由）。

连接后，TunnelX 会执行简短的**健康验证**（适配器/路由检查加真实隧道探测）。仅当至少一次端到端探测成功时才显示已连接面板。过期或配额耗尽的代理配置应在此失败，而非显示虚假的“已连接”状态。

## 出口 IP 与国家旗帜

连接期间，TunnelX 在面板显示公网出口 IP。国家名称与旗帜为可选增强：

- 出口 IP 通过本地 SOCKS/混合代理查询，使请求经隧道出口（`ipv4.icanhazip.com`、`api.ipify.org`、`ifconfig.me`）。
- 国家查询经同一路径的备用 API：`ip-api.com`、`ipwho.is`、`ipapi.co`。
- 旗帜为 `flagcdn.com` 的小 PNG（`/h20/{country-code}.png`），同样经隧道下载。

这些请求不是发送给 TunnelX 维护者的分析数据，而是应用按需发起的查询。详见 `docs/PRIVACY.md`。

## 连接类型

### L2TP/IPsec

输入服务器地址、用户名、密码和预共享密钥。TunnelX 创建 Windows VPN 连接，并按所选应用策略或全路由模式管理路由。

### V2Ray / Xray

将 V2Ray/Xray 链接或 JSON 配置粘贴到配置中。TunnelX 对常规配置使用 sing-box，对需要 Xray 特有行为（如 `xhttp`）的配置切换到 Xray-core。

Hysteria 由附带的 sing-box 1.12 运行（Xray 没有 Hysteria 出站）：

- **Hysteria 2：** `hysteria2://` 与 `hy2://`。认证在 userinfo，或 `auth` / `password` 参数。可选 `sni`、`insecure`、`obfs=salamander` 与 `obfs-password`、端口跳跃 `mport`，以及 `upmbps` / `downmbps`。
- **Hysteria 1：** `hysteria://` 仅 UDP（`auth`、`peer` 或 `sni`、`insecure`、`obfsParam`、`upmbps` / `downmbps`）。不支持 `faketcp` 与 `wechat-video`。未写带宽时默认为 100/100 Mbps。
- **sing-box JSON：** 已含 inbounds 的完整配置会原样使用。没有 inbounds 的裸 outbound，或仅含 `hysteria` / `hysteria2` 出站的文档，会包上 TunnelX 的 TUN 与 mixed inbound。示例：`examples/singbox-hysteria2.example.json`。

此版 sing-box 会忽略 `pinSHA256`。连接延迟走 sing-box；服务器按钮使用 ICMP，因为 Hysteria 是 QUIC/UDP。

### SOCKS5/HTTP Proxy

若已有外部代理端点，使用 SOCKS5/HTTP Proxy 配置。输入代理服务器、端口及可选凭据。这与连接后暴露的本地 `127.0.0.1` SOCKS5 代理不同，后者供需要本地代理地址的工具使用。

当该本地代理（以及引擎的 SOCKS/mixed 入站）断开或恢复时，TunnelX 会重置已有会话并宣布 `127.0.0.1:port`。Telethon 等客户端的观察方式见 `docs/PROXY_LIFECYCLE.md`。

### WireGuard

选择标准 WireGuard `.conf` 文件或将内容粘贴到配置中。TunnelX 通过 sing-box 运行 WireGuard 并保持 Windows 路由受 TunnelX 控制，因此应用分流、include/exclude 规则、DNS 重定向、IPv6 泄漏防护和全路由模式均通过现有路由引擎工作。

首个 WireGuard 实现每个配置仅支持一个 `[Peer]` 段。UDP 端点测试为尽力诊断，非保证握手检查；私钥与配置数据一同本地存储。

## OpenVPN

TunnelX 可运行已安装的 **OpenVPN Community** `openvpn.exe` 及用户选择的 `.ovpn` 配置，然后应用自身的分流策略，使仅选定应用和 include 目标使用 OpenVPN 隧道。

OpenVPN 未随 TunnelX 捆绑。请单独安装 OpenVPN Community，在 TunnelX 中选择 `.ovpn` 文件，若服务器需要则输入 OpenVPN 用户名/密码。仅安装 OpenVPN Connect 不足以使用此模式，因其通过自有客户端管理路由和 DNS。

为兼容分流，TunnelX 通过控制推送的路由和 DNS 行为准备 OpenVPN 配置。近期版本改进了多 `<connection>` 配置的稳定性：远程端口顺序（443/80 优先于 21/53）、保留 `tcp-client` 块、跳过无法解析的远程主机名，以及控制通道重置时更清晰的断开信息。若 OpenVPN 重连并更改隧道 IP、网关、接口或远程端点，TunnelX 会用新值重启数据包路由。

若 OpenVPN 服务器无法直连，可在同一配置中将上游代理设为 HTTP 或 SOCKS5，并填写地址、端口以及可选的用户名/密码。TunnelX 会保存这些字段，并在连接时写入 `http-proxy` 或 `socks-proxy`。仅 TCP 配置（`proto tcp` 或 `tcp-client`）可通过该代理连接。若 `.ovpn` 已包含代理指令，重新选择该文件即可把主机和端口填入配置。

## 路由说明

目标 include/exclude 规则匹配输入的域名及其子域名。例如添加 `githubusercontent.com` 在 DNS 解析后也会覆盖 `raw.githubusercontent.com`。若 HTTPS 客户端在证书吊销检查时失败，可能是因为 OCSP/CRL 主机无法通过所选路由访问；请将下载应用或相关吊销域名加入 include 列表。

- 排除的目标即使对选定应用也保持直连。
- 包含的目标即使未选定对应应用也走隧道。
- 对于 Store/MSIX、WebView2 或多进程应用，请保持应用打开并刷新应用列表。
- 全路由模式下整个系统走隧道；direct/exclude 规则仍可用于将特定目标保留在普通路由上。

## 本地数据与日志

配置、选定应用、include/exclude 目标、连接历史与日志存储在用户 Windows 机器上，通常在 `%LOCALAPPDATA%\TunnelX` 或应用旁（视功能而定）。TunnelX 不会故意向维护者发送分析或遥测。可选的出口 IP 与国家查询通过**隧道**访问第三方 HTTPS 端点；见 `docs/PRIVACY.md`。

日志可能包含进程名、主机名、IP 地址、端口和连接状态。公开发布日志前请删除服务器凭据、UUID、私钥、私有端点及其他敏感信息。

## 故障排除

- 连接失败时，检查 Administrator 权限、防火墙规则、配置有效性、代理端口及所选连接类型的前置条件。
- 若应用未走隧道，在应用选项卡启用、保持运行并刷新应用列表。
- 若仅一个站点或域名应走隧道，将其加入 include；若应直连，加入排除列表。
- 若 DNS 或 IPv6 状态异常，连接后查看健康卡片并重新连接以重建路由和 DNS 规则。
- OpenVPN 连接延迟时，验证 `.ovpn` 文件、凭据及 OpenVPN Community 安装。

## 截图

| 连接面板 | 配置与服务器设置 |
| --- | --- |
| ![TunnelX 连接面板](docs/ScreenShots/en/connection-dashboard.png) | ![TunnelX 应用分流](docs/ScreenShots/en/apps.png) |

| 路由规则 | 帮助与故障排除 |
| --- | --- |
| ![TunnelX 路由规则](docs/ScreenShots/en/routing-rules.png) | ![TunnelX 帮助](docs/ScreenShots/en/help.png) |

## 下载

公开发布通过 GitHub Releases：

[下载最新版本](https://github.com/MaxiFan/TunnelX/releases/latest)

发布资产由 GitHub Actions 构建上传。每个 standalone 可执行文件附带 `.sha256` 校验和文件，发布说明链接到生成该产物的 workflow 运行。

## 构建

standalone 发布的使用要求：

- Windows 10/11
- 64 位 Windows（`win-x64`）。当前发布包不支持 32 位 Windows。
- 运行时需要 Administrator 权限（路由与数据包拦截需要提升访问）
- self-contained standalone EXE 无需单独安装 .NET Runtime。

从源码构建需要：

- .NET 8 SDK

```powershell
dotnet build AppTunnel.sln -c Release
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

更多说明见 `CHANGELOG.md`、`docs/BUILD.md`。出口 IP 查询隐私见 `docs/PRIVACY.md`。未来计划见 `docs/ROADMAP.md`。

## 许可证

TunnelX 采用 **GPL-3.0-or-later** 许可。商业使用须遵守 GPL 条款。捆绑的第三方组件保留各自许可证。见：

- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- `docs/LEGAL.md`

## 支持、定制与捐赠

TunnelX 免费开源。捐赠为可选，有助于项目维护。

发布新闻与更新提醒：**[t.me/tunnelxx](https://t.me/tunnelxx)**。

直接联系、支持请求、私有定制或付费开发：[t.me/maxifaan](https://t.me/maxifaan)。

付费服务可单独提供私有支持、部署帮助、定制构建、企业定制或类似应用开发。这些付费服务不限制 GPL 授予用户的权利。

TunnelX 内可提供固定广告位，由维护者直接处理，不通过第三方广告网络，力求简单、静态且对用户安全。

捐赠选项：GitHub Funding 按钮或 `docs/DONATE.md`。

## 安全提示

TunnelX 是网络与路由工具。仅在允许运行 VPN、代理、数据包捕获和路由管理软件的环境中使用。本项目不提供法律建议。

软件按“原样”提供，无担保，维护者无义务提供更新、修复、支持或持续可用性。
