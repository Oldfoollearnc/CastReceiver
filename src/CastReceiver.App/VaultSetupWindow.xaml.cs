using System.Windows;
using CastReceiver.App.Services;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>创建加密账号（首次使用，或再开一个账号）。密码只用于加密，不保存在任何地方。</summary>
public partial class VaultSetupWindow : Window
{
    private readonly VaultService _vault;
    private bool _peeking;

    public VaultAccount? CreatedAccount { get; private set; }

    public VaultSetupWindow(VaultService vault, string? intro = null)
    {
        InitializeComponent();
        _vault = vault;
        if (!string.IsNullOrWhiteSpace(intro)) TxtIntro.Text = intro;
        if (vault.LoadError is not null)
        {
            ShowError(vault.LoadError);
            BtnCreate.IsEnabled = false;
        }
        Loaded += (_, _) => TxtUser.Focus();
    }

    // ------------------------------------------------------------------ 输入交互

    private void User_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        PhUser.Visibility = TxtUser.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStrength();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        PhPassword.Visibility = TxtPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PhConfirm.Visibility = TxtConfirm.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStrength();
    }

    private void BtnPeek_Click(object sender, RoutedEventArgs e)
    {
        _peeking = !_peeking;
        TxtPassword.PasswordChar = _peeking ? '\0' : '●';
        TxtConfirm.PasswordChar = _peeking ? '\0' : '●';
        BtnPeek.Content = _peeking ? "隐藏" : "显示";
    }

    /// <summary>强度条：按「长度 + 字符种类」粗算三档，并直接告诉用户哪里不合格。</summary>
    private void UpdateStrength()
    {
        string password = TxtPassword.Password;
        string? problem = VaultCrypto.ValidatePassword(password, TxtUser.Text);

        if (password.Length == 0)
        {
            StrengthBar.Width = 0;
            TxtStrength.Text = "未填写";
            TxtStrength.Foreground = (System.Windows.Media.Brush)FindResource("B.TextFaint");
            return;
        }

        int kinds = 0;
        if (password.Any(char.IsLower)) kinds++;
        if (password.Any(char.IsUpper)) kinds++;
        if (password.Any(char.IsDigit)) kinds++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) kinds++;
        int score = (password.Length >= 12 ? 2 : password.Length >= 8 ? 1 : 0) + (kinds >= 3 ? 2 : kinds == 2 ? 1 : 0);

        (double width, string label, string brush) = score switch
        {
            >= 3 => (150, "强度：好", "B.Ok"),
            2 => (100, "强度：可用", "B.Warn"),
            _ => (56, "强度：偏弱", "B.Bad"),
        };
        if (problem is not null) { width = Math.Min(width, 56); label = "强度：偏弱"; brush = "B.Bad"; }

        StrengthBar.Width = width;
        StrengthBar.Background = (System.Windows.Media.Brush)FindResource(brush);
        TxtStrength.Text = label;
        TxtStrength.Foreground = (System.Windows.Media.Brush)FindResource(brush);
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ 创建

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        ErrorBox.Visibility = Visibility.Collapsed;
        string user = TxtUser.Text.Trim();
        string password = TxtPassword.Password;

        if (password != TxtConfirm.Password) { ShowError("两次输入的密码不一致"); return; }

        var (ok, error, account) = _vault.CreateAccount(user, password);
        if (!ok || account is null) { ShowError(error ?? "创建失败"); return; }

        CreatedAccount = account;
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
