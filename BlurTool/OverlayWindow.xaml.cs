using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using BlurTool.Helpers;
using BlurTool.Models;
using WinRT.Interop;

namespace BlurTool;

public sealed partial class OverlayWindow : Window
{
    private readonly CanvasDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly object _bitmapLock = new();
    private readonly List<Border> _vertexThumbs = new();

    private IntPtr _targetHwnd;
    private IntPtr _thisHwnd;
    private WindowCaptureMode _captureMode = WindowCaptureMode.GraphicsCapture;
    private int _blurRadius = 40;
    private int _frameRate = 30;
    private bool _isEditMode;
    private bool _isClosing;
    private List<Point> _points = CreateDefaultPolygon();
    private CanvasBitmap? _bitmap;
    private WgcCapture? _wgc;
    private int _lastWgcWidth;
    private int _lastWgcHeight;

    private DesktopDuplicationCapture? _desktopDuplication;
    private DwmSharedSurfaceCapture? _dwmSharedSurface;
    private int _captureFailures;

    // 带边距的模糊输入画布（边距用于给边缘模糊提供真实邻域）。
    private CanvasRenderTarget? _blurPad;
    private int _blurPadWidth;
    private int _blurPadHeight;

    private int _lastDipX = int.MinValue;
    private int _lastDipY = int.MinValue;
    private int _lastDipW = int.MinValue;
    private int _lastDipH = int.MinValue;
    private bool _hasShown;
    private int _childStyleTick;
    private bool _closeRequested;

    /// <summary>进入编辑模式之前是否处于模糊状态，用于退出编辑时恢复原状态。</summary>
    private bool _blurredBeforeEdit;
    private bool _stopped;

    // 子类化窗口过程，用于实现滚轮转发与非激活穿透。
    private NativeMethods.WndProcDelegate? _hookProc;
    private IntPtr _originalWndProc;

    /// <summary>用户在编辑模式下确认选区后触发，参数为归一化后的多边形顶点。</summary>
    public event EventHandler<IReadOnlyList<Point>>? RegionConfirmed;

    public bool IsEditMode => _isEditMode;

    /// <summary>当前叠加层处理的目标进程名。</summary>
    public string TargetProcessName => ProcessHelper.GetProcessName(_targetHwnd);

    public OverlayWindow()
    {
        InitializeComponent();

        _device = CanvasDevice.GetSharedDevice();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = new DispatcherTimer();
        _timer.Tick += OnTick;

        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        BlurCanvas.SizeChanged += OnCanvasSizeChanged;
    }

    public void AttachToWindow(
        IntPtr hwnd,
        int blurRadius,
        int frameRate,
        WindowCaptureMode captureMode,
        IReadOnlyList<Point> points,
        bool startInEditMode)
    {
        _targetHwnd = hwnd;
        _blurRadius = Math.Clamp(blurRadius, 0, 200);
        _frameRate = Math.Clamp(frameRate, 5, 120);
        _captureMode = captureMode;
        _points = NormalizePoints(points);
        _isEditMode = startInEditMode;

        // 直接进入编辑（此前未模糊）时，退出编辑应回到未模糊状态。
        _blurredBeforeEdit = !startInEditMode;

        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _frameRate);

        Log.Write($"AttachToWindow edit={startInEditMode} capture={_captureMode} points={_points.Count}");

        ConfigureWindowStyles();
        ApplyModeStyles();
        InitializeCaptureBackend();
        InstallHook();

        if (_thisHwnd == IntPtr.Zero)
        {
            // 拿不到 HWND 时直接放弃，绝不调用 Activate() 抢焦点，
            // 否则叠加层会成为前台窗口，导致滚轮与按键被吞掉。
            Log.Write("overlay hwnd is zero, skip");
            return;
        }

        // 先不显示窗口，等第一帧捕获完成后再显示，避免出现白屏。
        // 放到目标窗口上方（而非全局置顶），这样目标窗口被其它窗口遮挡时，
        // 模糊层也会一起被遮住。
        ApplyZOrder(NativeMethods.SWP_FRAMECHANGED);

        UpdateWindowBounds();
        RebuildVertexThumbs();
        UpdatePolygonVisuals();

        _timer.Start();
        BlurCanvas.Invalidate();
    }

    /// <summary>幂等关闭，避免重复 Close 抛异常。</summary>
    public void SafeClose()
    {
        if (_closeRequested) return;
        _closeRequested = true;

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>显示 WinUI 叠加窗口（模糊模式与编辑模式共用同一窗口）。</summary>
    private void EnsureVisible()
    {
        if (_hasShown || _isClosing || _closeRequested || _thisHwnd == IntPtr.Zero) return;

        _hasShown = true;
        NativeMethods.ShowWindow(_thisHwnd, NativeMethods.SW_SHOWNOACTIVATE);

        // 显示之后 WinUI 才会创建内容子窗口，这里再同步一次穿透样式。
        ApplyChildInputTransparency(!_isEditMode);
        ApplyZOrder(NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>实时更新模糊半径，无需重新打开叠加层。</summary>
    public void SetBlurAmount(int blurRadius)
    {
        if (_isClosing || _closeRequested) return;

        _blurRadius = Math.Clamp(blurRadius, 0, 200);
        BlurCanvas.Invalidate();
    }

    /// <summary>切换到编辑模式，显示可增删的多边形顶点。</summary>
    public void EnterEditMode()
    {
        if (_isEditMode || _isClosing || _closeRequested) return;

        try
        {
            _isEditMode = true;

            // 从模糊模式进入编辑，退出后应保持模糊。
            _blurredBeforeEdit = true;

            ApplyModeStyles();
            UpdateWindowBounds();
            RebuildVertexThumbs();
            UpdatePolygonVisuals();

            BlurCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _isEditMode = false;
            try { ApplyModeStyles(); } catch { }
        }
    }

    /// <summary>
    /// 退出编辑模式：保存选区。此前已在模糊则回到模糊模式，否则关闭叠加层。
    /// </summary>
    public void ConfirmEdit()
    {
        if (!_isEditMode || _isClosing || _closeRequested) return;

        _isEditMode = false;

        try
        {
            RegionConfirmed?.Invoke(this, _points.ToList());
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        if (!_blurredBeforeEdit)
        {
            SafeClose();
            RestoreTargetForeground();
            return;
        }

        ApplyModeStyles();
        RebuildVertexThumbs();
        UpdatePolygonVisuals();
        UpdateWindowBounds();
        BlurCanvas.Invalidate();
        RestoreTargetForeground();
    }

    /// <summary>退出编辑后把前台窗口还给目标窗口，保证快捷键仍然判定得到目标。</summary>
    private void RestoreTargetForeground()
    {
        try
        {
            if (_targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_targetHwnd)) return;

            if (NativeMethods.GetForegroundWindow() == _thisHwnd)
            {
                NativeMethods.SetForegroundWindow(_targetHwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private static List<Point> CreateDefaultPolygon()
    {
        return new List<Point>
        {
            new(0, 0),
            new(1, 0),
            new(1, 1),
            new(0, 1)
        };
    }

    private static List<Point> NormalizePoints(IReadOnlyList<Point>? points)
    {
        if (points == null || points.Count < 3)
        {
            return CreateDefaultPolygon();
        }

        var result = new List<Point>(points.Count);
        foreach (var point in points)
        {
            result.Add(new Point(
                Math.Clamp(point.X, 0, 1),
                Math.Clamp(point.Y, 0, 1)));
        }

        return result;
    }

    /// <summary>按设置初始化捕获后端；不可用时回退到 GDI。</summary>
    private void InitializeCaptureBackend()
    {
        _captureFailures = 0;

        switch (_captureMode)
        {
            case WindowCaptureMode.GraphicsCapture:
                StartWgc();
                break;

            case WindowCaptureMode.DesktopDuplication:
                var d3d = D3D11CaptureDevice.GetShared();
                if (d3d == null)
                {
                    Log.Write("DesktopDuplication unavailable, fallback to GDI");
                    _captureMode = WindowCaptureMode.GDI;
                    break;
                }

                _desktopDuplication = new DesktopDuplicationCapture(d3d);
                break;

            case WindowCaptureMode.DwmSharedSurface:
                var d3dShared = D3D11CaptureDevice.GetShared();
                if (d3dShared == null)
                {
                    Log.Write("DwmSharedSurface unavailable, fallback to GDI");
                    _captureMode = WindowCaptureMode.GDI;
                    break;
                }

                _dwmSharedSurface = new DwmSharedSurfaceCapture(d3dShared);
                break;
        }
    }

    private void StartWgc()
    {
        _wgc = new WgcCapture(_device, _dispatcher);
        _wgc.FrameArrived += OnWgcFrame;

        if (!_wgc.Start(_targetHwnd))
        {
            _wgc.FrameArrived -= OnWgcFrame;
            _wgc.Dispose();
            _wgc = null;
            _captureMode = WindowCaptureMode.GDI;
            return;
        }

        if (!_wgc.BorderDisabled)
        {
            // 无法关闭 WGC 黄色边框时，改用无边框的 GDI 捕获。
            Log.Write("WGC border not disabled, fallback to GDI");
            _wgc.FrameArrived -= OnWgcFrame;
            _wgc.Dispose();
            _wgc = null;
            _captureMode = WindowCaptureMode.GDI;
        }
    }

    private void ConfigureWindowStyles()
    {
        _thisHwnd = WindowNative.GetWindowHandle(this);
        if (_thisHwnd == IntPtr.Zero) return;

        // Border-less popup
        uint style = NativeMethods.GetWindowLong(_thisHwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
                   NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_MINIMIZEBOX);
        style |= NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(_thisHwnd, NativeMethods.GWL_STYLE, style);

        // Base extended styles: tool window + never activate.
        uint exStyle = NativeMethods.GetWindowLong(_thisHwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_thisHwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;

        // 对捕获 API 隐藏自身，避免桌面捕获到模糊后的画面。
        NativeMethods.SetWindowDisplayAffinity(_thisHwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>根据当前模式切换点击穿透、窗口区域与编辑控件显示。</summary>
    private void ApplyModeStyles()
    {
        if (_isClosing || _closeRequested) return;
        if (_thisHwnd == IntPtr.Zero) return;

        uint exStyle = NativeMethods.GetWindowLong(_thisHwnd, NativeMethods.GWL_EXSTYLE);

        // WS_EX_NOACTIVATE 两种模式都保留：窗口收到鼠标消息并不需要被激活，
        // 但一旦被激活抢走前台窗口，退出编辑后前台会落到 BlurTool 本体上，
        // 导致后续快捷键判定不到目标窗口而失效。
        if (_isEditMode)
        {
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }

        exStyle |= NativeMethods.WS_EX_NOACTIVATE;

        NativeMethods.SetWindowLong(_thisHwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // WinUI 的实际内容在一个子窗口里，只给顶层加 WS_EX_TRANSPARENT
        // 并不能让滚轮穿透，子窗口同样要处理。
        ApplyChildInputTransparency(!_isEditMode);

        EditLayer.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        EditToolbar.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;

        ApplyZOrder(NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 计算让叠加层紧贴目标窗口「上方」所需的 hWndInsertAfter。
    /// SetWindowPos 的 hWndInsertAfter 表示「排在我们前面的窗口」，
    /// 因此不能传目标窗口本身（那会把叠加层插到目标窗口下面），
    /// 而要传目标窗口上面的那个窗口。
    /// </summary>
    private void ResolveZOrder(out IntPtr anchor, out uint noZOrderFlag)
    {
        anchor = NativeMethods.HWND_TOP;
        noZOrderFlag = 0;

        if (_thisHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_targetHwnd))
        {
            // 目标窗口暂不可用时不要乱动层级。
            noZOrderFlag = NativeMethods.SWP_NOZORDER;
            return;
        }

        var above = NativeMethods.GetWindow(_targetHwnd, NativeMethods.GW_HWNDPREV);

        if (above == _thisHwnd)
        {
            // 已经紧贴目标窗口上方，保持现有 z 序。
            noZOrderFlag = NativeMethods.SWP_NOZORDER;
            return;
        }

        if (above != IntPtr.Zero)
        {
            anchor = above;
            return;
        }

        // 目标窗口已在所在分组的最上层。
        anchor = IsTopmostWindow(_targetHwnd) ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP;
    }

    private static bool IsTopmostWindow(IntPtr hwnd)
    {
        uint exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        return (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
    }

    /// <summary>
    /// 把 WS_EX_TRANSPARENT 同步到所有子窗口（WinUI 的内容窗口就在子窗口里），
    /// 否则点击虽然能穿透，滚轮仍会被子窗口吃掉。
    /// </summary>
    private void ApplyChildInputTransparency(bool transparent)
    {
        if (_thisHwnd == IntPtr.Zero) return;

        try
        {
            NativeMethods.EnumChildWindows(_thisHwnd, (child, _) =>
            {
                uint style = NativeMethods.GetWindowLong(child, NativeMethods.GWL_EXSTYLE);
                uint updated = transparent
                    ? style | NativeMethods.WS_EX_TRANSPARENT
                    : style & ~NativeMethods.WS_EX_TRANSPARENT;

                if (updated != style)
                {
                    NativeMethods.SetWindowLong(child, NativeMethods.GWL_EXSTYLE, updated);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>只调整 z 序，不改动位置与大小。</summary>
    private void ApplyZOrder(uint extraFlags = 0)
    {
        if (_thisHwnd == IntPtr.Zero) return;

        ResolveZOrder(out var anchor, out var noZOrderFlag);
        NativeMethods.SetWindowPos(_thisHwnd, anchor, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | extraFlags | noZOrderFlag);
    }

    private void InstallHook()
    {
        if (_thisHwnd == IntPtr.Zero || _originalWndProc != IntPtr.Zero) return;

        _hookProc = HookProc;

        try
        {
            _originalWndProc = NativeMethods.SetWindowLongPtr(
                _thisHwnd, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hookProc));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _originalWndProc = IntPtr.Zero;
            _hookProc = null;
        }
    }

    private void RemoveHook()
    {
        if (_thisHwnd == IntPtr.Zero || _originalWndProc == IntPtr.Zero) return;

        try
        {
            NativeMethods.SetWindowLongPtr(_thisHwnd, NativeMethods.GWLP_WNDPROC, _originalWndProc);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        _originalWndProc = IntPtr.Zero;
        _hookProc = null;
    }

    /// <summary>
    /// 模糊模式下让整个窗口（含 WinUI 子窗口）对鼠标透明，
    /// 并把误投递过来的滚轮消息转发给目标窗口。
    /// </summary>
    private IntPtr HookProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!_isEditMode)
            {
                switch (msg)
                {
                    case NativeMethods.WM_NCHITTEST:
                        return new IntPtr(NativeMethods.HTTRANSPARENT);

                    case NativeMethods.WM_MOUSEACTIVATE:
                        return new IntPtr(NativeMethods.MA_NOACTIVATE);

                    case NativeMethods.WM_MOUSEWHEEL:
                    case NativeMethods.WM_MOUSEHWHEEL:
                        ForwardWheel(msg, wParam, lParam);
                        return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        return NativeMethods.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 把滚轮消息转发给光标下方的目标窗口子窗口。
    /// 直接发给顶层窗口很多程序不会滚动，必须命中到实际控件。
    /// </summary>
    private void ForwardWheel(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!NativeMethods.IsWindow(_targetHwnd)) return;

        try
        {
            long value = lParam.ToInt64();

            var point = new NativeMethods.POINT
            {
                x = unchecked((short)(value & 0xFFFF)),
                y = unchecked((short)((value >> 16) & 0xFFFF))
            };

            NativeMethods.ScreenToClient(_targetHwnd, ref point);

            IntPtr target = NativeMethods.ChildWindowFromPointEx(
                _targetHwnd, point,
                NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPDISABLED);

            if (target == IntPtr.Zero) target = _targetHwnd;

            NativeMethods.SendMessage(target, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void OnWgcFrame(CanvasBitmap bitmap)
    {
        if (bitmap == null) return;

        if (_isClosing || _closeRequested)
        {
            bitmap.Dispose();
            return;
        }

        CanvasBitmap? old;
        lock (_bitmapLock)
        {
            old = _bitmap;
            _bitmap = bitmap;
        }

        // 释放旧位图即使失败也不能清掉刚存进去的新位图。
        try
        {
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        // 回调可能在窗口关闭后才执行，必须自带异常保护。
        _dispatcher.TryEnqueue(() =>
        {
            if (_isClosing || _closeRequested) return;

            try
            {
                UpdateWindowBounds();
                if (_isEditMode)
                {
                    PositionVertexThumbs();
                    UpdatePolygonVisuals();
                }
                EnsureVisible();
                BlurCanvas.Invalidate();
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        });
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // 注意：这里不能带 SWP_SHOWWINDOW，否则会不断把模糊模式下的
        // WinUI 叠加窗口重新显示出来（表现为整片纯白）。
        if (args.DidSizeChange || args.DidPositionChange)
        {
            ApplyZOrder();
        }
    }

    private void OnTick(object? sender, object? e)
    {
        try
        {
        if (_isClosing || _closeRequested) return;

        if (!NativeMethods.IsWindow(_targetHwnd) || !NativeMethods.IsWindowVisible(_targetHwnd))
        {
            SafeClose();
            return;
        }

        if (!WindowBounds.TryGetVisibleRect(_targetHwnd, out var rc) || rc.Width <= 0 || rc.Height <= 0)
            return;

        // WinUI 内部可能重建内容子窗口，定期把穿透样式补回去。
        if (++_childStyleTick >= 30)
        {
            _childStyleTick = 0;
            ApplyChildInputTransparency(!_isEditMode);
        }

        if (_captureMode == WindowCaptureMode.GraphicsCapture && _wgc != null && _wgc.IsRunning)
        {
            if (rc.Width != _lastWgcWidth || rc.Height != _lastWgcHeight)
            {
                _lastWgcWidth = rc.Width;
                _lastWgcHeight = rc.Height;
                _wgc.Resize(new SizeInt32 { Width = rc.Width, Height = rc.Height });
            }

            // 画面渲染由 WGC 的 FrameArrived 驱动，但位置与层级要每个 tick 跟进，
            // 否则目标窗口被其它窗口覆盖时模糊层不会同步被盖住。
            UpdateWindowBounds();
            return;
        }

        var pixels = CaptureFrame(out int capWidth, out int capHeight);
        if (pixels != null)
        {
            var device = BlurCanvas.Device ?? _device;
            var bitmap = CanvasBitmap.CreateFromBytes(
                device,
                pixels,
                capWidth,
                capHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96f,
                CanvasAlphaMode.Ignore);

            CanvasBitmap? old;
            lock (_bitmapLock)
            {
                old = _bitmap;
                _bitmap = bitmap;
            }
            old?.Dispose();

            EnsureVisible();
        }

        UpdateWindowBounds();
        if (_isEditMode)
        {
            PositionVertexThumbs();
            UpdatePolygonVisuals();
        }
        BlurCanvas.Invalidate();
    }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>按当前捕获方式取一帧 BGRA 像素；连续失败则回退 GDI。</summary>
    private byte[]? CaptureFrame(out int width, out int height)
    {
        width = 0;
        height = 0;

        byte[]? pixels = null;

        switch (_captureMode)
        {
            case WindowCaptureMode.DesktopDuplication:
                pixels = _desktopDuplication?.Capture(_targetHwnd, out width, out height);
                break;

            case WindowCaptureMode.DwmSharedSurface:
                pixels = _dwmSharedSurface?.Capture(_targetHwnd, out width, out height);
                break;
        }

        if (pixels != null)
        {
            _captureFailures = 0;
            return pixels;
        }

        if (_captureMode != WindowCaptureMode.GDI)
        {
            _captureFailures++;
            if (_captureFailures < 60)
            {
                return null;
            }

            Log.Write($"{_captureMode} capture failed {_captureFailures} times, fallback to GDI");
            _captureMode = WindowCaptureMode.GDI;

            _desktopDuplication?.Dispose();
            _desktopDuplication = null;

            _dwmSharedSurface?.Dispose();
            _dwmSharedSurface = null;

            _captureFailures = 0;
        }

        return WindowCapture.CaptureWindow(_targetHwnd, out width, out height);
    }

    private void UpdateWindowBounds()
    {
        if (_isClosing || _closeRequested) return;
        if (_thisHwnd == IntPtr.Zero || _targetHwnd == IntPtr.Zero) return;

        // 用窗口可见区域（不含隐形缩放边框），才能和捕获到的画面严丝合缝。
        if (!WindowBounds.TryGetVisibleRect(_targetHwnd, out var rc) || rc.Width <= 0 || rc.Height <= 0)
            return;

        // 模糊与编辑都覆盖整个目标窗口，保证叠加层一定可见，
        // 多边形以外由 OnDraw 绘制原始画面。
        int x = rc.left;
        int y = rc.top;
        int w = rc.Width;
        int h = rc.Height;

        uint dpi = NativeMethods.GetDpiForWindow(_targetHwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        int dipX = (int)Math.Round(x / scale);
        int dipY = (int)Math.Round(y / scale);
        int dipW = (int)Math.Round(w / scale);
        int dipH = (int)Math.Round(h / scale);

        // 位置和尺寸没变时只重贴层级，避免每帧都做一次 MoveAndResize。
        if (dipX == _lastDipX && dipY == _lastDipY && dipW == _lastDipW && dipH == _lastDipH)
        {
            ApplyZOrder();
            return;
        }

        _lastDipX = dipX;
        _lastDipY = dipY;
        _lastDipW = dipW;
        _lastDipH = dipH;

        try
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(dipX, dipY, dipW, dipH));
        }
        catch (Exception ex)
        {
            // 窗口已关闭时 AppWindow 会抛异常，这里忽略即可。
            Log.Write(ex);
            return;
        }

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED;
        if (_hasShown)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        ResolveZOrder(out var anchor, out var noZOrderFlag);
        NativeMethods.SetWindowPos(_thisHwnd, anchor, x, y, w, h, flags | noZOrderFlag);
    }

    private void RebuildVertexThumbs()
    {
        foreach (var thumb in _vertexThumbs)
        {
            EditLayer.Children.Remove(thumb);
        }
        _vertexThumbs.Clear();

        if (!_isEditMode) return;

        for (int i = 0; i < _points.Count; i++)
        {
            var thumb = CreateVertexThumb(i);
            _vertexThumbs.Add(thumb);
            EditLayer.Children.Add(thumb);
        }

        PositionVertexThumbs();
    }

    private static Brush GetAccentBrush()
    {
        try
        {
            if (Application.Current.Resources["AccentFillColorDefaultBrush"] is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
            // fall through to default brush
        }

        return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    }

    private Border CreateVertexThumb(int index)
    {
        var thumb = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = GetAccentBrush(),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(2),
            Tag = index,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0.9
        };

        thumb.PointerEntered += Handle_PointerEntered;
        thumb.PointerExited += Handle_PointerExited;
        thumb.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        thumb.ManipulationDelta += Vertex_ManipulationDelta;
        thumb.RightTapped += Vertex_RightTapped;

        return thumb;
    }

    private void PositionVertexThumbs()
    {
        if (!_isEditMode) return;

        var size = BlurCanvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;

        double half = 8;

        for (int i = 0; i < _vertexThumbs.Count && i < _points.Count; i++)
        {
            var thumb = _vertexThumbs[i];
            double x = _points[i].X * size.X;
            double y = _points[i].Y * size.Y;

            // 视觉上让顶点不要紧贴窗口边缘，保证整个圆点都可见。
            x = Math.Clamp(x, half, Math.Max(half, size.X - half));
            y = Math.Clamp(y, half, Math.Max(half, size.Y - half));

            Canvas.SetLeft(thumb, x - half);
            Canvas.SetTop(thumb, y - half);
        }
    }

    private void UpdatePolygonVisuals()
    {
        var size = BlurCanvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;

        // PointCollection 不能在同一时刻被两个 Polygon 共享，这里分别创建。
        var polygonPoints = new PointCollection();
        var glowPoints = new PointCollection();
        foreach (var point in _points)
        {
            var p = new Point(point.X * size.X, point.Y * size.Y);
            polygonPoints.Add(p);
            glowPoints.Add(p);
        }

        SelectionPolygon.Points = polygonPoints;
        SelectionGlowPolygon.Points = glowPoints;

        RegionSizeText.Text = $"顶点：{_points.Count}";
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionVertexThumbs();
        UpdatePolygonVisuals();
    }

    private void Vertex_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_isEditMode) return;
        if (sender is not Border thumb || thumb.Tag is not int index) return;
        if (index < 0 || index >= _points.Count) return;

        var size = BlurCanvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;

        double dx = e.Delta.Translation.X / size.X;
        double dy = e.Delta.Translation.Y / size.Y;

        var point = _points[index];
        _points[index] = new Point(
            Math.Clamp(point.X + dx, 0, 1),
            Math.Clamp(point.Y + dy, 0, 1));

        UpdatePolygonVisuals();
        PositionVertexThumbs();
        BlurCanvas.Invalidate();
    }

    private void Vertex_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!_isEditMode) return;
        if (_points.Count <= 3) return;
        if (sender is not Border thumb || thumb.Tag is not int index) return;
        if (index < 0 || index >= _points.Count) return;

        _points.RemoveAt(index);
        RebuildVertexThumbs();
        UpdatePolygonVisuals();
        BlurCanvas.Invalidate();
    }

    private void Selection_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_isEditMode || _points.Count == 0) return;

        var size = BlurCanvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;

        double dx = e.Delta.Translation.X / size.X;
        double dy = e.Delta.Translation.Y / size.Y;

        double minX = _points.Min(p => p.X);
        double maxX = _points.Max(p => p.X);
        double minY = _points.Min(p => p.Y);
        double maxY = _points.Max(p => p.Y);

        dx = Math.Clamp(dx, -minX, 1 - maxX);
        dy = Math.Clamp(dy, -minY, 1 - maxY);

        for (int i = 0; i < _points.Count; i++)
        {
            _points[i] = new Point(_points[i].X + dx, _points[i].Y + dy);
        }

        UpdatePolygonVisuals();
        PositionVertexThumbs();
        BlurCanvas.Invalidate();
    }

    private void EditLayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isEditMode || _points.Count < 3) return;
        if (e.OriginalSource is Border) return;

        var size = BlurCanvas.ActualSize;
        if (size.X <= 0 || size.Y <= 0) return;

        var position = e.GetPosition(EditLayer);

        int insertIndex = -1;
        double bestDistance = double.MaxValue;
        Point bestPoint = default;

        for (int i = 0; i < _points.Count; i++)
        {
            var a = new Point(_points[i].X * size.X, _points[i].Y * size.Y);
            var b = new Point(_points[(i + 1) % _points.Count].X * size.X, _points[(i + 1) % _points.Count].Y * size.Y);

            var projected = ProjectPointOnSegment(position, a, b, out double t);
            double distance = Distance(position, projected);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPoint = projected;
                insertIndex = i + 1;
            }
        }

        // 双击任意位置：把该点投影到最近的边上，并按边的顺序插入新顶点。
        if (insertIndex < 0)
        {
            return;
        }

        var normalized = new Point(
            Math.Clamp(bestPoint.X / size.X, 0, 1),
            Math.Clamp(bestPoint.Y / size.Y, 0, 1));

        _points.Insert(insertIndex, normalized);
        RebuildVertexThumbs();
        UpdatePolygonVisuals();
        BlurCanvas.Invalidate();
    }

    private static Point ProjectPointOnSegment(Point p, Point a, Point b, out double t)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon)
        {
            t = 0;
            return a;
        }

        t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return new Point(a.X + t * dx, a.Y + t * dy);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>把模糊范围重置为整个窗口。</summary>
    public void ResetRegion()
    {
        _points = CreateDefaultPolygon();

        if (_isEditMode)
        {
            RebuildVertexThumbs();
            UpdatePolygonVisuals();
        }

        BlurCanvas.Invalidate();
    }

    private void ResetRegion_Click(object sender, RoutedEventArgs e)
    {
        ResetRegion();
    }

    private void ConfirmEdit_Click(object sender, RoutedEventArgs e)
    {
        ConfirmEdit();
    }

    private void Handle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.Opacity = 1.0;
        }
    }

    private void Handle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.Opacity = 0.9;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
        lock (_bitmapLock)
        {
            if (_bitmap == null) return;

            var size = sender.ActualSize;
            if (size.X <= 0 || size.Y <= 0) return;

            double bw = _bitmap.SizeInPixels.Width;
            double bh = _bitmap.SizeInPixels.Height;
            if (bw <= 0 || bh <= 0) return;

            var fullDest = new Rect(0, 0, size.X, size.Y);

            // 始终覆盖整个目标窗口：先画原画面，再把多边形区域替换为模糊结果。
            var displayBounds = new Rect(0, 0, 1, 1);
            var sourceRect = new Rect(0, 0, bw, bh);

            args.DrawingSession.DrawImage(_bitmap, fullDest, sourceRect);

            if (_isEditMode)
            {
                // 编辑模式额外压暗，方便看清手柄。
                args.DrawingSession.FillRectangle(fullDest, Windows.UI.Color.FromArgb(170, 0, 0, 0));
            }

            if (_points.Count >= 3 && _blurRadius > 0)
            {
                var device = sender.Device ?? _device;
                using var geometry = BuildGeometry(device, size, displayBounds);

                // 先把画面放进一张带边距的画布：边缘区域用拉伸后的原图填充，
                // 相当于给模糊提供了真实可采样的邻域内容，
                // 这样模糊在整块区域内强度才会是均一的，不会出现边缘瑕疵。
                double pad = Math.Min(_blurRadius, Math.Min(bw, bh) / 4.0);
                int padWidth = (int)Math.Ceiling(bw + pad * 2);
                int padHeight = (int)Math.Ceiling(bh + pad * 2);

                if (padWidth > 0 && padHeight > 0)
                {
                    EnsureBlurPad(device, _bitmap, padWidth, padHeight, bw, bh, pad);

                    using (args.DrawingSession.CreateLayer(1f, geometry))
                    {
                        using var blur = new GaussianBlurEffect
                        {
                            Source = _blurPad,
                            BlurAmount = (float)_blurRadius,
                            BorderMode = EffectBorderMode.Hard,
                            Optimization = EffectOptimization.Balanced
                        };

                        args.DrawingSession.DrawImage(blur, fullDest, new Rect(pad, pad, bw, bh));
                    }
                }
            }
        }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            try
            {
                args.DrawingSession.Clear(Microsoft.UI.Colors.Black);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// 把原图绘制到带边距的画布中心：边距区域用整体拉伸的原图填充，
    /// 相当于把窗口边缘的内容向外延伸，模糊时边缘才有真实邻域可采样。
    /// </summary>
    private void EnsureBlurPad(
        CanvasDevice device,
        CanvasBitmap bitmap,
        int padWidth,
        int padHeight,
        double bitmapWidth,
        double bitmapHeight,
        double pad)
    {
        if (_blurPad == null || _blurPadWidth != padWidth || _blurPadHeight != padHeight)
        {
            _blurPad?.Dispose();
            _blurPad = new CanvasRenderTarget(device, padWidth, padHeight, 96);
            _blurPadWidth = padWidth;
            _blurPadHeight = padHeight;
        }

        using var session = _blurPad.CreateDrawingSession();

        // 不透明底，避免透明边造成灰雾。
        session.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        var sourceRect = new Rect(0, 0, bitmapWidth, bitmapHeight);

        // 先整体拉伸铺满（含边距），边距区域即获得接近边缘的真实内容。
        session.DrawImage(bitmap, new Rect(0, 0, padWidth, padHeight), sourceRect);

        // 再把原尺寸画面画回中心，保证主区域内容完全准确。
        session.DrawImage(bitmap, new Rect(pad, pad, bitmapWidth, bitmapHeight), sourceRect);
    }

    private CanvasGeometry BuildGeometry(CanvasDevice device, Vector2 size, Rect bounds)
    {
        double boundWidth = bounds.Width <= 0 ? 1 : bounds.Width;
        double boundHeight = bounds.Height <= 0 ? 1 : bounds.Height;

        var points = new System.Numerics.Vector2[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            points[i] = new System.Numerics.Vector2(
                (float)((_points[i].X - bounds.X) / boundWidth * size.X),
                (float)((_points[i].Y - bounds.Y) / boundHeight * size.Y));
        }

        return CanvasGeometry.CreatePolygon(device, points);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _isClosing = true;
        Stop();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        Stop();
        BlurCanvas.RemoveFromVisualTree();
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;

        try { _timer.Stop(); } catch (Exception ex) { Log.Write(ex); }

        RemoveHook();

        if (_wgc != null)
        {
            _wgc.FrameArrived -= OnWgcFrame;
            try { _wgc.Dispose(); } catch (Exception ex) { Log.Write(ex); }
            _wgc = null;
        }

        try { _desktopDuplication?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        _desktopDuplication = null;

        try { _dwmSharedSurface?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        _dwmSharedSurface = null;

        lock (_bitmapLock)
        {
            try { _bitmap?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
            _bitmap = null;

            try { _blurPad?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
            _blurPad = null;
        }
    }
}
