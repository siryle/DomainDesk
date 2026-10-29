using System.Windows;
using DomainDesk.Services;

namespace DomainDesk.Dialogs;

public partial class ConnectDialog : Window
{
    private readonly ConnectionPreferences _preferences;
    public ConnectionSettings? Settings { get; private set; }

    public ConnectDialog(ConnectionPreferences? preferences = null)
    {
        InitializeComponent();
        var store = _preferences = preferences ?? new ConnectionPreferences();
        var profile = store.Load("AD");
        ServerBox.Text = profile.Server;
        UsernameBox.Text = profile.Username;
        CurrentIdentityBox.IsChecked = profile.UseCurrentIdentity;
        TlsBox.IsChecked = profile.UseTls;
        ValidationText.Text = store.Warning ?? "服务器、账号及选项会记住；密码不保存，也不自动连接。";
    }

    private void Identity_Changed(object sender, RoutedEventArgs e)
    {
        if (CredentialPanel is not null) CredentialPanel.IsEnabled = CurrentIdentityBox.IsChecked != true;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        try { RemoteValidation.ValidateServer(server); }
        catch (ArgumentException ex)
        {
            ValidationText.Text = ex.Message;
            return;
        }
        using var suppliedPassword = PasswordInput.SecurePassword;
        if (CurrentIdentityBox.IsChecked != true && (string.IsNullOrWhiteSpace(UsernameBox.Text) || suppliedPassword.Length == 0))
        {
            ValidationText.Text = "请填写账号和密码，或使用当前 Windows 身份。";
            return;
        }
        try { _preferences.Save("AD", new(server, UsernameBox.Text.Trim(), CurrentIdentityBox.IsChecked == true, TlsBox.IsChecked == true, true)); }
        catch (Exception ex) { ValidationText.Text = "连接设置保存失败：" + ex.Message; return; }
        Settings = new ConnectionSettings
        {
            Server = server, UseTls = TlsBox.IsChecked == true, UseCurrentIdentity = CurrentIdentityBox.IsChecked == true,
            Username = UsernameBox.Text.Trim(), Password = CurrentIdentityBox.IsChecked == true ? null : suppliedPassword.Copy(),
            AllowWrites = true
        };
        PasswordInput.Clear();
        DialogResult = true;
    }
}
