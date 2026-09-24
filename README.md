# WindowBlurTool

Windows 窗口高斯模糊叠加工具（程序名 BlurTool），基于 WinUI 3。

## 功能

- 全局快捷键开启 / 关闭模糊，可自定义
- 模糊半径 0 ~ 150 实时生效
- 任意多边形模糊区域，支持加点、删点、整体拖动
- 可锁定目标进程，之后只对该进程响应快捷键
- 四种画面捕获方式
- 鼠标点击与滚轮穿透，不抢键盘焦点

## 快捷键

| 功能 | 默认 |
| --- | --- |
| 开启 / 关闭模糊 | `Ctrl + Shift + B` |
| 调整模糊范围 | `Ctrl + Shift + E` |
| 锁定目标进程 | `Ctrl + Shift + L` |

## 使用

1. 目标窗口置于前台，按 `Ctrl + Shift + B` 开关模糊。
2. 按 `Ctrl + Shift + E` 编辑区域：拖动定位点，双击边线加点，右键顶点删点，点「确定」退出。退出后保持进入编辑前的模糊状态。
3. 在「目标进程」中选择进程即可锁定。

## 捕获方式

| 方式 | 说明 |
| --- | --- |
| Graphics Capture | 默认，兼容性最好 |
| Desktop Duplication | 整屏捕获后裁剪到窗口 |
| GDI | `PrintWindow` 优先，失败回退 `BitBlt` |
| DwmSharedSurface | 实验性 |

失败时自动回退到 GDI。

## 构建

需要 .NET 8 SDK 与 Windows 10 2004（19041）以上系统。

```powershell
dotnet build
dotnet run --project BlurTool
```

## 发布

```powershell
# 框架依赖单文件，约 41 MB，需要 .NET 8 Desktop Runtime 与 Windows App Runtime
powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1

# 自包含目录，约 164 MB，无运行时依赖
powershell -ExecutionPolicy Bypass -File .\publish-singlefile.ps1 -Mode Portable
```

## 说明

- WinUI 窗口无法逐像素透明，模糊区域以外显示的是捕获到的原始画面
- 叠加层设置了 `WDA_EXCLUDEFROMCAPTURE`，不会被录屏软件拍到

## 许可证

[MIT](LICENSE)
