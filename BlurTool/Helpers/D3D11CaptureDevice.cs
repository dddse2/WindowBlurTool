using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace BlurTool.Helpers;

/// <summary>Desktop Duplication 与 DwmSharedSurface 共用的 D3D11 设备。</summary>
internal sealed class D3D11CaptureDevice : IDisposable
{
    private static readonly object Gate = new();
    private static D3D11CaptureDevice? _shared;
    private static bool _unavailable;
    private bool _disposed;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>获取共享设备；创建失败时返回 null（调用方回退到 GDI）。</summary>
    public static D3D11CaptureDevice? GetShared()
    {
        lock (Gate)
        {
            if (_shared is { _disposed: false }) return _shared;
            if (_unavailable) return null;

            try
            {
                _shared = new D3D11CaptureDevice();
            }
            catch (Exception ex)
            {
                Log.Write($"D3D11 device creation failed: {ex.Message}");
                _unavailable = true;
                _shared = null;
            }

            return _shared;
        }
    }

    private D3D11CaptureDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_0 };

        if (TryCreate(DriverType.Hardware, levels, out var device, out var context) ||
            TryCreate(DriverType.Warp, levels, out device, out context))
        {
            Device = device!;
            Context = context!;
            return;
        }

        throw new InvalidOperationException("无法创建 D3D11 设备。");
    }

    private static bool TryCreate(
        DriverType driverType,
        FeatureLevel[] levels,
        out ID3D11Device? device,
        out ID3D11DeviceContext? context)
    {
        device = null;
        context = null;

        try
        {
            var result = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                driverType,
                DeviceCreationFlags.BgraSupport,
                levels,
                out ID3D11Device created,
                out ID3D11DeviceContext createdContext);

            if (result.Failure)
            {
                created?.Dispose();
                createdContext?.Dispose();
                return false;
            }

            if (created == null || createdContext == null)
            {
                created?.Dispose();
                createdContext?.Dispose();
                return false;
            }

            device = created;
            context = createdContext;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (Gate)
        {
            if (ReferenceEquals(_shared, this))
            {
                _shared = null;
            }
        }

        try { Context.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        try { Device.Dispose(); } catch (Exception ex) { Log.Write(ex); }
    }
}
