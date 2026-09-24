using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BlurTool.Helpers;

/// <summary>DXGI Desktop Duplication：捕获显示器画面后裁剪到目标窗口区域。</summary>
internal sealed class DesktopDuplicationCapture : IDisposable
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private readonly D3D11CaptureDevice _d3d;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat = Format.Unknown;

    private IntPtr _monitor;
    private NativeMethods.RECT _monitorRect;
    private bool _initialized;
    private bool _unsupported;
    private bool _disposed;

    private byte[]? _lastFrame;
    private int _lastWidth;
    private int _lastHeight;

    public DesktopDuplicationCapture(D3D11CaptureDevice d3d)
    {
        _d3d = d3d;
    }

    public byte[]? Capture(IntPtr hwnd, out int width, out int height)
    {
        width = _lastWidth;
        height = _lastHeight;

        if (_disposed || _unsupported) return _lastFrame;

        // 裁剪窗口可见区域，与叠加层的选区保持一致。
        if (!WindowBounds.TryGetVisibleRect(hwnd, out var windowRect) ||
            windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            return null;
        }

        if (!EnsureInitialized(hwnd) || _duplication == null)
        {
            return _lastFrame;
        }

        // 目标窗口在显示器中的位置。
        int sourceX = windowRect.left - _monitorRect.left;
        int sourceY = windowRect.top - _monitorRect.top;
        int regionWidth = windowRect.Width;
        int regionHeight = windowRect.Height;

        if (sourceX < 0)
        {
            regionWidth += sourceX;
            sourceX = 0;
        }

        if (sourceY < 0)
        {
            regionHeight += sourceY;
            sourceY = 0;
        }

        int maxWidth = _monitorRect.Width - sourceX;
        int maxHeight = _monitorRect.Height - sourceY;
        regionWidth = Math.Min(regionWidth, maxWidth);
        regionHeight = Math.Min(regionHeight, maxHeight);

        if (regionWidth <= 0 || regionHeight <= 0) return _lastFrame;

        IDXGIResource? desktopResource = null;
        bool acquired = false;

        try
        {
            // 超时设得很短，避免在 UI 线程上阻塞；没有新帧时直接复用上一帧。
            var result = _duplication.AcquireNextFrame(1, out _, out desktopResource);

            if (result.Failure)
            {
                if (result.Code == DxgiErrorAccessLost)
                {
                    // 分辨率切换 / 安全桌面等场景会丢帧，需要重建 duplication。
                    ResetDuplication();
                }
                else if (result.Code != DxgiErrorWaitTimeout)
                {
                    Log.Write($"AcquireNextFrame failed: 0x{result.Code:X8}");
                }

                return _lastFrame;
            }

            acquired = true;

            using var desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            var format = desktopTexture.Description.Format;

            // 只支持 8 位 BGRA/RGBA；其它格式（如 HDR 的 10 位）直接放弃并回退。
            if (format != Format.B8G8R8A8_UNorm && format != Format.R8G8B8A8_UNorm)
            {
                Log.Write($"desktop duplication unsupported format: {format}");
                _unsupported = true;
                return _lastFrame;
            }

            EnsureStaging(regionWidth, regionHeight, format);

            _d3d.Context.CopySubresourceRegion(
                _staging!,
                0,
                0,
                0,
                0,
                desktopTexture,
                0,
                new Box(sourceX, sourceY, 0, sourceX + regionWidth, sourceY + regionHeight, 1));

            var pixels = ReadStaging(regionWidth, regionHeight);
            if (pixels != null)
            {
                _lastFrame = pixels;
                _lastWidth = regionWidth;
                _lastHeight = regionHeight;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            try { desktopResource?.Dispose(); } catch (Exception ex) { Log.Write(ex); }

            if (acquired)
            {
                try { _duplication?.ReleaseFrame(); } catch (Exception ex) { Log.Write(ex); }
            }
        }

        width = _lastWidth;
        height = _lastHeight;
        return _lastFrame;
    }

    private byte[]? ReadStaging(int width, int height)
    {
        var map = _d3d.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            var buffer = new byte[width * height * 4];
            int rowBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(map.DataPointer, (int)((uint)y * map.RowPitch)),
                    buffer,
                    y * rowBytes,
                    rowBytes);
            }

            return buffer;
        }
        finally
        {
            _d3d.Context.Unmap(_staging!, 0);
        }
    }

    private void EnsureStaging(int width, int height, Format format)
    {
        if (_staging != null && _stagingWidth == width && _stagingHeight == height && _stagingFormat == format)
        {
            return;
        }

        _staging?.Dispose();
        _staging = null;

        var description = new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1,
            0,
            ResourceOptionFlags.None);

        _staging = _d3d.Device.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
    }

    private bool EnsureInitialized(IntPtr hwnd)
    {
        if (_initialized) return _duplication != null;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        _monitor = monitor;
        _monitorRect = info.rcMonitor;
        _initialized = true;

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0;
                 factory.EnumAdapters1(adapterIndex, out var adapter).Success;
                 adapterIndex++)
            {
                try
                {
                    for (uint outputIndex = 0;
                         adapter.EnumOutputs(outputIndex, out var output).Success;
                         outputIndex++)
                    {
                        try
                        {
                            var bounds = output.Description.DesktopCoordinates;
                            if (bounds.Left != _monitorRect.left || bounds.Top != _monitorRect.top)
                            {
                                continue;
                            }

                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            _duplication = output1.DuplicateOutput(_d3d.Device);

                            if (_duplication != null)
                            {
                                _lastFrame = null;
                                _lastWidth = 0;
                                _lastHeight = 0;
                                Log.Write("desktop duplication initialised");
                                return true;
                            }
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        _initialized = false;
        return false;
    }

    private void ResetDuplication()
    {
        try { _duplication?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        _duplication = null;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ResetDuplication();

        try { _staging?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        _staging = null;
        _lastFrame = null;
    }
}
