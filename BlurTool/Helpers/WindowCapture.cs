using System.Runtime.InteropServices;

namespace BlurTool.Helpers;

/// <summary>GDI 捕获：PrintWindow 优先，失败或全黑时回退 BitBlt。输出 BGRA 像素。</summary>
internal static class WindowCapture
{
    /// <summary>
    /// 捕获窗口画面，并裁剪到窗口可见区域，
    /// 保证叠加层的多边形选区与画面像素一一对应。
    /// </summary>
    public static byte[]? CaptureWindow(IntPtr hwnd, out int width, out int height)
    {
        var pixels = CaptureWholeWindow(hwnd, out int fullWidth, out int fullHeight);

        width = fullWidth;
        height = fullHeight;

        if (pixels == null || fullWidth <= 0 || fullHeight <= 0) return pixels;

        var (offsetX, offsetY) = WindowBounds.GetVisibleOffset(hwnd);
        if (offsetX == 0 && offsetY == 0) return pixels;

        if (!WindowBounds.TryGetVisibleRect(hwnd, out var visible)) return pixels;

        int cropWidth = Math.Min(visible.Width, fullWidth - offsetX);
        int cropHeight = Math.Min(visible.Height, fullHeight - offsetY);

        if (offsetX < 0 || offsetY < 0 || cropWidth <= 0 || cropHeight <= 0 ||
            (cropWidth == fullWidth && cropHeight == fullHeight))
        {
            return pixels;
        }

        var cropped = new byte[cropWidth * cropHeight * 4];

        for (int y = 0; y < cropHeight; y++)
        {
            int sourceOffset = ((y + offsetY) * fullWidth + offsetX) * 4;
            Buffer.BlockCopy(pixels, sourceOffset, cropped, y * cropWidth * 4, cropWidth * 4);
        }

        width = cropWidth;
        height = cropHeight;
        return cropped;
    }

    private static byte[]? CaptureWholeWindow(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            return null;

        NativeMethods.RECT rc;
        if (!NativeMethods.GetWindowRect(hwnd, out rc))
            return null;

        width = rc.Width;
        height = rc.Height;

        if (width <= 0 || height <= 0)
            return null;

        IntPtr hdcWindow = NativeMethods.GetWindowDC(hwnd);
        if (hdcWindow == IntPtr.Zero)
            return null;

        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcWindow, width, height);
        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        // 优先尝试捕获窗口自身的绘制结果，这样即使窗口被其他窗口部分遮挡，
        // 叠加层也能显示目标窗口的真实内容（更接近 Magpie 的处理方式）。
        bool captured = NativeMethods.PrintWindow(hwnd, hdcMem, NativeMethods.PW_RENDERFULLCONTENT);

        if (!captured)
        {
            NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        }

        var bih = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = (uint)(width * height * 4)
        };

        var bi = new NativeMethods.BITMAPINFO { bmiHeader = bih };
        byte[] pixels = new byte[width * height * 4];
        NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixels, ref bi, NativeMethods.DIB_RGB_COLORS);

        NativeMethods.SelectObject(hdcMem, hOld);
        NativeMethods.DeleteObject(hBitmap);
        NativeMethods.DeleteDC(hdcMem);
        NativeMethods.ReleaseDC(hwnd, hdcWindow);

        // PrintWindow 对部分硬件加速窗口会返回全黑画面，此时回退到 BitBlt。
        if (captured && IsBlank(pixels))
        {
            return CaptureByBitBlt(hwnd, out width, out height);
        }

        return pixels;
    }

    private static byte[]? CaptureByBitBlt(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        NativeMethods.RECT rc;
        if (!NativeMethods.GetWindowRect(hwnd, out rc))
            return null;

        width = rc.Width;
        height = rc.Height;

        if (width <= 0 || height <= 0)
            return null;

        IntPtr hdcWindow = NativeMethods.GetWindowDC(hwnd);
        if (hdcWindow == IntPtr.Zero)
            return null;

        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcWindow, width, height);
        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0,
            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

        var bih = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = (uint)(width * height * 4)
        };

        var bi = new NativeMethods.BITMAPINFO { bmiHeader = bih };
        byte[] pixels = new byte[width * height * 4];
        NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixels, ref bi, NativeMethods.DIB_RGB_COLORS);

        NativeMethods.SelectObject(hdcMem, hOld);
        NativeMethods.DeleteObject(hBitmap);
        NativeMethods.DeleteDC(hdcMem);
        NativeMethods.ReleaseDC(hwnd, hdcWindow);

        return pixels;
    }

    private static bool IsBlank(byte[] pixels)
    {
        // 采样检查是否全黑（PrintWindow 失败时的典型表现）。
        for (int i = 0; i < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
