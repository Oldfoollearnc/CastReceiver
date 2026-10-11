using System.Security.Cryptography;
using System.Text;

namespace CastReceiver.Core.Security;

/// <summary>
/// 加密库的密码学原语（只做算法，不管文件格式与界面）。
///
/// 密钥体系（为什么这样设计）：
///   账号生成一对 RSA-2048：公钥明文保存，私钥用「密码派生的 KEK」加密后保存。
///   写内容（截图/录制）只需要公钥 —— 所以启动、投屏、录制全程不需要输密码；
///   读内容必须输入密码解出私钥 —— 所以「打开我的库要密码」是加密学保证，
///   而不是界面上的一个开关（拿到整个目录 + accounts.json 也解不出任何一帧）。
/// </summary>
public static class VaultCrypto
{
    /// <summary>PBKDF2-HMAC-SHA256 迭代次数（约 0.3-0.6 秒/次，用于抵御离线爆破）。</summary>
    public const int PasswordIterations = 600_000;

    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int RsaKeySize = 2048;

    /// <summary>RSA-OAEP(SHA-256) 用 2048 位密钥包裹 32 字节内容密钥，输出固定 256 字节。</summary>
    public const int WrappedKeySize = 256;

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>密码 → KEK。</summary>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>用 KEK 生成的校验值：用于判断密码是否正确，无需触碰私钥。</summary>
    public static byte[] VerifierFor(byte[] kek)
        => HMACSHA256.HashData(kek, "CastReceiver.Vault.Verify.v1"u8);

    /// <summary>账号名 → 不可逆索引（磁盘上不出现明文账号名，且无法枚举账号列表）。</summary>
    public static string LoginHashFor(string userName)
        => Convert.ToBase64String(HMACSHA256.HashData(
            "CastReceiver.Vault.LoginName.v1"u8,
            Encoding.UTF8.GetBytes((userName ?? "").Trim().ToLowerInvariant())));

    /// <summary>投屏设备指纹：设备名 + MAC（只用于判断归属，绝不作为密钥或密码）。</summary>
    public static string DeviceFingerprint(string? name, string? macAddress)
    {
        string n = (name ?? "").Trim().ToLowerInvariant();
        string m = (macAddress ?? "").Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n + "|" + m)));
    }

    /// <summary>设备指纹 → 绑定索引（磁盘上不出现明文 MAC）。</summary>
    public static string FingerprintHash(string fingerprint)
        => Convert.ToBase64String(HMACSHA256.HashData(
            "CastReceiver.Vault.Device.v1"u8,
            Encoding.UTF8.GetBytes(fingerprint ?? "")));

    // ---------------------------------------------------------------- AES-256-GCM

    /// <summary>加密并附带 16 字节认证标签（输出 = 密文 || 标签）。</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var output = new byte[plaintext.Length + TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize), aad);
        return output;
    }

    /// <summary>解密并校验标签；任何一处不符（被篡改/被截断/密钥不对）返回 null。</summary>
    public static byte[]? TryOpen(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad)
    {
        if (ciphertextAndTag.Length < TagSize) return null;
        int payloadLength = ciphertextAndTag.Length - TagSize;
        var plaintext = new byte[payloadLength];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertextAndTag[..payloadLength], ciphertextAndTag[payloadLength..], plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    // ---------------------------------------------------------------- RSA 信封

    public static (string PublicKeySpki, byte[] PrivateKeyPkcs8) CreateAccountKeyPair()
    {
        using var rsa = RSA.Create(RsaKeySize);
        return (Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), rsa.ExportPkcs8PrivateKey());
    }

    /// <summary>用账号公钥包裹内容密钥（写内容时唯一需要的操作，无需密码）。</summary>
    public static byte[] WrapItemKey(string publicKeySpki, byte[] itemKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
        return rsa.Encrypt(itemKey, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>用账号私钥解包内容密钥；私钥不对返回 null。</summary>
    public static byte[]? TryUnwrapItemKey(byte[] privateKeyPkcs8, byte[] wrappedKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
            return rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // ---------------------------------------------------------------- DPAPI（仅用于非关键元数据）

    public static byte[] DpapiProtect(byte[] data, string purpose)
        => ProtectedData.Protect(data, EntropyFor(purpose), DataProtectionScope.CurrentUser);

    public static byte[]? TryDpapiUnprotect(byte[] data, string purpose)
    {
        try { return ProtectedData.Unprotect(data, EntropyFor(purpose), DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    public static string DpapiProtectString(string value, string purpose)
        => Convert.ToBase64String(DpapiProtect(Encoding.UTF8.GetBytes(value ?? ""), purpose));

    public static string? TryDpapiUnprotectString(string base64, string purpose)
    {
        try
        {
            var raw = TryDpapiUnprotect(Convert.FromBase64String(base64), purpose);
            return raw is null ? null : Encoding.UTF8.GetString(raw);
        }
        catch { return null; }
    }

    private static byte[] EntropyFor(string purpose)
        => SHA256.HashData(Encoding.UTF8.GetBytes("CastReceiver.Vault.v1|" + purpose));

    // ---------------------------------------------------------------- 工具

    public static bool FixedEquals(byte[]? a, byte[]? b)
        => a is not null && b is not null && CryptographicOperations.FixedTimeEquals(a, b);

    public static void Zero(params byte[]?[] buffers)
    {
        foreach (var b in buffers) if (b is not null) CryptographicOperations.ZeroMemory(b);
    }

    /// <summary>
    /// 常见弱口令「词根」：密码去掉结尾数字后命中即拒绝。
    /// 这样 password1 / admin123 / qwerty12345 之类都会被挡住，而不是只挡完全相同的字符串。
    /// </summary>
    private static readonly string[] WeakStems =
    {
        "password", "passw0rd", "passwd", "pwd", "qwerty", "qwertyui", "asdf", "zxcvbn", "qazwsx", "1qaz2wsx",
        "admin", "administrator", "root", "master", "login", "guest", "user", "test",
        "letmein", "welcome", "iloveyou", "monkey", "dragon", "sunshine", "princess", "football",
        "baseball", "superman", "batman", "starwars", "pokemon", "computer", "internet", "windows", "microsoft",
        "samsung", "huawei", "xiaomi", "woaini", "5201314", "abc", "abcd", "123456", "23456", "111111", "888888",
        "666666", "000000", "123qwe", "qwe123",
    };

    private static bool IsWeakStem(string lower)
    {
        if (WeakStems.Contains(lower)) return true;
        // 去掉结尾最多 4 位数字后再看是否命中词根（password1、admin123、qwerty2024…）
        string trimmed = lower.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (trimmed.Length >= 4 && trimmed.Length != lower.Length && WeakStems.Contains(trimmed)) return true;
        // 数字夹在中间的常见写法：password123!、admin@123 → 递归看字母部分
        string lettersOnly = new string(lower.Where(char.IsLetter).ToArray());
        if (lettersOnly.Length >= 4 && WeakStems.Contains(lettersOnly)) return true;
        return false;
    }

    /// <summary>密码强度校验；返回 null 表示通过，否则是给用户看的错误文案。</summary>
    public static string? ValidatePassword(string? password, string? userName = null)
    {
        if (string.IsNullOrEmpty(password)) return "请输入密码";
        if (password.Length < 8) return "密码至少 8 位";
        if (password.Length > 128) return "密码最长 128 位";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "密码需同时包含字母和数字";
        if (password.Distinct().Count() < 4) return "密码过于简单（重复字符太多）";
        string lower = password.ToLowerInvariant();
        if (IsWeakStem(lower)) return "这是常见弱密码，请换一个";
        if (!string.IsNullOrWhiteSpace(userName))
        {
            string name = userName.Trim().ToLowerInvariant();
            if (name.Length >= 3 && lower.Contains(name)) return "密码不能包含账号名";
        }
        return null;
    }
}
