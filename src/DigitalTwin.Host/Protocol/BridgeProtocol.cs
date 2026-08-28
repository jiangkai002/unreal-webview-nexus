using System.Text.Json;

namespace DigitalTwin.Host.Protocol;

public readonly record struct BridgeMessageMetadata(
    string Version,
    string Id,
    string Kind,
    string Type);

public static class BridgeProtocol
{
    public const string Version = "1.0";
    public const int MaximumMessageBytes = 1024 * 1024;

    private static readonly HashSet<string> AllowedMessageTypes = new(StringComparer.Ordinal)
    {
        "system.hello",
        "system.ready",
        "system.progress",
        "system.heartbeat",
        "system.shutdown",
        "system.bridgeState",
        "scene.load",
        "scene.loaded",
        "room.show",
        "path.show",
        "actor.select",
        "actor.selected",
        "actor.focus",
        "actor.hovered",
        "actor.setVisible",
        "actor.setColor",
        "camera.setLevel",
        "camera.setView",
        "camera.setDirection",
        "camera.setMode",
        "camera.changed",
        "camera.consumed",
        "device.show",
        "heatmap.show",
        "control.show",
        "light.show",
        "airCondition.show",
        "route.show",
        "roaming.begin",
        "exhibit.set",
        "exhibit.image.set",
        "model.setSection",
        "elevator.set",
        "overlay.updated",
        "alarm.locate"
    };

    public static bool IsAllowedMessageType(string type) => AllowedMessageTypes.Contains(type);

    public static bool TryReadMetadata(
        string json,
        out BridgeMessageMetadata metadata,
        out string error)
    {
        metadata = default;
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadRequiredString(root, "version", out var version) ||
                !TryReadRequiredString(root, "id", out var id) ||
                !TryReadRequiredString(root, "kind", out var kind) ||
                !TryReadRequiredString(root, "type", out var type))
            {
                error = "消息信封缺少 version、id、kind 或 type。";
                return false;
            }

            if (version != Version)
            {
                error = $"不支持的协议版本：{version}。";
                return false;
            }

            if (kind is not ("request" or "response" or "event"))
            {
                error = $"不支持的消息类型：{kind}。";
                return false;
            }

            metadata = new BridgeMessageMetadata(version, id, kind, type);
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string CreateErrorResponse(
        BridgeMessageMetadata request,
        string code,
        string message) =>
        JsonSerializer.Serialize(new
        {
            version = Version,
            id = request.Id,
            kind = "response",
            type = request.Type,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            success = false,
            error = new { code, message }
        });

    public static string CreateEvent(string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            version = Version,
            id = Guid.NewGuid().ToString("D"),
            kind = "event",
            type,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload
        });

    private static bool TryReadRequiredString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }
}
