namespace CastReceiver.Core.Android;

/// <summary>
/// 一次「手机截屏」的结果。
/// 只认 PNG：<c>exec-out screencap -p</c> 在极少数机型/老版本上会退化成原始 RGBA
/// （那种数据没有文件头，我们**不猜格式**，直接如实报失败，免得存进去一张打不开的图）。
/// </summary>
public sealed record AndroidCapture(bool Ok, byte[]? Png, int Width, int Height, string? Error, string CommandLine)
{
    public int ByteCount => Png?.Length ?? 0;

    /// <summary>给人看的一句话。</summary>
    public string Describe() => Ok
        ? $"手机截屏 {Width}×{Height}（PNG {ByteCount / 1024.0:F0} KB）"
        : "截屏失败：" + (Error ?? "未知原因");
}

/// <summary>
/// PNG 的判定与尺寸读取（纯函数，可用真实字节样本自检）。
/// 有了它，"截屏成功"就不是靠"进程退出码为 0"推出来的 —— 能真的验一遍拿到的是不是图。
/// </summary>
public static class PngProbe
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 24) return false;
        for (int i = 0; i < Signature.Length; i++)
        {
            if (bytes[i] != Signature[i]) return false;
        }

        // IHDR 必须是第一个数据块，否则不是一张正常的 PNG
        return bytes[12] == 'I' && bytes[13] == 'H' && bytes[14] == 'D' && bytes[15] == 'R';
    }

    /// <summary>从 IHDR 里读宽高（大端 4 字节，偏移 16 与 20）。不是 PNG 就返回 null。</summary>
    public static (int Width, int Height)? ReadSize(byte[]? bytes)
    {
        if (!IsPng(bytes)) return null;
        int width = ReadBigEndian(bytes!, 16);
        int height = ReadBigEndian(bytes!, 20);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>拿到的到底是什么（失败提示里用，避免只说一句"格式不对"）。</summary>
    public static string Describe(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "没有拿到任何字节";

        if (IsPng(bytes))
        {
            (int Width, int Height)? size = ReadSize(bytes);
            return size is null ? "PNG（读不出尺寸）" : $"PNG {size.Value.Width}×{size.Value.Height}";
        }

        int take = Math.Min(8, bytes.Length);
        string head = string.Join(' ', bytes.Take(take).Select(b => b.ToString("X2")));
        return $"{bytes.Length} 字节，开头是 {head}（不是 PNG）";
    }

    private static int ReadBigEndian(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
