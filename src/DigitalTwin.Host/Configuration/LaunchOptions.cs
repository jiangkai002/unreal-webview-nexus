namespace DigitalTwin.Host.Configuration;

public sealed record LaunchOptions(Uri? StartUri, string? UnrealExecutablePath, string? ErrorMessage)
{
    private const string UrlEnvironmentVariable = "DIGITALTWIN_WEB_URL";

    public static LaunchOptions Parse(IReadOnlyList<string> arguments, ClientOptions options)
    {
        string? candidate = null;
        string? unrealExecutable = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index].Trim();
            if (argument.Equals("--url", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count)
                {
                    return new LaunchOptions(null, null, "--url 参数后缺少网址。");
                }

                candidate = arguments[++index];
                continue;
            }

            if (argument.Equals("--unreal", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count)
                {
                    return new LaunchOptions(null, null, "--unreal 参数后缺少 EXE 路径。");
                }

                unrealExecutable = arguments[++index];
                continue;
            }

            if (argument.StartsWith("--unreal=", StringComparison.OrdinalIgnoreCase))
            {
                unrealExecutable = argument[9..];
                continue;
            }

            if (argument.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                candidate = argument[6..];
                continue;
            }

            if (!argument.StartsWith('-') && candidate is null)
            {
                candidate = argument;
            }
        }

        candidate = FirstNonEmpty(
            candidate,
            Environment.GetEnvironmentVariable(UrlEnvironmentVariable),
            options.Web.Url);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return new LaunchOptions(
                null,
                unrealExecutable,
                "未提供 Web URL。\n\n启动方式：\nDigitalTwinClient.exe https://your-site.example\n\n或：\nDigitalTwinClient.exe --url https://your-site.example");
        }

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new LaunchOptions(null, unrealExecutable, $"Web URL 无效，仅支持 http:// 或 https:// 地址：\n{candidate}");
        }

        return new LaunchOptions(uri, unrealExecutable, null);
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
