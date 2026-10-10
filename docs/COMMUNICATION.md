# Web ↔ WPF ↔ Unreal communication guide

**English** | [简体中文](COMMUNICATION.zh-CN.md)

The browser must not open the localhost WebSocket directly. JavaScript sends a WebView2 web message to WPF; WPF validates and forwards it through the authenticated `digital-twin-v1` WebSocket to Unreal. Messages from Unreal travel in the opposite direction.

```text
JavaScript ─ window.chrome.webview ─ WPF ─ WebSocket ─ Unreal C++
```

## 1. Add Unreal module dependencies

Add these modules to your game's or plugin's `.Build.cs` file:

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

## 2. Connect Unreal to the host

The WPF host automatically passes `-BridgePort`, `-BridgeToken`, and `-ParentSessionId` when it launches the packaged game. Read them and create the socket in a `UGameInstanceSubsystem`, game-instance object, or another object whose lifetime covers the application session.

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
    // Must match MainWindow.EditorDebugConnection. Development/loopback only.
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
        // WebSocket callbacks must not mutate Actors directly.
        AsyncTask(ENamedThreads::GameThread, [Message]()
        {
            HandleBridgeMessageOnGameThread(Message);
        });
    });
    BridgeSocket->Connect();
    return true;
}
```

The functions referenced above serialize the common protocol envelope. `payload` must contain valid JSON.

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
    Envelope->SetStringField(TEXT("id"), RequestId); // Must match the request ID.
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

Parse requests on the game thread and return a response with the same `id` and `type`:

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
            // Find and select the Actor here. This code is now on the game thread.
            SendBridgeResponse(Id, Type, true, TEXT("{\"accepted\":true}"));
        }
    }
}
```

Call the connection function during initialization, close and reset the socket during shutdown, and implement bounded exponential reconnect delays for production use.

For example, a `UGameInstanceSubsystem` can own the connection lifecycle:

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

## 3. Send and receive in JavaScript

Copy [`samples/web/bridge-client.js`](../samples/web/bridge-client.js) into your frontend. JavaScript communicates with WPF, not with `ws://127.0.0.1`:

```js
import { UnrealBridgeClient } from "./bridge-client.js";

const unreal = new UnrealBridgeClient();

// Web -> Unreal request. Unreal returns a response with the same ID.
const result = await unreal.request("actor.select", {
  guid: "3e713d",
  focus: true,
});
console.log("Unreal accepted selection", result);

// Unreal -> Web event.
unreal.on("actor.selected", (payload) => {
  console.log("Selected in Unreal", payload.guid);
});

unreal.on("system.bridgeState", ({ connected }) => {
  console.log("Unreal bridge connected:", connected);
});
```

For `camera.changed`, acknowledge only after the camera data has been applied to the DOM. This lets WPF and Unreal keep only one in-flight frame and one latest pending frame instead of building a latency queue:

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

Only types in [`BridgeProtocol.AllowedMessageTypes`](../src/DigitalTwin.Host/Protocol/BridgeProtocol.cs) are forwarded. Add new application message types to that allowlist before using them.

When the page refreshes or navigates, WPF automatically releases any unacknowledged camera frame owned by the old document, retains only the latest state while loading, and resumes delivery after the new document has loaded. A refresh therefore cannot permanently stall `camera.changed` ACK backpressure.
