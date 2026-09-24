using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using BlurTool.Helpers;
using BlurTool.Models;
using Windows.System;
using Windows.UI.Core;

namespace BlurTool.Pages;

public sealed partial class HomePage : Page
{
    private enum HotkeyTarget
    {
        None,
        Blur,
        Region,
        Lock
    }

    public AppSettings Settings { get; set; } = new AppSettings();

    private HotkeyTarget _capturingTarget;
    private bool _initializing;

    public HomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (MainWindow.Current != null)
        {
            Settings = MainWindow.Current.Settings;
            MainWindow.Current.BlurStateChanged += OnBlurStateChanged;
        }

        _initializing = true;
        BlurSlider.Value = Settings.BlurRadius;
        FpsSlider.Value = Settings.FrameRate;
        _initializing = false;

        UpdateStatus();
        UpdateHotkeyDisplay();
        UpdateValueLabels();
        RefreshProcessList();
        UpdateTargetProcess();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (MainWindow.Current != null)
        {
            MainWindow.Current.BlurStateChanged -= OnBlurStateChanged;
        }
    }

    private void OnBlurStateChanged(object? sender, EventArgs e)
    {
        UpdateStatus();
        UpdateTargetProcess();
    }

    private void UpdateTargetProcess()
    {
        var main = MainWindow.Current;
        var name = main?.CurrentTargetProcessName ?? string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            TargetProcessText.Text = "未检测到目标";
        }
        else
        {
            TargetProcessText.Text = main?.IsLocked == true ? $"{name}（已锁定）" : name;
        }

        // 同步下拉框选择，不触发 SelectionChanged。
        var previous = _initializing;
        _initializing = true;

        try
        {
            if (main?.IsLocked == true && !string.IsNullOrEmpty(name))
            {
                SelectProcessItem(name);
            }
            else if (ProcessCombo.SelectedIndex != 0)
            {
                ProcessCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _initializing = previous;
        }
    }

    private void SelectProcessItem(string processName)
    {
        if (ProcessCombo.ItemsSource is not IEnumerable<string> items) return;

        int index = 0;
        foreach (var item in items)
        {
            if (string.Equals(item, processName, StringComparison.OrdinalIgnoreCase))
            {
                ProcessCombo.SelectedIndex = index;
                return;
            }

            index++;
        }

        ProcessCombo.SelectedIndex = 0;
    }

    private void RefreshProcessList()
    {
        var previous = _initializing;
        _initializing = true;

        try
        {
            var items = new List<string> { "跟随前台窗口（不锁定）" };
            items.AddRange(ProcessHelper.EnumerateWindowProcesses());

            ProcessCombo.ItemsSource = items;

            var locked = MainWindow.Current?.LockedProcessName;
            if (MainWindow.Current?.IsLocked == true && !string.IsNullOrEmpty(locked) && items.Contains(locked))
            {
                ProcessCombo.SelectedItem = locked;
            }
            else
            {
                ProcessCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _initializing = previous;
        }
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcessList();
    }

    private void ProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ProcessCombo.SelectedItem is not string value) return;

        MainWindow.Current?.SetLockedProcess(ProcessCombo.SelectedIndex <= 0 ? null : value);
        UpdateTargetProcess();
    }

    private void UpdateStatus()
    {
        var mainWindow = MainWindow.Current;
        bool active = mainWindow?.IsBlurActive ?? false;
        bool editing = mainWindow?.IsEditModeActive ?? false;

        if (!active)
        {
            StatusText.Text = "当前状态：未模糊";
        }
        else if (editing)
        {
            StatusText.Text = "当前状态：正在编辑范围";
        }
        else
        {
            StatusText.Text = "当前状态：正在模糊";
        }

        StatusDot.Fill = active
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
    }

    private void UpdateHotkeyDisplay()
    {
        HotkeyChips.ItemsSource = BuildChips(Settings.HotkeyModifiers, Settings.HotkeyKey);
        RegionHotkeyChips.ItemsSource = BuildChips(Settings.RegionHotkeyModifiers, Settings.RegionHotkeyKey);
        LockHotkeyChips.ItemsSource = BuildChips(Settings.LockHotkeyModifiers, Settings.LockHotkeyKey);
    }

    private static List<string> BuildChips(int modifiersValue, int keyValue)
    {
        var chips = new List<string>();
        var modifiers = (ModifierKeys)modifiersValue;

        if ((modifiers & ModifierKeys.Control) != 0) chips.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift) != 0) chips.Add("Shift");
        if ((modifiers & ModifierKeys.Alt) != 0) chips.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) chips.Add("Win");

        var key = (VirtualKey)keyValue;
        if (key != VirtualKey.None)
        {
            chips.Add(key.ToString());
        }
        else if (chips.Count == 0)
        {
            chips.Add("未设置");
        }

        return chips;
    }

    private void UpdateValueLabels()
    {
        BlurValueText.Text = $"{Settings.BlurRadius}";
        FpsValueText.Text = $"{Settings.FrameRate} FPS";
    }

    private void EditHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingTarget = HotkeyTarget.Blur;
        HotkeyTip.Text = "请按下想要的“开启/关闭模糊”快捷键组合...";
        HotkeyTip.Visibility = Visibility.Visible;
    }

    private void EditRegionHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingTarget = HotkeyTarget.Region;
        HotkeyTip.Text = "请按下想要的“调整模糊范围”快捷键组合...";
        HotkeyTip.Visibility = Visibility.Visible;
    }

    private void EditLockHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingTarget = HotkeyTarget.Lock;
        HotkeyTip.Text = "请按下想要的“锁定目标进程”快捷键组合...";
        HotkeyTip.Visibility = Visibility.Visible;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturingTarget == HotkeyTarget.None) return;

        e.Handled = true;

        // Ignore bare modifier keys
        if (e.Key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
                   or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.LeftShift
                   or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl
                   or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            return;
        }

        var modifiers = ModifierKeys.None;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Shift;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Control;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Alt;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(CoreVirtualKeyStates.Down) ||
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Windows;

        if (_capturingTarget == HotkeyTarget.Blur)
        {
            Settings.HotkeyModifiers = (int)modifiers;
            Settings.HotkeyKey = (int)e.Key;
        }
        else if (_capturingTarget == HotkeyTarget.Region)
        {
            Settings.RegionHotkeyModifiers = (int)modifiers;
            Settings.RegionHotkeyKey = (int)e.Key;
        }
        else if (_capturingTarget == HotkeyTarget.Lock)
        {
            Settings.LockHotkeyModifiers = (int)modifiers;
            Settings.LockHotkeyKey = (int)e.Key;
        }

        _capturingTarget = HotkeyTarget.None;
        HotkeyTip.Visibility = Visibility.Collapsed;

        UpdateHotkeyDisplay();
        MainWindow.Current?.SaveAndApplyHotkeys();
    }

    private void BlurSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;

        Settings.BlurRadius = (int)e.NewValue;
        UpdateValueLabels();
        MainWindow.Current?.UpdateBlurRadius(Settings.BlurRadius);
    }

    private void FpsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;

        Settings.FrameRate = (int)e.NewValue;
        UpdateValueLabels();
        MainWindow.Current?.SaveSettings();
    }

    private void BlurNowButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.StartBlurOnForegroundWindow();
    }

    private void EditRegionNowButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.StartEditRegionOnForegroundWindow();
    }

    private void ResetRegionButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.ResetRegion();
    }
}
