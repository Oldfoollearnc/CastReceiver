using System.Windows;
using CastReceiver.App.Services;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>修改某个账号的密码（用新密码重新包裹私钥，已保存的内容无需重新加密）。</summary>
public partial class VaultChangePasswordWindow : Window
{
    private readonly VaultService _vault;

    public bool Changed { get; private set; }

    public VaultChangePasswordWindow(VaultService vault)
    {
        InitializeComponent();
        _vault = vault;
        foreach (var account in vault.Accounts) CmbAccount.Items.Add(account.DisplayName);
        if (CmbAccount.Items.Count > 0) CmbAccount.SelectedIndex = 0;
        Loaded += (_, _) => TxtOld.Focus();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Text = "";
        int index = CmbAccount.SelectedIndex;
        if (index < 0 || index >= _vault.Accounts.Count) { TxtError.Text = "请选择账号"; return; }

        string newPassword = TxtNew.Password;
        if (newPassword != TxtConfirm.Password) { TxtError.Text = "两次输入的新密码不一致"; return; }

        string? weak = VaultCrypto.ValidatePassword(newPassword);
        if (weak is not null) { TxtError.Text = weak; return; }

        var account = _vault.Accounts[index];
        if (!_vault.ChangePassword(account.Id, TxtOld.Password, newPassword, out string? error))
        {
            TxtError.Text = error ?? "修改失败";
            return;
        }

        Changed = true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; Close(); }
        base.OnPreviewKeyDown(e);
    }
}
