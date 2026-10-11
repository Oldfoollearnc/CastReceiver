using System.IO;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Security;

namespace CastReceiver.App.Services;

/// <summary>
/// 加密库的运行期门面（应用层唯一入口）：
///   · 归属：由「投屏设备指纹」决定，不需要密码 —— 所以启动、投屏、截屏、录制全程免密；
///   · 查看：必须用「账号名 + 密码」解锁（解出私钥并打开库）—— 没有密码，磁盘上的一切都解不开；
///   · 上锁：私钥立即清零，任何解密都不可能，直到再次输入密码。
/// </summary>
public sealed class VaultService
{
    private VaultLibrary? _library;
    private byte[]? _privateKey;

    public VaultService(CastAppHost host)
    {
        Host = host;
        Store = VaultAccountStore.Load();
    }

    public CastAppHost Host { get; }
    public VaultAccountStore Store { get; }

    /// <summary>已解锁的库（未解锁时为 null）。</summary>
    public VaultLibrary? Library => _library;

    public bool IsUnlocked => _library is not null;

    /// <summary>当前投屏设备的归属账号（决定新截图/录像进谁的库）。未绑定或未投屏时为 null。</summary>
    public VaultAccount? ActiveAccount { get; private set; }

    /// <summary>当前投屏设备的绑定信息（用于界面显示设备别名）。</summary>
    public DeviceBinding? ActiveBinding { get; private set; }

    /// <summary>账号文件损坏等致命问题（非空时禁止创建/登录）。</summary>
    public string? LoadError => Store.LoadError;

    public bool HasAccount => Store.Accounts.Count > 0;

    public IReadOnlyList<VaultAccount> Accounts => Store.Accounts;

    public event Action? StateChanged;

    public static string RootPath => VaultPaths.Root;

    private void Raise()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    // ------------------------------------------------------------------ 设备归属

    public sealed record DeviceResolution(VaultAccount? Account, DeviceBinding? Binding, bool SuspectedSameName, string FingerprintHash);

    /// <summary>投屏设备连上来时调用：命中绑定表就把归属切到对应账号。</summary>
    public DeviceResolution ResolveDevice(string? transmitterName, string? macAddress)
    {
        string fingerprint = VaultCrypto.DeviceFingerprint(transmitterName, macAddress);
        string fingerprintHash = VaultCrypto.FingerprintHash(fingerprint);

        var hit = Store.FindBinding(fingerprintHash);
        if (hit is not null)
        {
            Store.TouchBinding(hit.Value.Account.Id, fingerprintHash);
            ActiveAccount = hit.Value.Account;
            ActiveBinding = hit.Value.Binding;
            Raise();
            return new DeviceResolution(hit.Value.Account, hit.Value.Binding, false, fingerprintHash);
        }

        // MAC 可能随机化：名字相同就提示「疑似同一台设备」
        var sameName = Store.FindBindingByLabel(transmitterName ?? "");
        ActiveAccount = null;
        ActiveBinding = null;
        Raise();
        return new DeviceResolution(null, null, sameName is not null, fingerprintHash);
    }

    /// <summary>设备断开：清掉归属，避免下一台未绑定设备被误归。</summary>
    public void ClearActiveDevice()
    {
        if (ActiveAccount is null && ActiveBinding is null) return;
        ActiveAccount = null;
        ActiveBinding = null;
        Raise();
    }

    /// <summary>直接指定归属账号（演示模式 / 自检用：没有真实投屏设备时模拟一台已绑定设备）。</summary>
    public void ForceActiveAccount(VaultAccount account)
    {
        ActiveAccount = account;
        ActiveBinding = account.Bindings.FirstOrDefault();
        Raise();
    }

    public bool BindDevice(Guid accountId, string fingerprintHash, string label)
    {
        bool ok = Store.BindDevice(accountId, fingerprintHash, label);
        if (ok)
        {
            var account = Store.FindById(accountId);
            ActiveAccount = account;
            ActiveBinding = account?.Bindings.FirstOrDefault(b => b.FingerprintHash == fingerprintHash);
            Raise();
        }
        return ok;
    }

    public void UnbindDevice(Guid accountId, string fingerprintHash)
    {
        Store.UnbindDevice(accountId, fingerprintHash);
        if (ActiveBinding?.FingerprintHash == fingerprintHash) { ActiveAccount = null; ActiveBinding = null; }
        Raise();
    }

    // ------------------------------------------------------------------ 账号

    public (bool Ok, string? Error, VaultAccount? Account) CreateAccount(string userName, string password)
    {
        var result = Store.Create(userName, password);
        if (result.Ok) Raise();
        return result;
    }

    public bool ChangePassword(Guid accountId, string oldPassword, string newPassword, out string? error)
        => Store.ChangePassword(accountId, oldPassword, newPassword, out error);

    public bool DeleteAccount(Guid accountId, bool wipeItems)
    {
        bool wasActive = ActiveAccount?.Id == accountId;
        bool unlocked = _library?.AccountId == accountId;
        if (unlocked) Lock();
        bool ok = Store.Delete(accountId, wipeItems);
        if (wasActive) { ActiveAccount = null; ActiveBinding = null; }
        if (ok) Raise();
        return ok;
    }

    // ------------------------------------------------------------------ 解锁 / 上锁

    public bool Unlock(string userName, string password, out string? error)
    {
        error = null;
        var result = Store.Login(userName, password);
        if (!result.Ok || result.Account is null || result.PrivateKey is null)
        {
            error = result.Error ?? "账号名或密码不正确";
            Raise();
            return false;
        }

        var library = VaultLibrary.Open(result.Account.Id, result.PrivateKey, out string? libraryError);
        if (library is null)
        {
            VaultCrypto.Zero(result.PrivateKey);
            error = libraryError ?? "打开库失败";
            Raise();
            return false;
        }

        Lock(raise: false);
        _privateKey = result.PrivateKey;
        _library = library;
        if (ActiveAccount is null) ActiveAccount = result.Account;   // 没有投屏设备时，按解锁的账号归属
        Host.Log.Info("vault", $"库已解锁（{library.Items.Count} 条）");
        Raise();
        return true;
    }

    /// <summary>上锁：私钥清零、库关闭。之后任何读取都必须重新输密码。</summary>
    public void Lock(bool raise = true)
    {
        try { _library?.Dispose(); } catch { }
        _library = null;
        if (_privateKey is not null) { VaultCrypto.Zero(_privateKey); _privateKey = null; }
        if (raise) { Host.Log.Info("vault", "库已上锁"); Raise(); }
    }

    /// <summary>退出程序时调用（同 Lock，但不再触发界面刷新）。</summary>
    public void Shutdown() => Lock(raise: false);

    // ------------------------------------------------------------------ 便于统计/配额

    public long ActiveAccountBytes => ActiveAccount is null ? 0 : VaultPaths.MeasureAccountBytes(ActiveAccount.Id);

    public long QuotaBytes => Math.Max(256, Host.Settings.VaultQuotaMB) * 1024L * 1024L;

    public bool IsOverQuota => ActiveAccountBytes > QuotaBytes;
}
