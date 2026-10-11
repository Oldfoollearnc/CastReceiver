using System.IO;
using CastReceiver.Core.Security;

namespace CastReceiver.App.Services;

/// <summary>
/// 旧版本留下的明文截图/录像 → 导入加密库（导入成功后删除明文原文件）。
/// 顺带清理旧实现写进 %TEMP% 的明文帧目录。
/// </summary>
public static class LegacyMigration
{
    public sealed record LegacyFile(string Path, VaultItemKind Kind, long Bytes);

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mkv", ".mov", ".wmv" };

    /// <summary>旧版本的明文保存目录（新版不再写入）。</summary>
    public static IReadOnlyList<string> Directories => new[] { CaptureService.OutputDirectory, CaptureService.LegacyDirectory };

    public static IReadOnlyList<LegacyFile> Find()
    {
        var list = new List<LegacyFile>();
        foreach (var directory in Directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                VaultItemKind? kind = ImageExtensions.Contains(extension) ? VaultItemKind.Screenshot
                                    : VideoExtensions.Contains(extension) ? VaultItemKind.Recording
                                    : null;
                if (kind is null) continue;
                long bytes = 0;
                try { bytes = new FileInfo(path).Length; } catch { }
                list.Add(new LegacyFile(path, kind.Value, bytes));
            }
        }
        return list;
    }

    /// <summary>清理旧实现留下的明文帧目录（%TEMP%\CastReceiverFrames_*）。</summary>
    public static int CleanupTempFrameDirectories()
    {
        int removed = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), "CastReceiverFrames_*"))
            {
                try { Directory.Delete(directory, recursive: true); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>把旧明文文件导入指定账号的库；deleteSource=true 时导入成功后删除原文件。</summary>
    public static async Task<(int Imported, int Failed, string? Error)> ImportAsync(
        VaultAccount account, IProgress<string>? progress, bool deleteSource, CancellationToken ct)
    {
        var files = Find();
        if (files.Count == 0) return (0, 0, null);

        string itemsDirectory = VaultPaths.ItemsDirectory(account.Id);
        int imported = 0, failed = 0;
        string? lastError = null;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在导入 {Path.GetFileName(file.Path)}…");
            try
            {
                if (file.Kind == VaultItemKind.Screenshot)
                {
                    using var bitmap = new System.Drawing.Bitmap(file.Path);
                    var meta = VaultItemMeta.Screenshot(bitmap.Width, bitmap.Height, "legacy-import",
                        Path.GetFileNameWithoutExtension(file.Path));
                    using var writer = VaultItemWriter.Create(itemsDirectory, account.PublicKeySpki, meta);
                    writer.AddFrame(ImageCodec.EncodePng(bitmap), VaultFrameCodec.Png, ImageCodec.MakeThumbnailJpeg(bitmap));
                    writer.Complete();
                }
                else
                {
                    await ImportVideoAsync(file.Path, account, itemsDirectory, progress, ct).ConfigureAwait(false);
                }

                imported++;
                if (deleteSource) { try { File.Delete(file.Path); } catch { } }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                lastError = $"{Path.GetFileName(file.Path)}：{ex.Message}";
            }
        }

        progress?.Report($"导入完成：成功 {imported}，失败 {failed}");
        return (imported, failed, lastError);
    }

    /// <summary>把旧 MP4 按帧抽出来重建为加密录像条目（旧容器本身不加密，不能直接沿用）。</summary>
    private static async Task ImportVideoAsync(string path, VaultAccount account, string itemsDirectory,
                                               IProgress<string>? progress, CancellationToken ct)
    {
        var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var composition = new Windows.Media.Editing.MediaComposition();
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(storage);
        composition.Clips.Add(clip);

        double totalSeconds = Math.Max(0.1, composition.Duration.TotalSeconds);
        const int preferredFps = 5;
        const int maxFrames = 1800;                       // 最多 1800 帧，避免超长视频导入过久
        int fps = (int)Math.Max(1, Math.Min(preferredFps, Math.Floor(maxFrames / totalSeconds)));
        int frameCount = Math.Max(1, (int)Math.Floor(totalSeconds * fps));

        int width = 1280, height = 720;
        try
        {
            var properties = clip.GetVideoEncodingProperties();
            if (properties is not null && properties.Width > 0 && properties.Height > 0)
            {
                width = (int)properties.Width;
                height = (int)properties.Height;
            }
        }
        catch { }

        var meta = VaultItemMeta.Recording(width, height, fps, "legacy-import");
        using var writer = VaultItemWriter.Create(itemsDirectory, account.PublicKeySpki, meta);
        for (int i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var position = TimeSpan.FromSeconds((double)i / fps);
            var image = await composition.GetThumbnailAsync(position, width, height,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);
            byte[] encoded = await ReadAllBytesAsync(image).ConfigureAwait(false);
            if (encoded.Length == 0) continue;

            byte[]? thumbnail = null;
            if (writer.FrameCount == 0)
            {
                using var bitmap = ImageCodec.Decode(encoded);
                if (bitmap is not null) thumbnail = ImageCodec.MakeThumbnailJpeg(bitmap);
            }
            writer.AddFrame(encoded, VaultFrameCodec.Jpeg, thumbnail);
            if (i % 25 == 0) progress?.Report($"正在导入 {Path.GetFileName(path)}：{i + 1}/{frameCount} 帧");
        }
        writer.Complete();
    }

    private static async Task<byte[]> ReadAllBytesAsync(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        uint size = (uint)Math.Min(stream.Size, int.MaxValue);
        if (size == 0) return Array.Empty<byte>();
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var buffer = new byte[size];
        reader.ReadBytes(buffer);
        return buffer;
    }
}
