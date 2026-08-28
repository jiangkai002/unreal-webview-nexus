# UnrealWebView Nexus

**English** | [简体中文](README.zh-CN.md)

**Native WebView2 UI for Unreal Engine — modern web capabilities without Unreal's legacy CEF or paid browser plugins.**

UnrealWebView Nexus is a Windows dual-process host that combines an Unreal Engine application with an independent WPF + Microsoft Edge WebView2 overlay. It synchronizes both native windows, routes input through transparent areas, and provides a local bridge for Web ↔ WPF ↔ Unreal communication.

> This project is a desktop overlay solution. It does not render DOM elements onto an Unreal texture or a 3D world surface.

## Why this project

Unreal's built-in browser is tied to the CEF version shipped with the engine. Older engine releases cannot update Chromium promptly, so modern applications may encounter missing browser APIs and frontend framework compatibility problems. Engine upgrades are costly, while third-party browser plugins can introduce license fees and engine-version coupling.

UnrealWebView Nexus takes a different approach:

- **Modern browser runtime:** uses the native Microsoft Edge WebView2 runtime instead of Unreal's embedded CEF.
- **Web/Unreal separation:** develop, deploy, and update the website independently without repackaging Unreal for UI changes.
- **No paid plugin dependency:** the browser runs in the WPF host; Unreal only needs a lightweight communication bridge.
- **Transparent interactive overlay:** HTML controls receive input while transparent areas pass input through to Unreal.
- **Synchronized native windows:** coordinates position, size, minimize/restore, full screen, focus, ownership, and Z-order.
- **Bidirectional bridge:** connects the page, WPF, and Unreal through a loopback-only WebSocket.
- **Packaged and Editor workflows:** launches a packaged Unreal executable or attaches to a Standalone Game window.

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
│ Native rendering, camera, interaction, simulation, twin  │
└──────────────────────────────────────────────────────────┘
```

The host starts Unreal with a random bridge port, an ephemeral token, and a parent session ID. It then discovers the Unreal window and continuously maintains the overlay relationship. Web content can come from any HTTP/HTTPS deployment and does not need to be bundled into the Unreal project.

## Requirements

- Windows 10 1809+ or Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) on the target machine
- An Unreal Windows executable or Standalone Game window with a compatible bridge client

## Quick start

Build the WPF host:

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

To test WebView2 without Unreal, set `Unreal.Enabled` to `false`. For Editor integration, enable `Unreal.EditorDebugMode` and run the game in a separate Standalone Game window.

## Web integration

The page does not need an Unreal-specific browser plugin. It runs as a normal modern website inside WebView2. The host injects support for interactive hit regions and forwards application messages through `window.chrome.webview`.

Mark UI roots that should explicitly receive mouse input with:

```html
<button data-web-hit>Open panel</button>
```

Unmatched transparent areas continue passing mouse input to Unreal. The included [DOM drag-and-drop grid sample](samples/drag-drop-grid.html) is useful for validating pointer and drag behavior.

## Unreal integration

The Unreal side remains focused on native rendering, scenes, and application logic. A lightweight bridge client should:

1. Read `-BridgePort`, `-BridgeToken`, and `-ParentSessionId` from the command line.
2. Connect to the WPF host's loopback WebSocket.
3. Send and receive `digital-twin-v1` message envelopes.
4. Use latest-only delivery or ACK backpressure for high-frequency state so stale camera frames cannot queue.

The bridge is completely independent of Unreal's Web Browser widget. It does not embed Chromium in the engine and does not require a commercial browser plugin.

For copy-ready Unreal C++ and JavaScript examples, including module dependencies, authenticated WebSocket setup, request/response handling, WebView2 calls, and camera-frame ACK backpressure, see the **[communication integration guide](docs/COMMUNICATION.md)** and [`bridge-client.js`](samples/web/bridge-client.js).

## Configuration

Key settings in `appsettings.json`:

| Setting | Purpose |
| --- | --- |
| `Web.Url` | Default HTTP/HTTPS page to load |
| `Web.TransparentBackground` | `auto`, `true`, or `false` background behavior |
| `Web.EnableAutomaticHitRegions` | Detect and synchronize interactive DOM regions |
| `Unreal.Enabled` | Enable Unreal launch and window integration |
| `Unreal.ExecutablePath` | Default packaged Unreal executable path |
| `Unreal.EditorDebugMode` | Attach to a Standalone Game window instead of launching an EXE |
| `Unreal.EditorStandaloneWindowTitle` | Window title used for Editor attachment |

## Repository layout

```text
src/DigitalTwin.Host/   WPF host, WebView2 overlay, bridge, and Win32 integration
samples/                Standalone web behavior samples
docs/COMMUNICATION.md   Unreal C++ and JavaScript communication guide
```

Packaged Unreal builds, local publish outputs, and test captures are intentionally excluded from Git. This repository does not redistribute Unreal Engine or Microsoft Edge WebView2 Runtime binaries.
