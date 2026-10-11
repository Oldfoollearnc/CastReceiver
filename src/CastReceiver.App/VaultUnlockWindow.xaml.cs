using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CastReceiver.App.Services;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>
/// 解锁「我的库」：账号名 + 密码 → 解出私钥（仅内存）→ 打开库。
/// 连续输错会指数延时（1、2、4…上限 30 秒），延时期间按钮不可用。
/// </summary>
public partial class VaultUnlockWindow : Window
{
    private readonly VaultService _vault;
    private readonly DispatcherTimer _lockoutTimer;
    private int _failCount;
    private int _lockoutRemaining;
    private bool _accountEditable;
    private bool _peeking;

    public VaultAccount? UnlockedAccount { get; private set; }

    public VaultUnlockWindow(VaultService vault, string? prefillAccount = null, string? deviceHint = null)
    {
        InitializeComponent();
        _vault = vault;

        foreach (var account in vault.Accounts)
            CmbUser.Items.Add(account.DisplayName);

        string? prefill = prefillAccount
                          ?? vault.ActiveAccount?.DisplayName
                          ?? vault.Accounts.FirstOrDefault()?.DisplayName;

        bool hasAccounts = vault.Accounts.Count > 0;
        TxtAccountName.Text = prefill ?? "";
        TxtAccountEmpty.Visibility = hasAccounts ? Visibility.Collapsed : Visibility.Visible;
        BtnSwitchAccount.Visibility = vault.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (hasAccounts) TxtAccountHint.Text = vault.Accounts.Count > 1
            ? $"本机共有 {vault.Accounts.Count} 个账号，点「换账号」可切换。"
            : "这是你创建账号时设置的账号名。";

        if (!string.IsNullOrWhiteSpace(deviceHint))
        {
            TxtDevice.Text = deviceHint;
            TxtDevice.Visibility = Visibility.Visible;
        }

        if (!hasAccounts)
        {
            ShowError("还没有任何账号，请先创建账号。");
            BtnUnlock.IsEnabled = false;
        }

        _lockoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockoutTimer.Tick += (_, _) =>
        {
            _lockoutRemaining--;
            if (_lockoutRemaining <= 0)
            {
                _lockoutTimer.Stop();
                BtnUnlock.IsEnabled = true;
                BtnUnlock.Content = "解锁";
                HideError();
            }
            else
            {
                BtnUnlock.Content = $"请等待 {_lockoutRemaining}s";
            }
        };

        Loaded += (_, _) => { if (hasAccounts) TxtPassword.Focus(); };
    }

    // ------------------------------------------------------------------ 输入交互

    private void Password_Changed(object sender, RoutedEventArgs e)
        => PhPassword.Visibility = TxtPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Password_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) BtnUnlock_Click(sender, e);
    }

    private void BtnPeek_Click(object sender, RoutedEventArgs e)
    {
        _peeking = !_peeking;
        TxtPassword.PasswordChar = _peeking ? '\0' : '●';
        BtnPeek.Content = _peeking ? "隐藏" : "显示";
    }

    private void BtnSwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        _accountEditable = !_accountEditable;
        AccountDisplay.Visibility = _accountEditable ? Visibility.Collapsed : Visibility.Visible;
        CmbUser.Visibility = _accountEditable ? Visibility.Visible : Visibility.Collapsed;
        if (_accountEditable)
        {
            CmbUser.Text = TxtAccountName.Text;
            CmbUser.Focus();
            TxtAccountHint.Text = "输入或选择要解锁的账号名。";
        }
        else
        {
            TxtAccountName.Text = (CmbUser.Text ?? "").Trim();
            TxtAccountHint.Text = "这是你创建账号时设置的账号名。";
        }
    }

    private string CurrentAccountName
        => (_accountEditable ? CmbUser.Text : TxtAccountName.Text ?? "").Trim();

    // ------------------------------------------------------------------ 错误提示

    private void ShowError(string message)
    {
        TxtError.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBox.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ 解锁

    private void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnUnlock.IsEnabled) return;
        string user = CurrentAccountName;
        string password = TxtPassword.Password;
        if (user.Length == 0) { ShowError("请输入账号名"); return; }
        if (password.Length == 0) { ShowError("请输入密码"); return; }

        if (_vault.Unlock(user, password, out string? error))
        {
            UnlockedAccount = _vault.Library is null
                ? null
                : _vault.Accounts.FirstOrDefault(a => a.Id == _vault.Library.AccountId);
            DialogResult = true;
            Close();
            return;
        }

        _failCount++;
        var account = _vault.Accounts.FirstOrDefault(a => a.LoginHash == VaultCrypto.LoginHashFor(user));
        int delay = VaultAccountStore.FailureDelaySeconds(Math.Max(_failCount, account?.FailCount ?? 0));
        TxtPassword.Clear();
        PhPassword.Visibility = Visibility.Visible;
        ShowError((error ?? "账号名或密码不正确") + (delay > 0 ? $"，请等待 {delay} 秒后重试" : ""));
        if (delay > 0)
        {
            _lockoutRemaining = delay;
            BtnUnlock.IsEnabled = false;
            BtnUnlock.Content = $"请等待 {delay}s";
            _lockoutTimer.Start();
        }
        TxtPassword.Focus();
    }

    private void BtnForgot_Click(object sender, RoutedEventArgs e)
    {
        string user = CurrentAccountName;
        var account = _vault.Accounts.FirstOrDefault(a => a.LoginHash == VaultCrypto.LoginHashFor(user));
        if (account is null)
        {
            MessageBox.Show(this,
                "请先在上面填入要处理的账号名。\n\n密码不会被保存在任何地方，也没有找回功能 —— 这是加密库的设计前提。",
                "忘记密码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var first = MessageBox.Show(this,
            $"「{account.DisplayName}」的密码无法找回。\n\n" +
            "唯一的办法是删除这个账号，并同时清空它名下的全部截图与录像（删除后不可恢复）。\n\n" +
            "要继续吗？",
            "忘记密码", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        var second = MessageBox.Show(this,
            "最后确认：删除账号及其全部加密内容？此操作无法撤销。",
            "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (second != MessageBoxResult.Yes) return;

        _vault.DeleteAccount(account.Id, wipeItems: true);
        MessageBox.Show(this, "账号及其内容已删除。", "已删除", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = false;
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
