namespace CastReceiver.Core.Android;

/// <summary>画面在窗口里的适配方式（与 <c>CompositionVideoHost.VideoFit</c> 一一对应）。</summary>
public enum VideoFitMode
{
    /// <summary>适应：完整显示，四周留黑。</summary>
    Fit,
    /// <summary>铺满：等比放大填满，超出部分裁切。</summary>
    Fill,
    /// <summary>拉伸：强行填满（可能变形）。</summary>
    Stretch,
    /// <summary>原始：1:1 像素，不缩放。</summary>
    Native,
}

/// <summary>一块矩形（宿主坐标系）。</summary>
public readonly record struct VideoBox(double X, double Y, double Width, double Height)
{
    public bool IsUsable => Width > 0.5 && Height > 0.5;
}

/// <summary>映射结果。</summary>
public readonly record struct ScreenMapResult(bool Inside, double U, double V, int PhoneX, int PhoneY, string? Note)
{
    public static ScreenMapResult Outside(string? note) => new(false, 0, 0, 0, 0, note);
}

/// <summary>
/// 「画面上的一点」→「手机屏幕上的一点」的换算（纯数学，无 UI / 无 adb 依赖，所以可以逐条自检）。
///
/// 要处理三件事：
/// <list type="number">
/// <item>画面在窗口里不一定铺满：适应会留黑边、铺满会裁切、原始是 1:1、还可能叠加了缩放
///       —— 点在黑边上就不该往手机发点击；</item>
/// <item>手机可能横屏（旋转 0/1/2/3），此时屏幕宽高互换；</item>
/// <item>点按坐标系与当前方向的关系要按**实测结论**来（见 <see cref="TapSpace"/> 的说明）。</item>
/// </list>
/// </summary>
public static class ScreenCoordinateMapper
{
    /// <summary>
    /// <c>adb shell input tap x y</c> 用哪个方向的空间。
    ///
    /// 实测结论（本机 Android 14 模拟器，用「显示指针位置」的十字线反查落点）：
    /// 见 docs/升级-实现进度.md 的证据段 —— <see cref="TapCoordinateSpace.Current"/> 表示
    /// 坐标按**当前方向**（横屏时宽高已互换）解释，<see cref="TapCoordinateSpace.Natural"/>
    /// 表示按**自然方向**（wm size 报的那组）解释。
    /// </summary>
    public static TapCoordinateSpace TapSpace { get; set; } = TapCoordinateSpace.Current;

    /// <summary>算出画面在宿主客户区里实际占据的矩形。</summary>
    /// <param name="hostWidth">宿主客户区宽（与返回值同单位，通常是 DIP）</param>
    /// <param name="hostHeight">宿主客户区高</param>
    /// <param name="videoWidth">视频像素宽（解码出来的真实分辨率）</param>
    /// <param name="videoHeight">视频像素高</param>
    /// <param name="zoom">额外缩放倍率（以宿主中心为基准）</param>
    /// <param name="dpiScale">显示缩放（原始 1:1 模式要用它把物理像素换算成 DIP）</param>
    public static VideoBox ComputeVideoBox(VideoFitMode fit, double hostWidth, double hostHeight,
                                           double videoWidth, double videoHeight, double zoom = 1.0, double dpiScale = 1.0)
    {
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return new VideoBox(0, 0, Math.Max(0, hostWidth), Math.Max(0, hostHeight));
        }

        if (videoWidth <= 0 || videoHeight <= 0)
        {
            // 还没有画面尺寸（还没解码出第一帧）：整块都当成画面，至少不算"点在黑边上"
            return new VideoBox(0, 0, hostWidth, hostHeight);
        }

        double boxWidth, boxHeight;
        switch (fit)
        {
            case VideoFitMode.Stretch:
                boxWidth = hostWidth;
                boxHeight = hostHeight;
                break;

            case VideoFitMode.Native:
                boxWidth = videoWidth / Math.Max(0.01, dpiScale);
                boxHeight = videoHeight / Math.Max(0.01, dpiScale);
                break;

            case VideoFitMode.Fill:
            {
                double scale = Math.Max(hostWidth / videoWidth, hostHeight / videoHeight);
                boxWidth = videoWidth * scale;
                boxHeight = videoHeight * scale;
                break;
            }

            default:
            {
                double scale = Math.Min(hostWidth / videoWidth, hostHeight / videoHeight);
                boxWidth = videoWidth * scale;
                boxHeight = videoHeight * scale;
                break;
            }
        }

        double x = (hostWidth - boxWidth) / 2;
        double y = (hostHeight - boxHeight) / 2;

        // 画面视觉是按宿主中心缩放的，所以矩形也要以中心为基准缩放
        if (Math.Abs(zoom - 1.0) > 0.001)
        {
            double cx = hostWidth / 2, cy = hostHeight / 2;
            x = cx + (x - cx) * zoom;
            y = cy + (y - cy) * zoom;
            boxWidth *= zoom;
            boxHeight *= zoom;
        }

        return new VideoBox(x, y, boxWidth, boxHeight);
    }

    /// <summary>
    /// 把宿主坐标上的一点映射成手机坐标。点落在画面之外（黑边）时返回 <c>Inside=false</c>，
    /// 调用方**必须**据此跳过这次点击（否则会点到手机屏幕上莫名其妙的地方）。
    /// </summary>
    public static ScreenMapResult Map(double hostX, double hostY, VideoBox box, AndroidScreen screen, double videoWidth = 0, double videoHeight = 0)
    {
        if (!box.IsUsable)
        {
            return ScreenMapResult.Outside("画面区域不可用");
        }

        if (!screen.IsValid)
        {
            return ScreenMapResult.Outside("还没拿到手机屏幕尺寸");
        }

        double u = (hostX - box.X) / box.Width;
        double v = (hostY - box.Y) / box.Height;
        if (u < 0 || u > 1 || v < 0 || v > 1)
        {
            return ScreenMapResult.Outside(null);       // 点在留黑/裁切区外，静默跳过
        }

        (int tapWidth, int tapHeight) = TapSpace switch
        {
            TapCoordinateSpace.Natural => (screen.NaturalWidth, screen.NaturalHeight),
            _ => (screen.CurrentWidth, screen.CurrentHeight),
        };

        // 画面比例与屏幕比例差得多时提示一句（有些 App 会在流内部再留黑边，那种情况坐标会偏）
        string? note = null;
        if (videoWidth > 0 && videoHeight > 0)
        {
            double videoAspect = videoWidth / videoHeight;
            double screenAspect = (double)tapWidth / tapHeight;
            if (Math.Abs(videoAspect - screenAspect) / screenAspect > 0.04)
            {
                note = $"画面比例 {videoAspect:F2} 与手机屏幕 {screenAspect:F2} 不一致，落点可能有偏差";
            }
        }

        int x = (int)Math.Round(u * tapWidth);
        int y = (int)Math.Round(v * tapHeight);
        x = Math.Clamp(x, 0, Math.Max(0, tapWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, tapHeight - 1));
        return new ScreenMapResult(true, u, v, x, y, note);
    }

    /// <summary>滚轮/拖动用的滑动方向换算：把"电脑上的位移"变成"手机上的手指位移"。</summary>
    /// <param name="delta">正数＝向上滚（内容往下走）</param>
    public static (int Dx, int Dy) ScrollVector(int delta, AndroidScreen screen)
    {
        // 手机上的手指方向与内容方向相反：鼠标向上滚＝内容往下走＝手指往下滑
        int distance = Math.Clamp(screen.CurrentHeight / 3, 120, 900);
        int sign = delta > 0 ? 1 : -1;
        return (0, sign * distance);
    }
}

/// <summary><c>input tap</c> 的坐标按哪个方向解释。</summary>
public enum TapCoordinateSpace
{
    /// <summary>按当前显示方向（横屏时宽高已互换）。</summary>
    Current,
    /// <summary>按自然方向（wm size 报的那组宽高）。</summary>
    Natural,
}
