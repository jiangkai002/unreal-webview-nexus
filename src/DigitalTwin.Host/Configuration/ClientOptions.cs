using System.IO;
using System.Text.Json;

namespace DigitalTwin.Host.Configuration;

public sealed class ClientOptions
{
    public WebOptions Web { get; init; } = new();

    public WindowOptions Window { get; init; } = new();

    public UnrealOptions Unreal { get; init; } = new();

    public static ClientOptions Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ClientOptions();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ClientOptions>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new ClientOptions();
    }
}

public sealed class WebOptions
{
    public string Url { get; init; } = string.Empty;

    public bool EnableDevTools { get; init; } = true;

    public bool EnableDefaultContextMenus { get; init; } = true;

    public bool EnableAutomaticHitRegions { get; init; } = true;

    public string TransparentBackground { get; init; } = "auto";
}

public sealed class WindowOptions
{
    public string Title { get; init; } = "Digital Twin Web Viewer";

    public double Width { get; init; } = 1440;

    public double Height { get; init; } = 900;

    public bool StartMaximized { get; init; } = true;
}

public sealed class UnrealOptions
{
    public bool Enabled { get; init; }

    // Editor 联调模式：启动固定端口 Bridge，并自动挂接 Standalone Game 窗口。
    public bool EditorDebugMode { get; init; }

    public string EditorStandaloneWindowTitle { get; init; } = "RemViewer";

    public string ExecutablePath { get; init; } = string.Empty;

    public int StartupTimeoutSeconds { get; init; } = 60;

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;
}
