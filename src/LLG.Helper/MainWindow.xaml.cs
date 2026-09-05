using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using LLG.Core;

namespace LLG.Helper;

public partial class MainWindow : Window
{
    private readonly EncryptedStore store;
    private readonly LlgClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private SavedState saved = new();
    private ConversionResult? converted;
    private bool busy;

    public MainWindow(string? dataDirectory = null)
    {
        InitializeComponent();
        store = new EncryptedStore(dataDirectory);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.png")); } catch (Exception error) when (error is System.IO.IOException or UriFormatException) { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            saved = await store.LoadAsync();
            RefreshSnapshot();
            ShowStatus(converted is not null ? $"已更新 {converted.Count} 个节点" : saved.Credentials.IsComplete ? "账号已保存，点击更新服务器列表" : "请先在设置中保存账号", converted is not null ? "StatusSuccess" : "StatusInfo");
        }
        catch (UserVisibleException error) { ShowStatus(error.Message, "StatusError"); }
        finally { SetBusy(false); }
    }

    private void RefreshSnapshot()
    {
        converted = saved.Snapshot is null ? null : NodeConverter.Convert(saved.Snapshot.Yaml);
        if (saved.Snapshot is not null)
        {
            var updated = saved.Snapshot.UpdatedAt.LocalDateTime;
            UpdatedText.Text = updated.Date == DateTime.Today ? $"最近更新 今天 {updated:HH:mm}" : $"最近更新 {updated:yyyy-MM-dd HH:mm}";
        }
        else UpdatedText.Text = "尚未更新服务器列表";
    }

    private void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings();

    private bool ShowSettings()
    {
        if (busy) return false;
        var dialog = new SettingsWindow(saved, store) { Owner = this };
        if (dialog.ShowDialog() != true) return false;
        saved = dialog.Saved;
        try { RefreshSnapshot(); }
        catch (UserVisibleException) { converted = null; }
        SetBusy(false);
        ShowStatus(converted is null ? "账号已保存，点击更新服务器列表" : $"账号已保存，当前 {converted.Count} 个节点", "StatusSuccess");
        return true;
    }

    private async void UpdateServers(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (!saved.Credentials.IsComplete && !ShowSettings()) return;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => { if (!lifetime.IsCancellationRequested) ShowStatus(message, "StatusInfo"); });
            var snapshot = await client.DownloadAsync(saved.Credentials, progress, lifetime.Token);
            var replacement = saved.WithSnapshot(snapshot);
            await store.SaveAsync(replacement);
            saved = replacement;
            RefreshSnapshot();
            ShowStatus($"已更新 {converted!.Count} 个节点", "StatusSuccess");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UserVisibleException error) { ShowStatus(error.Message, "StatusError"); }
        catch (Exception) { ShowStatus("更新未完成，请稍后重试。上次成功的列表仍然保留。", "StatusError"); }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private async void ConvertAndCopy(object sender, RoutedEventArgs e)
    {
        if (busy || saved.Snapshot is null) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => NodeConverter.Convert(saved.Snapshot.Yaml));
            for (var attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(result.Links, true); break; }
                catch (ExternalException) when (attempt < 4) { await Task.Delay(120, lifetime.Token); }
            }
            ShowStatus($"已复制 {result.Count} 个节点，请在 v2rayN 主窗口按 Ctrl+V。", "StatusSuccess");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ExternalException) { ShowStatus("剪贴板暂时被其他程序占用，请再点一次转换。", "StatusError"); }
        catch (UserVisibleException error) { ShowStatus(error.Message, "StatusError"); }
        catch (Exception) { ShowStatus("复制未完成，请重试。", "StatusError"); }
        finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SettingsButton.IsEnabled = !value;
        UpdateButton.IsEnabled = !value;
        ConvertButton.IsEnabled = !value && converted is not null;
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, string icon)
    {
        StatusText.Text = message;
        StatusText.Foreground = icon == "StatusError" ? new SolidColorBrush(Color.FromRgb(180, 35, 24)) : (Brush)FindResource("Ink");
        StatusIcon.Source = (ImageSource)FindResource(icon);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        lifetime.Cancel();
        client.Dispose();
    }
}
