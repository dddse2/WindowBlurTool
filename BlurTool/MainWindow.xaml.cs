using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using BlurTool.Helpers;
using BlurTool.Models;
using BlurTool.Pages;

namespace BlurTool;

public sealed partial class MainWindow : Window
{
    private readonly HotkeyManager _hotkeyManager;
    private OverlayWindow? _overlay;
    private DispatcherQueueTimer? _foregroundTimer;
    private string _lastTargetName = string.Empty;

    /// <summary>最近一次有效的目标窗口，前台落到自身窗口时用它兜底。</summary>
    private IntPtr _lastTargetHwnd = IntPtr.Zero;

    public static new MainWindow? Current { get; private set; }
    public AppSettings Settings { get; private set; }

    /// <summary>当前是否有活动的模糊叠加层。</summary>
    public bool IsBlurActive => _overlay != null;

    /// <summary>当前是否处于范围编辑模式。</summary>
    public bool IsEditModeActive => _overlay?.IsEditMode ?? false;

    /// <summary>是否已锁定到指定进程。</summary>
    public bool IsLocked => Settings.LockEnabled;

    /// <summary>锁定的目标进程名。</summary>
    public string LockedProcessName => Settings.LockedProcessName;

    /// <summary>当前会被处理的进程名。</summary>
    public string CurrentTargetProcessName
    {
        get
        {
            if (_overlay != null)
            {
                var name = _overlay.TargetProcessName;
                if (!string.IsNullOrEmpty(name)) return name;
            }

            if (Settings.LockEnabled) return Settings.LockedProcessName;

            return ProcessHelper.GetProcessName(ResolveTargetWindow());
        }
    }

    /// <summary>模糊开关状态变化时触发。</summary>
    public event EventHandler? BlurStateChanged;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;

        Settings = SettingsStorage.Load();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplySystemBackdrop();
        ApplyInitialWindowSize();

        _hotkeyManager = new HotkeyManager(DispatcherQueue.GetForCurrentThread());
        _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
        ApplyHotkeySettings();
        StartForegroundWatcher();

        Closed += (s, e) =>
        {
            try { _hotkeyManager.Dispose(); } catch (Exception ex) { Log.Write(ex); }
            try { _foregroundTimer?.Stop(); } catch (Exception ex) { Log.Write(ex); }
            _overlay?.SafeClose();
        };

        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));
    }

    /// <summary>每秒检查一次前台进程，用于刷新“当前目标”显示。</summary>
    private void StartForegroundWatcher()
    {
        _foregroundTimer = DispatcherQueue.CreateTimer();
        _foregroundTimer.Interval = TimeSpan.FromSeconds(1);
        _foregroundTimer.Tick += (s, e) =>
        {
            try
            {
                var name = CurrentTargetProcessName;
                if (string.Equals(name, _lastTargetName, StringComparison.OrdinalIgnoreCase)) return;

                _lastTargetName = name;
                RaiseBlurStateChanged();
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        };

        _foregroundTimer.Start();
    }

    /// <summary>初始窗口尺寸：在默认尺寸基础上，横向缩小 50%，纵向缩小 30%。</summary>
    private void ApplyInitialWindowSize()
    {
        var current = AppWindow.ClientSize;

        // 默认宽度先缩小 50%，再在此基础上增加 30%，即 0.5 * 1.3 = 0.65。
        int width = current.Width > 0 ? (int)(current.Width * 0.65) : 780;
        int height = current.Height > 0 ? (int)(current.Height * 0.7) : 560;

        if (width < 420) width = 420;
        if (height < 420) height = 420;

        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    private void ApplySystemBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    /// <summary>重新注册所有全局热键。</summary>
    public void ApplyHotkeySettings()
    {
        _hotkeyManager.SetHotkey(HotkeyIds.ToggleBlur,
            (uint)Settings.HotkeyModifiers, (uint)Settings.HotkeyKey);
        _hotkeyManager.SetHotkey(HotkeyIds.EditRegion,
            (uint)Settings.RegionHotkeyModifiers, (uint)Settings.RegionHotkeyKey);
        _hotkeyManager.SetHotkey(HotkeyIds.ToggleLock,
            (uint)Settings.LockHotkeyModifiers, (uint)Settings.LockHotkeyKey);
    }

    /// <summary>设置或清除锁定的目标进程。</summary>
    public void SetLockedProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            Settings.LockEnabled = false;
        }
        else
        {
            Settings.LockEnabled = true;
            Settings.LockedProcessName = processName;

            // 已存在的叠加层若不属于锁定进程，直接关掉。
            var overlay = _overlay;
            if (overlay != null &&
                !string.Equals(overlay.TargetProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                _overlay = null;
                overlay.SafeClose();
            }
        }

        _lastTargetName = string.Empty;
        SettingsStorage.Save(Settings);
        RaiseBlurStateChanged();
    }

    private void ToggleLock()
    {
        if (Settings.LockEnabled)
        {
            SetLockedProcess(null);
            return;
        }

        var name = _overlay?.TargetProcessName;
        if (string.IsNullOrEmpty(name))
        {
            name = ProcessHelper.GetProcessName(NativeMethods.GetForegroundWindow());
        }

        if (string.IsNullOrEmpty(name)) return;

        SetLockedProcess(name);
    }

    /// <summary>
    /// 解析本次快捷键要作用的目标窗口。
    /// 前台窗口属于自己的进程（主窗口 / 叠加窗口）时不能直接使用，
    /// 否则快捷键会因为「目标是自己」而被静默忽略，表现为快捷键失效。
    /// </summary>
    private IntPtr ResolveTargetWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();

        if (foreground != IntPtr.Zero && !IsOwnWindow(foreground))
        {
            return foreground;
        }

        if (_lastTargetHwnd != IntPtr.Zero &&
            NativeMethods.IsWindow(_lastTargetHwnd) &&
            NativeMethods.IsWindowVisible(_lastTargetHwnd))
        {
            return _lastTargetHwnd;
        }

        return IntPtr.Zero;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            return processId == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>锁定状态下只有前台窗口属于锁定进程时才响应快捷键。</summary>
    private bool IsEscapedFromLock()
    {
        if (!Settings.LockEnabled) return false;
        if (string.IsNullOrEmpty(Settings.LockedProcessName)) return false;

        var name = ProcessHelper.GetProcessName(ResolveTargetWindow());

        return !string.Equals(name, Settings.LockedProcessName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只保存设置，不重新注册热键。</summary>
    public void SaveSettings()
    {
        SettingsStorage.Save(Settings);
    }

    /// <summary>保存设置并重新注册热键。</summary>
    public void SaveAndApplyHotkeys()
    {
        SettingsStorage.Save(Settings);
        ApplyHotkeySettings();
    }

    /// <summary>实时更新当前叠加层的模糊半径。</summary>
    public void UpdateBlurRadius(int radius)
    {
        Settings.BlurRadius = radius;
        SettingsStorage.Save(Settings);

        try
        {
            _overlay?.SetBlurAmount(radius);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>把模糊范围重置为整个窗口。</summary>
    public void ResetRegion()
    {
        Settings.RegionPoints = new List<RegionPoint>();
        Settings.RegionX = 0;
        Settings.RegionY = 0;
        Settings.RegionWidth = 1;
        Settings.RegionHeight = 1;
        SettingsStorage.Save(Settings);

        try
        {
            _overlay?.ResetRegion();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void RaiseBlurStateChanged()
    {
        try
        {
            BlurStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>供主页“立即模糊”按钮调用。</summary>
    public void StartBlurOnForegroundWindow()
    {
        try
        {
            ToggleBlur();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    /// <summary>供主页“调整模糊范围”按钮调用。</summary>
    public void StartEditRegionOnForegroundWindow()
    {
        try
        {
            ToggleEditRegion();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void CreateOverlay(IntPtr targetHwnd, bool startInEditMode)
    {
        // 关掉旧叠加层；SafeClose 是幂等的，重复切换不会重复关闭同一个窗口。
        var previous = _overlay;
        _overlay = null;
        previous?.SafeClose();

        _lastTargetHwnd = targetHwnd;

        try
        {
            var overlay = new OverlayWindow();
            overlay.RegionConfirmed += OnRegionConfirmed;
            overlay.Closed += (s, e) =>
            {
                if (ReferenceEquals(_overlay, overlay))
                {
                    _overlay = null;
                    RaiseBlurStateChanged();
                }
            };

            _overlay = overlay;
            overlay.AttachToWindow(
                targetHwnd,
                Settings.BlurRadius,
                Settings.FrameRate,
                (WindowCaptureMode)Settings.CaptureMode,
                GetRegionPoints(),
                startInEditMode);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            var failed = _overlay;
            _overlay = null;
            failed?.SafeClose();
        }

        RaiseBlurStateChanged();
    }

    private IReadOnlyList<Point> GetRegionPoints()
    {
        if (Settings.RegionPoints is { Count: >= 3 })
        {
            return Settings.RegionPoints
                .Select(p => new Point(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)))
                .ToList();
        }

        // 旧版矩形设置回退为四个角的矩形。
        double x = Math.Clamp(Settings.RegionX, 0, 1);
        double y = Math.Clamp(Settings.RegionY, 0, 1);
        double w = Math.Clamp(Settings.RegionWidth, 0, 1 - x);
        double h = Math.Clamp(Settings.RegionHeight, 0, 1 - y);

        return new List<Point>
        {
            new(x, y),
            new(x + w, y),
            new(x + w, y + h),
            new(x, y + h)
        };
    }

    private void OnRegionConfirmed(object? sender, IReadOnlyList<Point> points)
    {
        if (points == null || points.Count < 3) return;

        Settings.RegionPoints = points
            .Select(p => new RegionPoint(p.X, p.Y))
            .ToList();

        // 同步更新旧版矩形字段，便于兼容与调试。
        Settings.RegionX = points.Min(p => p.X);
        Settings.RegionY = points.Min(p => p.Y);
        Settings.RegionWidth = points.Max(p => p.X) - Settings.RegionX;
        Settings.RegionHeight = points.Max(p => p.Y) - Settings.RegionY;

        SettingsStorage.Save(Settings);
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        Log.Write($"OnHotkeyPressed id={id}");

        // 快速连按时可能出现重入，这里统一做异常保护，避免任何一次异常导致进程崩溃。
        try
        {
            switch (id)
            {
                case HotkeyIds.ToggleBlur:
                    ToggleBlur();
                    break;
                case HotkeyIds.EditRegion:
                    ToggleEditRegion();
                    break;
                case HotkeyIds.ToggleLock:
                    ToggleLock();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void ToggleBlur()
    {
        if (IsEscapedFromLock())
        {
            Log.Write($"blur ignored, foreground is not {Settings.LockedProcessName}");
            return;
        }

        var overlay = _overlay;
        if (overlay != null)
        {
            if (overlay.IsEditMode)
            {
                // 编辑中按模糊快捷键：先结束编辑，按之前的状态决定是否保持模糊。
                overlay.ConfirmEdit();
                RaiseBlurStateChanged();
                return;
            }

            _overlay = null;
            overlay.SafeClose();
            RaiseBlurStateChanged();
            return;
        }

        IntPtr targetHwnd = ResolveTargetWindow();
        if (targetHwnd == IntPtr.Zero)
            return;

        CreateOverlay(targetHwnd, startInEditMode: false);
    }

    private void ToggleEditRegion()
    {
        if (IsEscapedFromLock())
        {
            Log.Write($"edit ignored, foreground is not {Settings.LockedProcessName}");
            return;
        }

        var overlay = _overlay;
        if (overlay != null)
        {
            if (overlay.IsEditMode)
            {
                // 由叠加层根据「进入编辑前是否在模糊」决定回到模糊还是关闭。
                overlay.ConfirmEdit();
            }
            else
            {
                overlay.EnterEditMode();
            }

            RaiseBlurStateChanged();
            return;
        }

        IntPtr targetHwnd = ResolveTargetWindow();
        if (targetHwnd == IntPtr.Zero)
            return;

        CreateOverlay(targetHwnd, startInEditMode: true);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        if (tag == "Home")
        {
            ContentFrame.Navigate(typeof(HomePage));
        }
        else if (tag == "Settings")
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }
}
