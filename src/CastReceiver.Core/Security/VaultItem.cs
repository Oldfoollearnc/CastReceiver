using System.Text.Json;
using System.Text.Json.Serialization;

namespace CastReceiver.Core.Security;

public enum VaultItemKind { Screenshot, Recording }

public enum VaultFrameCodec { Jpeg, Png }

/// <summary>写入时提供的条目元数据（全部落在加密头里，磁盘其余位置看不到）。</summary>
public sealed record VaultItemMeta(
    VaultItemKind Kind,
    DateTimeOffset StartUtc,
    int Width,
    int Height,
    int Fps,
    string DeviceFingerprintHash,
    string? Title = null,
    string[]? Tags = null)
{
    public static VaultItemMeta Screenshot(int width, int height, string deviceFpHash, string? title = null)
        => new(VaultItemKind.Screenshot, DateTimeOffset.UtcNow, width, height, 0, deviceFpHash, title);

    public static VaultItemMeta Recording(int width, int height, int fps, string deviceFpHash)
        => new(VaultItemKind.Recording, DateTimeOffset.UtcNow, width, height, fps, deviceFpHash);
}

/// <summary>库里一条条目（列表用，缩略图按需解密）。</summary>
public sealed record VaultItemInfo(
    Guid ItemId,
    string Path,
    VaultItemKind Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    int FrameCount,
    int Width,
    int Height,
    int Fps,
    string DeviceFingerprintHash,
    string? Title,
    string[] Tags,
    long Bytes,
    bool Completed,
    byte[]? ThumbnailJpeg)
{
    [JsonIgnore]
    public TimeSpan Duration => EndUtc is null ? TimeSpan.Zero : EndUtc.Value - StartUtc;

    /// <summary>列表里显示的名字：标题优先，其次类型 + 时间（时间来自加密头，不是文件名）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? $"{(Kind == VaultItemKind.Recording ? "录像" : "截图")} {StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : Title!;
}

/// <summary>加密头的磁盘表示（字段名尽量短，反正也是加密的）。</summary>
internal sealed class VaultHeaderDto
{
    public int v { get; set; } = 1;
    public string kind { get; set; } = "recording";
    public long start { get; set; }
    public long? end { get; set; }
    public int frames { get; set; }
    public int w { get; set; }
    public int h { get; set; }
    public int fps { get; set; }
    public string dev { get; set; } = "";
    public string? title { get; set; }
    public string[]? tags { get; set; }

    public static VaultHeaderDto From(VaultItemMeta meta) => new()
    {
        kind = meta.Kind == VaultItemKind.Recording ? "recording" : "screenshot",
        start = meta.StartUtc.ToUnixTimeMilliseconds(),
        w = meta.Width,
        h = meta.Height,
        fps = meta.Fps,
        dev = meta.DeviceFingerprintHash,
        title = meta.Title,
        tags = meta.Tags,
    };

    public VaultItemMeta ToMeta() => new(
        string.Equals(kind, "screenshot", StringComparison.OrdinalIgnoreCase) ? VaultItemKind.Screenshot : VaultItemKind.Recording,
        DateTimeOffset.FromUnixTimeMilliseconds(start), w, h, fps, dev, title, tags);
}
