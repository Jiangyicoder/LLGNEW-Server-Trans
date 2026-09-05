using System.Windows;

namespace LLG.Helper;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Test runs can use an isolated data directory without touching the user's saved account.
        var dataDirectory = Environment.GetEnvironmentVariable("LLG_HELPER_DATA_DIR");
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDirectory ?? Environment.UserName)))[..16];
        instanceMutex = new Mutex(true, @"Local\LLG.SubscriptionHelper." + suffix, out var created);
        if (!created)
        {
            MessageBox.Show("LLG 订阅助手已经在运行，请切换到已打开的窗口。", "LLG 订阅助手", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var window = new MainWindow(dataDirectory);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
