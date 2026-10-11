using System.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace SplashTool;

/// <summary>
/// 启动动画的素材工具：抽帧（挑镜头）与转码成小文件（随程序发布）。
///
/// 全程**不用** StorageFile / MediaClip：未打包的 Win32 程序把它们交给 WinRT 文件代理时
/// 会被拒（实测 UnauthorizedAccessException："没有位于应用程序可以访问的位置"）。
/// 所以：
///   抽帧 → MediaPlayer 的帧服务器（只吃 URI）+ 内存里编码 PNG；
///   转码 → MediaSource + 普通文件流包装成的 IRandomAccessStream。
/// 输出的小视频不再裁剪：启动动画在播放时从指定位置开始、到指定位置结束（更灵活）。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        if (args.Length < 2)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  splash-tool frames <video> <outDir> <t1,t2,...> [宽] [高]");
            Console.WriteLine("  splash-tool clip   <video> <out.mp4> [宽] [高] [kbps]");
            return 2;
        }

        return args[0].ToLowerInvariant() switch
        {
            "frames" => await FramesAsync(args).ConfigureAwait(false),
            "clip" => await ClipAsync(args).ConfigureAwait(false),
            _ => 2,
        };
    }

    // ------------------------------------------------------------------ 抽帧

    private static async Task<int> FramesAsync(string[] args)
    {
        string video = Path.GetFullPath(args[1]);
        string outDir = args[2];
        double[] times = (args.Length > 3 ? args[3] : "0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t, CultureInfo.InvariantCulture))
            .ToArray();
        int width = args.Length > 4 ? int.Parse(args[4]) : 960;
        int height = args.Length > 5 ? int.Parse(args[5]) : 540;

        Directory.CreateDirectory(outDir);
        string baseName = Path.GetFileNameWithoutExtension(video);

        foreach (double t in times)
        {
            try
            {
                byte[]? png = await GrabFrameAsync(video, TimeSpan.FromSeconds(t), width, height).ConfigureAwait(false);
                if (png is null)
                {
                    Console.WriteLine($"ERR {t,6:0.00}s : 没取到帧");
                    continue;
                }

                string path = Path.Combine(outDir, $"{baseName}-{t:0.00}s.png");
                await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);
                Console.WriteLine($"OK  {t,6:0.00}s -> {path}（{png.Length / 1024} KB）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERR {t,6:0.00}s : {ex.GetType().Name}: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>用 MediaPlayer 的帧服务器在指定位置取一帧，编码成 PNG。</summary>
    private static async Task<byte[]?> GrabFrameAsync(string path, TimeSpan position, int width, int height)
    {
        var player = new MediaPlayer { IsVideoFrameServerEnabled = true, IsMuted = true };
        var done = new TaskCompletionSource<byte[]?>();
        bool captured = false;

        player.VideoFrameAvailable += async (s, _) =>
        {
            if (captured) return;
            captured = true;
            try
            {
                var session = player.PlaybackSession;
                double vw = session.NaturalVideoWidth, vh = session.NaturalVideoHeight;
                if (vw <= 0 || vh <= 0) { done.TrySetResult(null); return; }

                // 保持宽高比缩到目标宽度
                double scale = Math.Min(width / vw, height / vh);
                int w = Math.Max(2, (int)(vw * scale));
                int h = Math.Max(2, (int)(vh * scale));

                using var frame = new VideoFrame(BitmapPixelFormat.Bgra8, w, h);
                player.CopyFrameToVideoSurface(frame.Direct3DSurface);

                using var stream = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(frame.SoftwareBitmap);
                await encoder.FlushAsync();

                var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                byte[] bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                done.TrySetResult(bytes);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };

        player.Source = MediaSource.CreateFromUri(new Uri(path));
        player.PlaybackSession.Position = position;
        player.Play();

        Task completed = await Task.WhenAny(done.Task, Task.Delay(15000)).ConfigureAwait(false);
        try { player.Pause(); } catch { }
        try { player.Dispose(); } catch { }

        if (completed is not Task<byte[]?> || !done.Task.IsCompletedSuccessfully) return null;
        return await done.Task.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 转码成小文件

    private static async Task<int> ClipAsync(string[] args)
    {
        string video = Path.GetFullPath(args[1]);
        string outPath = Path.GetFullPath(args[2]);
        uint width = args.Length > 3 ? uint.Parse(args[3]) : 1280;
        uint height = args.Length > 4 ? uint.Parse(args[4]) : 720;
        uint kbps = args.Length > 5 ? uint.Parse(args[5]) : 700;
        double start = args.Length > 6 ? double.Parse(args[6], CultureInfo.InvariantCulture) : 0;
        double end = args.Length > 7 ? double.Parse(args[7], CultureInfo.InvariantCulture) : 8;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.Bitrate = kbps * 1000;
        profile.Audio = null;                  // 启动动画不需要声音

        // 先裁剪再整段转码：MediaComposition 要 MediaClip，而 MediaClip 要"授权过的" StorageFile
        StorageFile source = await StorageFile.GetFileFromPathAsync(video);
        StorageApplicationPermissions.FutureAccessList.Add(source);
        var clip = await MediaClip.CreateFromFileAsync(source);
        double total = clip.OriginalDuration.TotalSeconds;
        double from = Math.Clamp(start, 0, Math.Max(0, total - 0.3));
        double to = Math.Clamp(end, from + 0.3, total);
        clip.TrimTimeFromStart = TimeSpan.FromSeconds(from);
        clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, total - to));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        Console.WriteLine($"裁 {from:F1}s → {to:F1}s（原片 {total:F1}s）并转成 {width}×{height} {kbps}kbps …");
        string stagedOut = Path.Combine(Path.GetTempPath(), "splash-out-" + Guid.NewGuid().ToString("N") + ".mp4");
        StorageFolder tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        StorageFile target = await tempFolder.CreateFileAsync(Path.GetFileName(stagedOut), CreationCollisionOption.ReplaceExisting);
        TranscodeFailureReason reason = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Fast, profile);
        if (reason != TranscodeFailureReason.None)
        {
            Console.WriteLine("转码失败：" + reason);
            return 1;
        }
        File.Copy(target.Path, outPath, overwrite: true);
        var info = new FileInfo(outPath);
        Console.WriteLine($"完成：{outPath}（{info.Length / 1024.0 / 1024.0:F2} MB）");
        return 0;
    }
}
