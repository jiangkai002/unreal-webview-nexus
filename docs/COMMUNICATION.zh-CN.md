# Web ↔ WPF ↔ Unreal 通信接入指南

[English](COMMUNICATION.md) | **简体中文**

网页不要直接连接本机 WebSocket。JavaScript 通过 WebView2 WebMessage 把消息交给 WPF，WPF 校验后使用带鉴权的 `digital-twin-v1` WebSocket 转发给 Unreal。Unreal 发出的消息按相反路径返回。

```text
JavaScript ─ window.chrome.webview ─ WPF ─ WebSocket ─ Unreal C++
```

## 1. 添加 Unreal 模块依赖

在游戏模块或插件的 `.Build.cs` 文件中加入以下模块：

```csharp
PublicDependencyModuleNames.AddRange(new[]
{
    "Core",
    "CoreUObject",
    "Engine",
    "Json",
    "JsonUtilities",
    "WebSockets"
});
```

## 2. 让 Unreal 连接宿主

WPF 宿主启动打包后的游戏时，会自动传入 `-BridgePort`、`-BridgeToken` 和 `-ParentSessionId`。在 `UGameInstanceSubsystem`、Game Instance 对象或其他贯穿应用会话生命周期的对象中读取参数并创建 Socket。

```cpp
#include "Async/Async.h"
#include "IWebSocket.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"
#include "WebSocketsModule.h"

TSharedPtr<IWebSocket> BridgeSocket;

void SendBridgeEvent(const FString& Type, const FString& PayloadJson);
void HandleBridgeMessageOnGameThread(const FString& Message);

bool ConnectToNexus()
{
    int32 BridgePort = 0;
    FString BridgeToken;
    FString ParentSessionId;
    FParse::Value(FCommandLine::Get(), TEXT("BridgePort="), BridgePort);
    FParse::Value(FCommandLine::Get(), TEXT("BridgeToken="), BridgeToken);
    FParse::Value(FCommandLine::Get(), TEXT("ParentSessionId="), ParentSessionId);

#if WITH_EDITOR
    // 必须与 MainWindow.EditorDebugConnection 一致，仅用于本机开发联调。
    if (BridgePort <= 0 || BridgeToken.IsEmpty())
    {
        BridgePort = 52317;
        BridgeToken = TEXT("remviewer-editor-debug-token-v1");
        ParentSessionId = TEXT("8f5d9a44-469e-4f9a-bc43-ff719644a501");
    }
#endif

    if (BridgePort <= 0 || BridgePort > 65535 || BridgeToken.IsEmpty())
    {
        UE_LOG(LogTemp, Error, TEXT("UnrealWebView Nexus bridge arguments are missing."));
        return false;
    }

    FWebSocketsModule& Module =
        FModuleManager::LoadModuleChecked<FWebSocketsModule>(TEXT("WebSockets"));

    const FString Url = FString::Printf(TEXT("ws://127.0.0.1:%d/unreal"), BridgePort);
    TMap<FString, FString> Headers;
    Headers.Add(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *BridgeToken));

    BridgeSocket = Module.CreateWebSocket(Url, TEXT("digital-twin-v1"), Headers);
    BridgeSocket->OnConnected().AddLambda([]()
    {
        UE_LOG(LogTemp, Log, TEXT("Connected to UnrealWebView Nexus."));
        SendBridgeEvent(TEXT("system.ready"), TEXT("{\"ready\":true}"));
    });
    BridgeSocket->OnConnectionError().AddLambda([](const FString& Error)
    {
        UE_LOG(LogTemp, Error, TEXT("Nexus connection failed: %s"), *Error);
    });
    BridgeSocket->OnMessage().AddLambda([](const FString& Message)
    {
        // WebSocket 回调不能直接修改 Actor。
        AsyncTask(ENamedThreads::GameThread, [Message]()
        {
            HandleBridgeMessageOnGameThread(Message);
        });
    });
    BridgeSocket->Connect();
    return true;
}
```

上面引用的函数负责序列化公共协议消息信封。`payload` 必须是有效 JSON。

```cpp
#include "Dom/JsonObject.h"
#include "Misc/DateTime.h"
#include "Misc/Guid.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

static double UnixTimeMilliseconds()
{
    const FDateTime Now = FDateTime::UtcNow();
    return static_cast<double>(Now.ToUnixTimestamp()) * 1000.0 + Now.GetMillisecond();
}

static TSharedPtr<FJsonValue> ParsePayload(const FString& PayloadJson)
{
    TSharedPtr<FJsonObject> Wrapper;
    const auto Reader = TJsonReaderFactory<>::Create(
        FString::Printf(TEXT("{\"value\":%s}"), *PayloadJson));
    if (FJsonSerializer::Deserialize(Reader, Wrapper) && Wrapper.IsValid())
    {
        return Wrapper->TryGetField(TEXT("value"));
    }
    return MakeShared<FJsonValueObject>(MakeShared<FJsonObject>());
}

void SendBridgeEvent(const FString& Type, const FString& PayloadJson)
{
    if (!BridgeSocket.IsValid() || !BridgeSocket->IsConnected()) return;

    const TSharedRef<FJsonObject> Envelope = MakeShared<FJsonObject>();
    Envelope->SetStringField(TEXT("version"), TEXT("1.0"));
    Envelope->SetStringField(TEXT("id"), FGuid::NewGuid().ToString());
    Envelope->SetStringField(TEXT("kind"), TEXT("event"));
    Envelope->SetStringField(TEXT("type"), Type);
    Envelope->SetNumberField(TEXT("timestamp"), UnixTimeMilliseconds());
    Envelope->SetField(TEXT("payload"), ParsePayload(PayloadJson));

    FString Json;
    const auto Writer = TJsonWriterFactory<>::Create(&Json);
    FJsonSerializer::Serialize(Envelope, Writer);
    BridgeSocket->Send(Json);
}

void SendBridgeResponse(
    const FString& RequestId,
    const FString& Type,
    bool bSuccess,
    const FString& PayloadJson)
{
    if (!BridgeSocket.IsValid() || !BridgeSocket->IsConnected()) return;

    const TSharedRef<FJsonObject> Envelope = MakeShared<FJsonObject>();
    Envelope->SetStringField(TEXT("version"), TEXT("1.0"));
    Envelope->SetStringField(TEXT("id"), RequestId); // 必须与请求 ID 一致。
    Envelope->SetStringField(TEXT("kind"), TEXT("response"));
    Envelope->SetStringField(TEXT("type"), Type);
    Envelope->SetNumberField(TEXT("timestamp"), UnixTimeMilliseconds());
    Envelope->SetBoolField(TEXT("success"), bSuccess);
    Envelope->SetField(TEXT("payload"), ParsePayload(PayloadJson));

    FString Json;
    const auto Writer = TJsonWriterFactory<>::Create(&Json);
    FJsonSerializer::Serialize(Envelope, Writer);
    BridgeSocket->Send(Json);
}
```

在 Game Thread 上解析请求，并使用相同的 `id` 和 `type` 返回响应：

```cpp
void HandleBridgeMessageOnGameThread(const FString& Message)
{
    TSharedPtr<FJsonObject> Envelope;
    const auto Reader = TJsonReaderFactory<>::Create(Message);
    if (!FJsonSerializer::Deserialize(Reader, Envelope) || !Envelope.IsValid()) return;

    FString Id, Kind, Type;
    if (!Envelope->TryGetStringField(TEXT("id"), Id) ||
        !Envelope->TryGetStringField(TEXT("kind"), Kind) ||
        !Envelope->TryGetStringField(TEXT("type"), Type)) return;

    if (Kind == TEXT("request") && Type == TEXT("actor.select"))
    {
        const TSharedPtr<FJsonObject>* Payload = nullptr;
        FString Guid;
        if (Envelope->TryGetObjectField(TEXT("payload"), Payload) &&
            Payload && (*Payload)->TryGetStringField(TEXT("guid"), Guid))
        {
            // 在这里查找并选中 Actor。当前代码已经运行在 Game Thread。
            SendBridgeResponse(Id, Type, true, TEXT("{\"accepted\":true}"));
        }
    }
}
```

在初始化阶段调用连接函数，在退出阶段关闭并释放 Socket；生产环境还应实现有上限的指数退避重连。

例如，可以由 `UGameInstanceSubsystem` 管理连接生命周期：

```cpp
void UYourBridgeSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
    ConnectToNexus();
}

void UYourBridgeSubsystem::Deinitialize()
{
    if (BridgeSocket.IsValid())
    {
        BridgeSocket->Close(1000, TEXT("Unreal is stopping"));
        BridgeSocket.Reset();
    }
    Super::Deinitialize();
}
```

## 3. 在 JavaScript 中收发消息

把 [`samples/web/bridge-client.js`](../samples/web/bridge-client.js) 复制到前端项目。JavaScript 与 WPF 通信，不直接连接 `ws://127.0.0.1`：

```js
import { UnrealBridgeClient } from "./bridge-client.js";

const unreal = new UnrealBridgeClient();

// Web -> Unreal 请求。Unreal 使用相同 ID 返回响应。
const result = await unreal.request("actor.select", {
  guid: "3e713d",
  focus: true,
});
console.log("Unreal 已接受选择", result);

// Unreal -> Web 事件。
unreal.on("actor.selected", (payload) => {
  console.log("Unreal 当前选中", payload.guid);
});

unreal.on("system.bridgeState", ({ connected }) => {
  console.log("Unreal Bridge 已连接：", connected);
});
```

处理 `camera.changed` 时，必须等相机数据真正应用到 DOM 后再确认。这样 WPF 和 Unreal 只保留一个正在处理的帧以及一个最新待处理帧，不会形成延迟队列：

```js
unreal.on("camera.changed", (camera) => {
  requestAnimationFrame(() => {
    try {
      updateAllOverlayPositions(camera);
    } finally {
      unreal.acknowledgeCameraFrame();
    }
  });
});
```

WPF 只会转发 [`BridgeProtocol.AllowedMessageTypes`](../src/DigitalTwin.Host/Protocol/BridgeProtocol.cs) 白名单中的类型。使用新的业务消息类型前，需要先将其加入白名单。
