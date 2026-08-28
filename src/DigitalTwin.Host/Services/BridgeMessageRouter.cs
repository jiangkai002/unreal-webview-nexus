using System.Text;
using DigitalTwin.Host.Protocol;

namespace DigitalTwin.Host.Services;

public sealed class BridgeMessageRouter : IDisposable
{
    private readonly BridgeWebSocketServer _server;
    private readonly Func<string, Task> _postToWebAsync;
    private readonly object _cameraLock = new();
    private string? _latestPendingCameraMessage;
    private bool _cameraMessageInFlight;
    private bool _disposed;

    public BridgeMessageRouter(
        BridgeWebSocketServer server,
        Func<string, Task> postToWebAsync)
    {
        _server = server;
        _postToWebAsync = postToWebAsync;
        _server.MessageReceivedAsync = RouteUnrealMessageAsync;
        _server.ConnectionChanged += Server_OnConnectionChanged;
    }

    public async Task RouteWebMessageAsync(string json, CancellationToken cancellationToken)
    {
        if (_disposed || Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MaximumMessageBytes)
        {
            return;
        }

        if (!BridgeProtocol.TryReadMetadata(json, out var metadata, out _))
        {
            return;
        }

        if (metadata.Type.StartsWith("host.", StringComparison.Ordinal))
        {
            return;
        }

        if (!BridgeProtocol.IsAllowedMessageType(metadata.Type))
        {
            await ReplyToRejectedRequestAsync(
                metadata,
                "MESSAGE_TYPE_NOT_ALLOWED",
                $"WPF 不允许转发消息：{metadata.Type}。");
            return;
        }

        if (!_server.IsConnected)
        {
            await ReplyToRejectedRequestAsync(
                metadata,
                "UNREAL_DISCONNECTED",
                "Unreal 尚未连接到 WPF Bridge。");
            return;
        }

        if (!await _server.SendAsync(json, cancellationToken))
        {
            await ReplyToRejectedRequestAsync(
                metadata,
                "BRIDGE_SEND_FAILED",
                "WPF 向 Unreal 发送消息失败。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_cameraLock)
        {
            _latestPendingCameraMessage = null;
            _cameraMessageInFlight = false;
        }
        _server.ConnectionChanged -= Server_OnConnectionChanged;
        _server.MessageReceivedAsync = null;
    }

    public async Task AcknowledgeCameraMessageAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _server.SendAsync(
            BridgeProtocol.CreateEvent("camera.consumed", new { }),
            CancellationToken.None);

        string? nextMessage;
        lock (_cameraLock)
        {
            nextMessage = _latestPendingCameraMessage;
            _latestPendingCameraMessage = null;
            if (nextMessage is null)
            {
                _cameraMessageInFlight = false;
                return;
            }
        }

        await PostCameraMessageAsync(nextMessage);
    }

    private async Task RouteUnrealMessageAsync(string json, CancellationToken cancellationToken)
    {
        if (_disposed ||
            Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MaximumMessageBytes ||
            !BridgeProtocol.TryReadMetadata(json, out var metadata, out _) ||
            metadata.Type.StartsWith("host.", StringComparison.Ordinal) ||
            !BridgeProtocol.IsAllowedMessageType(metadata.Type))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.Type == "camera.changed")
        {
            string? messageToPost = null;
            lock (_cameraLock)
            {
                if (_cameraMessageInFlight)
                {
                    _latestPendingCameraMessage = json;
                }
                else
                {
                    _cameraMessageInFlight = true;
                    messageToPost = json;
                }
            }

            if (messageToPost is not null)
            {
                await PostCameraMessageAsync(messageToPost);
            }
            return;
        }

        await _postToWebAsync(json);
    }

    private async Task PostCameraMessageAsync(string json)
    {
        try
        {
            await _postToWebAsync(json);
        }
        catch
        {
            lock (_cameraLock)
            {
                _cameraMessageInFlight = false;
            }
            throw;
        }
    }

    private void Server_OnConnectionChanged(bool connected)
    {
        _ = NotifyConnectionChangedAsync(connected);
    }

    private async Task NotifyConnectionChangedAsync(bool connected)
    {
        try
        {
            await _postToWebAsync(BridgeProtocol.CreateEvent(
                "system.bridgeState",
                new { connected }));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            // WebView is already shutting down.
        }
    }

    private Task ReplyToRejectedRequestAsync(
        BridgeMessageMetadata metadata,
        string code,
        string message)
    {
        return metadata.Kind == "request"
            ? _postToWebAsync(BridgeProtocol.CreateErrorResponse(metadata, code, message))
            : Task.CompletedTask;
    }
}
