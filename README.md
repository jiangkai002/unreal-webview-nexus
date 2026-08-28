# UnrealWebView Nexus

**Native WebView2 UI for Unreal Engine — modern web capabilities without Unreal's legacy CEF or paid browser plugins.**

[中文](#中文说明) · [English](#english)

## English

UnrealWebView Nexus is a Windows desktop host that combines an Unreal Engine application with an independent WPF + Microsoft Edge WebView2 overlay. It keeps the web application outside Unreal, synchronizes both native windows, routes input through transparent areas, and provides a local WebSocket bridge for Web ↔ WPF ↔ Unreal messages.

> This project is a dual-process desktop overlay. It does not render DOM elements onto an Unreal texture or a 3D world surface.

## Why this project

Unreal's built-in browser is tied to the CEF version shipped with the engine. That can leave modern web applications blocked by an old Chromium runtime, unsupported browser APIs, or costly engine upgrades. Marketplace browser plugins may solve part of the problem, but add licensing cost and engine-version coupling.

UnrealWebView Nexus takes a different route:

- **Modern browser runtime:** uses the native Microsoft Edge WebView2 runtime instead of Unreal's embedded CEF.
- **Web/Unreal separation:** deploy and update the website independently without repackaging the Unreal project.
- **No paid web plugin dependency:** the browser lives in the WPF host; Unreal only needs a lightweight bridge implementation.
- **Transparent interactive overlay:** HTML controls receive input while transparent regions pass mouse input to Unreal.
- **Synchronized native windows:** position, size, minimize/restore, full screen, focus, ownership, and Z-order are coordinated.
- **Bidirectional bridge:** a loopback-only WebSocket channel connects the web page, WPF host, and Unreal process.
- **Packaged and Editor workflows:** launch a packaged Unreal executable or attach to a Standalone Game window during development.

## Architecture

```text
┌──────────────────────────────────────────────────────────┐
│ WPF host                                                  │
│  ┌────────────────────────────────────────────────────┐  │
│  │ WebView2CompositionControl                         │  │
│  │ Modern HTML / CSS / JavaScript UI                  │  │
│  └────────────────────────────────────────────────────┘  │
│          │ web messages              │ hit regions       │
│          ▼                           ▼                   │
│  Message router              transparent input routing   │
└──────────┬───────────────────────────────────────────────┘
           │ authenticated loopback WebSocket
           ▼
┌──────────────────────────────────────────────────────────┐
│ Unreal Engine process                                    │
│ Rendering, camera, interaction, simulation, digital twin │
└──────────────────────────────────────────────────────────┘
```

The host starts Unreal with a random bridge port, an ephemeral token, and a parent session ID. It then discovers the Unreal window and maintains the overlay relationship. Web content can come from any HTTP/HTTPS deployment and is not bundled into the Unreal package.

## Requirements

- Windows 10 1809+ or Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) on the target machine
- An Unreal Windows executable or Standalone Game window with a compatible bridge client

## Quick start

Build the host:

```powershell
dotnet restore .\DigitalTwinClient.slnx
dotnet build .\DigitalTwinClient.slnx -c Release
```

Run it with a website and a packaged Unreal executable:

```powershell
.\src\DigitalTwin.Host\bin\Release\net10.0-windows10.0.19041.0\win-x64\DigitalTwinClient.exe `
  --url "https://your-web-app.example" `
  --unreal "D:\YourGame\YourGame.exe"
```

You may also put defaults in `src/DigitalTwin.Host/appsettings.json`, or set the web address through `DIGITALTWIN_WEB_URL`. Command-line values take precedence.

To run WebView2 without Unreal, set `Unreal.Enabled` to `false`. For Unreal Editor integration, enable `Unreal.EditorDebugMode` and run the game in a separate Standalone Game window.

## Web integration

The page does not need an Unreal-specific browser plugin. It runs as a normal modern website inside WebView2. The host injects support for interactive hit regions and forwards application messages through `window.chrome.webview`.

For explicit input control, mark interactive UI roots with:

```html
<button data-web-hit>Open panel</button>
```

The rest of the transparent page can remain click-through so Unreal continues receiving camera and scene input. The included [drag/drop grid sample](samples/drag-drop-grid.html) is useful for validating DOM pointer behavior.

## Unreal integration

The Unreal side remains renderer- and game-logic-focused. A small native bridge client should:

1. Read `-BridgePort`, `-BridgeToken`, and `-ParentSessionId` from the command line.
2. Connect to the host's loopback WebSocket endpoint.
3. Send and receive the `digital-twin-v1` message envelopes used by the host.
4. Keep high-frequency state streams latest-only or acknowledged to prevent stale camera frames from queuing.

The bridge is intentionally independent of Unreal's Web Browser widget: it does not embed Chromium in the engine and does not require a commercial web plugin.

For copy-ready Unreal C++ and JavaScript examples, including module dependencies, authenticated WebSocket setup, request/response handling, WebView2 calls, and camera-frame ACK backpressure, see the **[communication integration guide](docs/COMMUNICATION.md)** and [`bridge-client.js`](samples/web/bridge-client.js).

## Configuration

Key settings in `appsettings.json`:

| Setting | Purpose |
| --- | --- |
| `Web.Url` | Default HTTP/HTTPS page to load |
| `Web.TransparentBackground` | `auto`, `true`, or `false` background behavior |
| `Web.EnableAutomaticHitRegions` | Detect and synchronize interactive DOM regions |
| `Unreal.Enabled` | Enable Unreal launch/window integration |
| `Unreal.ExecutablePath` | Default packaged Unreal executable path |
| `Unreal.EditorDebugMode` | Attach to a Standalone Game window instead of launching an EXE |
| `Unreal.EditorStandaloneWindowTitle` | Window title used for Editor development attachment |

## Repository layout

```text
src/DigitalTwin.Host/   WPF host, WebView2 overlay, bridge, and Win32 integration
samples/                Small standalone web behavior samples
docs/COMMUNICATION.md   Copy-ready Unreal C++ and JavaScript bridge examples
```

Packaged Unreal builds and local publish artifacts are intentionally excluded from Git. This repository does not redistribute Unreal Engine or Microsoft Edge WebView2 Runtime binaries.

---

## 中文说明

**面向 Unreal Engine 的原生 WebView2 UI 融合方案：绕过 Unreal 内置旧版 CEF，不依赖收费网页插件。**

UnrealWebView Nexus 是一个 Windows 双进程融合宿主。它通过 WPF 承载 Microsoft Edge WebView2，把现代网页以透明覆盖层的形式与 Unreal 窗口组合，并负责窗口同步、透明区域输入穿透以及 Web ↔ WPF ↔ Unreal 本地通信。

> 本项目采用桌面透明覆盖层方案，并不是把 DOM 渲染到 Unreal 纹理或三维世界表面上。

### 它解决什么问题

Unreal 内置浏览器依赖引擎自带的 CEF。旧引擎中的 Chromium 版本通常无法及时升级，现代网页可能遇到浏览器 API 缺失、前端框架兼容性差等问题；升级引擎成本高，而第三方网页插件还可能带来授权费用和版本绑定。

本项目把浏览器从 Unreal 中拆出来：

- **解决旧 CEF 限制：** 使用系统原生 Microsoft Edge WebView2，获得现代 Chromium/Web API 能力。
- **网页与 Unreal 解耦：** 网页独立开发、部署和更新，不必为了更新 UI 重新打包 Unreal。
- **不依赖收费插件：** 不使用 Unreal Web Browser 控件或商业浏览器插件；Unreal 侧只需要轻量通信桥接。
- **透明交互覆盖：** HTML 控件区域接收鼠标，透明区域把输入继续交给 Unreal。
- **窗口统一管理：** 同步位置、尺寸、最小化/恢复、全屏、焦点、Owner 与 Z-Order。
- **双向低延迟通信：** 通过仅监听本机回环地址的 WebSocket 串联网页、WPF 和 Unreal。
- **支持打包与编辑器联调：** 可启动打包后的 EXE，也可挂接 Editor 的 Standalone Game 窗口。

### 工作方式

WPF 是总宿主：上层运行 WebView2 网页，下层保持 Unreal 原生渲染窗口。宿主启动 Unreal 时注入随机端口、临时令牌和会话 ID，随后查找 Unreal HWND 并持续同步两个窗口。网页只需要是普通的 HTTP/HTTPS 应用，不需要被打进 Unreal 包中。

### 环境要求

- Windows 10 1809+ 或 Windows 11，x64
- 编译需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 目标机器安装 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 一个带兼容通信客户端的 Unreal Windows EXE，或 Editor Standalone Game 窗口

### 快速开始

编译 WPF 宿主：

```powershell
dotnet restore .\DigitalTwinClient.slnx
dotnet build .\DigitalTwinClient.slnx -c Release
```

传入网页地址和 Unreal EXE 运行：

```powershell
.\src\DigitalTwin.Host\bin\Release\net10.0-windows10.0.19041.0\win-x64\DigitalTwinClient.exe `
  --url "https://your-web-app.example" `
  --unreal "D:\YourGame\YourGame.exe"
```

也可以在 `src/DigitalTwin.Host/appsettings.json` 中配置默认值，或用环境变量 `DIGITALTWIN_WEB_URL` 指定网页地址；命令行参数优先级最高。

只测试网页时，把 `Unreal.Enabled` 设为 `false`。需要 Editor 联调时，启用 `Unreal.EditorDebugMode`，并用独立的 Standalone Game 窗口运行游戏。

### 网页接入

网页就是普通的现代 Web 应用，通过 WebView2 的 `window.chrome.webview` 与宿主交换消息。需要明确参与鼠标命中的 UI 根节点可以标记为：

```html
<button data-web-hit>打开面板</button>
```

未命中的透明区域继续把鼠标交给 Unreal。仓库中的 [DOM 拖放网格样例](samples/drag-drop-grid.html) 可用于验证指针和拖放行为。

### Unreal 接入

Unreal 侧只负责原生渲染、场景与业务逻辑，并实现一个轻量桥接客户端：

1. 读取命令行中的 `-BridgePort`、`-BridgeToken` 和 `-ParentSessionId`。
2. 连接 WPF 宿主的本地 WebSocket。
3. 按 `digital-twin-v1` 消息信封收发业务数据。
4. 对相机等高频状态使用“只保留最新帧”或 ACK 背压，避免旧消息排队造成视觉延迟。

该桥接与 Unreal Web Browser 控件完全独立，不在引擎内部嵌入 Chromium，也不要求购买网页插件。

可直接参考的 Unreal C++ 与 JavaScript 代码，包括模块依赖、WebSocket 鉴权连接、请求/响应处理、WebView2 调用和相机帧 ACK 背压，请阅读 **[通信接入指南](docs/COMMUNICATION.md)**，网页端封装位于 [`bridge-client.js`](samples/web/bridge-client.js)。

### 配置说明

`appsettings.json` 的主要配置：

| 配置项 | 作用 |
| --- | --- |
| `Web.Url` | 默认加载的 HTTP/HTTPS 网页 |
| `Web.TransparentBackground` | `auto`、`true` 或 `false` 背景模式 |
| `Web.EnableAutomaticHitRegions` | 自动识别并同步可交互 DOM 区域 |
| `Unreal.Enabled` | 是否启用 Unreal 启动和窗口融合 |
| `Unreal.ExecutablePath` | 默认 Unreal 打包 EXE 路径 |
| `Unreal.EditorDebugMode` | 是否挂接 Standalone Game，而不是启动 EXE |
| `Unreal.EditorStandaloneWindowTitle` | Editor 联调时用于匹配的窗口标题 |

### 项目结构

```text
src/DigitalTwin.Host/   WPF 宿主、WebView2、通信桥和 Win32 窗口融合
samples/                独立网页行为测试样例
docs/COMMUNICATION.md   可复制的 Unreal C++ 与 JavaScript 通信示例
```

Unreal 打包产物、本机发布目录和测试截图不会提交到 Git；仓库也不分发 Unreal Engine 或 WebView2 Runtime 二进制文件。
