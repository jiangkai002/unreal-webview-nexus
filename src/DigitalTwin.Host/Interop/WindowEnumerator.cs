using System.Runtime.InteropServices;
using System.Text;

namespace DigitalTwin.Host.Interop;

internal static class WindowEnumerator
{
    internal static IntPtr FindUnrealStandaloneWindow(string titleHint)
    {
        var bestWindow = IntPtr.Zero;
        var bestScore = 0L;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == Environment.ProcessId ||
                !NativeMethods.GetClientRect(hwnd, out var rect) ||
                rect.Width < 320 ||
                rect.Height < 200)
            {
                return true;
            }

            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (!className.ToString().Equals("UnrealWindow", StringComparison.Ordinal))
            {
                return true;
            }

            var title = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var titleText = title.ToString();
            if (titleText.Contains("Unreal Editor", StringComparison.OrdinalIgnoreCase) ||
                titleText.Contains("虚幻编辑器", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(titleHint) &&
                 !titleText.Contains(titleHint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var score = (long)rect.Width * rect.Height;
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    internal static IntPtr FindBestWindow(IReadOnlySet<uint> processIds)
    {
        var bestWindow = IntPtr.Zero;
        var bestScore = 0L;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains(processId) ||
                !NativeMethods.GetClientRect(hwnd, out var rect) ||
                rect.Width < 320 ||
                rect.Height < 200)
            {
                return true;
            }

            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            var unrealBonus = className.ToString().Equals("UnrealWindow", StringComparison.Ordinal)
                ? 1_000_000_000L
                : 0L;
            var score = unrealBonus + ((long)rect.Width * rect.Height);
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    internal static HashSet<uint> GetProcessTree(uint rootProcessId)
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return [rootProcessId];
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
                ExeFile = string.Empty
            };

            if (NativeMethods.Process32First(snapshot, ref entry))
            {
                do
                {
                    parents[entry.ProcessId] = entry.ParentProcessId;
                    entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        var result = new HashSet<uint> { rootProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parents)
            {
                if (!result.Contains(pair.Key) && result.Contains(pair.Value))
                {
                    result.Add(pair.Key);
                    changed = true;
                }
            }
        }

        return result;
    }
}
