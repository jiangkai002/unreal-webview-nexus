using System.Buffers;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DigitalTwin.Host.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalTwin.Host.Services;

public sealed class BridgeWebSocketServer : IDisposable, IAsyncDisposable
{
    private const string BridgeSubProtocol = "digital-twin-v1";
    private readonly object _socketLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly UTF8Encoding _strictUtf8 = new(false, true);
    private WebApplication? _application;
    private WebSocket? _socket;
    private bool _disposed;

    public BridgeConnectionInfo? ConnectionInfo { get; private set; }

    public Func<string, CancellationToken, Task>? MessageReceivedAsync { get; set; }

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected
    {
        get
        {
            lock (_socketLock)
            {
                return _socket?.State == WebSocketState.Open;
            }
        }
    }

    public async Task<BridgeConnectionInfo> StartAsync(
        CancellationToken cancellationToken,
        BridgeConnectionInfo? fixedConnection = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ConnectionInfo is not null)
        {
            return ConnectionInfo;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, fixedConnection?.Port ?? 0));

        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
        application.Map("/unreal", HandleWebSocketRequestAsync);

        _application = application;
        await application.StartAsync(cancellationToken);

        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("无法获取 Bridge WebSocket 监听地址。");
        var uri = new Uri(address);

        ConnectionInfo = fixedConnection ?? new BridgeConnectionInfo(
            uri.Port,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Guid.NewGuid());
        return ConnectionInfo;
    }

    public async Task<bool> SendAsync(string message, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(message) > BridgeProtocol.MaximumMessageBytes)
        {
            return false;
        }

        WebSocket? socket;
        lock (_socketLock)
        {
            socket = _socket;
        }

        if (socket?.State != WebSocketState.Open)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MessageReceivedAsync = null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        WebSocket? socket;
        lock (_socketLock)
        {
            socket = _socket;
            _socket = null;
        }

        try
        {
            if (socket?.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "WPF host is stopping",
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or WebSocketException)
        {
            // Best-effort shutdown.
        }
        finally
        {
            socket?.Dispose();
        }

        try
        {
            if (_application is not null)
            {
                await _application.StopAsync(timeout.Token).ConfigureAwait(false);
                await _application.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException)
        {
            // Best-effort shutdown. The OS will release remaining listener resources.
        }

        _sendLock.Dispose();
    }

    private async Task HandleWebSocketRequestAsync(HttpContext context)
    {
        if (ConnectionInfo is null || !HasValidToken(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(
                BridgeSubProtocol,
                StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        lock (_socketLock)
        {
            if (_socket?.State is WebSocketState.Open or WebSocketState.Connecting)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(BridgeSubProtocol);
        lock (_socketLock)
        {
            _socket = socket;
        }

        ConnectionChanged?.Invoke(true);
        try
        {
            await ReceiveLoopAsync(socket, context.RequestAborted);
        }
        finally
        {
            lock (_socketLock)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
            }

            ConnectionChanged?.Invoke(false);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Connection closed",
                            cancellationToken);
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only text messages are supported",
                            cancellationToken);
                        return;
                    }

                    if (message.Length + result.Count > BridgeProtocol.MaximumMessageBytes)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds 1 MiB",
                            cancellationToken);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string text;
                try
                {
                    text = _strictUtf8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                }
                catch (DecoderFallbackException)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.InvalidPayloadData,
                        "Message is not valid UTF-8",
                        cancellationToken);
                    return;
                }

                var receiver = MessageReceivedAsync;
                if (receiver is not null)
                {
                    await receiver(text, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host or connection shutdown.
        }
        catch (WebSocketException)
        {
            // The connection-state notification is raised by the caller's finally block.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool HasValidToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var expected = $"Bearer {ConnectionInfo!.Token}";
        var actualBytes = Encoding.UTF8.GetBytes(authorization);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
