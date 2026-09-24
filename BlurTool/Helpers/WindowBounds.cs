using System.Runtime.InteropServices;

namespace BlurTool.Helpers;

/// <summary>
/// 窗口矩形计算。
/// GetWindowRect 包含 Windows 的隐形缩放边框（约 8px），而捕获到的画面是窗口可见区域，
/// 直接用它会导致叠加层比实际窗口大一圈，模糊范围看起来就跑偏了。
/// </summary>
internal static class WindowBounds
{
    /// <summary>取窗口可见区域（屏幕坐标）；失败时回退到 GetWindowRect。</summary>
    public static bool TryGetVisibleRect(IntPtr hwnd, out NativeMethods.RECT rect)
    {
        rect = default;

        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int size = Marshal.SizeOf<NativeMethods.RECT>();
            int hr = NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var value, size);

            if (hr == 0 && value.Width > 0 && value.Height > 0)
            {
                rect = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        return NativeMethods.GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>取可见区域相对 GetWindowRect 的偏移，用于裁剪整窗截图。</summary>
    public static (int Left, int Top) GetVisibleOffset(IntPtr hwnd)
    {
        NativeMethods.RECT deviceRect;
        if (!NativeMethods.GetWindowRect(hwnd, out deviceRect)) return (0, 0);

        NativeMethods.RECT visibleRect;
        if (!TryGetVisibleRect(hwnd, out visibleRect)) return (0, 0);

        return (visibleRect.left - deviceRect.left, visibleRect.top - deviceRect.top);
    }
}
