# WindowBlurTool

A WinUI 3 overlay that applies GPU-accelerated Gaussian blur to any window on Windows.

[中文说明](README.zh-CN.md)

## Features

- Global hotkeys to toggle blur, customizable
- Blur radius 0–150, applied live
- Polygonal blur region; add, remove and drag vertices
- Lock onto a target process so hotkeys only affect it
- Four capture backends
- Click and wheel pass through, keyboard focus is never stolen

## Hotkeys

| Action | Default |
| --- | --- |
| Toggle blur | `Ctrl + Shift + B` |
| Edit blur region | `Ctrl + Shift + E` |
| Lock target process | `Ctrl + Shift + L` |

## Usage

1. Bring the target window to the foreground and press `Ctrl + Shift + B`.
2. Press `Ctrl + Shift + E` to edit the region. Drag a vertex to move it, double-click an edge to add a vertex, right-click a vertex to remove it. Click **OK** to leave edit mode; the blur state from before the edit is restored.
3. Pick a process under **Target process** to lock onto it.

## Capture backends

| Backend | Notes |
| --- | --- |
| Graphics Capture | Default; best compatibility |
| Desktop Duplication | Captures the whole display, then crops to the window |
| GDI | `PrintWindow`, falling back to `BitBlt` |
| DwmSharedSurface | Experimental |

A backend that keeps failing falls back to GDI.

## Build

Requires the .NET 8 SDK and Windows 10 2004 (build 19041) or later.

```powershell
dotnet run --project BlurTool
```

## License

[MIT](LICENSE)
