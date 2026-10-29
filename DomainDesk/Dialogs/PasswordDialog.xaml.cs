using System.Runtime.InteropServices;
using System.Security;
using System.Windows;

namespace DomainDesk.Dialogs;

public partial class PasswordDialog : Window
{
    public SecureString? NewPassword { get; private set; }
    public bool MustChangePassword => MustChangeBox.IsChecked == true;

    public PasswordDialog(DirectoryItem target, bool isDemo)
    {
        InitializeComponent();
        TargetText.Text = $"{(isDemo ? "[演示模拟] " : "[真实域] ")}{target.Name} / {target.Account}\n{target.DistinguishedName}";
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        using var first = NewPasswordBox.SecurePassword;
        using var second = ConfirmPasswordBox.SecurePassword;
        if (first.Length == 0 || !Equal(first, second))
        {
            ValidationText.Text = "密码不能为空，且两次输入必须一致。";
            return;
        }
        NewPassword = first.Copy();
        NewPassword.MakeReadOnly();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        DialogResult = true;
    }

    public static bool Equal(SecureString first, SecureString second)
    {
        if (first.Length != second.Length) return false;
        var a = Marshal.SecureStringToGlobalAllocUnicode(first);
        var b = Marshal.SecureStringToGlobalAllocUnicode(second);
        try
        {
            var difference = 0;
            for (var i = 0; i < first.Length; i++) difference |= Marshal.ReadInt16(a, i * 2) ^ Marshal.ReadInt16(b, i * 2);
            return difference == 0;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(a);
            Marshal.ZeroFreeGlobalAllocUnicode(b);
        }
    }
}
