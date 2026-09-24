using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace BlurTool.Helpers;

/// <summary>基于 Windows Graphics Capture 的窗口捕获。</summary>
internal sealed class WgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly CanvasDevice _device;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private bool _running;
    private bool _disposed;
    private readonly object _lock = new();

    public WgcCapture(CanvasDevice device, DispatcherQueue dispatcher)
    {
        _device = device;
    }

    public bool IsRunning => _running;

    /// <summary>是否成功关闭了捕获黄色边框。</summary>
    public bool BorderDisabled { get; private set; }

    /// <summary>每帧到达时在 UI 线程触发，参数为拥有独立纹理的位图。</summary>
    public event Action<CanvasBitmap>? FrameArrived;

    public bool Start(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_running || _disposed) return _running;
        }

        try
        {
            _item = CreateItemForWindow(hwnd);
            var size = _item.Size;
            if (size.Width <= 0 || size.Height <= 0) return false;

            // Create（而非 CreateFreeThreaded）会让 FrameArrived 在创建帧池的 UI 线程触发。
            _framePool = Direct3D11CaptureFramePool.Create(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);
            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;

            try
            {
                _session.IsBorderRequired = false;
                BorderDisabled = true;
            }
            catch (Exception ex)
            {
                BorderDisabled = false;
                Log.Write($"IsBorderRequired=false failed: {ex.Message}");
            }

            _session.StartCapture();

            lock (_lock)
            {
                _running = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Cleanup();
            return false;
        }
    }

    public void Resize(SizeInt32 size)
    {
        Direct3D11CaptureFramePool? pool;
        lock (_lock)
        {
            if (!_running || _disposed || _framePool == null) return;
            if (size.Width <= 0 || size.Height <= 0) return;
            pool = _framePool;
        }

        try
        {
            pool.Recreate(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_lock)
        {
            if (_disposed || !_running) return;
        }

        CanvasRenderTarget? target = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            using var source = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            int width = (int)source.SizeInPixels.Width;
            int height = (int)source.SizeInPixels.Height;
            if (width <= 0 || height <= 0) return;

            // 把帧拷贝到自有纹理，之后即使 frame 被回收也不影响显示。
            target = new CanvasRenderTarget(_device, width, height, 96);
            using (var drawingSession = target.CreateDrawingSession())
            {
                drawingSession.DrawImage(source);
            }

            var handler = FrameArrived;
            if (handler == null)
            {
                // 没有订阅者时立即释放，避免渲染目标泄漏。
                target.Dispose();
                return;
            }

            handler(target);
            target = null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            target?.Dispose();
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        IntPtr hstring = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            int hr = NativeMethods.WindowsCreateString(className, (uint)className.Length, out hstring);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = NativeMethods.RoGetActivationFactory(hstring, ref interopIid, out factoryPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = GraphicsCaptureItemIid;

            hr = interop.CreateForWindow(hwnd, ref itemIid, out IntPtr itemPtr);
            if (hr < 0 || itemPtr == IntPtr.Zero)
            {
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                throw new InvalidOperationException("CreateForWindow returned a null item.");
            }

            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }

            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }

    private void Cleanup()
    {
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? pool;

        lock (_lock)
        {
            session = _session;
            pool = _framePool;
            _session = null;
            _framePool = null;
            _item = null;
            _running = false;
        }

        try { session?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        try { pool?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Cleanup();
    }
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig]
    int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);

    [PreserveSig]
    int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
}
