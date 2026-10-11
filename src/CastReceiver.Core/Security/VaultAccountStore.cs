using System.Text.Json;
using System.Text.Json.Serialization;

namespace CastReceiver.Core.Security;

/// <summary>一台已绑定的投屏设备。设备身份只决定「内容归属哪个账号」，不作为密码或密钥。</summary>
public sealed class DeviceBinding
{
    /// <summary>指纹哈希（HMACSHA256），磁盘上不出现明文 MAC。</summary>
    public string FingerprintHash { get; set; } = "";

    /// <summary>设备别名（DPAPI 加密，只有当前 Windows 用户能解）。</summary>
    public string LabelDpapi { get; set; } = "";

    /// <summary>绑定时间（DPAPI 加密）。</summary>
    public string BoundDpapi { get; set; } = "";

    /// <summary>最近一次投屏时间（DPAPI 加密）。</summary>
    public string LastSeenDpapi { get; set; } = "";

    [JsonIgnore]
    public string Label
    {
        get => VaultCrypto.TryDpapiUnprotectString(LabelDpapi, "binding.label") ?? "已绑定设备";
        set => LabelDpapi = VaultCrypto.DpapiProtectString(value, "binding.label");
    }

    [JsonIgnore]
    public DateTimeOffset? LastSeenUtc
    {
        get
        {
            var s = VaultCrypto.TryDpapiUnprotectString(LastSeenDpapi, "binding.lastseen");
            return long.TryParse(s, out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
        set => LastSeenDpapi = VaultCrypto.DpapiProtectString((value ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(), "binding.lastseen");
    }

    public void MarkSeen() => LastSeenUtc = DateTimeOffset.UtcNow;
}

/// <summary>一个本地账号：一份 RSA 密钥对 + 自己的加密库 + 已绑定设备表。</summary>
public sealed class VaultAccount
{
    public Guid Id { get; set; }
    public string LoginHash { get; set; } = "";

    /// <summary>
    /// 账号显示名（DPAPI 加密：只有当前 Windows 用户能读到；磁盘上不出现明文账号名）。
    /// 登录仍然只认账号名 + 密码，这里只是为了在界面上把账号列出来给你选。
    /// </summary>
    public string NameDpapi { get; set; } = "";

    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = VaultCrypto.PasswordIterations;
    public string Verifier { get; set; } = "";
    public string PublicKeySpki { get; set; } = "";
    public string PrivateNonce { get; set; } = "";
    public string PrivateWrap { get; set; } = "";
    public List<DeviceBinding> Bindings { get; set; } = new();

    /// <summary>连续登录失败次数（跨重启生效，用于指数延时）。</summary>
    public int FailCount { get; set; }

    [JsonIgnore] public byte[] SaltBytes => Convert.FromBase64String(Salt);
    [JsonIgnore] public byte[] VerifierBytes => Convert.FromBase64String(Verifier);
    [JsonIgnore] public byte[] PrivateWrapBytes => Convert.FromBase64String(PrivateWrap);
    [JsonIgnore] public byte[] PrivateNonceBytes => Convert.FromBase64String(PrivateNonce);

    /// <summary>界面展示用名字；DPAPI 解不开时退化为不含身份信息的占位名。</summary>
    [JsonIgnore]
    public string DisplayName
        => VaultCrypto.TryDpapiUnprotectString(NameDpapi, "account.name") ?? "账号 " + Id.ToString("N")[..6];

    /// <summary>磁盘上是否还存着这个名字（DPAPI 解不开时用于提示你手输账号名）。</summary>
    [JsonIgnore] public bool HasReadableName => VaultCrypto.TryDpapiUnprotectString(NameDpapi, "account.name") is not null;
}

public sealed record VaultLoginResult(bool Ok, string? Error, VaultAccount? Account, byte[]? PrivateKey);

/// <summary>
/// accounts.json 的读写与登录校验。
/// 磁盘上只保留「不可逆索引 + 公钥 + 被密码加密的私钥 + 加密后的设备别名」，
/// 不出现明文账号名、明文 MAC、明文时间。
/// </summary>
public sealed class VaultAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<VaultAccount> Accounts { get; set; } = new();
    }

    private readonly StoreFile _file = new();
    private readonly object _gate = new();

    public string RootPath { get; }
    public string FilePath { get; }

    /// <summary>accounts.json 损坏时为非空：此时禁止创建/登录，避免「毁掉文件 = 重置库」的错觉。</summary>
    public string? LoadError { get; private set; }

    private VaultAccountStore(string root)
    {
        RootPath = root;
        FilePath = Path.Combine(root, "accounts.json");
    }

    public static VaultAccountStore Load(string? root = null)
    {
        string dir = root ?? VaultPaths.Root;
        var store = new VaultAccountStore(dir);
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(store.FilePath))
            {
                var parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(store.FilePath), JsonOptions);
                if (parsed is null) store.LoadError = "账号文件为空或格式不正确";
                else
                {
                    store._file.Version = parsed.Version;
                    store._file.Accounts = parsed.Accounts ?? new();
                }
            }
        }
        catch (Exception ex)
        {
            store.LoadError = "账号文件读取失败：" + ex.Message;
        }
        return store;
    }

    public IReadOnlyList<VaultAccount> Accounts => _file.Accounts;

    public bool HasAnyAccount => _file.Accounts.Count > 0;

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_file, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }

    public VaultAccount? FindById(Guid id) => _file.Accounts.FirstOrDefault(a => a.Id == id);

    public VaultAccount? FindByLoginHash(string loginHash) => _file.Accounts.FirstOrDefault(a => a.LoginHash == loginHash);

    // ------------------------------------------------------------------ 账号

    public (bool Ok, string? Error, VaultAccount? Account) Create(string userName, string password)
    {
        if (LoadError is not null) return (false, LoadError, null);
        userName = (userName ?? "").Trim();
        if (userName.Length < 2) return (false, "账号名至少 2 个字符", null);
        if (userName.Length > 32) return (false, "账号名最长 32 个字符", null);
        string? weak = VaultCrypto.ValidatePassword(password, userName);
        if (weak is not null) return (false, weak, null);

        string loginHash = VaultCrypto.LoginHashFor(userName);
        if (FindByLoginHash(loginHash) is not null) return (false, "这个账号名已经存在", null);

        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.DeriveKey(password, salt, VaultCrypto.PasswordIterations);
        var (publicSpki, privatePkcs8) = VaultCrypto.CreateAccountKeyPair();
        try
        {
            var nonce = VaultCrypto.RandomBytes(VaultCrypto.NonceSize);
            var wrapped = VaultCrypto.Seal(kek, nonce, privatePkcs8, "CastReceiver.Vault.PrivateKey.v1"u8);
            var account = new VaultAccount
            {
                Id = Guid.NewGuid(),
                LoginHash = loginHash,
                NameDpapi = VaultCrypto.DpapiProtectString(userName, "account.name"),
                Salt = Convert.ToBase64String(salt),
                Iterations = VaultCrypto.PasswordIterations,
                Verifier = Convert.ToBase64String(VaultCrypto.VerifierFor(kek)),
                PublicKeySpki = publicSpki,
                PrivateNonce = Convert.ToBase64String(nonce),
                PrivateWrap = Convert.ToBase64String(wrapped),
            };
            _file.Accounts.Add(account);
            Save();
            VaultPaths.EnsureItemsDirectory(account.Id);
            return (true, null, account);
        }
        finally
        {
            VaultCrypto.Zero(kek, privatePkcs8);
        }
    }

    /// <summary>
    /// 登录：账号名 → 不可逆索引 → KEK → 校验密码 → 解出私钥（仅内存）。
    /// 账号不存在时也走一次 PBKDF2，避免通过耗时差异枚举账号名。
    /// </summary>
    public VaultLoginResult Login(string userName, string password)
    {
        if (LoadError is not null) return new VaultLoginResult(false, LoadError, null, null);
        string loginHash = VaultCrypto.LoginHashFor(userName);
        var account = FindByLoginHash(loginHash);

        if (account is null)
        {
            // 假动作：与真实账号同等耗时
            var dummySalt = new byte[VaultCrypto.SaltSize];
            var dummy = VaultCrypto.DeriveKey(password ?? "", dummySalt, VaultCrypto.PasswordIterations);
            VaultCrypto.Zero(dummy);
            return new VaultLoginResult(false, "账号名或密码不正确", null, null);
        }

        byte[] kek;
        try { kek = VaultCrypto.DeriveKey(password ?? "", account.SaltBytes, account.Iterations); }
        catch { return new VaultLoginResult(false, "账号数据损坏", null, null); }

        try
        {
            if (!VaultCrypto.FixedEquals(VaultCrypto.VerifierFor(kek), account.VerifierBytes))
            {
                account.FailCount++;
                Save();
                return new VaultLoginResult(false, "账号名或密码不正确", null, null);
            }

            var privateKey = VaultCrypto.TryOpen(kek, account.PrivateNonceBytes, account.PrivateWrapBytes,
                "CastReceiver.Vault.PrivateKey.v1"u8);
            if (privateKey is null)
                return new VaultLoginResult(false, "私钥解密失败（账号数据可能已损坏）", null, null);

            if (account.FailCount != 0) { account.FailCount = 0; Save(); }
            return new VaultLoginResult(true, null, account, privateKey);
        }
        finally { VaultCrypto.Zero(kek); }
    }

    /// <summary>登录失败后的等待秒数：1、2、4……上限 30 秒。</summary>
    public static int FailureDelaySeconds(int failCount)
        => failCount <= 0 ? 0 : Math.Min(30, 1 << Math.Min(failCount - 1, 5));

    public bool ChangePassword(Guid accountId, string oldPassword, string newPassword, out string? error)
    {
        error = null;
        var account = FindById(accountId);
        if (account is null) { error = "账号不存在"; return false; }

        // 改密只需原密码（不需要账号名）：KDF → 校验 → 解私钥 → 用新密码重新包裹
        var kek = VaultCrypto.DeriveKey(oldPassword ?? "", account.SaltBytes, account.Iterations);
        try
        {
            if (!VaultCrypto.FixedEquals(VaultCrypto.VerifierFor(kek), account.VerifierBytes))
            {
                error = "原密码不正确";
                return false;
            }

            var privateKey = VaultCrypto.TryOpen(kek, account.PrivateNonceBytes, account.PrivateWrapBytes,
                "CastReceiver.Vault.PrivateKey.v1"u8);
            if (privateKey is null) { error = "私钥解密失败（账号数据可能已损坏）"; return false; }
            try { return RewrapPrivateKey(account, privateKey, newPassword, ref error); }
            finally { VaultCrypto.Zero(privateKey); }
        }
        finally { VaultCrypto.Zero(kek); }
    }

    private bool RewrapPrivateKey(VaultAccount account, byte[] privateKey, string newPassword, ref string? error)
    {
        string? weak = VaultCrypto.ValidatePassword(newPassword);
        if (weak is not null) { error = weak; return false; }

        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.DeriveKey(newPassword, salt, VaultCrypto.PasswordIterations);
        try
        {
            var nonce = VaultCrypto.RandomBytes(VaultCrypto.NonceSize);
            account.Salt = Convert.ToBase64String(salt);
            account.Iterations = VaultCrypto.PasswordIterations;
            account.Verifier = Convert.ToBase64String(VaultCrypto.VerifierFor(kek));
            account.PrivateNonce = Convert.ToBase64String(nonce);
            account.PrivateWrap = Convert.ToBase64String(
                VaultCrypto.Seal(kek, nonce, privateKey, "CastReceiver.Vault.PrivateKey.v1"u8));
            account.FailCount = 0;
            Save();
            return true;
        }
        finally { VaultCrypto.Zero(kek); }
    }

    /// <summary>删除账号；wipeItems=true 时连同它的加密库一起删除（不可恢复）。</summary>
    public bool Delete(Guid accountId, bool wipeItems)
    {
        var account = FindById(accountId);
        if (account is null) return false;
        _file.Accounts.Remove(account);
        Save();
        if (wipeItems)
        {
            try { Directory.Delete(VaultPaths.AccountDirectory(accountId), recursive: true); } catch { }
        }
        return true;
    }

    /// <summary>该账号是否已经有绑定设备。</summary>
    public bool HasBindings(Guid accountId) => FindById(accountId)?.Bindings.Count > 0;

    // ------------------------------------------------------------------ 设备绑定

    public static string HashFingerprint(string fingerprint) => VaultCrypto.FingerprintHash(fingerprint);

    public (VaultAccount Account, DeviceBinding Binding)? FindBinding(string fingerprintHash)
    {
        foreach (var account in _file.Accounts)
            foreach (var binding in account.Bindings)
                if (binding.FingerprintHash == fingerprintHash) return (account, binding);
        return null;
    }

    public bool BindDevice(Guid accountId, string fingerprintHash, string label)
    {
        var account = FindById(accountId);
        if (account is null) return false;
        var existing = account.Bindings.FirstOrDefault(b => b.FingerprintHash == fingerprintHash);
        if (existing is not null)
        {
            existing.Label = label;
            existing.MarkSeen();
        }
        else
        {
            var binding = new DeviceBinding { FingerprintHash = fingerprintHash, Label = label };
            binding.BoundDpapi = VaultCrypto.DpapiProtectString(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), "binding.bound");
            binding.MarkSeen();
            account.Bindings.Add(binding);
        }
        Save();
        return true;
    }

    public void UnbindDevice(Guid accountId, string fingerprintHash)
    {
        var account = FindById(accountId);
        if (account is null) return;
        account.Bindings.RemoveAll(b => b.FingerprintHash == fingerprintHash);
        Save();
    }

    public void TouchBinding(Guid accountId, string fingerprintHash)
    {
        var account = FindById(accountId);
        var binding = account?.Bindings.FirstOrDefault(b => b.FingerprintHash == fingerprintHash);
        if (binding is null) return;
        binding.MarkSeen();
        Save();
    }

    /// <summary>按设备名找「疑似同一台设备」（MAC 随机化导致指纹变化时的兜底）。</summary>
    public (VaultAccount Account, DeviceBinding Binding)? FindBindingByLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string target = label.Trim();
        foreach (var account in _file.Accounts)
            foreach (var binding in account.Bindings)
                if (string.Equals(binding.Label, target, StringComparison.OrdinalIgnoreCase))
                    return (account, binding);
        return null;
    }
}
