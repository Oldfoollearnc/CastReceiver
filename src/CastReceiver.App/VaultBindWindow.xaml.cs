using System.Windows;
using CastReceiver.App.Services;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

/// <summary>
/// 把「正在投屏的这台设备」绑定到一个账号。
/// 绑定只是归属规则（谁录的进谁的库），不构成任何解锁凭据。
/// </summary>
public partial class VaultBindWindow : Window
{
    private readonly VaultService _vault;
    private readonly string _fingerprintHash;
    private readonly string _deviceName;

    public Guid? BoundAccountId { get; private set; }

    public VaultBindWindow(VaultService vault, string deviceName, string macAddress, string fingerprintHash,
                           bool suspectedSameName = false)
    {
        InitializeComponent();
        _vault = vault;
        _fingerprintHash = fingerprintHash;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "未知设备" : deviceName;

        TxtDevice.Text = suspectedSameName
            ? $"疑似同一台设备：{_deviceName}"
            : $"新设备：{_deviceName}";
        TxtFingerprint.Text = $"设备地址：{MaskMac(macAddress)}　指纹：{ShortHash(fingerprintHash)}";
        if (suspectedSameName)
            TxtHint.Text = "设备名与已绑定设备相同，但无线地址变了（部分手机会随机化地址）。" +
                           "确认是你自己的设备就重新绑定；不确定就选「暂不绑定」。";

        ReloadAccounts();
        Loaded += (_, _) => { if (LstAccounts.Items.Count == 0) BtnBind.IsEnabled = false; };
    }

    private void ReloadAccounts()
    {
        LstAccounts.Items.Clear();
        foreach (var account in _vault.Accounts)
        {
            var bindings = account.Bindings.Count == 0 ? "未绑定设备" : $"已绑定 {account.Bindings.Count} 台设备";
            LstAccounts.Items.Add($"{account.DisplayName}　（{bindings}）");
        }
        if (LstAccounts.Items.Count > 0) LstAccounts.SelectedIndex = 0;
    }

    private static string MaskMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "未知";
        var parts = mac.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "已隐去";
        return $"{parts[0]}:{parts[1]}:••:••:••:{parts[^1]}";
    }

    private static string ShortHash(string hash)
        => string.IsNullOrEmpty(hash) ? "-" : hash[..Math.Min(12, hash.Length)] + "…";

    private void BtnBind_Click(object sender, RoutedEventArgs e)
    {
        int index = LstAccounts.SelectedIndex;
        if (index < 0 || index >= _vault.Accounts.Count) { MessageBox.Show(this, "请先选择一个账号。", "绑定设备"); return; }
        var account = _vault.Accounts[index];
        if (_vault.BindDevice(account.Id, _fingerprintHash, _deviceName))
        {
            BoundAccountId = account.Id;
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show(this, "绑定失败，请稍后再试。", "绑定设备", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnNewAccount_Click(object sender, RoutedEventArgs e)
    {
        var setup = new VaultSetupWindow(_vault, "新建一个账号：这台设备之后录下的内容都会归入它。") { Owner = this };
        if (setup.ShowDialog() != true || setup.CreatedAccount is null) return;
        ReloadAccounts();
        int index = _vault.Accounts.ToList().FindIndex(a => a.Id == setup.CreatedAccount.Id);
        if (index >= 0) LstAccounts.SelectedIndex = index;
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
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
