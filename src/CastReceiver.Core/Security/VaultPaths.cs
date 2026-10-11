namespace CastReceiver.Core.Security;

/// <summary>加密库的目录约定。全部放在 %LocalAppData%\CastReceiver\vault 下，随 Windows 用户隔离。</summary>
public static class VaultPaths
{
    public const string ItemExtension = ".crv";

    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver", "vault");

    /// <summary>库根目录；自检/测试可指向临时目录。</summary>
    public static string Root
    {
        get => _root;
        set => _root = value;
    }

    public static string AccountsFile => Path.Combine(Root, "accounts.json");

    public static string AccountDirectory(Guid accountId) => Path.Combine(Root, accountId.ToString("N"));

    public static string ItemsDirectory(Guid accountId) => Path.Combine(AccountDirectory(accountId), "items");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        TryHide(Root);
    }

    public static void EnsureItemsDirectory(Guid accountId)
    {
        var items = ItemsDirectory(accountId);
        Directory.CreateDirectory(items);
        TryHide(AccountDirectory(accountId));
        TryHide(items);
    }

    private static void TryHide(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (info.Exists && !info.Attributes.HasFlag(FileAttributes.Hidden))
                info.Attributes |= FileAttributes.Hidden;
        }
        catch { }
    }

    /// <summary>
    /// 所有条目文件的创建/修改/访问时间统一归一到同一个常量：
    /// 否则「什么时候录的」会从文件系统时间泄露出去（内容本身是加密的，时间也必须藏住）。
    /// </summary>
    public static readonly DateTime NormalizedFileTimeUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void NormalizeFileTimes(string path)
    {
        try
        {
            File.SetCreationTimeUtc(path, NormalizedFileTimeUtc);
            File.SetLastWriteTimeUtc(path, NormalizedFileTimeUtc);
            File.SetLastAccessTimeUtc(path, NormalizedFileTimeUtc);
        }
        catch { }
    }

    public static bool LooksLikeItem(string path) => path.EndsWith(ItemExtension, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> EnumerateItems(Guid accountId)
    {
        string dir = ItemsDirectory(accountId);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*" + ItemExtension))
            yield return file;
    }

    /// <summary>账号目录占用的字节数（用于配额与展示）。</summary>
    public static long MeasureAccountBytes(Guid accountId)
    {
        long total = 0;
        foreach (var file in EnumerateItems(accountId))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    public static int DeleteAllItems(Guid accountId)
    {
        int removed = 0;
        foreach (var file in EnumerateItems(accountId).ToArray())
        {
            try { File.Delete(file); removed++; } catch { }
        }
        return removed;
    }
}
