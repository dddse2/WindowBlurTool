namespace BlurTool.Models;

public class AppSettings
{
    /// <summary>设置结构版本，用于迁移旧配置。</summary>
    public int SettingsVersion { get; set; } = 5;

    public int BlurRadius { get; set; } = 40;
    public int FrameRate { get; set; } = 30;
    public int HotkeyModifiers { get; set; } = (int)(ModifierKeys.Control | ModifierKeys.Shift);
    public int HotkeyKey { get; set; } = (int)Windows.System.VirtualKey.B;

    /// <summary>调整模糊范围快捷键的修饰键。</summary>
    public int RegionHotkeyModifiers { get; set; } = (int)(ModifierKeys.Control | ModifierKeys.Shift);
    /// <summary>调整模糊范围快捷键的主键。</summary>
    public int RegionHotkeyKey { get; set; } = (int)Windows.System.VirtualKey.E;

    /// <summary>锁定目标进程的快捷键修饰键。</summary>
    public int LockHotkeyModifiers { get; set; } = (int)(ModifierKeys.Control | ModifierKeys.Shift);
    /// <summary>锁定目标进程的快捷键主键。</summary>
    public int LockHotkeyKey { get; set; } = (int)Windows.System.VirtualKey.L;

    /// <summary>是否锁定到指定进程。</summary>
    public bool LockEnabled { get; set; }

    /// <summary>锁定的目标进程名（如 notepad.exe）。</summary>
    public string LockedProcessName { get; set; } = string.Empty;

    /// <summary>
    /// 画面捕获方式，取值见 <see cref="WindowCaptureMode"/>。
    /// 默认与 Magpie 一致，使用 Graphics Capture。
    /// </summary>
    public int CaptureMode { get; set; } = (int)WindowCaptureMode.GraphicsCapture;

    /// <summary>
    /// 自定义模糊区域（任意多边形），坐标为相对目标窗口的归一化值（0~1）。
    /// 为空时回退到 RegionX/Y/Width/Height 描述的矩形。
    /// </summary>
    public List<RegionPoint> RegionPoints { get; set; } = new();

    /// <summary>旧版矩形区域的兼容字段。</summary>
    public double RegionX { get; set; } = 0;
    public double RegionY { get; set; } = 0;
    public double RegionWidth { get; set; } = 1;
    public double RegionHeight { get; set; } = 1;
}

public class RegionPoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public RegionPoint() { }

    public RegionPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// 窗口捕获方式，命名与 Magpie 保持一致。
/// </summary>
public enum WindowCaptureMode
{
    /// <summary>Windows.Graphics.Capture，Magpie 推荐方式，兼容性与流畅度最好。</summary>
    GraphicsCapture = 0,

    /// <summary>DXGI Desktop Duplication：捕获整个显示器后裁剪到目标窗口区域。</summary>
    DesktopDuplication = 1,

    /// <summary>GDI 捕获：BitBlt / PrintWindow，兼容旧系统与老程序。</summary>
    GDI = 2,

    /// <summary>DWM 共享表面（未公开的 DwmGetDxSharedSurface），实验性、不稳定。</summary>
    DwmSharedSurface = 3
}

[Flags]
public enum ModifierKeys : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}
