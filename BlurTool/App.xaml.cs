using Microsoft.UI.Xaml;
using BlurTool.Helpers;

namespace BlurTool;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new MainWindow();
        m_window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (e.Exception != null)
        {
            Log.Write(e.Exception);
        }

        // 避免直接崩溃，方便定位问题。
        e.Handled = true;
    }

    private Window? m_window;
}
