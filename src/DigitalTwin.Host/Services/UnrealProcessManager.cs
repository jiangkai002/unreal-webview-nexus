using System.Diagnostics;
using System.IO;
using DigitalTwin.Host.Configuration;
using DigitalTwin.Host.Interop;
using DigitalTwin.Host.Protocol;

namespace DigitalTwin.Host.Services;

public sealed class UnrealProcessManager : IDisposable
{
    private Process? _launcherProcess;
    private Process? _renderProcess;

    public async Task<IntPtr> StartAsync(
        string executablePath,
        UnrealOptions options,
        BridgeConnectionInfo? bridgeConnection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("未配置 Unreal EXE 路径。");
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到 Unreal EXE。", fullPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-WINDOWED");
        startInfo.ArgumentList.Add("-NoSplash");
        startInfo.ArgumentList.Add("-ForceRes");
        startInfo.ArgumentList.Add($"-ResX={Math.Max(640, options.Width)}");
        startInfo.ArgumentList.Add($"-ResY={Math.Max(480, options.Height)}");
        if (bridgeConnection is not null)
        {
            startInfo.ArgumentList.Add($"-BridgePort={bridgeConnection.Port}");
            startInfo.ArgumentList.Add($"-BridgeToken={bridgeConnection.Token}");
            startInfo.ArgumentList.Add($"-ParentSessionId={bridgeConnection.SessionId:D}");
        }

        _launcherProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Unreal 程序。");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.StartupTimeoutSeconds, 10, 180));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processIds = WindowEnumerator.GetProcessTree((uint)_launcherProcess.Id);
            var hwnd = WindowEnumerator.FindBestWindow(processIds);
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var renderProcessId);
                _renderProcess = Process.GetProcessById((int)renderProcessId);
                return hwnd;
            }

            if (_launcherProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"Unreal 启动器提前退出，退出码：{_launcherProcess.ExitCode}。");
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"等待 Unreal 渲染窗口超时（{timeout.TotalSeconds:0} 秒）。");
    }

    public void Dispose()
    {
        StopProcess(_renderProcess);
        StopProcess(_launcherProcess);
        _renderProcess?.Dispose();
        _launcherProcess?.Dispose();
        _renderProcess = null;
        _launcherProcess = null;
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup during host shutdown.
        }
    }
}
