using System.ComponentModel;
using System.Windows;
using LLG.Core;

namespace LLG.Helper;

public partial class SettingsWindow : Window
{
    private readonly EncryptedStore store;
    private bool saving;
    public SavedState Saved { get; private set; }

    public SettingsWindow(SavedState state, EncryptedStore encryptedStore)
    {
        InitializeComponent();
        Saved = state;
        store = encryptedStore;
        UsernameBox.Text = state.Credentials.Username;
        PasswordInput.Password = state.Credentials.Password;
        Loaded += (_, _) => UsernameBox.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && !saving) Close(); };
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        var credentials = new UserCredentials { Username = UsernameBox.Text.Trim(), Password = PasswordInput.Password };
        if (!credentials.IsComplete)
        {
            ErrorText.Text = "请填写用户名和密码。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        saving = true;
        SaveButton.IsEnabled = false;
        UsernameBox.IsEnabled = false;
        PasswordInput.IsEnabled = false;
        try
        {
            var replacement = Saved.WithCredentials(credentials);
            await store.SaveAsync(replacement);
            Saved = replacement;
            saving = false;
            PasswordInput.Clear();
            DialogResult = true;
        }
        catch (UserVisibleException error)
        {
            ErrorText.Text = error.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            saving = false;
            SaveButton.IsEnabled = true;
            UsernameBox.IsEnabled = true;
            PasswordInput.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (saving) e.Cancel = true; }
}
