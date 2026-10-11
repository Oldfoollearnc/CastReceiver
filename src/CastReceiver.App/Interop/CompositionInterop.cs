using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace CastReceiver.App.Interop;

/// <summary>
/// Windows.UI.Composition 与 WPF 的互操作。
/// 说明：官方教程里 (ICompositorDesktopInterop)(object)compositor 的强制转换写法在 .NET 5+ 已失效
/// （PlatformNotSupportedException），必须使用 CsWinRT 的 As&lt;T&gt;() 扩展。
/// </summary>
[ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICompositorDesktopInterop
{
    [PreserveSig]
    int CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);

    [PreserveSig]
    int EnsureOnThread(int threadId);
}

internal static class CoreMessaging
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    /// <summary>为当前 UI 线程创建 WinRT DispatcherQueue（Composition 与 Miracast 事件回投都需要）。</summary>
    public static bool EnsureDispatcherQueue()
    {
        try
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return true;
            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2,      // DQTYPE_THREAD_CURRENT
                apartmentType = 2,   // DQTAT_COM_STA
            };
            int hr = CreateDispatcherQueueController(options, out _);
            return hr >= 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// 承载 Miracast（或任意 MediaSource）画面的 WPF 控件：
/// 内部创建一个子 HWND，并在其上挂 Windows.UI.Composition 的 DesktopWindowTarget，
/// 再把 MediaPlayer.GetSurface() 得到的画面用 SurfaceBrush 绘制。
/// 关键点：
///   1) 表面之外的区域（上下/左右留黑）必须由我们自己铺一层黑色底，
///      否则会透出子窗口的默认白色背景（用户看到的“白边”）；
///   2) 同时拦截 WM_ERASEBKGND 用黑色填充，双保险；
///   3) 支持“适应/铺满”两种缩放；子窗口收到 Esc 时上报给上层用于退出全屏。
/// </summary>
public sealed class CompositionVideoHost : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private IntPtr _hwnd;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _background;
    private SpriteVisual? _visual;
    private MediaPlayer? _player;
    private MediaPlayerSurface? _surface;
    private MediaSource? _source;
    private bool _fill;
    private int _surfaceWidth = 1920, _surfaceHeight = 1080;

    /// <summary>用于显示「别人的画面」的视觉（画中画 / 并排时用）。</summary>
    private SpriteVisual? _mirrorVisual;

    /// <summary>正在共享本宿主画面的其它宿主（弱引用，避免互相持有不放）。</summary>
    private readonly List<WeakReference<CompositionVideoHost>> _mirrors = new();

    // ---------------------------------------------------------------- 共享合成器
    //
    // 为什么必须共享：一块 MediaPlayer 表面生成的 CompositionSurfaceBrush，
    // 只有两个窗口的 DesktopWindowTarget 属于**同一个 Compositor** 时才能同时使用。
    //
    // 实测结论（tools/ui-spike/share-spike）：同一个画刷挂到两个窗口的视觉树上，
    // 两边同时出画，且解码只做一次。于是「画中画 / 多设备并排」不需要另起一路解码，
    // 也不必改截帧与反截屏那条链路 —— 这是本设计能低风险落地的关键。
    //
    // 引用计数是必需的：某个宿主先销毁时，不能把别人还在用的合成器一起释放掉。
    private static readonly object SharedGate = new();
    private static Compositor? SharedCompositor;
    private static int SharedCompositorUsers;
    private static int SharedCompositorThreadId;

    private static Compositor AcquireSharedCompositor()
    {
        lock (SharedGate)
        {
            int current = Environment.CurrentManagedThreadId;
            if (SharedCompositor is not null && SharedCompositorThreadId != current)
            {
                // Composition 对象有线程亲和性：换线程用会失败，这里直接说清原因
                throw new InvalidOperationException(
                    $"共享 Compositor 属于线程 {SharedCompositorThreadId}，当前线程 {current} 不能使用");
            }

            if (SharedCompositor is null)
            {
                CoreMessaging.EnsureDispatcherQueue();
                SharedCompositor = new Compositor();
                SharedCompositorThreadId = current;
            }

            SharedCompositorUsers++;
            return SharedCompositor;
        }
    }

    private static void ReleaseSharedCompositor()
    {
        lock (SharedGate)
        {
            if (SharedCompositorUsers > 0)
            {
                SharedCompositorUsers--;
            }

            if (SharedCompositorUsers == 0 && SharedCompositor is not null)
            {
                try { SharedCompositor.Dispose(); } catch { }
                SharedCompositor = null;
            }
        }
    }

    /// <summary>诊断用：当前有几个宿主在用共享合成器。</summary>
    public static int SharedCompositorInUse
    {
        get
        {
            lock (SharedGate)
            {
                return SharedCompositorUsers;
            }
        }
    }

    public event Action<string>? Status;

    /// <summary>子窗口收到 Esc 键（用于退出全屏）。</summary>
    public event Action? EscapePressed;

    /// <summary>画面被双击（用于在窗口/全屏之间切换）。</summary>
    public event Action? DoubleClicked;

    // ---------------------------------------------------------------- 关于"画面上的鼠标"
    //
    // 实测（本机 Windows 11 26100 + 本控件）：**画面上的鼠标消息既不会进可重写的 WndProc，
    // 也不会变成这个控件的 WPF 鼠标事件**。子窗口("static")对自己的命中测试是透过的，
    // 消息落到父窗口（WPF 窗口）上；用 SetWindowLongPtr 换掉子窗口过程也收不到鼠标消息
    // （诊断计数：装了子类过程、收到几十条消息，其中鼠标消息 0 条）。
    //
    // 所以"点画面上的哪里"必须在**窗口级**的 PreviewMouse* 里取，再用
    // e.GetPosition(host) 换成画面坐标 —— MainWindow 就是这么做的（见 OnStagePointer）。
    // 这里不再提供 MouseActivity：留着一个永远不会触发的事件只会误导后来的人。

    public bool IsReady => _compositor is not null && _root is not null;
    public int FramesRendered { get; private set; }
    public string? LastError { get; private set; }
    public MediaPlayer? Player => _player;
    public bool IsFillMode => _fill;

    /// <summary>内部子窗口句柄（窗口取帧与反截屏自检用）。</summary>
    public IntPtr ChildHandle => _hwnd;

    /// <summary>
    /// 诊断：Windows.Graphics.Capture 能否直接对我们的合成视觉建捕获项。
    /// 只验证 API 是否接受（真正读帧还需要一层 D3D11 回读，本版未启用）。
    /// </summary>
    public string ProbeVisualCaptureSupport()
    {
        try
        {
            if (_visual is null) return "没有画面（未在镜像）";
            var item = Windows.Graphics.Capture.GraphicsCaptureItem.CreateFromVisual(_visual);
            if (item is null) return "返回空";
            return $"可创建（{item.Size.Width}×{item.Size.Height}）—— 后续版本可启用这条零降级路径";
        }
        catch (Exception ex) { return "不可用：" + ex.Message; }
    }

    /// <summary>当前渲染表面的像素尺寸（应与视频真实分辨率一致，否则会在表面内部留黑）。</summary>
    public int SurfaceWidth => _surfaceWidth;
    public int SurfaceHeight => _surfaceHeight;

    /// <summary>诊断用：当前画面的缩放模式（Uniform=适应 / UniformToFill=铺满 / none=没有画面）。</summary>
    public string StretchModeDebug =>
        (_visual?.Brush as CompositionSurfaceBrush)?.Stretch.ToString() ?? "none";

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        CoreMessaging.EnsureDispatcherQueue();
        int w = Math.Max(1, (int)ActualWidth), h = Math.Max(1, (int)ActualHeight);
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, w, h, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        try
        {
            _compositor = AcquireSharedCompositor();
            var interop = WinRT.CastExtensions.As<ICompositorDesktopInterop>(_compositor);
            Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(_hwnd, true, out IntPtr targetPtr));
            _target = DesktopWindowTarget.FromAbi(targetPtr);
            Marshal.Release(targetPtr);

            _root = _compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _target.Root = _root;

            // 黑色底：视频表面之外的留黑区域由它填充，避免透出子窗口的白色背景
            _background = _compositor.CreateSpriteVisual();
            _background.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            _background.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtBottom(_background);

            Status?.Invoke("Composition 视频宿主已就绪");
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            // 拿到共享合成器之后失败：必须把引用还回去，否则它会永远不被释放
            if (_compositor is not null)
            {
                try { ReleaseSharedCompositor(); } catch { }
                _compositor = null;
            }
            Status?.Invoke("Composition 初始化失败：" + LastError);
        }
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Detach();
        try { _target?.Dispose(); } catch { }
        _target = null;
        _root = null;
        _background = null;

        // 合成器是共享的：只减引用，绝不能直接 Dispose，
        // 否则会把其它窗口（小窗/并排的第二路）的画面一起弄没。
        if (_compositor is not null)
        {
            try { ReleaseSharedCompositor(); } catch { }
            _compositor = null;
        }
        try { DestroyWindow(hwnd.Handle); } catch { }
    }

    /// <summary>拦截背景擦除，用黑色填充（双保险，避免任何情况下露出白底）。</summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND)
        {
            handled = true;
            return new IntPtr(1);
        }
        if (msg == WM_KEYDOWN && wParam.ToInt32() == VK_ESCAPE)
        {
            try { EscapePressed?.Invoke(); } catch { }
            handled = true;
            return IntPtr.Zero;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_hwnd != IntPtr.Zero)
        {
            int w = Math.Max(1, (int)sizeInfo.NewSize.Width);
            int h = Math.Max(1, (int)sizeInfo.NewSize.Height);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h, 0x0004 /*SWP_NOZORDER*/ | 0x0010 /*SWP_NOACTIVATE*/);
        }
        UpdateCenterPoint();   // 尺寸变了，缩放中心点也要跟着更新
    }

    /// <summary>把 MediaSource 画到本控件里（Miracast 或本地/网络媒体均可）。必须在 UI 线程调用。</summary>
    public void AttachSource(MediaSource source, int pixelWidth = 1920, int pixelHeight = 1080, bool realTime = true)
    {
        if (_compositor is null || _root is null)
        {
            LastError = "Composition 尚未就绪";
            Status?.Invoke(LastError);
            return;
        }
        try
        {
            Detach();

            var player = new MediaPlayer
            {
                RealTimePlayback = realTime,   // Miracast 必需：否则会一直缓冲/花屏
                AutoPlay = false,
                IsVideoFrameServerEnabled = false,
            };
            try { player.CommandManager.IsEnabled = false; } catch { }

            player.SetSurfaceSize(new Windows.Foundation.Size(pixelWidth, pixelHeight)); // 默认仅 640x480
            _surfaceWidth = pixelWidth;
            _surfaceHeight = pixelHeight;
            var surface = player.GetSurface(_compositor);
            var brush = _compositor.CreateSurfaceBrush(surface.CompositionSurface);
            brush.Stretch = _fit == VideoFit.Fill ? CompositionStretch.UniformToFill : CompositionStretch.Uniform;
            brush.HorizontalAlignmentRatio = 0.5f;
            brush.VerticalAlignmentRatio = 0.5f;

            var visual = _compositor.CreateSpriteVisual();
            visual.Brush = brush;
            visual.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtTop(visual);
            _visual = visual;   // 必须记录：SetFillMode/Detach 都依赖它（漏掉会导致“适应/铺满”静默失效）

            player.MediaOpened += (_, _) =>
            {
                MatchSurfaceToVideo(player, brush);
                Status?.Invoke($"画面已就绪：{player.PlaybackSession.NaturalVideoWidth}×{player.PlaybackSession.NaturalVideoHeight}");
            };
            try
            {
                // 手机可能中途改变分辨率，同样需要让表面跟随，否则又会留黑
                player.PlaybackSession.NaturalVideoSizeChanged += (_, _) => MatchSurfaceToVideo(player, brush);
            }
            catch { }
            player.MediaFailed += (_, e) =>
            {
                LastError = $"媒体失败：{e.ErrorMessage} (0x{e.ExtendedErrorCode?.HResult:X8})";
                Status?.Invoke(LastError);
            };
            player.PlaybackSession.PlaybackStateChanged += (_, _) =>
            {
                FramesRendered++;
                Status?.Invoke($"播放状态：{player.PlaybackSession.PlaybackState}");
            };

            player.Volume = _volume;
            player.IsMuted = _muted;
            player.Source = source;
            player.Play();
            _player = player;
            _surface = surface;
            _source = source;
            Status?.Invoke("已开始渲染手机画面");
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Status?.Invoke("渲染启动失败：" + LastError);
        }
    }

    /// <summary>
    /// 关键修复：把渲染表面尺寸改成视频的真实分辨率。
    /// 之前表面固定 1920×1080，手机竖屏画面被装进这个 16:9 表面时内部就先留了黑边，
    /// 于是“适应/铺满”只是在缩放这块带黑边的图，用户看不到任何变化。
    /// </summary>
    private void MatchSurfaceToVideo(MediaPlayer player, CompositionSurfaceBrush brush)
    {
        try
        {
            var session = player.PlaybackSession;
            int w = (int)session.NaturalVideoWidth, h = (int)session.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return;
            if (w == _surfaceWidth && h == _surfaceHeight) return;
            if (_compositor is null) return;

            player.SetSurfaceSize(new Windows.Foundation.Size(w, h));
            var newSurface = player.GetSurface(_compositor);
            brush.Surface = newSurface.CompositionSurface;

            var old = _surface;
            _surface = newSurface;
            _surfaceWidth = w;
            _surfaceHeight = h;
            try { old?.Dispose(); } catch { }
            Status?.Invoke($"渲染表面已匹配视频：{w}×{h}");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"调整渲染表面失败（继续使用原尺寸）：{ex.Message}");
        }
    }

    // ---------------- 音量（镜像音频由 MediaPlayer 输出，可直接调节） ----------------

    private double _volume = 80;
    private bool _muted;

    /// <summary>镜像音量 0-100。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            try { if (_player is not null) _player.Volume = _volume; } catch { }
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            try { if (_player is not null) _player.IsMuted = value; } catch { }
        }
    }

    /// <summary>画面适配方式。</summary>
    public enum VideoFit
    {
        /// <summary>适应：完整显示画面，四周按需留黑（不变形、不裁切）。</summary>
        Fit,
        /// <summary>铺满：等比放大到填满，超出部分裁切（会裁掉边缘）。</summary>
        Fill,
        /// <summary>拉伸：强行填满整块区域（不裁切，但可能变形）。</summary>
        Stretch,
        /// <summary>原始：1:1 像素，不缩放（超出即裁切）。</summary>
        Native,
    }

    private VideoFit _fit = VideoFit.Fit;
    private double _zoom = 1.0;

    public VideoFit Fit
    {
        get => _fit;
        set { _fit = value; _fill = value == VideoFit.Fill; ApplyVisualSettings(); }
    }

    /// <summary>额外缩放倍率（0.5 - 2.0），在适配方式之上生效，以画面中心为基准。</summary>
    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 0.5, 2.0); ApplyVisualSettings(); }
    }

    private void ApplyVisualSettings()
    {
        try
        {
            if (_visual?.Brush is CompositionSurfaceBrush brush)
            {
                brush.Stretch = _fit switch
                {
                    VideoFit.Fill => CompositionStretch.UniformToFill,
                    VideoFit.Stretch => CompositionStretch.Fill,
                    VideoFit.Native => CompositionStretch.None,
                    _ => CompositionStretch.Uniform,
                };
            }
            if (_visual is not null)
            {
                _visual.Scale = new Vector3((float)_zoom, (float)_zoom, 1f);
                UpdateCenterPoint();
            }
        }
        catch { }
    }

    /// <summary>缩放以画面中心为基准，因此要按当前像素尺寸更新中心点。</summary>
    private void UpdateCenterPoint()
    {
        try
        {
            if (_visual is null) return;
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (dpi <= 0) dpi = 1;
            _visual.CenterPoint = new Vector3((float)(ActualWidth * dpi / 2), (float)(ActualHeight * dpi / 2), 0f);
        }
        catch { }
    }

    /// <summary>兼容旧接口：true=铺满，false=适应。</summary>
    public void SetFillMode(bool fill) => Fit = fill ? VideoFit.Fill : VideoFit.Fit;

    /// <summary>诊断用：当前适配方式。</summary>
    public string FitDebug => _fit.ToString();

    /// <summary>诊断用：视觉层实际生效的缩放倍率。</summary>
    public double ZoomDebug => _visual?.Scale.X ?? 1.0;

    // ---------------------------------------------------------------- 共享画面（画中画 / 并排）
    //
    // 一块画刷可以挂到任意多个视觉上，所以"同一路画面显示在两处"是零额外解码的。
    // 注意：Stretch（适应/铺满）存在**画刷**上，源窗口改适配方式时小窗会一起跟着变——
    // 这是刻意的一致行为，不是缺陷。Zoom 存在视觉上，两边各自独立。

    /// <summary>当前画面的画刷；没有画面时为 null。同一合成器下的其它窗口可直接用它显示同一路画面。</summary>
    public CompositionSurfaceBrush? SurfaceBrush => _visual?.Brush as CompositionSurfaceBrush;

    /// <summary>是否正在显示别人的画面。</summary>
    public bool IsMirroring => _mirrorVisual is not null;

    /// <summary>正在共享谁的画面（没有则为 null）。</summary>
    public CompositionVideoHost? MirrorSource { get; private set; }

    /// <summary>把 <paramref name="source"/> 的画面也铺到本宿主里（不额外解码）。</summary>
    public bool MirrorFrom(CompositionVideoHost source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_compositor is null || _root is null)
        {
            LastError = "Composition 尚未就绪";
            return false;
        }

        CompositionSurfaceBrush? brush = source.SurfaceBrush;
        if (brush is null)
        {
            LastError = "源宿主还没有画面";
            return false;
        }

        StopMirroring();
        try
        {
            var visual = _compositor.CreateSpriteVisual();
            visual.Brush = brush;
            visual.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtTop(visual);
            _mirrorVisual = visual;
            MirrorSource = source;
            _surfaceWidth = source.SurfaceWidth;
            _surfaceHeight = source.SurfaceHeight;
            source.RegisterMirror(this);
            Status?.Invoke("已接入共享画面");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Status?.Invoke("接入共享画面失败：" + LastError);
            return false;
        }
    }

    /// <summary>停止显示别人的画面。对没有在共享的宿主调用是安全的。</summary>
    public void StopMirroring()
    {
        try { if (_mirrorVisual is not null && _root is not null) _root.Children.Remove(_mirrorVisual); } catch { }
        _mirrorVisual = null;
        MirrorSource = null;
    }

    private void RegisterMirror(CompositionVideoHost mirror)
    {
        lock (_mirrors)
        {
            _mirrors.Add(new WeakReference<CompositionVideoHost>(mirror));
        }
    }

    /// <summary>
    /// 源宿主画面失效时（重新接入 / 断开）通知所有镜像方放手，
    /// 否则它们会继续引用一块已经释放的表面。
    /// </summary>
    private void NotifyMirrorsToStop()
    {
        lock (_mirrors)
        {
            foreach (WeakReference<CompositionVideoHost> weak in _mirrors)
            {
                if (weak.TryGetTarget(out CompositionVideoHost? mirror))
                {
                    try { mirror.StopMirroring(); } catch { }
                }
            }
            _mirrors.Clear();
        }
    }

    /// <summary>
    /// 断开当前画面。
    /// <paramref name="disposeSource"/> = false 时**保留** <see cref="MediaSource"/> 不释放 ——
    /// 多设备并排需要在不同宿主之间搬动同一路画面（谁被放大就挂到主宿主上），
    /// 而 MediaSource 一旦释放就不能再用，所以搬动时必须留着。
    /// </summary>
    public void Detach(bool disposeSource = true)
    {
        // 画面要没了：镜像方必须一起放手，否则会引用已释放的表面
        NotifyMirrorsToStop();
        StopMirroring();
        try { if (_visual is not null && _root is not null) _root.Children.Remove(_visual); } catch { }
        _visual = null;
        try { _player?.Pause(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        try { _surface?.Dispose(); } catch { }
        _surface = null;
        if (disposeSource)
        {
            try { _source?.Dispose(); } catch { }
        }
        _source = null;
    }
}
