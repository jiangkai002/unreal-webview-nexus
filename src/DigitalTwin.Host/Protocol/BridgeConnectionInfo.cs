namespace DigitalTwin.Host.Protocol;

public sealed record BridgeConnectionInfo(int Port, string Token, Guid SessionId);
