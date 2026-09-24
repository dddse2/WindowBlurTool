using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BlurTool.Models;

namespace BlurTool.Pages;

public sealed partial class SettingsPage : Page
{
    public AppSettings Settings { get; set; } = new AppSettings();

    private bool _initializing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (MainWindow.Current != null)
        {
            Settings = MainWindow.Current.Settings;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "BlurTool " + (version != null ? version.ToString(3) : "1.0.0");

        _initializing = true;
        SelectCaptureMode(Settings.CaptureMode);
        _initializing = false;
    }

    private void SelectCaptureMode(int mode)
    {
        foreach (var item in CaptureModeCombo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                int.TryParse(comboItem.Tag?.ToString(), out int tag) &&
                tag == mode)
            {
                CaptureModeCombo.SelectedItem = comboItem;
                return;
            }
        }
    }

    private void CaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (CaptureModeCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int mode))
        {
            Settings.CaptureMode = mode;
            MainWindow.Current?.SaveSettings();
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        Settings.BlurRadius = defaults.BlurRadius;
        Settings.FrameRate = defaults.FrameRate;
        Settings.HotkeyModifiers = defaults.HotkeyModifiers;
        Settings.HotkeyKey = defaults.HotkeyKey;
        Settings.RegionHotkeyModifiers = defaults.RegionHotkeyModifiers;
        Settings.RegionHotkeyKey = defaults.RegionHotkeyKey;
        Settings.LockHotkeyModifiers = defaults.LockHotkeyModifiers;
        Settings.LockHotkeyKey = defaults.LockHotkeyKey;
        Settings.LockEnabled = defaults.LockEnabled;
        Settings.LockedProcessName = defaults.LockedProcessName;
        Settings.CaptureMode = defaults.CaptureMode;
        Settings.RegionPoints = new List<RegionPoint>(defaults.RegionPoints);
        Settings.RegionX = defaults.RegionX;
        Settings.RegionY = defaults.RegionY;
        Settings.RegionWidth = defaults.RegionWidth;
        Settings.RegionHeight = defaults.RegionHeight;

        MainWindow.Current?.SaveAndApplyHotkeys();

        _initializing = true;
        SelectCaptureMode(Settings.CaptureMode);
        _initializing = false;

        var dialog = new ContentDialog
        {
            Title = "已恢复默认设置",
            Content = "设置已重置为默认值。",
            CloseButtonText = "确定",
            XamlRoot = XamlRoot
        };

        _ = dialog.ShowAsync();
    }
}
