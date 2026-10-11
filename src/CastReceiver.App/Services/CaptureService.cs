using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CastReceiver.App.Services;

/// <summary>
/// 屏幕抓取（镜像画面由 GPU 合成，只能从屏幕取）。
/// 注意：这里只负责「拿到像素」，保存一律走 <see cref="VaultCaptureService"/> 写入加密库 ——
/// 旧版本直接把明文 PNG / 明文 MP4 写进图片/视频文件夹，那正是本次要消除的隐私缺口。
/// </summary>
public static class CaptureService
{
    /// <summary>旧版本的默认保存目录（新版不再写入，仅用于迁移与清理）。</summary>
    public static string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "投屏Window");

    /// <summary>更早版本的默认目录（图片文件夹）。</summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "投屏Window");

    /// <summary>目录是否位于云盘同步目录内（OneDrive 等）——用于提醒用户。</summary>
    public static bool IsInCloudFolder(string path)
    {
        try
        {
            string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            return !string.IsNullOrEmpty(oneDrive) &&
                   path.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>清空旧版本的明文截图与录像（两个旧目录），返回删除的文件数。</summary>
    public static int ClearLegacy()
    {
        int removed = 0;
        foreach (var dir in new[] { OutputDirectory, LegacyDirectory })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir))
                {
                    try { File.Delete(file); removed++; } catch { }
                }
            }
            catch { }
        }
        return removed;
    }

    /// <summary>抓取屏幕矩形区域；失败返回 null。</summary>
    public static Bitmap? Grab(int x, int y, int width, int height)
    {
        try
        {
            if (width <= 0 || height <= 0) return null;
            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }
}
