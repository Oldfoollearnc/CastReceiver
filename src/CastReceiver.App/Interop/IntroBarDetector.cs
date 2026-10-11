using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CastReceiver.App.Interop;

/// <summary>
/// 检测视频画面里**自带**的黑边。
///
/// 为什么需要它：很多动画/电影压制把 2.4:1 的画面塞进 16:9 的编码框里，上下那两条黑边是
/// **录在像素里**的 —— 设置里的"铺满"只负责把整帧铺到窗口上，甩不掉它们（用户实测：
/// 选了铺满，画面四周还是有黑边）。要真铺满，只能把画面放大、把黑边移出可视区。
///
/// 做法：在一条独立 STA 线程上用 WPF 的 MediaPlayer 解一帧，画到 RenderTargetBitmap 上，
/// 再逐行/逐列量平均亮度，找出真正的画面区域。只在设置里选好素材后跑（两三秒），
/// 启动路径不跑它。
/// </summary>
public static class IntroBarDetector
{
    /// <summary>检测结果：四条黑边各占整帧的比例（0～1）。</summary>
    public sealed record Result(double Top, double Bottom, double Left, double Right, int FrameWidth, int FrameHeight)
    {
        public bool HasBars => Top + Bottom + Left + Right > 0.004;

        /// <summary>给界面看的一句话。</summary>
        public string Describe()
        {
            if (!HasBars) return "这段素材没有自带黑边";
            string where = (Top > 0.004 && Bottom > 0.004) ? "上下" : (Left > 0.004 && Right > 0.004) ? "左右" : "边上";
            return $"检测到{where}黑边（上 {Top * 100:0.#}% / 下 {Bottom * 100:0.#}% / 左 {Left * 100:0.#}% / 右 {Right * 100:0.#}%），已按它裁切";
        }
    }

    /// <summary>异步检测；<paramref name="done"/> 在**后台线程**上回调（调用方自己切回 UI 线程）。</summary>
    public static void DetectAsync(string path, double atSeconds, Action<Result?> done)
    {
        var thread = new Thread(() =>
        {
            Result? result = null;
            try { result = Detect(path, atSeconds); } catch { }
            try { done(result); } catch { }
        })
        {
            IsBackground = true,
            Name = "intro-bar-detect",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static Result? Detect(string path, double atSeconds)
    {
        if (!System.IO.File.Exists(path)) return null;

        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            bool opened = false;
            player.MediaOpened += (_, _) => opened = true;
            player.MediaFailed += (_, _) => opened = false;
            player.Open(new Uri(path));

            // 等它打开（要泵消息，否则 MediaOpened 不会来）
            for (int i = 0; i < 120 && !opened; i++) Pump(25);
            if (!opened) return null;

            int frameW = player.NaturalVideoWidth, frameH = player.NaturalVideoHeight;
            if (frameW <= 0 || frameH <= 0) return null;

            player.Volume = 0;
            player.Play();
            Pump(500);
            try { player.Position = TimeSpan.FromSeconds(Math.Max(0, atSeconds)); } catch { }
            Pump(1500);                 // 让它落到指定位置并把这一帧画出来

            // 画一帧到位图上（必须处于播放状态，暂停时 WPF 不会把视频合成进位图 —— 实测全黑）
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawVideo(player, new Rect(0, 0, frameW, frameH));
            }

            var bitmap = new RenderTargetBitmap(frameW, frameH, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            int stride = frameW * 4;
            byte[] buffer = new byte[stride * frameH];
            bitmap.CopyPixels(buffer, stride, 0);

            // 逐行/逐列平均亮度（抽样即可，4K 也不用全量算）
            const int Threshold = 22;
            int top = 0;
            while (top < frameH && RowLuma(buffer, stride, frameW, top) < Threshold) top++;
            int bottom = frameH - 1;
            while (bottom > 0 && RowLuma(buffer, stride, frameW, bottom) < Threshold) bottom--;
            int left = 0;
            while (left < frameW && ColLuma(buffer, stride, frameW, frameH, left) < Threshold) left++;
            int right = frameW - 1;
            while (right > 0 && ColLuma(buffer, stride, frameW, frameH, right) < Threshold) right--;

            // 全黑（解不出来）或几乎没剩下画面：当作检测失败
            if (right - left < frameW * 0.3 || bottom - top < frameH * 0.3) return null;

            double topF = top / (double)frameH, bottomF = (frameH - 1 - bottom) / (double)frameH;
            double leftF = left / (double)frameW, rightF = (frameW - 1 - right) / (double)frameW;

            // 太小的当噪声；太大的（超过 45%）说明判断错了，别裁
            if (topF + bottomF > 0.45 || leftF + rightF > 0.45) return null;
            if (topF < 0.004) topF = 0;
            if (bottomF < 0.004) bottomF = 0;
            if (leftF < 0.004) leftF = 0;
            if (rightF < 0.004) rightF = 0;

            return new Result(topF, bottomF, leftF, rightF, frameW, frameH);
        }
        finally
        {
            try { player?.Close(); } catch { }
        }
    }

    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            try
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            }
            catch { }
            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < end);
    }

    private static double RowLuma(byte[] buffer, int stride, int width, int y)
    {
        double sum = 0;
        int count = 0;
        for (int x = 0; x < width; x += 8)
        {
            int i = y * stride + x * 4;
            sum += buffer[i + 2] * 0.299 + buffer[i + 1] * 0.587 + buffer[i] * 0.114;
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    private static double ColLuma(byte[] buffer, int stride, int width, int height, int x)
    {
        double sum = 0;
        int count = 0;
        for (int y = 0; y < height; y += 8)
        {
            int i = y * stride + x * 4;
            sum += buffer[i + 2] * 0.299 + buffer[i + 1] * 0.587 + buffer[i] * 0.114;
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }
}
