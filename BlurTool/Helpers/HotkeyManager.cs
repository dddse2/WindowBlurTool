using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace BlurTool.Helpers;

/// <summary>全局热键编号。</summary>
public static class HotkeyIds
{
    public const int ToggleBlur = 1;
    public const int EditRegion = 2;
    public const int ToggleLock = 3;
}

public sealed class HotkeyManager : IDisposable
{
    private const int MsgRegister = NativeMethods.WM_USER + 1;

    private readonly Thread _thread;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly ManualResetEvent _ready = new(false);
    private readonly DispatcherQueue? _dispatcher;
    private readonly Dictionary<int, (uint Modifiers, uint Key)> _hotkeys = new();
    private readonly object _hotkeyLock = new();

    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>参数为热键编号（见 <see cref="HotkeyIds"/>）。</summary>
    public event EventHandler<int>? HotkeyPressed;

    public HotkeyManager(DispatcherQueue? dispatcher)
    {
        _dispatcher = dispatcher;
        _wndProc = WndProc;
        _thread = new Thread(WindowThread)
        {
            IsBackground = true,
            Name = "BlurToolHotkeyThread"
        };
        _thread.Start();
        _ready.WaitOne();
    }

    /// <summary>注册或更新一个全局热键；virtualKey 为 0 表示取消该热键。</summary>
    public void SetHotkey(int id, uint modifiers, uint virtualKey)
    {
        Log.Write($"SetHotkey id={id} mods=0x{modifiers:X} vk=0x{virtualKey:X}");

        lock (_hotkeyLock)
        {
            _hotkeys[id] = (modifiers, virtualKey);
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, (uint)MsgRegister, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void WindowThread()
    {
        string className = "BlurToolHotkey_" + Guid.NewGuid().ToString("N");

        var wcex = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className
        };

        NativeMethods.RegisterClassEx(ref wcex);

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            className,
            "BlurToolHotkey",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        _ready.Set();

        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private void ReregisterAll()
    {
        lock (_hotkeyLock)
        {
            foreach (var pair in _hotkeys)
            {
                NativeMethods.UnregisterHotKey(_hwnd, pair.Key);
                if (pair.Value.Key != 0)
                {
                    bool ok = NativeMethods.RegisterHotKey(_hwnd, pair.Key, pair.Value.Modifiers, pair.Value.Key);
                    if (!ok)
                    {
                        Log.Write($"RegisterHotKey failed id={pair.Key} mods=0x{pair.Value.Modifiers:X} vk=0x{pair.Value.Key:X} error={Marshal.GetLastWin32Error()}");
                    }
                }
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            Log.Write($"WM_HOTKEY id={id}");
            var handler = HotkeyPressed;
            if (handler != null)
            {
                if (_dispatcher != null)
                {
                    _dispatcher.TryEnqueue(() => handler(this, id));
                }
                else
                {
                    handler(this, id);
                }
            }
            return IntPtr.Zero;
        }

        if (msg == MsgRegister)
        {
            ReregisterAll();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_CLOSE)
        {
            NativeMethods.DestroyWindow(hWnd);
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            lock (_hotkeyLock)
            {
                foreach (var id in _hotkeys.Keys)
                {
                    NativeMethods.UnregisterHotKey(_hwnd, id);
                }
            }

            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
