using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CastReceiver.App.Interop;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace CastReceiver.App;

/// <summary>
/// **原生**启动入场动画窗口。
///
/// 为什么不用 WPF 里的遮罩：用户实测"点开图标后要黑等一会儿才出动画"。
/// 那段时间是 WPF 的固定开销（解析 XAML ~700ms + 显示第一个 WPF 窗口 ~950ms，与窗口复杂度无关），
/// 所以画面必须在 WPF 起来**之前**就放出来 —— 这个窗口在 <see cref="Program"/> 里
/// （创建 <c>App</c> 之前）就创建并起播，走的是和镜像画面同一条 Composition 渲染链路，
/// 完全不经过 WPF，因此几十到一两百毫秒就能出画。
///
/// 与主窗口的交接：主窗口就绪后调用 <see cref="Finish"/>；片子先放完就停在最后一帧等，
/// 因为用户明确要求"动画结束后不要看到任何加载等待"。
/// 只用 Win32 消息与定时器（SetTimer / PostMessage），不依赖 WPF 的 Dispatcher。
/// </summary>
internal sealed class NativeIntroWindow
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int SwShow = 5;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmPaint = 0x000F;
    private const int WmTimer = 0x0113;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmSetCursor = 0x0020;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const int WsExNoActivate = 0x08000000;   // 绝不抢焦点：抢了焦点你就没法在设置里打字
    private const int WsExTransparent = 0x00000020;
    private const int GwlExStyle = -20;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int WmKeyDown = 0x0100;
    private const int WmAppFinish = 0x8000 + 1;      // WM_APP + 1：跨线程请求收场
    private const int WmAppAlign = 0x8000 + 2;       // WM_APP + 2：跨线程要求"对齐到主窗口外框"
    private const int WmAppHardClose = 0x8000 + 3;   // WM_APP + 3：跨线程要求"立刻销毁（不等淡出）"
    private const int TimerWatch = 1;                // 播放进度监视
    private const int TimerClose = 2;                // 淡出结束后关窗
    private const int TimerSelfCheck = 3;            // 起播自检（没出画就自己退场）

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr HWnd; public uint Message; public IntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc; public bool Erase; public Rect Paint; public bool Restore; public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style; public IntPtr WndProc; public int ClsExtra; public int WndExtra;
        public IntPtr Instance; public IntPtr Icon; public IntPtr Cursor; public IntPtr Background;
        public string? MenuName; public string? ClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    private static readonly object Gate = new();
    private static NativeIntroWindow? _current;

    // "对齐到主窗口外框"的目标矩形（跨线程传值：写字段 + 发消息，不用指针）
    private static readonly object AlignGate = new();
    private static int _alignX, _alignY, _alignW, _alignH;

    private readonly WndProcDelegate _proc;      // 必须持有引用，否则委托会被回收
    private IntPtr _hwnd;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _visual;
    private SpriteVisual? _flash;                 // 出场时的一下白闪（转场用）
    private CompositionSurfaceBrush? _brush;
    private MediaPlayerSurface? _surface;
    private MediaPlayer? _player;
    private double _endSeconds;
    private double _extendSeconds;        // 正片放完后额外展示这么久（停在最后一帧）
    private double _extendFrom = -1;      // 延长段的起点（秒表时刻）
    private bool _inExtension;
    private bool _appReady;               // 主窗口说"我已经准备好了"
    private bool _userSkipped;            // 用户已经点了跳过
    private Style _style = DefaultStyle;  // 画面样式（填充/裁切位置/音量/淡入淡出/白闪）
    private bool _videoStarted;
    private bool _videoEnded;
    private bool _finishRequested;
    private bool _closing;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _expectedEnd = -1;      // 这段素材该放多久（含裁切），按秒表算，不依赖事件
    private double _lastPosition = -1;
    private double _hardDeadline = -1;     // 硬性期限（秒，相对秒表）：到点无条件收场
    private int _stalledFor;
    private bool _closed;

    private NativeIntroWindow() => _proc = WindowProc;

    /// <summary>正在放（主窗口据此决定"还要不要自己再放一遍遮罩动画"）。</summary>
    public static bool IsPlaying
    {
        get { lock (Gate) { return _current is not null && !_current._closed; } }
    }

    /// <summary>入场窗收场之后通知一声（预览用它把界面放回来）。</summary>
    public static event Action? Closed;

    /// <summary>片子已经播完（程序可能还没就绪）。</summary>
    public static bool HasEnded
    {
        get { lock (Gate) { return _current?._videoEnded ?? true; } }
    }

    /// <summary>
    /// 在独立 STA 线程上创建并起播；窗口一显示就返回 true（不等播完）。
    /// 返回 false 表示没能起来 —— 调用方回退到 WPF 内部的遮罩动画。
    /// </summary>
    /// <summary>入场画面的样式（都来自设置，用户要求"几乎什么都能编辑"）。</summary>
    public sealed record Style(string Stretch, double AlignH, double AlignV, double Volume, int FadeInMs, int FadeOutMs, bool Flash);

    public static bool Start(string videoPath, double startSeconds, double endSeconds, double extendSeconds,
        int x, int y, int width, int height, Style? style = null)
    {
        if (!File.Exists(videoPath)) return false;

        try
        {
            using var shown = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new NativeIntroWindow();
                    lock (Gate) { _current = window; }
                    window.Run(videoPath, startSeconds, endSeconds, extendSeconds, x, y, width, height, style ?? DefaultStyle, shown);
                }
                catch (Exception ex)
                {
                    Bootstrap.Log("原生入场动画失败：" + ex.Message);
                    shown.Set();
                }
                finally
                {
                    lock (Gate) { _current = null; }
                }
            })
            {
                IsBackground = true,
                Name = "投屏Window-入场动画",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            shown.Wait(TimeSpan.FromSeconds(3));
            return IsPlaying;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("原生入场动画启动失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 把入场窗对齐到主窗口的**真实外框**（含标题栏那一圈）。
    /// 为什么需要：入场窗一开始用的是"设置里记的窗口矩形"，而主窗口实际显示时可能不一样
    /// （比如上次是最大化关闭的、或 DPI 缩放不同）—— 用户实测看到主窗口标题栏露在入场画面上面。
    /// </summary>
    public static void AlignTo(int x, int y, int w, int h)
    {
        NativeIntroWindow? window;
        lock (Gate) { window = _current; }
        if (window is null || w < 100 || h < 100) return;

        lock (AlignGate) { _alignX = x; _alignY = y; _alignW = w; _alignH = h; }
        IntPtr hwnd = window._hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmAppAlign, IntPtr.Zero, IntPtr.Zero);
    }
    /// <summary>请它收场：淡出并把屏幕让给主窗口（幂等，可从任意线程调用）。</summary>
    public static void Finish()
    {
        NativeIntroWindow? window;
        lock (Gate) { window = _current; }
        if (window is null) return;

        IntPtr hwnd = window._hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmAppFinish, IntPtr.Zero, IntPtr.Zero);
    }

    private static readonly Style DefaultStyle = new("fill", 0.5, 0.5, 0.85, 760, 880, true);

    private void Run(string videoPath, double startSeconds, double endSeconds, double extendSeconds,
        int x, int y, int width, int height, Style style, ManualResetEventSlim shown)
    {
        _endSeconds = endSeconds;
        _extendSeconds = extendSeconds;
        _style = style ?? DefaultStyle;
        Bootstrap.Log($"入场动画参数：起始={startSeconds:F1}s 结束={endSeconds:F1}s 延长={extendSeconds:F1}s " +
                      $"填充={_style.Stretch} 音量={_style.Volume:F2} 进={_style.FadeInMs}ms 出={_style.FadeOutMs}ms");

        WndClass wc = new()
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = "CastReceiverIntroWindow",
            Background = CreateSolidBrush(0x000000),
        };
        RegisterClassW(ref wc);

        _hwnd = CreateWindowEx(WsExTopmost | WsExToolWindow | WsExNoActivate, wc.ClassName!, "投屏Window",
            WsPopup, x, y, width, height, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("入场动画窗口创建失败");

        CoreMessaging.EnsureDispatcherQueue();
        _compositor = new Compositor();
        var interop = WinRT.CastExtensions.As<ICompositorDesktopInterop>(_compositor);
        Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(_hwnd, true, out IntPtr targetPtr));
        _target = DesktopWindowTarget.FromAbi(targetPtr);
        Marshal.Release(targetPtr);

        _root = _compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;
        _target.Root = _root;

        var background = _compositor.CreateSpriteVisual();
        background.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        background.RelativeSizeAdjustment = Vector2.One;
        _root.Children.InsertAtBottom(background);

        // 音量按设置来（用户要"什么都可编辑"）
        _player = new MediaPlayer { AutoPlay = false, IsMuted = false, Volume = Math.Clamp(_style.Volume, 0, 1) };
        try { _player.AudioCategory = MediaPlayerAudioCategory.Media; } catch { }
        try { _player.CommandManager.IsEnabled = false; } catch { }
        _player.SetSurfaceSize(new Windows.Foundation.Size(1920, 1080));   // 默认只有 640×480；稍后按素材真实尺寸对齐

        _player.MediaOpened += (_, _) =>
        {
            try
            {
                if (startSeconds > 0.05) _player.PlaybackSession.Position = TimeSpan.FromSeconds(startSeconds);
                _player.Play();
                _videoStarted = true;
                MatchSurfaceToVideo();      // ← 关键：表面尺寸必须等于视频真实尺寸，否则上下会留黑边

                // 该放多久：设了结束秒就用它，否则用素材总长（都减去起始偏移）
                double total = _player.PlaybackSession.NaturalDuration.TotalSeconds;
                double end = _endSeconds > 0.05 ? _endSeconds : total;
                _expectedEnd = Math.Max(1.0, end - startSeconds);
                _clock.Restart();
                Bootstrap.Log($"原生入场动画已出画：{_player.PlaybackSession.NaturalVideoWidth}×" +
                              $"{_player.PlaybackSession.NaturalVideoHeight}，从 {startSeconds:F1}s 起");
            }
            catch (Exception ex) { Bootstrap.Log("原生入场动画起播失败：" + ex.Message); }
        };
        // 自然播完必须能收到：默认"播到片尾"就是靠它把 _videoEnded 置起来，
        // 漏了它入场窗就永远不退（实测：14 秒了还挂着一块盖住主界面的置顶窗）。
        _player.MediaEnded += (_, _) =>
        {
            // 设了"再延长 N 秒"时，片尾不是结束 —— 而是延长段的开始（停在最后一帧继续展示）。
            if (_extendSeconds > 0.05 && !_videoEnded)
            {
                if (!_inExtension)
                {
                    _inExtension = true;
                    _extendFrom = _clock.Elapsed.TotalSeconds;
                }
                return;
            }

            _videoEnded = true;
            if (_finishRequested) StartFadeOut();
        };
        _player.MediaFailed += (_, e) =>
        {
            Bootstrap.Log("原生入场动画播放失败：" + e.ErrorMessage);
            _videoEnded = true;
            PostMessage(_hwnd, WmAppFinish, IntPtr.Zero, IntPtr.Zero);
        };
        _player.PlaybackSession.PlaybackStateChanged += (_, _) =>
        {
            // 注意：进延长段时是**我们自己**把画面暂停住来看最后一帧的，那种 Paused 不算播完。
            // 少了这个 _inExtension 守卫，延长段会被这里立刻判成结束（实测只延长了 1.18 秒而不是 3 秒）。
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused && _videoStarted && !_inExtension)
            {
                _videoEnded = true;
            }
        };

        var surface = _player.GetSurface(_compositor);
        var brush = _compositor.CreateSurfaceBrush(surface.CompositionSurface);
        _brush = brush;
        _surface = surface;
        brush.Stretch = CompositionStretch.UniformToFill;
        brush.HorizontalAlignmentRatio = 0.5f;
        brush.VerticalAlignmentRatio = 0.5f;
        _visual = _compositor.CreateSpriteVisual();
        _visual.Brush = brush;
        _visual.RelativeSizeAdjustment = Vector2.One;
        _visual.CenterPoint = new Vector3(0.5f, 0.5f, 0f);
        _visual.Opacity = 0;             // 从黑里淡出来
        _root.Children.InsertAtTop(_visual);

        // 出场时用来"打一下"的白闪（转场感）：平时完全透明
        _flash = _compositor.CreateSpriteVisual();
        _flash.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        _flash.RelativeSizeAdjustment = Vector2.One;
        _flash.Opacity = 0;
        _root.Children.InsertAtTop(_flash);

        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = TimeSpan.FromMilliseconds(Math.Clamp(_style.FadeInMs, 80, 5000));
        _visual.StartAnimation("Opacity", fadeIn);

        // 进场也带一点推近（从 1.16 落到 1.0），比纯淡入更有"镜头进来"的感觉
        _visual.Scale = new Vector3(1.16f, 1.16f, 1f);
        _visual.CenterPoint = new Vector3(0.5f, 0.5f, 0f);
        var settle = _compositor.CreateVector3KeyFrameAnimation();
        settle.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        settle.Duration = TimeSpan.FromMilliseconds(1600);
        settle.InsertKeyFrame(0.35f, new Vector3(1.05f, 1.05f, 1f));
        _visual.StartAnimation("Scale", settle);

        _player.Source = MediaSource.CreateFromUri(new Uri(videoPath));

        ShowWindow(_hwnd, SwShow);
        SetTimer(_hwnd, TimerWatch, 120, IntPtr.Zero);
        SetTimer(_hwnd, TimerSelfCheck, 1600, IntPtr.Zero);   // 自检：一直没出画就让位
        shown.Set();

        while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            if (msg.Message == WmDestroy || msg.Message == WmClose) break;
        }

        CloseNow();
    }

    /// <summary>
    /// 把渲染表面尺寸对齐视频真实分辨率。
    /// **不这么做就会在表面内部留黑**：表面按 16:9 建，素材是 2.44:1 时播放器会把画面居中塞进去、
    /// 上下补黑，再被铺满窗体 —— 用户看到的就是"启动画面上下有黑边"（实测就是这个原因）。
    /// </summary>
    private void MatchSurfaceToVideo()
    {
        try
        {
            if (_player is null || _compositor is null || _brush is null) return;
            int w = (int)_player.PlaybackSession.NaturalVideoWidth;
            int h = (int)_player.PlaybackSession.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return;

            _player.SetSurfaceSize(new Windows.Foundation.Size(w, h));
            var surface = _player.GetSurface(_compositor);
            _brush.Surface = surface.CompositionSurface;

            // 填充方式与裁切位置都按设置来（用户要"裁切位置可编辑"）
            _brush.Stretch = _style.Stretch switch
            {
                "fit" => CompositionStretch.Uniform,        // 完整显示（可能有黑边）
                "stretch" => CompositionStretch.Fill,       // 拉伸填满
                _ => CompositionStretch.UniformToFill,      // 铺满并裁掉多余（默认）
            };
            _brush.HorizontalAlignmentRatio = (float)Math.Clamp(_style.AlignH, 0, 1);
            _brush.VerticalAlignmentRatio = (float)Math.Clamp(_style.AlignV, 0, 1);

            var old = _surface;
            _surface = surface;
            try { old?.Dispose(); } catch { }
            Bootstrap.Log($"入场画面表面已对齐素材：{w}×{h}");
        }
        catch (Exception ex) { Bootstrap.Log("对齐入场画面表面失败：" + ex.Message); }
    }
    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WmPaint:
            {
                GetClientRect(hwnd, out Rect rc);
                IntPtr brush = CreateSolidBrush(0x000000);
                IntPtr ps = BeginPaint(hwnd, out PaintStruct paint);
                FillRect(paint.Hdc, ref rc, brush);
                EndPaint(hwnd, ref paint);
                DeleteObject(brush);
                return IntPtr.Zero;
            }

            case WmLeftButtonDown:
            case WmKeyDown:
                // 用户要求：设了"额外延长"时，**正片期间不响应点击**（避免误触把开场切掉），
                // 超过默认时长（进入延长段）之后，点一下/按一下立即跳过。
                if (_extendSeconds > 0.05 && !_inExtension && !_videoEnded)
                {
                    return IntPtr.Zero;
                }

                OnUserSkip();
                return IntPtr.Zero;

            case WmSetCursor:
                // 鼠标停在这块画面上时不显示指针（用户要求：不要让指针压在入场画面上）。
                // 直接吃掉 WM_SETCURSOR 并把指针设成空，比全局隐藏系统指针干净。
                SetCursor(IntPtr.Zero);
                return new IntPtr(1);

            case WmAppFinish:
                RequestFinish();
                return IntPtr.Zero;

            case WmAppAlign:
            {
                int ax, ay, aw, ah;
                lock (AlignGate) { ax = _alignX; ay = _alignY; aw = _alignW; ah = _alignH; }
                if (aw > 100 && ah > 100)
                {
                    SetWindowPos(hwnd, HwndTopmost, ax, ay, aw, ah, SwpNoActivate | SwpShowWindow);
                }
                return IntPtr.Zero;
            }

            case WmTimer:
                if (w.ToInt64() == TimerWatch) WatchProgress();
                else if (w.ToInt64() == TimerClose) { KillTimer(hwnd, TimerClose); DestroyWindow(hwnd); }
                else if (w.ToInt64() == TimerSelfCheck)
                {
                    KillTimer(hwnd, TimerSelfCheck);
                    if (_videoStarted) return IntPtr.Zero;

                    // 1.6 秒还没出画：与其一直黑着，不如退出，让界面内那层遮罩接手
                    Bootstrap.Log("原生入场动画 1.6 秒未出画，退出让位给界面内遮罩");
                    _videoEnded = true;
                    _finishRequested = true;
                    StartFadeOut();
                }
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, w, l);
    }

    /// <summary>到设定的结束秒就停住（只裁时长，不裁画质）；片子放了 + 程序也要求收场 → 淡出。</summary>
    private void WatchProgress()
    {
        if (_closed || _player is null) return;

        // 只把"读播放器状态"放进 try：读失败（比如播放器已释放）也不能让整个判断逻辑被跳过 ——
        // 之前的写法是整段包在 try/catch 里，一旦读位置抛异常，收场逻辑就永远不执行，
        // 结果留下一块**透明但盖在最上层**的窗（用户实测：输入框点不动、窗口也关不掉）。
        double pos = -1, duration = -1;
        try
        {
            pos = _player.PlaybackSession.Position.TotalSeconds;
            duration = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        }
        catch { }

        try
        {
            if (!_videoEnded && !_inExtension)
            {
                // ① 用户设了结束秒 → 到点停；② 没设 → 到片尾停（不依赖事件，自己比位置）；
                // ③ 秒表兜底：位置/事件都可能拖半秒到一秒，收场时间才准
                bool baseEnded = (_endSeconds > 0.05 && pos >= _endSeconds - 0.02)
                                 || (duration > 0.2 && pos >= duration - 0.08)
                                 || (_expectedEnd > 0 && _clock.Elapsed.TotalSeconds >= _expectedEnd + 0.10);
                if (baseEnded)
                {
                    Bootstrap.Log($"正片结束判据命中：pos={pos:F2}s duration={duration:F2}s 预期={_expectedEnd:F2}s 秒表={_clock.Elapsed.TotalSeconds:F2}s 延长={_extendSeconds:F2}s");
                    // 顺序要紧：先标记"进入延长段"，再 Pause。否则下面 PlaybackStateChanged
                    // 会把这个**故意的暂停**当成"自然播完"，延长段一秒都不会有（实测踩过）。
                    if (_extendSeconds > 0.05)
                    {
                        _inExtension = true;
                        _extendFrom = _clock.Elapsed.TotalSeconds;
                        Bootstrap.Log($"进入延长段：展示最后一帧 {_extendSeconds:F1}s");
                    }
                    else
                    {
                        _videoEnded = true;
                    }

                    try { _player.Pause(); } catch { }        // 停在最后一帧
                }
            }

            if (_inExtension && !_videoEnded)
            {
                if (_clock.Elapsed.TotalSeconds >= _extendFrom + _extendSeconds)
                {
                    Bootstrap.Log($"延长段结束（共 {_clock.Elapsed.TotalSeconds - _extendFrom:F1}s）");
                    _videoEnded = true;
                }
            }

            // 保险：程序早就就绪了，画面却不再前进（解码卡住、事件没来），到点强制收场 ——
            // 绝不能留一块置顶窗盖住界面，那在用户看来就是"点了没反应"。
            if (!_closed && _finishRequested && !_closing && !_inExtension)
            {
                if (Math.Abs(pos - _lastPosition) < 0.01)
                {
                    _stalledFor += 120;
                    if (_stalledFor > 800)
                    {
                        Bootstrap.Log("原生入场动画停住不动了，强制收场");
                        _videoEnded = true;
                    }
                }
                else
                {
                    _stalledFor = 0;
                }
                _lastPosition = pos;
            }

            if (_videoEnded && _finishRequested) StartFadeOut();
        }
        catch (Exception ex)
        {
            Bootstrap.Log("入场动画进度判断异常（下次直接硬收场）：" + ex.Message);
            _stalledFor += 240;      // 异常也算"没进展"，避免异常把收场卡死
        }

        // ------- 硬性期限：不依赖任何事件/位置，到点一定收场 -------
        // 这是最后一道防线：无论播放器、事件、动画出什么问题，窗口都必须离开。
        if (!_closed)
        {
            double deadline = _expectedEnd + _extendSeconds + 8.0;
            if (_hardDeadline <= 0) _hardDeadline = deadline;
            if (_clock.Elapsed.TotalSeconds > _hardDeadline + 6.0)
            {
                Bootstrap.Log("入场动画超过硬性期限，直接销毁窗口");
                DestroyNow();
            }
            else if (_clock.Elapsed.TotalSeconds > _hardDeadline && !_videoEnded)
            {
                Bootstrap.Log("入场动画超过硬性期限，强制收场");
                _videoEnded = true;
                _finishRequested = true;
                StartFadeOut();
            }
        }
    }

    /// <summary>立刻销毁窗口并通知 Closed（任何异常都不能拦住它）。</summary>
    private void DestroyNow()
    {
        try { _closed = true; } catch { }
        try { if (_hwnd != IntPtr.Zero) { KillTimer(_hwnd, TimerWatch); DestroyWindow(_hwnd); } } catch { }
        try { Closed?.Invoke(); } catch { }
    }
    /// <summary>不等淡出，直接销毁窗口（最后一道保险；可从任意线程调用）。</summary>
    public static void ForceClose()
    {
        NativeIntroWindow? window;
        lock (Gate) { window = _current; }
        if (window is null) return;

        IntPtr hwnd = window._hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmAppHardClose, IntPtr.Zero, IntPtr.Zero);
    }
    /// <summary>主窗口说"我已经就绪了"：片子也放完（或用户已跳过）就立刻收场，否则停在最后一帧等。</summary>
    private void RequestFinish()
    {
        _appReady = true;
        _finishRequested = true;
        if (_videoEnded || _userSkipped) StartFadeOut();
    }

    /// <summary>用户点了跳过：正片放完就收场（程序还没就绪时先记下来，等它就绪那一刻再退）。</summary>
    private void OnUserSkip()
    {
        _userSkipped = true;
        if (_appReady)
        {
            _videoEnded = true;
            StartFadeOut();
        }
    }

    private void StartFadeOut()
    {
        if (_closing || _closed || _compositor is null || _visual is null) return;
        _closing = true;

        try
        {
            // 转场三部曲（用户要"更明显的转场"）：
            // ① 先打一下白闪（90ms 升到 22%，再 320ms 退掉）——像镜头切换时的一下曝光
            // ② 画面本身明显推近（1.0 → 1.30）并淡出，比原来 1.10 强得多
            // ③ 主窗口在这期间淡入（由 MainWindow 那边配合），形成交叉溶解
            // 淡出这期间就让它"点击穿透"：万一后面销毁失败，也绝不会挡住你点界面
            try
            {
                long ex = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
                SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(ex | WsExTransparent));
            }
            catch { }

            if (_flash is not null && _style.Flash)
            {
                var flash = _compositor.CreateScalarKeyFrameAnimation();
                flash.InsertKeyFrame(0.22f, 0.22f);
                flash.InsertKeyFrame(1f, 0f);
                flash.Duration = TimeSpan.FromMilliseconds(420);
                _flash.StartAnimation("Opacity", flash);
            }

            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromMilliseconds(Math.Clamp(_style.FadeOutMs, 100, 5000));
            _visual.StartAnimation("Opacity", fade);

            var push = _compositor.CreateVector3KeyFrameAnimation();
            push.InsertKeyFrame(1f, new Vector3(1.30f, 1.30f, 1f));
            push.Duration = TimeSpan.FromMilliseconds(Math.Clamp(_style.FadeOutMs, 100, 5000));
            _visual.StartAnimation("Scale", push);
        }
        catch { }

        SetTimer(_hwnd, TimerClose, (uint)Math.Clamp(_style.FadeOutMs + 40, 200, 5200), IntPtr.Zero);
    }

    private void CloseNow()
    {
        if (_closed) return;
        _closed = true;

        try { KillTimer(_hwnd, TimerWatch); } catch { }
        try { _player?.Pause(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        try { _target?.Dispose(); } catch { }
        _target = null;
        _root = null;
        _visual = null;
        try { _compositor?.Dispose(); } catch { }
        _compositor = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        Bootstrap.Log("原生入场动画已收场");
        try { Closed?.Invoke(); } catch { }
    }
}
