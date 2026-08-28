# UnrealWebView Nexus

[English](README.md) | **简体中文**

**面向 Unreal Engine 的原生 WebView2 UI 融合方案：绕过 Unreal 内置旧版 CEF，不依赖收费网页插件。**

UnrealWebView Nexus 是一个 Windows 双进程融合宿主。它通过 WPF 承载 Microsoft Edge WebView2，把现代网页以透明覆盖层的形式与 Unreal 窗口组合，并负责窗口同步、透明区域输入穿透以及 Web ↔ WPF ↔ Unreal 本地通信。

> 本项目采用桌面透明覆盖层方案，并不是把 DOM 渲染到 Unreal 纹理或三维世界表面上。

## 为什么需要这个项目

Unreal 内置浏览器依赖引擎自带的 CEF。旧引擎中的 Chromium 版本通常无法及时升级，现代网页可能遇到浏览器 API 缺失、前端框架兼容性差等问题；升级引擎成本高，而第三方网页插件还可能带来授权费用和版本绑定。

UnrealWebView Nexus 采用另一条技术路线：

- **使用现代浏览器内核：** 使用系统原生 Microsoft Edge WebView2，替代 Unreal 内嵌 CEF。
- **网页与 Unreal 解耦：** 网页独立开发、部署和更新，不必为了更新 UI 重新打包 Unreal。
- **不依赖收费插件：** 浏览器运行在 WPF 宿主中；Unreal 侧只需要轻量通信桥接。
- **透明交互覆盖：** HTML 控件区域接收鼠标，透明区域把输入继续交给 Unreal。
- **同步原生窗口：** 协调位置、尺寸、最小化/恢复、全屏、焦点、Owner 与 Z-Order。
- **双向通信桥：** 通过仅监听本机回环地址的 WebSocket 串联网页、WPF 和 Unreal。
- **支持打包与编辑器联调：** 可启动打包后的 Unreal EXE，也可挂接 Standalone Game 窗口。

## 架构

```text
┌──────────────────────────────────────────────────────────┐
│ WPF 宿主                                                  │
│  ┌────────────────────────────────────────────────────┐  │
│  │ WebView2CompositionControl                         │  │
│  │ 现代 HTML / CSS / JavaScript UI                    │  │
│  └────────────────────────────────────────────────────┘  │
│          │ Web 消息                  │ 命中区域           │
│          ▼                           ▼                   │
│  消息路由                    透明区域输入穿透             │
└──────────┬───────────────────────────────────────────────┘
           │ 带鉴权的本机回环 WebSocket
           ▼
┌──────────────────────────────────────────────────────────┐
│ Unreal Engine 进程                                        │
│ 原生渲染、相机、交互、仿真和数字孪生                       │
└──────────────────────────────────────────────────────────┘
```

宿主使用随机 Bridge 端口、临时 Token 和父会话 ID 启动 Unreal，然后发现 Unreal 窗口并持续维护覆盖关系。网页可以来自任意 HTTP/HTTPS 部署，不需要打包进 Unreal 项目。

## 环境要求

- Windows 10 1809+ 或 Windows 11，x64
- 编译需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 目标机器安装 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 一个带兼容通信客户端的 Unreal Windows EXE，或 Standalone Game 窗口

## 快速开始

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

## 网页接入

网页不需要 Unreal 专用浏览器插件，而是作为普通现代网站运行在 WebView2 中。宿主会注入交互命中区域支持，并通过 `window.chrome.webview` 转发应用消息。

需要明确参与鼠标命中的 UI 根节点可以标记为：

```html
<button data-web-hit>打开面板</button>
```

未命中的透明区域继续把鼠标交给 Unreal。仓库中的 [DOM 拖放网格样例](samples/drag-drop-grid.html) 可用于验证指针和拖放行为。

## Unreal 接入

Unreal 侧只负责原生渲染、场景与业务逻辑。轻量桥接客户端需要：

1. 读取命令行中的 `-BridgePort`、`-BridgeToken` 和 `-ParentSessionId`。
2. 连接 WPF 宿主的本机回环 WebSocket。
3. 按 `digital-twin-v1` 消息信封收发业务数据。
4. 对高频状态使用“只保留最新帧”或 ACK 背压，避免旧相机帧排队。

该桥接与 Unreal Web Browser 控件完全独立，不在引擎内部嵌入 Chromium，也不要求购买网页插件。

可直接参考的 Unreal C++ 与 JavaScript 代码，包括模块依赖、WebSocket 鉴权连接、请求/响应处理、WebView2 调用和相机帧 ACK 背压，请阅读 **[通信接入指南](docs/COMMUNICATION.zh-CN.md)**，网页端封装位于 [`bridge-client.js`](samples/web/bridge-client.js)。

## 配置

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

## 仓库结构

```text
src/DigitalTwin.Host/        WPF 宿主、WebView2、通信桥和 Win32 窗口融合
samples/                     独立网页行为测试样例
docs/COMMUNICATION.zh-CN.md  Unreal C++ 与 JavaScript 通信接入指南
```

Unreal 打包产物、本机发布目录和测试截图不会提交到 Git；仓库也不分发 Unreal Engine 或 WebView2 Runtime 二进制文件。
