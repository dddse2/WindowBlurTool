# WindowBlurTool

一个用于 Windows 的窗口高斯模糊叠加工具（程序与解决方案名为 BlurTool）。按下全局快捷键即可对指定窗口实时模糊，可自由选择模糊区域、模糊强度与画面捕获方式。

界面基于 WinUI 3，支持 Windows 11 Mica 材质。

## 功能

- 全局快捷键开启 / 关闭模糊，可自定义
- 模糊半径 0 ~ 150 实时调节，拖动立即生效
- 自定义任意形状的模糊区域（多边形），支持加点、删点、整体拖动
- 目标进程：显示当前处理的进程，可锁定到指定进程
- 四种画面捕获方式：Graphics Capture / Desktop Duplication / GDI / DwmSharedSurface
- 叠加层点击与滚轮穿透，不抢键盘焦点，不影响目标程序操作
- 叠加层贴在目标窗口上方，被其他窗口遮挡时会一起被遮住
- 刷新帧率 5 ~ 60 FPS 可调

## 快捷键

| 功能 | 默认快捷键 |
| --- | --- |
| 开启 / 关闭模糊 | `Ctrl + Shift + B` |
| 调整模糊范围 | `Ctrl + Shift + E` |
| 锁定 / 解除锁定目标进程 | `Ctrl + Shift + L` |

均可在主页点击铅笔按钮重新录制。

## 使用

1. 打开目标窗口并使其成为前台窗口。
2. 按 `Ctrl + Shift + B` 开启模糊，再按一次关闭。
3. 按 `Ctrl + Shift + E` 进入范围编辑：拖动定位点调整形状，双击边线加点，右键顶点删点，点「确定」退出。
   - 进入编辑前已模糊 → 退出后保持模糊；
   - 直接进入编辑 → 退出后回到未模糊状态。
4. 在「目标进程」卡片中选择进程即可锁定，之后只有该进程的窗口响应快捷键。

## 捕获方式

命名与 [Magpie](https://github.com/Blinue/Magpie) 对齐。

| 方式 | 说明 |
| --- | --- |
| Graphics Capture | 默认。兼容性与流畅度最好，部分系统会显示黄色捕获边框 |
| Desktop Duplication | 捕获整屏后裁剪到窗口，适合全屏内容 |
| GDI | `PrintWindow` 优先、失败回退 `BitBlt`，兼容性最好但内容受限 |
| DwmSharedSurface | 实验性，基于未公开的 `DwmGetDxSharedSurface`，不稳定 |

任意方式连续失败后会自动回退到 GDI。

## 构建

需要 .NET 8 SDK、Windows App SDK 与 Windows 10 2004（19041）以上系统。

```powershell
dotnet build
dotnet run --project BlurTool
```

## 发布

```powershell
# Small（默认）：框架依赖单文件，约 41 MB
# 需要目标机器已安装 .NET 8 Desktop Runtime 与 Windows App Runtime
powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1

# Portable：自包含目录，约 164 MB，不依赖任何运行时
powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1 -Mode Portable
```

体积相关的几点实测结论：

- WinUI 3 不支持可靠的 IL 裁剪，因此 `PublishTrimmed` 保持关闭；
- 单文件压缩会让 WinUI 找不到 `Microsoft.UI.Xaml/Themes/themeresources.xaml`，因此不使用压缩；
- 单文件打包不会把 `resources.pri` 放到 exe 旁边，脚本会在发布后自动复制过去。

## 项目结构

```
BlurTool/
  MainWindow.xaml / .cs           主窗口：导航、快捷键、叠加层管理
  OverlayWindow.xaml / .cs        叠加窗口：模糊渲染与范围编辑
  Pages/                          主页与设置页
  Helpers/                        捕获、热键、进程、窗口等实现
  Models/                         设置模型
tools/
  make-icon.ps1                   由 PNG 生成多尺寸 AppIcon.ico
```

## 已知限制

- WinUI 顶层窗口不支持逐像素透明，模糊区域以外绘制的是捕获到的原始画面
- DwmSharedSurface 与 Desktop Duplication 在部分环境不可用，会自动回退到 GDI
- 叠加窗口使用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，不会被录屏软件拍到

## 许可证

[MIT](LICENSE)
