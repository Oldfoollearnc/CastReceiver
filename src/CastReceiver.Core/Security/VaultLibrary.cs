namespace CastReceiver.Core.Security;

/// <summary>
/// 一次「已解锁的库会话」：持有账号私钥（仅内存），提供条目列表、缩略图、删除与改名。
/// 关闭库窗口时必须 <see cref="Dispose"/> —— 私钥会被清零，之后无法再解密任何内容。
/// </summary>
public sealed class VaultLibrary : IDisposable
{
    /// <summary>打开库时预解密这么多条缩略图（其余按需解密），避免上千条时卡顿。</summary>
    private const int EagerThumbnailLimit = 200;

    private readonly byte[] _privateKey;
    private readonly List<VaultItemInfo> _items = new();
    private readonly Dictionary<Guid, VaultItemInfo> _byId = new();
    private bool _disposed;

    public Guid AccountId { get; }
    public string ItemsDirectory { get; }
    public int UnreadableItems { get; private set; }

    private VaultLibrary(Guid accountId, byte[] privateKey, string itemsDirectory)
    {
        AccountId = accountId;
        _privateKey = privateKey;
        ItemsDirectory = itemsDirectory;
    }

    public IReadOnlyList<VaultItemInfo> Items => _items;

    public long TotalBytes { get; private set; }

    public int RecordingCount => _items.Count(i => i.Kind == VaultItemKind.Recording);

    public int ScreenshotCount => _items.Count(i => i.Kind == VaultItemKind.Screenshot);

    public static VaultLibrary? Open(Guid accountId, byte[] privateKeyPkcs8, out string? error, string? itemsDirectory = null)
    {
        error = null;
        if (privateKeyPkcs8 is null || privateKeyPkcs8.Length == 0) { error = "尚未解锁账号"; return null; }
        string directory = itemsDirectory ?? VaultPaths.ItemsDirectory(accountId);
        var library = new VaultLibrary(accountId, privateKeyPkcs8, directory);
        try
        {
            library.Reload();
            return library;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            library.Dispose();
            return null;
        }
    }

    public void Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _items.Clear();
        _byId.Clear();
        UnreadableItems = 0;
        TotalBytes = 0;

        if (!Directory.Exists(ItemsDirectory)) return;

        var paths = Directory.EnumerateFiles(ItemsDirectory, "*" + VaultPaths.ItemExtension).ToArray();
        int index = 0;
        foreach (var path in paths)
        {
            try { TotalBytes += new FileInfo(path).Length; } catch { }
            var reader = VaultItemReader.Open(path, _privateKey, out _);
            if (reader is null) { UnreadableItems++; continue; }
            try
            {
                var info = reader.Info;
                if (index >= EagerThumbnailLimit) info = info with { ThumbnailJpeg = null };
                _items.Add(info);
                _byId[info.ItemId] = info;
                index++;
            }
            finally { reader.Dispose(); }
        }

        _items.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
    }

    public VaultItemInfo? Find(Guid itemId) => _byId.TryGetValue(itemId, out var info) ? info : null;

    /// <summary>按需解密缩略图（列表靠后、启动时未预解密的条目）。</summary>
    public byte[]? LoadThumbnail(Guid itemId, out string? error)
    {
        error = null;
        var info = Find(itemId);
        if (info is null) { error = "条目不存在"; return null; }
        if (info.ThumbnailJpeg is { Length: > 0 }) return info.ThumbnailJpeg;

        using var reader = VaultItemReader.Open(info.Path, _privateKey, out error);
        if (reader is null) return null;
        var thumb = reader.ThumbnailJpeg;
        if (thumb is { Length: > 0 })
        {
            var updated = info with { ThumbnailJpeg = thumb };
            int at = _items.IndexOf(info);
            if (at >= 0) _items[at] = updated;
            _byId[itemId] = updated;
        }
        return thumb;
    }

    /// <summary>打开条目用于播放/查看（调用方负责 Dispose）。</summary>
    public VaultItemReader? OpenItem(Guid itemId, out string? error)
    {
        error = null;
        var info = Find(itemId);
        if (info is null) { error = "条目不存在"; return null; }
        return VaultItemReader.Open(info.Path, _privateKey, out error);
    }

    public bool SetTitle(Guid itemId, string? title, out string? error)
    {
        error = null;
        var info = Find(itemId);
        if (info is null) { error = "条目不存在"; return false; }

        using var reader = VaultItemReader.Open(info.Path, _privateKey, out error);
        if (reader is null) return false;
        if (!reader.RewriteHeaderTitle(title, out error)) return false;

        var updated = info with { Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() };
        int at = _items.IndexOf(info);
        if (at >= 0) _items[at] = updated;
        _byId[itemId] = updated;
        return true;
    }

    public bool Delete(Guid itemId, out string? error)
    {
        error = null;
        var info = Find(itemId);
        if (info is null) { error = "条目不存在"; return false; }

        // 可能仍有播放器/读取器开着这个文件（同一进程内的句柄），删不掉时稍等重试一次
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(info.Path);
                _items.Remove(info);
                _byId.Remove(itemId);
                TotalBytes = Math.Max(0, TotalBytes - info.Bytes);
                return true;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(120);
            }
            catch (UnauthorizedAccessException ex)
            {
                error = ex.Message;
                Thread.Sleep(120);
            }
        }

        error = error is null ? "删除失败" : $"删除失败：{error}（文件可能正被播放占用，请先点其它条目再删）";
        return false;
    }

    public int DeleteAll()
    {
        int removed = 0;
        foreach (var info in _items.ToArray())
            if (Delete(info.ItemId, out _)) removed++;
        return removed;
    }

    /// <summary>
    /// 按保留策略清理（只有解锁后才可能执行：时间藏在加密头里）。
    /// 先按天数删过旧的，再按总容量从最旧的删到配额以内。
    /// </summary>
    public int ApplyRetention(TimeSpan? maxAge, long? maxTotalBytes)
    {
        int removed = 0;
        if (maxAge is { } age)
        {
            var cutoff = DateTimeOffset.UtcNow - age;
            foreach (var info in _items.ToArray())
                if (info.StartUtc < cutoff && Delete(info.ItemId, out _)) removed++;
        }
        if (maxTotalBytes is { } limit && limit > 0)
        {
            long total = _items.Sum(i => i.Bytes);
            for (int i = _items.Count - 1; i >= 0 && total > limit; i--)   // 列表按时间倒序：从最旧的删
            {
                var info = _items[i];
                if (Delete(info.ItemId, out _)) { total -= info.Bytes; removed++; }
            }
        }
        return removed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VaultCrypto.Zero(_privateKey);
        _items.Clear();
        _byId.Clear();
    }
}
