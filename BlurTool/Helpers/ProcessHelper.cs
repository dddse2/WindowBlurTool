using System.Diagnostics;

namespace BlurTool.Helpers;

/// <summary>进程名解析与窗口查找。</summary>
internal static class ProcessHelper
{
    /// <summary>取窗口所属进程名，形如 notepad.exe；失败返回空字符串。</summary>
    public static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return string.Empty;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return string.Empty;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>枚举当前可见且有标题的顶层窗口所属进程名。</summary>
    public static List<string> EnumerateWindowProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;
                if (NativeMethods.IsIconic(hwnd)) return true;

                var name = GetProcessName(hwnd);
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>查找指定进程当前最靠前的可见窗口。</summary>
    public static IntPtr FindBestWindow(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;
                if (NativeMethods.IsIconic(hwnd)) return true;

                if (string.Equals(GetProcessName(hwnd), processName, StringComparison.OrdinalIgnoreCase))
                {
                    best = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        return best;
    }
}
