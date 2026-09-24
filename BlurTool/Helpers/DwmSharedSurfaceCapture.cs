using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlurTool.Helpers;

/// <summary>通过未公开的 user32!DwmGetDxSharedSurface 捕获窗口的 DWM 共享表面。</summary>
internal sealed class DwmSharedSurfaceCapture : IDisposable
{
    private readonly D3D11CaptureDevice _d3d;

    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat = Format.Unknown;

    private bool _unavailable;
    private bool _disposed;

    private byte[]? _lastFrame;
    private int _lastWidth;
    private int _lastHeight;

    public DwmSharedSurfaceCapture(D3D11CaptureDevice d3d)
    {
        _d3d = d3d;
    }

    /// <summary>该方式在当前系统上是否已经确定不可用。</summary>
    public bool IsUnavailable => _unavailable;

    public byte[]? Capture(IntPtr hwnd, out int width, out int height)
    {
        width = _lastWidth;
        height = _lastHeight;

        if (_disposed || _unavailable) return null;

        IntPtr surface;

        try
        {
            if (!NativeMethods.DwmGetDxSharedSurface(hwnd, out surface, out _, out _, out _, out _) ||
                surface == IntPtr.Zero)
            {
                return _lastFrame;
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            // 系统上不存在该未公开导出，直接标记不可用。
            Log.Write($"DwmGetDxSharedSurface not available: {ex.Message}");
            _unavailable = true;
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return _lastFrame;
        }

        try
        {
            using var surfaceTexture = _d3d.Device.OpenSharedResource<ID3D11Texture2D>(surface);
            if (surfaceTexture == null)
            {
                return _lastFrame;
            }

            var description = surfaceTexture.Description;
            int surfaceWidth = (int)description.Width;
            int surfaceHeight = (int)description.Height;
            if (surfaceWidth <= 0 || surfaceHeight <= 0) return _lastFrame;

            // 只支持 8 位 BGRA/RGBA；其它格式直接放弃并让调用方回退。
            if (description.Format != Format.B8G8R8A8_UNorm &&
                description.Format != Format.R8G8B8A8_UNorm)
            {
                Log.Write($"DwmSharedSurface unsupported format: {description.Format}");
                _unavailable = true;
                return _lastFrame;
            }

            EnsureStaging(surfaceWidth, surfaceHeight, description.Format);

            _d3d.Context.CopyResource(_staging!, surfaceTexture);

            var pixels = ReadStaging(surfaceWidth, surfaceHeight);
            if (pixels != null)
            {
                _lastFrame = pixels;
                _lastWidth = surfaceWidth;
                _lastHeight = surfaceHeight;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _unavailable = true;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _staging?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        _staging = null;
        _lastFrame = null;
    }
}
