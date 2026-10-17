using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Bitmap = System.Drawing.Bitmap;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CastReceiver.App.Services;
using CastReceiver.Core.Android;
using CastReceiver.Core.Diagnostics;
using CastReceiver.Core.Devices;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Miracast;
using CastReceiver.Core.Security;
using Windows.Media.Core;

namespace CastReceiver.App;

/// <summary>
/// 主界面（纯手机镜像接收）。
/// 设计约定：中性深色、无渐变；主界面只留"画面 + 状态 + 两个动作"，设置/自检/日志在独立窗口。
/// 性能约定：UI 线程不做 WinRT/进程调用与大文本重排；探测在后台，定时器统一刷新。
/// </summary>
public partial class MainWindow : Window
{
    private readonly CastAppHost _host;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _mirrorPollTimer;
    private readonly DispatcherTimer _netPollTimer;
    private readonly DispatcherTimer _cursorTimer;

    private readonly StringBuilder _eventBuffer = new();
    private bool _eventDirty;

    private MiracastStatus _mirrorStatus = new(false, "Unknown", "Unknown", 0, "", null);
    private bool _closing;
    private bool _busyPrepare;
    private bool _suppressToggle;
    private bool _mirrorBusy;
    private string _busyText = "";
    private bool _fullscreen;
    private bool _fillMode;
    private Interop.CompositionVideoHost.VideoFit _fit = Interop.CompositionVideoHost.VideoFit.Fit;
    private double _zoom = 1.0;
    private bool _fitExplicit;      // 用户是否明确选择过画面适配
    private bool _zoomExplicit;     // 用户是否明确调过缩放
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;
    private bool _prevTopmost;
    private WindowState _prevState;
    private Rect _prevBounds;
    private string _lastSsid = "";
    private bool _masked;
    private string _lastTrayTip = "";
    private DateTime? _connectingSince;
    private OverlayBarWindow? _bar;
    private StatsOverlayWindow? _stats;
    private readonly MirrorStats _statsData = new();
    private DateTime _lastBarActivity = DateTime.UtcNow;
    private bool _tutorialOpen;
    /// <summary>未连接时右侧轮播的「上手引导」页（图标用矢量路径，不依赖字体字形）。</summary>
    private sealed record TipPage(string Title, string Body, string Icon);

    private static readonly TipPage[] TipPages =
    {
        new("先连同一个 Wi-Fi",
            "手机与电脑连同一个无线网络。推荐 5GHz，画面更跟手。",
            "M2.5,7 A10.5,10.5 0 0 1 17.5,7 M5.5,10.2 A6.5,6.5 0 0 1 14.5,10.2 M8.6,13.4 A2.2,2.2 0 0 1 11.4,13.4"),
        new("手机上点「无线投屏」",
            "下拉快捷面板 →「无线投屏 / 投射屏幕」→ 选中本机名称。",
            "M6.5,2.5 H13.5 A1.5,1.5 0 0 1 15,4 V16 A1.5,1.5 0 0 1 13.5,17.5 H6.5 A1.5,1.5 0 0 1 5,16 V4 A1.5,1.5 0 0 1 6.5,2.5 Z M8,5.2 H12 M9,15.2 H11"),
        new("电脑上确认一次",
            "首次连接电脑会弹确认框；可在设置里改成要求 PIN。",
            "M3.5,10.6 L7.6,14.6 L16.5,4.6"),
        new("随手留存画面",
            "截屏 / 录制会加密存进「我的库」，只有你的密码能看。",
            "M10,2.2 L16.2,4.6 V9.2 C16.2,13.2 13.4,16.2 10,17.8 C6.6,16.2 3.8,13.2 3.8,9.2 V4.6 Z"),
    };

    // ------------------------------------------------------------------ 顶栏「外观」按钮的图标

    /// <summary>深色主题显示的「月亮」（填充绘制）。外弧 r=8 走左半圆，内弧 r=8.9 收在 (7,10) 形成月牙。
    /// 内弧半径必须大于 8（两端点弦长 16 的一半）：否则按 SVG 规则半径会被等比放大到 8，
    /// 与外弧完全重合、图形面积归零，深色主题下这个按钮就"看不见"了。</summary>
    private const string MoonIconPath = "M12,2 A8,8 0 1 0 12,18 A8.9,8.9 0 0 1 12,2 Z";

    /// <summary>浅色主题显示的「太阳」（描边绘制：圆盘 + 8 道光芒）。</summary>
    private const string SunIconPath =
        "M10,6.2 A3.8,3.8 0 1 1 9.99,6.2 Z M10,1.4 V3.2 M10,16.8 V18.6 M1.4,10 H3.2 M16.8,10 H18.6 " +
        "M3.9,3.9 L5.2,5.2 M14.8,14.8 L16.1,16.1 M16.1,3.9 L14.8,5.2 M5.2,14.8 L3.9,16.1";

    private int _tipIndex;
    private DateTime _tipSince = DateTime.UtcNow;
    private DateTime? _sessionSince;
    private int _sessionShots;
    private int _sessionRecords;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private VaultService? _vault;
    private VaultCaptureService? _capture;
    private VaultWindow? _vaultWindow;
    private DispatcherTimer? _recordTimer;
    private bool _frameBusy;
    private bool _keepAwake;
    private string _lastLanKey = "";
    private readonly List<ToastWindow> _toasts = new();
    private DateTime _lastMouseMove = DateTime.UtcNow;
    private POINT _lastCursor;
    private bool _autoFullscreenDone;

    private readonly Stopwatch _tickWatch = Stopwatch.StartNew();
    private TimeSpan _lastTick;
    private bool _busyShown = true;

    /// <summary>窗口入场动效只播一次（重连/重绘不该重播）。</summary>
    private bool _entrancePlayed;

    /// <summary>右侧卡片当前显示的是"上手引导"还是"本次投屏"（用来做交叉淡入，而不是每秒重播）。</summary>
    private bool? _lastSessionUi;

    /// <summary>待机引导正在做"连接收尾"淡出；以及用于取消过期的淡出回调。</summary>
    private bool _guideExiting;
    private int _guideEpoch;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    public MainWindow()
    {
        InitializeComponent();
        _host = ((App)Application.Current).Host;

        // 加密库：归属由投屏设备决定（免密），查看内容必须解锁（密码）
        _vault = new VaultService(_host);
        _capture = new VaultCaptureService(_vault, _host.Settings);
        _vault.StateChanged += () => Dispatcher.BeginInvoke(RefreshUi, DispatcherPriority.Background);

        // 电脑控制手机（安卓 · adb）：会话 + 画面上的鼠标动作 + 系统键/文字/截屏
        // ⚠️ 这个功能**默认下线**（设置 → 安全 → 电脑控制手机，默认关闭，见 AdbEnabled）。
        //    会话对象照旧建出来（构造函数不启动任何进程，也没有线程），
        //    但所有会真正驱动 adb / 动界面的地方都由 AdbEnabled 堵住 ——
        //    用户在设置里打开即生效，不需要重新编译。
        _adb = new AdbWirelessSession(_host.Log);
        if (App.AdbDiscoveryFile is { Length: > 0 } discoveryFile)
        {
            // 自检用：发现结果从文件读（无人值守的验证里，手机是桩程序扮演的）
            _adb.DiscoveryOutputProvider = () =>
            {
                try { return Task.FromResult(File.ReadAllText(discoveryFile)); }
                catch { return Task.FromResult(""); }
            };
        }
        _adb.Changed += () => Dispatcher.BeginInvoke(RefreshAdbUi, DispatcherPriority.Background);
        _adb.EverPaired = _host.Settings.AdbPairedOnce;
        // 诊断报告里附上本机网络（手机与电脑在不在同一网段，是这类问题最要紧的一条）
        _adb.NetworkFixer = () =>
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        sb.AppendLine($"{nic.Name}（{nic.Description}）：{ua.Address}");
                    }
                }
                sb.AppendLine($"Wi-Fi：{_lastSsid}{(string.IsNullOrEmpty(_lastSsid) ? "" : "")}");
            }
            catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }
            return sb.ToString();
        };
        _adbMouseOn = AdbEnabled && _host.Settings.AdbMouseControl;
        ApplyAdbFeatureVisibility();

        // 画面上的鼠标动作（电脑控制手机）与"点格子放大"（④）都在**窗口级**接：
        // 画面是 HwndHost 的子窗口，它对命中测试是透过的，鼠标消息既不到控件的 WndProc，
        // 也不变成控件的 WPF 事件（实测，见 CompositionInterop 的说明），只有窗口级事件收得到。
        PreviewMouseLeftButtonDown += OnStagePointerDown;
        PreviewMouseLeftButtonUp += OnStagePointerUp;
        PreviewMouseWheel += OnStagePointerWheel;
        PreviewMouseMove += OnStageMouseMove;
        MouseLeave += (_, _) => { try { GuideModel.ResetPointer(); } catch { } };
        // 键盘直通：键也一样在窗口级接（只把"能翻译成手机输入"的键吃掉，其它键照旧给界面）
        PreviewKeyDown += OnStageKeyDown;

        RestoreWindowPlacement();       // 回到上次的位置/大小（离屏则居中）
        ClampInitialSizeToWorkArea();   // 首次运行没有历史位置时，别让 1120×720 在高缩放小屏上超出工作区

        LoadSettingsIntoUi();
        HookHostEvents();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        _mirrorPollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _mirrorPollTimer.Tick += (_, _) => _ = PollMirrorStatusAsync();
        _mirrorPollTimer.Start();

        _netPollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _netPollTimer.Tick += (_, _) => _ = PollNetworkAsync();
        _netPollTimer.Start();

        // 全屏时轮询鼠标位置：靠近底部才显示悬浮控制条，保持沉浸
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _cursorTimer.Tick += (_, _) => OnCursorTick();

        LocationChanged += (_, _) => { PositionStats(); PositionBar(); AlignIntroToThisWindow(); };
        SizeChanged += (_, _) => { PositionStats(); PositionBar(); RelayoutToasts(); SyncMultiView("窗口尺寸变化"); AlignIntroToThisWindow(); };
        StateChanged += (_, _) =>
        {
            bool minimized = WindowState == WindowState.Minimized;
            if (minimized)
            {
                // 最小化时把浮层与视频宿主都收起来，否则它们会继续显示在桌面上
                _stats?.Hide();
                _bar?.Hide();
                ApplyStageVisibility();

                // 小窗 A：主窗口收起来了，但正在镜像 —— 用一个独立小窗把画面留着。
                // 小窗与主窗口共用同一块画刷，所以主窗口的宿主隐藏不会让画面停。
                if (_host.Settings.MiniOnMinimize && _mini is null && IsMirroringActive)
                {
                    ShowMiniWindow(auto: true);
                }
            }
            else
            {
                PositionStats();
                RefreshUi();

                // 因为最小化而自动弹出的小窗，主窗口一恢复就收起；手动打开的保留
                if (_miniAuto && _mini is not null)
                {
                    _miniAuto = false;
                    HideMiniWindow();
                }
            }
            UpdateMaximizeGlyph();
            UpdateWindowedBar();
        };
        Activated += (_, _) => { UpdateWindowedBar(); HealLeftoverIntroWindow(); };
        Deactivated += (_, _) => UpdateWindowedBar();

        // 兜底巡检：万一有入场窗残留（透明却盖在最上层 → 界面点不动），每 4 秒清一次
        var healTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background,
            (_, _) => HealLeftoverIntroWindow(), Dispatcher);
        healTimer.Start();

        InitTrayAndHotkeys();
        BuildTipDots();
        ShowTipPage(0, animate: false);
        try { Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch; } catch { }
        SourceInitialized += (_, _) =>
        {
            try
            {
                _windowHook = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                _windowHook?.AddHook(OnWindowMessage);
            }
            catch { }
        };
        MiracastHost.EscapePressed += () => Dispatcher.BeginInvoke(() => SetFullscreen(false));
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;

        // 底栏显示真实版本号（版本唯一来源是 Directory.Build.props，别在这里写死）
        try
        {
            Version? version = typeof(MainWindow).Assembly.GetName().Version;
            TxtVersion.Text = version is null ? "v2.1.1" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
        catch { }
    }

    // ------------------------------------------------------------------ 初始化

    /// <summary>托盘图标与全局快捷键。</summary>
    private void InitTrayAndHotkeys()
    {
        _tray = new TrayIconService(this,
            showWindow: () => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }),
            toggleFullscreen: () => Dispatcher.BeginInvoke(() => SetFullscreen(!_fullscreen)),
            screenshot: () => Dispatcher.BeginInvoke(TakeScreenshot),
            settings: () => Dispatcher.BeginInvoke(() => BtnSettings_Click(this, new RoutedEventArgs())),
            exit: () => Dispatcher.BeginInvoke(() => { _exitRequested = true; Close(); }));
        _tray.RecordRequested += () => Dispatcher.BeginInvoke(ToggleRecording);
        _tray.OpenFolderRequested += () => Dispatcher.BeginInvoke(OpenVault);
        _tray.LockRequested += () => Dispatcher.BeginInvoke(LockVault);
        _tray.PrivacyMaskRequested += () => Dispatcher.BeginInvoke(TogglePrivacyMask);
        _tray.Show(_host.Settings.DeviceName);

        // 托盘菜单按当前状态显示勾选/禁用，所以把它需要知道的状态回读接口挂上
        _tray.CanCaptureState = CanCapture;
        _tray.MaskedState = () => _masked;
        _tray.RecordingState = () => _capture?.IsRecording == true;
        _tray.FullscreenState = () => _fullscreen;

        SourceInitialized += (_, _) =>
        {
            _hotkeys = new HotkeyService(this);
            if (_host.Settings.GlobalHotkeys)
            {
                _hotkeys.Register();
                // 界面完全就绪后再提示，避免在 SourceInitialized 阶段弹提示条
                Dispatcher.BeginInvoke(new Action(ReportHotkeyConflicts),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            _hotkeys.Pressed += id => Dispatcher.BeginInvoke(() =>
            {
                switch (id)
                {
                    case HotkeyService.IdFullscreen: SetFullscreen(!_fullscreen); break;
                    case HotkeyService.IdScreenshot: TakeScreenshot(); break;
                    case HotkeyService.IdRecord: ToggleRecording(); break;
                    case HotkeyService.IdPanic: PanicHide(); break;
                    case HotkeyService.IdLock: LockVault(); break;
                    case HotkeyService.IdPrivacy: TogglePrivacyMask(); break;
                }
            });
        };
    }

    private bool _exitRequested;

    private System.Windows.Interop.HwndSource? _windowHook;

    /// <summary>
    /// 收到「新实例要接管」的广播：优雅退出（忽略"最小化到托盘"），让新版接管。
    /// 对应需求：同时只能有一个投屏程序，新启动的替换掉旧的。
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 最大化：无边框窗口默认会铺满整块屏幕（连任务栏一起盖住）。
        // 这里把最大化尺寸限制到显示器"工作区"，让任务栏正常露出来；沉浸全屏是另一条路径（手动铺满），不受影响。
        if (msg == WmGetMinMaxInfo && !_fullscreen)
        {
            try
            {
                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    mmi.ptMinTrackSize.X = (int)MinWidth;
                    mmi.ptMinTrackSize.Y = (int)MinHeight;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            catch { }
        }

        if (App.TakeOverMessageId != 0 && msg == (int)App.TakeOverMessageId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _host.Log.Info("app", "收到新实例的接管请求，旧实例退出"); } catch { }
                _exitRequested = true;
                Close();
            }));
        }
        return IntPtr.Zero;
    }

    /// <summary>截取当前镜像画面（屏幕抓取，GPU 合成的画面只能这样拿）→ 加密写入库，不留明文文件。</summary>
    public void TakeScreenshot()
    {
        if (!CanCapture()) { ShowToast("暂时不能截屏", "手机镜像开始后才能截取画面。", null, null, ok: false); return; }
        if (!EnsureCaptureAccount()) return;
        try
        {
            bool printWindow = _host.Settings.AntiCapture && _captureViaPrintWindow;
            if (!printWindow && !IsStageForeground())
            {
                ShowToast("窗口不在最前", "请先切回投屏窗口再截屏，否则会把别的窗口拍进去。", null, null, ok: false);
                return;
            }

            // 走屏幕抓取时：反截屏保护会挡住自己，所以瞬时解除、抓完立刻恢复
            bool paused = false;
            if (_host.Settings.AntiCapture && !printWindow)
            {
                WindowProtection.SetProtection(this, false);
                paused = true;
            }

            Bitmap? bmp;
            try { bmp = GrabStageFrame(GetStageScreenRect()); }
            finally
            {
                if (paused) WindowProtection.SetProtection(this, true);
            }

            using var _ = bmp;
            if (bmp is null) { _host.Publish("截屏失败"); return; }

            var (ok, error, item) = _capture!.SaveScreenshot(bmp);
            if (!ok || item is null)
            {
                _host.Publish("截屏保存失败");
                ShowToast("截屏失败", error ?? "写入加密库失败。", null, null, ok: false);
                return;
            }
            _host.Publish("已加密保存一条截图");
            _sessionShots++;
            _tray?.Balloon("已保存截图（加密）", "内容已加密存入「我的库」，只能在本程序内查看。");
            ShowToast("已保存截图", "已加密存入「我的库」，需要用密码解锁后才能查看。", "打开我的库", OpenVault);
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"截屏异常：{ex.Message}"); }
    }

    // ------------------------------------------------------------------ 反截屏与取帧路径

    private bool _captureViaPrintWindow;
    private bool _protectionPausedForRecording;
    private DateTime _statsIssueSince = DateTime.MinValue;

    /// <summary>把「防其他程序截屏」的保护套用到本程序的所有窗口。</summary>
    public void ApplyAntiCapture()
    {
        bool on = _host.Settings.AntiCapture;
        try
        {
            WindowProtection.SetProtection(this, on);
            if (_bar is not null) WindowProtection.SetProtection(_bar, on);
            if (_stats is not null) WindowProtection.SetProtection(_stats, on);
            if (_vaultWindow is not null) WindowProtection.SetProtection(_vaultWindow, on);
        }
        catch { }
    }

    /// <summary>投屏开始后实测一次取帧路径：决定录制能不能在「保护一直开着」的前提下进行。</summary>
    private async void ProbeAntiCaptureAsync()
    {
        if (!_host.Settings.AntiCapture) return;
        await Task.Delay(1500);
        try
        {
            var result = AntiCaptureProbe.Run(this, CaptureHost.ChildHandle, GetStageScreenRect(),
                () => MiracastHost.ProbeVisualCaptureSupport());
            _captureViaPrintWindow = result.CanCaptureWhileProtected;
            foreach (var line in result.Report.Split('\n'))
                if (line.Trim().Length > 0) _host.Log.Info("privacy", line.Trim());
        }
        catch (Exception ex) { _host.Log.Debug("privacy", $"反截屏自检异常：{ex.Message}"); }
    }

    /// <summary>供设置页手动运行反截屏自检，返回可读报告。</summary>
    public string RunAntiCaptureCheck()
    {
        var result = AntiCaptureProbe.Run(this, CaptureHost.ChildHandle, GetStageScreenRect(),
            () => MiracastHost.ProbeVisualCaptureSupport());
        _captureViaPrintWindow = result.CanCaptureWhileProtected;
        return result.Report;
    }

    /// <summary>
    /// 取一帧镜像画面：优先「向自己的窗口要帧」（保护不降级），否则回退到屏幕抓取。
    /// 注意：rect 必须由界面线程算好传进来 —— 这个方法会在后台线程被调用，
    /// 任何对 WPF 控件的访问（PointToScreen / ActualWidth）都会抛异常并导致录不到画面。
    /// </summary>
    private Bitmap? GrabStageFrame((int X, int Y, int W, int H) rect)
    {
        if (_host.Settings.AntiCapture && _captureViaPrintWindow && CaptureHost.ChildHandle != IntPtr.Zero)
        {
            var shot = WindowProtection.GrabWindow(CaptureHost.ChildHandle);
            if (shot is not null) return shot;
        }
        if (rect.W <= 0 || rect.H <= 0) return null;
        return CaptureService.Grab(rect.X, rect.Y, rect.W, rect.H);
    }

    /// <summary>是否处于可截取状态（正在镜像，或演示模式）。</summary>
    private bool CanCapture() => _demoCast || _mirrorStatus.ConnectionCount > 0;

    /// <summary>演示模式（--demo-cast）：用本地样例视频冒充镜像，便于无手机自检录制链路。</summary>
    private bool _demoCast;

    private void StartDemoCast()
    {
        try
        {
            string asset = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp4");
            if (!File.Exists(asset))
            {
                _host.Log.Warn("demo", "演示模式缺少样例视频：" + asset);
                return;
            }
            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(asset));
            MiracastHost.AttachSource(source, 1280, 720, realTime: false);
            MiracastHost.SetFillMode(_fillMode);
            _demoCast = true;
            _host.Publish("演示模式：正在用本地样例视频代替手机镜像（用于自检截屏/录制）");
            RefreshUi();
            // 演示模式等价于"手机已经投上来了"：自检要验的就是"投上来的那一刻自动连手机控制"这条链路
            _ = AutoStartPhoneControlAsync();
        }
        catch (Exception ex) { _host.Log.Warn("demo", "演示模式启动失败：" + ex.Message); }
    }

    private async Task StartDemoCastAfterAsync(int delayMs)
    {
        await Task.Delay(Math.Clamp(delayMs, 100, 60_000));
        if (_closing) return;
        StartDemoCast();
    }

    /// <summary>抓屏前确认投屏窗口在最前（避免把压在上面的别的窗口拍进去）。</summary>
    private bool IsStageForeground()
    {
        if (_demoCast) return true;      // 演示模式：窗口可能不是前台，仍允许抓取
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return true;
            IntPtr self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (foreground == self) return true;
            GetWindowThreadProcessId(foreground, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return true; }
    }

    /// <summary>
    /// 截屏/录制前确认「这条内容归谁」：没有账号就先创建，设备没绑定就问一次。
    /// 注意：这里要的不是密码 —— 归属只需要公钥，所以录制全程依然免密。
    /// </summary>
    private bool EnsureCaptureAccount()
    {
        if (_vault is null) return false;
        if (_vault.LoadError is not null)
        {
            ShowToast("加密库不可用", _vault.LoadError, null, null, ok: false);
            return false;
        }

        if (!_vault.HasAccount)
        {
            var setup = new VaultSetupWindow(_vault,
                "开始记录前先创建一个账号：截图与录像都会加密保存，只有这个账号的密码能查看。") { Owner = this };
            if (setup.ShowDialog() != true) return false;
        }

        if (_vault.ActiveAccount is not null) return true;

        // 演示模式（自检用）：没有真实投屏设备，直接把已有账号当作归属，避免弹绑定对话框
        if (_demoCast)
        {
            var demoAccount = _vault.Accounts.FirstOrDefault();
            if (demoAccount is not null) { _vault.ForceActiveAccount(demoAccount); return true; }
        }

        // 当前设备还没有归属：问一次
        var bind = new VaultBindWindow(_vault, _currentTransmitterName, _currentTransmitterMac, _currentFingerprintHash,
            _currentDeviceSuspected) { Owner = this };
        if (bind.ShowDialog() == true && _vault.ActiveAccount is not null)
        {
            RefreshUi();
            return true;
        }

        ShowToast("还没有归属账号", "这台投屏设备没有绑定账号，暂时无法保存。点这里重新绑定。", "绑定设备", () =>
        {
            var again = new VaultBindWindow(_vault, _currentTransmitterName, _currentTransmitterMac, _currentFingerprintHash,
                _currentDeviceSuspected) { Owner = this };
            again.ShowDialog();
            RefreshUi();
        }, ok: false);
        return false;
    }

    private string _currentTransmitterName = "";
    private string _currentTransmitterMac = "";
    private string _currentFingerprintHash = "";
    private bool _currentDeviceSuspected;

    /// <summary>右下角提示条：带"打开文件夹"这类可直接操作的入口。</summary>
    public void ShowToast(string title, string body, string? actionText = null, Action? action = null, bool ok = true)
    {
        try
        {
            // 最多同时留 3 条：提示条会向上排队，堆多了就会一路盖到画面中间
            while (_toasts.Count >= 3)
            {
                ToastWindow oldest = _toasts[0];
                _toasts.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }

            var toast = new ToastWindow(this, title, body, actionText, action, ok);
            toast.Closed += (_, _) => { _toasts.Remove(toast); RelayoutToasts(); };
            _toasts.Add(toast);
            toast.Show();
            RelayoutToasts(slideInLast: true);
        }
        catch { }
    }

    /// <summary>
    /// 把提示条沿右下角向上排队摆放（新的一条在最下方，把旧的往上顶），
    /// 而不是全部叠在同一个位置。
    ///
    /// 整摞的总高度**封顶**（最多占画面区高度的 40%，且不超过 320px）：
    /// 超出的旧提示条直接收掉 —— 宁可少显示一条，也不要把画面盖住。
    /// </summary>
    private void RelayoutToasts(bool slideInLast = false)
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            // 「电脑控制手机」控制条在底部时，提示条要往上让开（不然会盖住"发送/手机截屏"）
            double barLift = _phoneBarShown && PhoneBar.ActualHeight > 0 ? (PhoneBar.ActualHeight + 8) * dpi : 0;
            double right = topLeft.X + ActualWidth * dpi - 24;
            double bottom = topLeft.Y + ActualHeight * dpi - 24 - barLift;
            double maxStack = Math.Min(320, ActualHeight * dpi * 0.4);

            double offset = 0;
            for (int i = _toasts.Count - 1; i >= 0; i--)   // 从最旧（最上面）开始摆
            {
                var toast = _toasts[i];
                if (!toast.IsVisible) continue;
                double h = toast.ActualHeight > 0 ? toast.ActualHeight : 96;
                double top = bottom - offset - h;
                toast.Left = right - toast.ActualWidth;
                if (ReferenceEquals(toast, _toasts[^1]) && slideInLast)
                {
                    toast.Top = top;
                    toast.SlideIn();
                }
                else toast.MoveTo(top);
                offset += h + 8;
                if (offset > maxStack)
                {
                    break;      // 再往上就盖到画面里了，剩下的这一轮先不摆
                }
            }
        }
        catch { }
    }
    /// <summary>加密库服务（设置窗口需要读状态/解锁/锁定）。</summary>
    public VaultService? Vault => _vault;

    /// <summary>打开「我的库」：需要密码解锁；关闭库窗口会自动上锁。</summary>
    public void OpenVault()
    {
        try
        {
            _host.Log.Info("vault", $"打开我的库：账号数={_vault?.Accounts.Count ?? -1}，已解锁={_vault?.IsUnlocked}，库={VaultPaths.Root}");
            if (_vault is null) return;
            if (_vault.LoadError is not null)
            {
                ShowToast("加密库不可用", _vault.LoadError, null, null, ok: false);
                return;
            }
            if (!_vault.HasAccount)
            {
                _host.Log.Info("vault", "尚未创建账号，弹出创建账号窗口");
                var setup = new VaultSetupWindow(_vault,
                    "先创建一个账号：截图与录像会加密保存，只有这个账号的密码能查看。") { Owner = this };
                if (setup.ShowDialog() != true) { _host.Log.Info("vault", "用户取消了创建账号"); return; }
            }

            if (!_vault.IsUnlocked)
            {
                _host.Log.Info("vault", "弹出解锁窗口");
                string? hint = _vault.ActiveBinding is not null && _vault.ActiveAccount is not null
                    ? $"当前投屏设备已绑定到「{_vault.ActiveAccount.DisplayName}」。"
                    : null;
                var unlock = new VaultUnlockWindow(_vault, deviceHint: hint) { Owner = this };
                if (unlock.ShowDialog() != true) { _host.Log.Info("vault", "用户取消了解锁"); return; }
            }

            if (_vaultWindow is { IsVisible: true })
            {
                _vaultWindow.Activate();
                return;
            }

            _vaultWindow = new VaultWindow(_vault) { Owner = this };
            _vaultWindow.Closed += (_, _) => { _vaultWindow = null; RefreshUi(); };
            _vaultWindow.ShowDialog();     // 模态：关闭即上锁
            RefreshUi();
        }
        catch (Exception ex)
        {
            _host.Log.Warn("vault", $"打开我的库失败：{ex.Message}");
            ShowToast("打开我的库失败", ex.Message, null, null, ok: false);
        }
    }

    /// <summary>立即上锁：清空内存中的密钥，并关闭已打开的库窗口。</summary>
    public void LockVault()    {
        try
        {
            if (_vault is null) return;
            bool wasUnlocked = _vault.IsUnlocked;
            _vault.Lock();
            if (_vaultWindow is { IsVisible: true })
            {
                try { _vaultWindow.Close(); } catch { }
                _vaultWindow = null;
            }
            RefreshUi();
            if (wasUnlocked) ShowToast("已锁定", "加密库已上锁，查看内容需要重新输入密码。");
        }
        catch { }
    }

    /// <summary>打开旧版本的明文保存目录（仅用于迁移/清理）。</summary>
    public void OpenLegacyFolder()
    {
        try
        {
            string dir = CaptureService.OutputDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"打开目录失败：{ex.Message}"); }
    }

    /// <summary>
    /// 截屏/录制的取帧区域：**主画面那一块**（多设备并排时只截聚焦那一路，不是整个画面区）。
    /// 拿不到宿主尺寸时退回整块画面区。
    /// </summary>
    private (int X, int Y, int W, int H) GetStageScreenRect()
    {
        try
        {
            Interop.CompositionVideoHost host = PrimaryHost;
            if (host.IsReady && host.ActualWidth > 1 && host.ActualHeight > 1)
            {
                Point topLeft = host.PointToScreen(new Point(0, 0));
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return ((int)topLeft.X, (int)topLeft.Y,
                        (int)(host.ActualWidth * dpi), (int)(host.ActualHeight * dpi));
            }
        }
        catch { }

        var fallback = StageCard.PointToScreen(new Point(0, 0));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return ((int)fallback.X, (int)fallback.Y,
                (int)(StageCard.ActualWidth * scale), (int)(StageCard.ActualHeight * scale));
    }

    /// <summary>
    /// 开始/停止录屏：帧在内存里编码后立刻加密写库 —— 磁盘上从头到尾没有明文帧，
    /// 也不再产生 %TEMP% 明文目录与明文 MP4（旧实现的两个隐私缺口）。
    /// </summary>
    public void ToggleRecording()
    {
        if (_capture is null) return;

        if (!_capture.IsRecording)
        {
            if (!CanCapture()) { ShowToast("暂时不能录制", "手机镜像开始后才能录制画面。", null, null, ok: false); return; }
            if (!EnsureCaptureAccount()) return;

            var rect = GetStageScreenRect();
            var (ok, error) = _capture.StartRecording(rect.W, rect.H);
            if (!ok) { ShowToast("无法开始录制", error ?? "未知原因", null, null, ok: false); return; }

            // 反截屏与「自己录制」的取舍：PrintWindow 可用就全程保护；否则按设置降级
            if (_host.Settings.AntiCapture && !_captureViaPrintWindow)
            {
                if (string.Equals(_host.Settings.AntiCaptureFallback, "BlockRecording", StringComparison.OrdinalIgnoreCase))
                {
                    _capture.AbortRecording();
                    ShowToast("已按设置拒绝录制",
                        "当前设置是「保护优先」：反截屏保护开启时不允许录制。可在 设置 → 安全 里改为「录制期间暂停保护」。",
                        null, null, ok: false);
                    return;
                }
                WindowProtection.ApplyToAll(false);
                _protectionPausedForRecording = true;
                ShowToast("录制期间保护已暂停", "本机 PrintWindow 取不到画面，为保证录制成功，防截屏保护在录制期间暂时关闭。");
            }

            int interval = Math.Max(30, 1000 / Math.Max(1, _capture.Fps));
            _recordTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(interval) };
            _recordTimer.Tick += (_, _) => OnRecordTick();
            _recordTimer.Start();
            _bar?.SetRecording(true);
            _host.Publish("开始录制（加密写入）…再次点击停止并保存");
            ShowToast("正在录制（加密）", "再次点击录制按钮即可停止；内容加密保存在「我的库」。");
            RefreshUi();
            return;
        }

        StopRecording();
    }

    /// <summary>采集一帧：抓屏 + 编码 + 加密都在后台线程，界面线程只负责算一次区域。</summary>
    private void OnRecordTick()
    {
        if (_capture is null || !_capture.IsRecording || _frameBusy) return;
        if (_capture.ReachedLimit) { StopRecording(autoStopped: true); return; }

        var rect = GetStageScreenRect();      // 必须在界面线程算：后台线程不能碰 WPF 控件
        if (rect.W <= 0 || rect.H <= 0) return;
        _frameBusy = true;
        Task.Run(() =>
        {
            try
            {
                using var bmp = GrabStageFrame(rect);
                if (bmp is not null) _capture.AppendFrame(bmp);
                else _capture.NoteFrameFailure("抓屏返回空");
            }
            catch (Exception ex) { _capture.NoteFrameFailure(ex.Message); }
            finally { _frameBusy = false; }
        });
    }

    private void StopRecording(bool autoStopped = false)
    {
        _recordTimer?.Stop();
        _recordTimer = null;
        _bar?.SetRecording(false);
        if (_protectionPausedForRecording)
        {
            WindowProtection.ApplyToAll(true);
            _protectionPausedForRecording = false;
        }
        if (_capture is null) return;

        var (ok, error, item) = _capture.StopRecording();
        if (!ok || item is null)
        {
            _host.Publish("录制未保存");
            ShowToast("录制未保存", error ?? "没有采集到画面。", null, null, ok: false);
            RefreshUi();
            return;
        }

        string size = item.Bytes >= 1024 * 1024 ? $"{item.Bytes / 1024.0 / 1024.0:F1} MB" : $"{item.Bytes / 1024} KB";
        _sessionRecords++;
        _host.Publish($"已加密保存一条录像（{item.FrameCount} 帧）");
        _tray?.Balloon("录制已保存（加密）", "内容已加密存入「我的库」。");
        ShowToast(autoStopped ? "已到时长上限，录制已保存" : "已保存录像（加密）",
            $"{item.FrameCount} 帧 · {size} · 已加密存入「我的库」", "打开我的库", OpenVault);
        RefreshUi();
    }

    private void LoadSettingsIntoUi()
    {
        var s = _host.Settings;
        _fit = ParseFit(s.MirrorFit);
        _fillMode = _fit == Interop.CompositionVideoHost.VideoFit.Fill;
        _zoom = Math.Clamp(s.MirrorZoom, 0.5, 2.0);
        BtnLatency.Content = s.LowLatency ? "延迟优先" : "画质优先";
        // 顶栏的本机名与手机投屏列表里看到的名字保持同一个来源
        string deviceName = string.IsNullOrWhiteSpace(s.DeviceName) ? "本机" : s.DeviceName.Trim();
        TxtHeaderDevice.Text = $"本机 · {deviceName}";
        UpdateStageChrome();
        ApplyVolumeToHosts();
        // 底栏只在窗口模式可见（全屏时 StatusBarRow 被折叠），所以这里提示 F11 进入全屏即可；
        // 原先写的「Esc 退出全屏」在窗口模式下按了没有反应，属于误导。
        TxtHint.Text = "F11 进入全屏 · Ctrl+Alt+P 遮挡";
    }

    private void HookHostEvents()
    {
        _host.StateChanged += () => Dispatcher.BeginInvoke(RefreshUi, DispatcherPriority.Background);
        _host.Notice += message => Dispatcher.BeginInvoke(() =>
        {
            TxtStatus.Text = message;
            AppendEvent(message);
        }, DispatcherPriority.Background);

        _host.Miracast.MediaSourceCreated += (_, source) => Dispatcher.BeginInvoke(() =>
        {
            MiracastHost.AttachSource(source);
            MiracastHost.SetFillMode(_fillMode);
            MiracastHost.Volume = _host.Settings.Volume;
            _activeProtocol = "无线投屏";
            SyncMultiView("Miracast 出画");   // Miracast 有画面时它就是主画面，AirPlay 各路的格子要让位
            ApplyVolumeToHosts();
            AttachMiniPicture();         // 小窗开着就重新接上：换源后旧画刷已释放
            ApplyAudioSwitchPreference();
            ResetMetrics();
            _hadCast = true;            // 真的投过屏了（断开时才允许退全屏）
            AppendEvent("已开始镜像");
            ApplyAntiCapture();          // 画面出现后再确认一次保护（新窗口也要覆盖）
            ProbeAntiCaptureAsync();     // 实测取帧路径，决定录制是否需要降级
            if (_host.Settings.AutoFullscreen && !_fullscreen && !_autoFullscreenDone)
            {
                _autoFullscreenDone = true;
                SetFullscreen(true);
            }
            RefreshUi();
        }, DispatcherPriority.Background);

        _host.Miracast.Disconnected += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            MiracastHost.Detach();
            SyncMultiView("Miracast 断开");   // 主画面可能落回 AirPlay 某一路
            AttachMiniPicture();          // 有别的画面就接着看，一路都没了才收起小窗
            _activeIdentity = null;
            RestoreAudioSwitch();
            AppendEvent("手机已断开");
            _vault?.ClearActiveDevice();
            _sessionSince = null;
            _currentTransmitterName = "";
            _currentTransmitterMac = "";
            _currentFingerprintHash = "";
            _currentDeviceSuspected = false;
            if (_host.Settings.NotifyOnConnect)
            {
                try { System.Media.SystemSounds.Hand.Play(); } catch { }
                _tray?.Balloon("手机已断开", "镜像已结束，仍在等待下一台设备");
            }
            _tray?.UpdateTooltip($"投屏Window · {_host.Settings.DeviceName}");
            // 只有"真的投过屏"才退全屏：启动阶段会收到空断开事件，
            // 那会把首次启动刚进的全屏踢掉（动画收场后窗口缩回 1120×720 就是这个原因）
            if (_fullscreen && _hadCast) SetFullscreen(false);
            RefreshUi();
        }, DispatcherPriority.Background);

        // ---- AirPlay（iPhone/iPad）走同一套渲染链路，只是每一路有自己的宿主与 MediaSource ----
        // 截屏/录制/反截屏/画质档位都建立在"当前主画面宿主"上（见 PrimaryHost），这里不需要另做一套。
        // 会话的增删与聚焦变化统一走 SessionsChanged（它覆盖了"某一路开始"这一情形）。
        _host.AirPlay.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(OnAirPlaySessionsChanged, DispatcherPriority.Background);

        _host.AirPlay.Disconnected += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // 走到这里表示**最后一路**也结束了；中途断一路由 SessionsChanged 处理（另一路还在跑）
            OnAirPlaySessionsChanged();
            SyncMultiView("AirPlay 全部结束");
            AttachMiniPicture();          // 主画面可能落到 Miracast 上（或没画面了 → 小窗收起）
            RestoreAudioSwitch();
            AppendEvent("AirPlay 已断开");
            if (!AnyMirroring) EndSession();
            if (_host.Settings.NotifyOnConnect)
            {
                try { System.Media.SystemSounds.Hand.Play(); } catch { }
                _tray?.Balloon("手机已断开", "AirPlay 镜像已结束");
            }
            if (_fullscreen && !AnyMirroring && _hadCast) SetFullscreen(false);
            RefreshUi();
        }, DispatcherPriority.Background);

        _host.AirPlay.DeviceIdentified += identity => Dispatcher.BeginInvoke(() =>
        {
            _activeProtocol = "AirPlay";
            SetActiveIdentity(identity);
        }, DispatcherPriority.Background);

        _host.Miracast.ConnectionCreated += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            _currentTransmitterName = e.TransmitterName;
            _currentTransmitterMac = e.MacAddress;
            // 安卓侧 Windows 只给名字和 MAC，没有型号/系统版本：能说什么说什么，不猜
            SetActiveIdentity(DeviceModelCatalog.FromMiracast(e.TransmitterName, e.MacAddress));
            _currentDeviceSuspected = false;
            var resolution = _vault?.ResolveDevice(e.TransmitterName, e.MacAddress);
            _currentFingerprintHash = resolution?.FingerprintHash ?? "";
            _currentDeviceSuspected = resolution?.SuspectedSameName ?? false;

            // 隐私优先：默认不把手机名字写进界面/动态/日志
            bool showName = _host.Settings.ShowTransmitterName;
            AppendEvent(showName ? $"手机已连接（{e.TransmitterName}）" : "手机已连接");
            _autoFullscreenDone = false;
            // 电脑控制手机（安卓）：手机一投上来就把控制准备好 —— 用户不需要点任何东西
            _ = AutoStartPhoneControlAsync();

            if (_host.Settings.NotifyOnConnect)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                _tray?.Balloon("手机已连接", showName
                    ? $"{e.TransmitterName} 正在投屏到本机"
                    : "设备正在投屏到本机");
            }
            _tray?.UpdateTooltip($"投屏Window · 镜像中");

            if (resolution?.Account is not null)
            {
                ShowToast("已识别投屏设备", $"截屏与录制将归入「{resolution.Account.DisplayName}」。", "打开我的库", OpenVault);
            }
            else if (_vault?.HasAccount == true)
            {
                ShowToast("新设备", "这台设备还没有归属账号，截屏/录制前会问你一次。", "现在绑定", () =>
                {
                    var bind = new VaultBindWindow(_vault, e.TransmitterName, e.MacAddress, _currentFingerprintHash,
                        _currentDeviceSuspected) { Owner = this };
                    bind.ShowDialog();
                    RefreshUi();
                });
            }
            RefreshUi();
        }, DispatcherPriority.Background);

        _host.Miracast.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            _mirrorStatus = status;
            // 这一轮投屏结束了（没有任何连接）：把"用户手动关掉控制"的记忆清掉，
            // 下次手机再投上来才重新自动准备（否则关过一次之后就再也不自动了）。
            if (status.ConnectionCount == 0) _phoneControlRefusedByUser = false;
            RefreshUi();
        }, DispatcherPriority.Background);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowBusy("正在初始化…");

        // 入场动画先起播（下面紧接着要进全屏；顺序反了的话，进全屏时会把控制条显示出来盖在动画上）
        StartIntroIfEnabled();

        Interop.CoreMessaging.EnsureDispatcherQueue();
        ApplyMica(_host.Settings.UseMica);
        ApplyCaptureFolder();
        ApplyDensity(_host.Settings.Density);
        ApplyAntiCapture();          // 反截屏：其他程序截本窗口会得到黑屏
        UpdateAppearanceIcon();      // 顶栏图标跟着当前主题（深色=月亮 / 浅色=太阳）
        App.ThemeChanged += UpdateAppearanceIcon;
        _host.Miracast.QualityPreset = _host.Settings.QualityPreset;
        RefreshUi();

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);

        TxtBusyText.Text = "正在检测网络…";
        await PollNetworkAsync();

        if (_host.Settings.AutoStartMiracast && !_host.Miracast.IsListening)
        {
            TxtBusyText.Text = "正在开启镜像接收…";
            await StartMirrorAsync(showError: false);
        }
        else if (_host.Settings.AutoStartAirPlay && !_host.AirPlay.IsRunning)
        {
            // 用户只开了 AirPlay（关掉 Miracast 自动启动）时也要广播
            _host.StartAirPlay();
        }

        TxtBusyText.Text = "准备就绪";
        RefreshUi();
        await Task.Delay(160);
        HideBusy();
        PlayEntrance();            // 遮罩淡出的同时，界面内容错峰入场
        ShowBar();                 // 控制条在窗口模式下也常驻（浮在画面底部）
        PositionBar();
        FinishIntroWhenReady();    // 程序准备好了：动画放完就收场（没放完就等它）
        await PollMirrorStatusAsync();
        if (App.DemoCast)
        {
            // 诊断用：CAST_DEMO_AFTER_MS=5000 可把演示投屏推迟 5 秒，
            // 方便在"已经稳定显示待机页"之后再观察连接动效（不影响正常启动）。
            int delay = int.TryParse(Environment.GetEnvironmentVariable("CAST_DEMO_AFTER_MS"), out int ms) ? ms : 0;
            if (delay > 0) _ = StartDemoCastAfterAsync(delay);
            else StartDemoCast();     // 演示模式：无手机也能验证截屏/录制
        }
        RefreshAdbUi();
        // 「电脑控制手机」默认下线：启动时也不去碰 adb（不连、不预热）
        if (AdbEnabled)
        {
            if (_adbMouseOn) _ = TryAutoConnectAdbAsync();   // 开关是开的（含上次自动打开的）就直接连一次
            // 预热 adb server：省掉"第一次调用 adb"那几百毫秒到一秒多的启动等待
            if (_host.Settings.AdbAutoConnect) _ = _adb.WarmUpAsync();
        }

        // 首次启动的使用引导：别和入场动画抢画面，等动画收场后再放
        if (_introActive) _pendingTutorial = true;
        else MaybeShowTutorial();
    }

    /// <summary>入场动画还在放，先把首次引导压后。</summary>
    private bool _pendingTutorial;

    // ------------------------------------------------------------------ 启动入场动画（主窗口上的一层遮罩）

    /// <summary>节目已经开演（MediaOpened 与兜底定时器都可能来叫，只做一次）。</summary>
    private bool _introAnimationsStarted;

    /// <summary>兜底起播定时器（不指望 MediaOpened 一定触发）。</summary>
    private DispatcherTimer? _introKick;

    /// <summary>本次运行里真的投过屏（启动期的空断开事件不该退全屏）。</summary>
    private bool _hadCast;

    /// <summary>动画期间被压后的控制条（动画一结束就补显示）。</summary>
    private bool _barPendingIntro;

    /// <summary>入场遮罩是不是正在放（**回退路径**：原生窗口没起来时才用它）。</summary>
    private bool _introActive;

    /// <summary>原生入场窗口正在放（正常路径：在 WPF 起来之前就开始播了）。</summary>
    private bool _introNative;

    /// <summary>动画已经放完（程序可能还在准备）。</summary>
    private bool _introDone;

    /// <summary>正在收场（避免重复触发）。</summary>
    private bool _introLeaving;

    /// <summary>设置里点了"预览"：这块画面由预览流程负责，界面交接定时器不许插手。</summary>
    private bool _introPreviewActive;

    /// <summary>启动交接定时器（一次性：把界面从入场动画手里接回来）。</summary>
    private DispatcherTimer? _introHandoff;

    /// <summary>动画放完了但程序还没准备好：先停住等。</summary>
    private bool _introWaiting;

    /// <summary>程序已经准备好可以让位了（OnLoaded 走到"准备就绪"时为真）。</summary>
    private bool ReadyForIntroExit { get; set; }

    /// <summary>
    /// 起播入场动画：**盖在主窗口自己身上**的一层遮罩，不是另开一个窗口 ——
    /// 用户要的是"原来的主界面上加一层遮罩"的感觉。
    /// 失败一律静默跳过：宁可没有动画，也不能让程序起不来。
    /// </summary>
    /// <summary>
    /// 把原生入场窗对齐到本窗口的**真实外框**（含标题栏那一圈）。
    /// 入场窗最初用的是"设置里记的窗口矩形"，和实际显示可能不一致（最大化、DPI 等），
    /// 用户实测会看到主窗口标题栏露在入场画面上面。
    /// </summary>
    private void AlignIntroToThisWindow()
    {
        if (!_introNative) return;
        try
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            if (!GetWindowRect(handle, out RECT rect)) return;
            NativeIntroWindow.AlignTo(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>预览入场动画前调用：把主窗口变隐形、控制条收起来，看起来就跟"刚启动"一样。</summary>
    public void PrepareIntroPreview()
    {
        try
        {
            // 启动那一次的"界面交接"定时器必须取消：它原本每 800ms 就跑一次，
            // 会把预览刚设好的 _introNative 又抹成 false、还把控制条显示出来，
            // 于是界面自愈巡检以为"界面已恢复"，每 4 秒去强制销毁一次预览窗（打成一团）。
            _introHandoff?.Stop();
            _introHandoff = null;

            _introPreviewActive = true;
            _introNative = true;
            Opacity = 0;
            try { _bar?.Hide(); } catch { }
            ApplyStageVisibility();

            // 保险：万一入场窗没能正常收场，界面也绝不能一直隐形
            // （否则用户看到的是"输入框点不动、窗口也关不掉" —— 实测踩过）
            _previewGuard?.Stop();
            _previewGuard = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Normal, (_, _) =>
            {
                _previewGuard?.Stop();
                _previewGuard = null;
                try { _host.Log.Warn("startup", "预览超时未收场，强制恢复界面"); } catch { }
                NativeIntroWindow.ForceClose();
                FinishIntroPreview();
            }, Dispatcher);
            _previewGuard.Start();
        }
        catch { }
    }

    private DispatcherTimer? _previewGuard;

    /// <summary>预览结束调用：把主窗口淡回来、恢复控制条。</summary>
    public void FinishIntroPreview()
    {
        try
        {
            _previewGuard?.Stop();
            _previewGuard = null;
            _introPreviewActive = false;
            _introNative = false;
            FadeInWindow();
            Opacity = 1;                 // 兜底：不管淡入动画是否被打断，都必须看得见
            ApplyStageVisibility();
            if (_bar is not null) ShowBar();
            Activate();
        }
        catch { }
    }

    /// <summary>自愈：主窗口已经正常显示了，却还留着一个入场窗盖在上面 → 立刻销毁它。</summary>
    private void HealLeftoverIntroWindow()
    {
        try
        {
            if (!NativeIntroWindow.IsPlaying) return;
            if (_introNative || _introPreviewActive) return;   // 界面还在等它 / 预览在做主：不插手
            // 只处理"真卡住"的窗：正常播放期间界面早已就绪，那会儿掐掉它 =
            // 用户看到的"启动动画放到一半突然没了"（实测踩过，见 Invoke-StartupCheck.ps1）
            if (!NativeIntroWindow.IsStuck) return;
            _host.Log.Warn("startup", "入场窗卡住没收场（界面已恢复），强制销毁");
            NativeIntroWindow.ForceClose();
        }
        catch { }
    }
    private void StartIntroIfEnabled()
    {
        try
        {
            // 正常路径：Program 里已经在 WPF 之前用原生窗口起播了（点开就有画面）。
            // 这条分支必须最先判断，否则会走到下面的界面内遮罩去（而且从不去关那个原生窗）。
            if (NativeIntroWindow.IsPlaying)
            {
                _introNative = true;
                Opacity = 1;                  // 主窗口就在原生窗后面，等它淡出即可
                try { _bar?.Hide(); } catch { }
                _host.Log.Info("startup", "入场动画由原生窗口播放（界面内不再叠加遮罩）");
                return;
            }

            if (!App.ShowIntro) { _host.Log.Info("startup", "按设置跳过入场动画"); return; }

            // 回退路径（原生窗没起来时才用）：素材同样按设置解析（内置片段或用户导入的文件）
            string video = IntroClips.ResolvePath(_host.Settings.IntroSource, AppContext.BaseDirectory);
            if (video.Length == 0 || !System.IO.File.Exists(video))
            {
                _host.Log.Info("startup", "没有入场动画素材，跳过");
                return;
            }

            IntroOverlay.Visibility = Visibility.Visible;
            IntroOverlay.Opacity = 1;
            _introActive = true;
            _introAnimationsStarted = false;
            try { _bar?.Hide(); } catch { }     // 全屏时控制条是置顶小窗，会飘在动画上

            IntroMedia.MediaOpened += (_, _) =>
            {
                _host.Log.Info("startup", "入场动画媒体已打开");
                BeginIntroAnimations();
            };
            IntroMedia.MediaEnded += (_, _) => OnIntroVideoEnded();
            IntroMedia.MediaFailed += (_, e) =>
            {
                _host.Log.Info("startup", "入场动画播放失败：" + e.ErrorException?.Message);
                OnIntroVideoEnded();
            };
            IntroMedia.Source = new Uri(video);

            // 兜底：不指望 MediaOpened 一定触发（实测有的机器/时机它不触发，
            // 结果视频与标题都停在透明状态 → 用户看到的是一片黑）。
            // 到点就直接开始进场动画，Play() 会在媒体就绪后自行开始。
            var kick = new DispatcherTimer(TimeSpan.FromMilliseconds(650), DispatcherPriority.Normal, (_, _) =>
            {
                _introKick?.Stop();
                _introKick = null;
                BeginIntroAnimations();
            }, Dispatcher);
            _introKick = kick;
            kick.Start();

            // 点一下或按一下＝跳过（急着用的人不该被动画拦着；界面上不写提示，保持画面干净）
            PreviewMouseLeftButtonDown += OnIntroSkipCandidate;
            PreviewKeyDown += OnIntroSkipCandidate;

            _host.Log.Info("startup", "入场动画已起播（主窗口遮罩，带原声）");
        }
        catch (Exception ex)
        {
            _host.Log.Debug("startup", "入场动画起播失败：" + ex.Message);
            _introActive = false;
            try { IntroOverlay.Visibility = Visibility.Collapsed; } catch { }
        }
    }

    /// <summary>进场：画面从黑里淡出（带轻微回拉），标题稍后浮现，音量渐入。</summary>
    private void BeginIntroAnimations()
    {
        if (_introAnimationsStarted) return;      // MediaOpened 与兜底定时器都可能来叫，只做一次
        _introAnimationsStarted = true;
        try
        {
            // 回退路径同样遵守"从第几秒播到第几秒"（正常路径由原生窗口负责）
            IntroMedia.Position = TimeSpan.FromSeconds(Math.Max(0, _host.Settings.IntroStart));
            IntroMedia.Play();
            StartIntroEndWatcher();
            _host.Log.Info("startup", "入场动画开始播放");

            IntroMedia.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            var settle = new DoubleAnimation(1.07, 1.0, TimeSpan.FromSeconds(2.4))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, settle);
            IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, settle);

            // 声音渐入：不做成"啪"地一声炸出来
            IntroMedia.BeginAnimation(MediaElement.VolumeProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(1100)));

            // 标题 1.5 秒后淡入，左侧那条薄荷绿细线同时展开
            var delay = TimeSpan.FromMilliseconds(1500);
            IntroTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = delay });
            IntroAccent.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(0, 3, TimeSpan.FromMilliseconds(600)) { BeginTime = delay });
        }
        catch (Exception ex)
        {
            _host.Log.Debug("startup", "入场动画进场失败：" + ex.Message);
        }
    }

    /// <summary>回退路径的"播到第几秒就停"（正常路径由原生窗口负责）。</summary>
    private void StartIntroEndWatcher()
    {
        double end = _host.Settings.IntroEnd;
        if (end <= 0.05) return;

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (_, _) =>
        {
            try
            {
                if (!_introActive || _introLeaving) return;
                if (IntroMedia.Position.TotalSeconds >= end)
                {
                    _introDone = true;
                    FinishIntro();
                }
            }
            catch { }
        }, Dispatcher);
        timer.Start();
    }

    private void OnIntroVideoEnded()
    {
        _introDone = true;

        // 程序还没准备好的话，画面会停在最后一帧继续等 —— 但一动不动会显得"死了"。
        // 这里给最后一帧加一个极慢的呼吸（推近再退回），并且**不加任何文字**：
        // 用户明确要求"动画结束后不想看到任何加载等待"。
        if (!ReadyForIntroExit)
        {
            try
            {
                var breathe = new DoubleAnimation(1.0, 1.05, TimeSpan.FromSeconds(9))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breathe);
                IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breathe);
            }
            catch { }
        }

        FinishIntro();       // 程序还没准备好时不会真的收场，会停在最后一帧等
    }

    /// <summary>点一下或按一下＝跳过。</summary>
    private void OnIntroSkipCandidate(object sender, InputEventArgs e)
    {
        if (!_introActive || _introLeaving) return;
        e.Handled = true;                   // 别让这一下点到下面的界面
        FinishIntro();
    }

    /// <summary>
    /// 收场：标题先淡掉 → 画面缓缓推近并压黑 → 遮罩整体淡出，露出下面已经就绪的界面。
    /// 只有"程序准备好了"且"动画也放完了"才会真的走完；否则停在最后一帧等。
    /// </summary>
    private void FinishIntro()
    {
        if (!_introActive || _introLeaving) return;
        if (!_introDone || !ReadyForIntroExit) { _introWaiting = true; return; }
        _introLeaving = true;

        try
        {
            IntroTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(IntroTitle.Opacity, 0, TimeSpan.FromMilliseconds(260)));
            IntroMedia.BeginAnimation(MediaElement.VolumeProperty, new DoubleAnimation(IntroMedia.Volume, 0, TimeSpan.FromMilliseconds(700)));

            var push = new DoubleAnimation(1.0, 1.10, TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, push);
            IntroZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, push);

            var dim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(760))
            {
                BeginTime = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            dim.Completed += (_, _) => EndIntroNow();
            IntroMedia.BeginAnimation(OpacityProperty, dim);
        }
        catch (Exception ex)
        {
            _host.Log.Debug("startup", "入场动画收场失败：" + ex.Message);
            EndIntroNow();
        }
    }

    /// <summary>把遮罩彻底收掉：停掉媒体、解开挂接、露出主窗口。</summary>
    private void EndIntroNow()
    {
        try
        {
            PreviewMouseLeftButtonDown -= OnIntroSkipCandidate;
            PreviewKeyDown -= OnIntroSkipCandidate;
            IntroMedia.Stop();
            IntroMedia.Source = null;
            IntroMedia.BeginAnimation(OpacityProperty, null);
            IntroOverlay.Visibility = Visibility.Collapsed;
        }
        catch { }

        _introActive = false;
        _introLeaving = false;
        _introWaiting = false;
        HideIntroSkipHint();
        BringToFrontAfterIntro();

        // 动画结束的这一刻，程序必须已经**完全就绪**：控制条补上、画面按状态恢复、
        // 不能有任何"正在准备/加载"的残留（用户明确要求）。
        try
        {
            if (_barPendingIntro)
            {
                _barPendingIntro = false;
                ShowBar();
            }

            ApplyStageVisibility();
        }
        catch { }

        // 动画收起后才放首次使用引导，免得两个东西同时挤在屏幕上
        if (_pendingTutorial)
        {
            _pendingTutorial = false;
            MaybeShowTutorial();

        }
    }

    /// <summary>程序准备好了：动画也放完了就收场；还没放完就等它放完（用户要看的就是它）。</summary>
    private void FinishIntroWhenReady()
    {
        ReadyForIntroExit = true;

        // 正常路径：请原生窗口淡出，把屏幕让给已经就绪的主界面。
        // （漏掉这一步的后果实测过：那块置顶窗永远不退，用户看到的就是"点了没反应"。）
        if (_introNative)
        {
            NativeIntroWindow.Finish();
            ShowIntroSkipHint();           // 都加载好了：在下边那条黑边里提醒"单击鼠标跳过"
            // **一次性**定时器：交给界面后就把自己停掉。
            // 以前这个 DispatcherTimer 从不 Stop，于是每 800ms 都会把 _introNative 抹成 false、
            // 顺手把控制条显示出来 —— 预览期间它会把预览状态一起冲掉（界面上就是"黑屏还在、
            // 巡检以为自己该去销毁它"）。实测日志：预览开始 4 秒后开始每 4 秒刷一条"发现残留的入场窗"。
            //
            // 注意：DispatcherTimer 的构造函数会**校验 callback 非空**。早先这里为了"先建后挂 Tick"
            // 传了 null!，结果启动时直接抛 ArgumentNullException（对话框：Value cannot be null.
            // (Parameter 'callback')）—— 现在给它一个真实的处理器，在里面 Stop 自己。
            DispatcherTimer? timer = null;
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Normal, (_, _) =>
            {
                timer?.Stop();
                if (ReferenceEquals(_introHandoff, timer)) _introHandoff = null;
                if (_introPreviewActive) return;      // 这期间是预览在做主，别去动它的状态

                _introNative = false;
                HideIntroSkipHint();
                ApplyStageVisibility();
                if (_barPendingIntro) { _barPendingIntro = false; ShowBar(); }
                if (_pendingTutorial) { _pendingTutorial = false; MaybeShowTutorial(); }
                BringToFrontAfterIntro();      // 置顶的入场窗收场后，前台要显式还给主窗口
            }, Dispatcher);
            _introHandoff = timer;
            timer.Start();
            return;
        }

        FinishIntro();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested && _host.Settings.MinimizeToTray)
        {
            SaveWindowPlacement();      // 最小化到托盘时也记住位置：下次启动原样打开
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            _tray?.Balloon("投屏Window 仍在后台运行", "从托盘图标可以重新打开或退出");
            return;
        }
        _closing = true;
        SaveWindowPlacement();          // 记住窗口位置/大小，下次原样打开
        if (_host.Settings.ClearCapturesOnExit)
        {
            int n = 0;
            var accounts = _vault?.Accounts;
            if (accounts is not null)
                foreach (var account in accounts) n += VaultPaths.DeleteAllItems(account.Id);
            n += CaptureService.ClearLegacy();
            _host.Log.Info("privacy", $"退出时按设置清空了 {n} 条加密内容与旧明文文件");
        }
        try { _hotkeys?.Dispose(); } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _recordTimer?.Stop(); _capture?.Dispose(); } catch { }
        try { _vault?.Shutdown(); } catch { }
        WindowEffects.SetKeepAwake(false);
        WindowEffects.SetCursorHidden(false);
        _uiTimer.Stop();
        _mirrorPollTimer.Stop();
        _netPollTimer.Stop();
        _cursorTimer.Stop();
        try { _bar?.Close(); } catch { }
        try { _stats?.Close(); } catch { }
        try { _adbWindow?.Close(); } catch { }
        try { _mini?.Close(); } catch { }
        _mini = null;

        // 并排的各路宿主：窗口要关了，先把画面摘掉（否则面板销毁时还在引用已释放的表面）
        foreach (MultiViewTile tile in _tiles.Values)
        {
            try { tile.Host.Detach(); } catch { }
        }
        _tiles.Clear();
        _attachedSessions.Clear();

        try { _host.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ 顶部按钮

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(true);

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>「我的库」：侧栏「本次投屏」卡片里的入口还在用这个处理器。</summary>
    private void BtnOpenVault_Click(object sender, RoutedEventArgs e) => OpenVault();

    // 截屏 / 录制 / 一键诊断三个磁贴随「快捷操作」卡片一起按需求删除，对应的事件处理器也一并去掉。
    // 功能没有丢：截屏 Ctrl+Alt+S、录制 Ctrl+Alt+R、托盘菜单、画面下方常驻的悬浮控制条都还在；
    // 一键诊断在 设置 → 诊断 →「一键连接诊断」。

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) return;   // 全屏时不处理
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateMaximizeGlyph()
    {
        try
        {
            bool max = WindowState == WindowState.Maximized;
            BtnMaximize.Content = max ? "\uE923" : "\uE922";     // 还原 / 最大化
            System.Windows.Automation.AutomationProperties.SetName(BtnMaximize, max ? "还原" : "最大化");
            BtnMaximize.ToolTip = max ? "还原" : "最大化";
        }
        catch { }
    }
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_host, RenderChannelProbeAsync) { Owner = this };
        win.ShowDialog();
        LoadSettingsIntoUi();
        _host.Miracast.MuteAudio = _host.Settings.MuteAudio;
        ApplyVolumeToHosts();
        _fit = ParseFit(_host.Settings.MirrorFit);
        _zoom = Math.Clamp(_host.Settings.MirrorZoom, 0.5, 2.0);
        ApplyFillMode();
        if (!_host.Settings.ShowStats) _stats?.Hide();
        UpdateAppearanceIcon();
        RefreshUi();
    }

    // ------------------------------------------------------------------ 顶栏：外观快捷切换

    /// <summary>左键：在深色 / 浅色之间切换（当前是「跟随系统」时切到与当前相反的那套）。</summary>
    private void BtnAppearance_Click(object sender, RoutedEventArgs e)
    {
        string current = App.ResolveTheme(_host.Settings.Theme);
        string next = string.Equals(current, "Light", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        App.ApplyTheme(next);          // 立即生效并记住
        UpdateAppearanceIcon();
        ShowToast(next == "Light" ? "已切换到浅色外观" : "已切换到深色外观",
            "点这里随时切换；要改外观档位或其它设置，点旁边的齿轮。");
    }

    /// <summary>右键：直接打开设置（外观的三档选择在 设置 → 通用 → 外观）。</summary>
    private void BtnAppearance_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        BtnSettings_Click(sender, new RoutedEventArgs());
    }

    /// <summary>图标跟着当前主题走：深色显示月亮，浅色显示太阳。</summary>
    private void UpdateAppearanceIcon()
    {
        try
        {
            bool light = string.Equals(App.CurrentThemeFile, "Light", StringComparison.OrdinalIgnoreCase);
            var brush = (System.Windows.Media.Brush)FindResource("B.TextDim");
            if (light)
            {
                AppearanceIcon.Data = System.Windows.Media.Geometry.Parse(SunIconPath);
                AppearanceIcon.Fill = null;
                AppearanceIcon.Stroke = brush;
                AppearanceIcon.StrokeThickness = 1.5;
                AppearanceIcon.StrokeStartLineCap = System.Windows.Media.PenLineCap.Round;
                AppearanceIcon.StrokeEndLineCap = System.Windows.Media.PenLineCap.Round;
            }
            else
            {
                // 月亮：内弧 r=8.9 必须 > 外弧 r=8（弦长 16 的一半），否则半径被放大到 8 → 与外弧重合 → 面积归零（图标消失）
                AppearanceIcon.Data = System.Windows.Media.Geometry.Parse(MoonIconPath);
                AppearanceIcon.Fill = brush;
                AppearanceIcon.Stroke = null;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 忙碌遮罩

    public void ShowBusy(string text, string? hint = null)
    {
        if (_closing) return;
        TxtBusyText.Text = text;
        if (hint is not null) TxtBusyHint.Text = hint;
        if (BusyOverlay.Visibility != Visibility.Visible)
        {
            BusyOverlay.Visibility = Visibility.Visible;
            Motion.FadeIn(BusyOverlay, 180);
        }
        // 空转动画由 XAML 的 Loaded 触发只跑一次；HideBusy 会把它停掉，再次显示必须重新起转
        Motion.Spin(BusySpin, true, 1300);
        _busyShown = true;
    }

    /// <summary>首次启动时播放使用引导动画；之后可在设置里重看。</summary>
    private void MaybeShowTutorial()
    {
        if (_host.Settings.HasSeenTutorial || _closing) return;
        try
        {
            _tutorialOpen = true;
            var tutorial = new TutorialWindow(_host.Settings.DeviceName) { Owner = this };
            tutorial.ShowDialog();
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"引导窗口异常：{ex.Message}"); }
        finally { _tutorialOpen = false; }
        _host.Settings.HasSeenTutorial = true;
        _host.Settings.Save();
        UpdateWindowedBar();
    }

    /// <summary>
    /// 旧版本用来切换明文保存目录；新版固定写入加密库，这里只保留旧目录设置（供迁移/清理使用）。
    /// </summary>
    public void ApplyCaptureFolder()
    {
        try
        {
            string dir = _host.Settings.CaptureFolder;
            if (!string.IsNullOrWhiteSpace(dir)) CaptureService.OutputDirectory = dir;
        }
        catch { }
    }

    /// <summary>紧急隐藏：立即停止镜像、上锁加密库并把窗口藏到托盘（Ctrl+Alt+H）。</summary>
    public async void PanicHide()
    {
        try
        {
            LockVault();
            _host.Publish("已紧急停止镜像、锁定加密库并隐藏窗口");
            if (_fullscreen) SetFullscreen(false);
            if (_host.Miracast.IsListening) await StopMirrorAsync();
            MiracastHost.Detach();
            Hide();
            _tray?.Balloon("已隐藏", "投屏已停止、加密库已上锁。点击托盘图标可以重新打开。");
        }
        catch { }
    }

    /// <summary>全局快捷键开关。</summary>
    public void ApplyGlobalHotkeySetting(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _hotkeyConflictReported = false;   // 用户主动改设置时允许再提醒一次
                _hotkeys?.Register();
                ReportHotkeyConflicts();
            }
            else _hotkeys?.Unregister();
        }
        catch { }
    }

    /// <summary>注册失败的全局快捷键（空＝全部可用）。设置页用它显示真实状态。</summary>
    public IReadOnlyList<string> HotkeyFailures
    {
        get
        {
            if (!_host.Settings.GlobalHotkeys) return Array.Empty<string>();
            return _hotkeys?.FailedCombos ?? Array.Empty<string>();
        }
    }

    private bool _hotkeyConflictReported;

    /// <summary>
    /// 快捷键被别的程序占用时如实告知。此前 a||b||c… 一个成功就算注册成功，
    /// 失败被静默吞掉，而 tooltip 照样写着「Ctrl+Alt+S」，用户按了没反应也查不出原因。
    /// </summary>
    private void ReportHotkeyConflicts()
    {
        if (_hotkeyConflictReported) return;
        var failed = _hotkeys?.FailedCombos;
        if (failed is null || failed.Count == 0) return;
        _hotkeyConflictReported = true;
        try { _host.Log.Warn("hotkey", "全局快捷键注册失败：" + string.Join("、", failed)); } catch { }
        ShowToast("有快捷键没能注册",
            $"{string.Join("、", failed)} 已被其它程序占用。可在设置里关掉「全局快捷键」，改用界面按钮操作。",
            null, null, ok: false);
    }

    /// <summary>云母材质背景（Win11）；失败时自动回退为纯色。</summary>
    public void ApplyMica(bool enabled)
    {
        try
        {
            bool ok = WindowEffects.TryEnableMica(this, enabled);
            // 云母开启时用"半透明主题底色"，而不是全透明：
            // 全透明会让桌面壁纸直接透出来，看上去像主题坏了。
            RootFrame.SetResourceReference(BackgroundProperty, enabled && ok ? "B.WindowMica" : "B.WindowBg");
        }
        catch { }
    }
    /// <summary>界面密度：紧凑模式收窄留白。</summary>
    public void ApplyDensity(string density)
    {
        try
        {
            bool compact = density.Equals("Compact", StringComparison.OrdinalIgnoreCase);

            // 全屏时侧栏宽度是 0、外边距是 0；这时再按密度改布局会把全屏布局弄坏。
            if (!_fullscreen)
            {
                MainArea.Margin = new Thickness(compact ? 12 : 18, compact ? 4 : 8, compact ? 12 : 18, compact ? 6 : 10);
                SideColumnDef.Width = new GridLength(compact ? 272 : 300);
                HeaderBar.Margin = new Thickness(compact ? 12 : 18, 0, compact ? 8 : 10, 0);
                StageCard.Margin = new Thickness(0, 0, compact ? 10 : 14, 0);
            }

            TxtStateBig.FontSize = compact ? 18 : 19;
            Dispatcher.BeginInvoke(() => { PositionBar(); PositionStats(); }, DispatcherPriority.Loaded);
        }
        catch { }
    }

    /// <summary>供设置窗口重新打开引导。</summary>
    public void ShowTutorialAgain()
    {
        try { new TutorialWindow(_host.Settings.DeviceName) { Owner = this }.ShowDialog(); } catch { }
    }

    // ------------------------------------------------------------------ 隐私遮挡（Ctrl+Alt+P / 锁屏自动）

    /// <summary>遮挡画面：收起视频宿主并盖上面板。投屏不中断，点一下即可恢复。</summary>
    public void ShowPrivacyMask(string? reason = null)
    {
        try
        {
            if (_masked) return;
            _masked = true;
            TxtMaskTitle.Text = reason ?? "画面已遮挡";
            TxtMaskHint.Text = "投屏仍在进行 · 点一下或按 Ctrl+Alt+P 恢复";
            ApplyStageVisibility();          // HwndHost 盖不住，只能先把所有画面宿主收起来
            PrivacyMask.Visibility = Visibility.Visible;
            PrivacyMask.Focus();             // 遮挡后把焦点交给面板，键盘用户按 Enter/空格即可恢复
            _host.Log.Info("privacy", "画面已遮挡");
        }
        catch { }
    }

    public void HidePrivacyMask()
    {
        try
        {
            if (!_masked) return;
            _masked = false;
            PrivacyMask.Visibility = Visibility.Collapsed;
            RefreshUi();      // 由 RefreshUi 决定视频宿主该不该显示
            _host.Log.Info("privacy", "画面遮挡已解除");
        }
        catch { }
    }

    public void TogglePrivacyMask()
    {
        if (_masked) HidePrivacyMask();
        else ShowPrivacyMask();
    }

    // Button.Click：鼠标点击与键盘 Enter/空格都会走到这里
    private void PrivacyMask_Click(object sender, System.Windows.RoutedEventArgs e) => HidePrivacyMask();

    /// <summary>锁屏 / 切换用户 / 断开远程会话：立即上锁并遮挡画面。</summary>
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (!_host.Settings.LockOnSessionLock) return;
        bool away = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock
                    or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                    or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect
                    or Microsoft.Win32.SessionSwitchReason.SessionLogoff;
        if (!away) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                LockVault();
                ShowPrivacyMask("电脑已锁屏，画面已遮挡");
                _host.Log.Info("privacy", $"会话事件 {e.Reason}：已上锁并遮挡画面");
            }
            catch { }
        }));
    }

    /// <summary>手机搜不到设备时：重建接收广播（等价于"重启程序"那一下）。</summary>
    private async void BtnRefreshBroadcast_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnRefreshBroadcast.IsEnabled = false;
            // 用**设置里的当前名字**刷新：早先这里读的是接收端内部的旧快照，
            // 于是"改完名字点刷新广播"仍然是旧名字（用户报的就是这个现象）。
            bool ok = await _host.Miracast.RefreshBroadcastAsync(_host.Settings.DeviceName);
            await PollMirrorStatusAsync();
            RefreshUi();
            ShowToast(ok ? "接收广播已刷新" : "刷新失败",
                ok ? "现在在手机上重新搜索本机名称即可。" : "请稍后再试，或点「重启接收端」。",
                null, null, ok);
        }
        catch (Exception ex) { _host.Log.Warn("ui", "刷新广播失败：" + ex.Message); }
        finally { BtnRefreshBroadcast.IsEnabled = true; }
    }

    /// <summary>主窗口淡入（启动闪屏关闭后调用）。</summary>
    private IntroHintWindow? _introHint;

    /// <summary>
    /// 在入场画面上浮出"单击鼠标跳过"。
    /// 只在**全部加载完成**之后调（就是 FinishIntroWhenReady 那一刻），位置落在入场窗下边的黑边里。
    /// </summary>
    public void ShowIntroSkipHint(string text = "单击鼠标跳过")
    {
        try
        {
            if (_closing) return;
            if (!NativeIntroWindow.TryGetBounds(out int x, out int y, out int w, out int h)) return;

            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            _introHint ??= new IntroHintWindow();
            _introHint.Hint = text;
            _introHint.ShowFor(x, y, w, h, dpi);
        }
        catch { }
    }

    /// <summary>收掉跳过提示。</summary>
    public void HideIntroSkipHint()
    {
        try
        {
            IntroHintWindow? hint = _introHint;
            _introHint = null;
            hint?.FadeOutAndClose();
        }
        catch { }
    }

    /// <summary>
    /// 把主窗口带回前台（入场动画收场后必须做）。
    ///
    /// 为什么：入场窗是一块 **WS_EX_TOPMOST 的原生窗**，它自己是前台窗口；
    /// 它一销毁，Windows 并不会自动把前台交回我们的主窗口 —— 用户实测到的现象就是
    /// "动画演完之后界面像被最小化了：任务栏有按钮，屏幕上却看不到窗口"。
    /// 这里把状态归位 + 显式激活 + SetForegroundWindow，双保险。
    /// </summary>
    private void BringToFrontAfterIntro()
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            Activate();
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public void FadeInWindow()
    {
        try
        {
            // 入场动画还盖着（原生窗口或界面内遮罩）：不用淡入，露出来正好是它淡出的那一刻
            if (_introActive)
            {
                Opacity = 1;
                return;
            }

            Opacity = 1;
            // 窗口本身 420ms 淡入；内容（顶栏/舞台/侧栏）由 PlayEntrance 错峰入场。
            Motion.FadeIn(this, 420, 0, 0);
        }
        catch { }
    }

    /// <summary>
    /// 窗口内容的错峰入场：顶栏从上落下 → 舞台从下浮起 → 侧栏从右滑入 → 底栏淡入。
    /// 只在第一次显示时播；重连、刷新、切主题都不会重播。
    /// </summary>
    private void PlayEntrance()
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;
        try
        {
            Motion.Reveal(HeaderBar, HeaderSlide, 0, -14, 520, 0, 0);
            Motion.Reveal(StageCard, StageSlide, 0, 16, 600, 60, 0);
            Motion.Reveal(SideScroll, SideSlide, 18, 0, 620, 130, 0);
            Motion.FadeIn(StatusBarRow, 500, 220);
            GuideModel.PlayEntrance();
        }
        catch { }
    }

    public void HideBusy()
    {
        if (!_busyShown) return;
        _busyShown = false;
        Motion.FadeOut(BusyOverlay, 260, () =>
        {
            if (_busyShown) return;
            BusyOverlay.Visibility = Visibility.Collapsed;
            Motion.Spin(BusySpin, false);   // 停掉空转动画
        });
    }

    // ------------------------------------------------------------------ 轮询

    private async Task PollMirrorStatusAsync()
    {
        if (_closing) return;
        try
        {
            var status = await Task.Run(() => _host.Miracast.QueryStatus());
            await Dispatcher.InvokeAsync(() =>
            {
                // 状态没变就什么都不做：轮询很频繁，避免无意义的重排与重绘
                if (status == _mirrorStatus) return;
                _mirrorStatus = status;
                RefreshUi();
            }, DispatcherPriority.Background);
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"状态轮询异常：{ex.Message}"); }
    }

    /// <summary>网络变化后的自动恢复：重新检查放行状态，必要时重启接收端。</summary>
    private async Task AutoRecoverAfterNetworkChangeAsync()
    {
        try
        {
            await Task.Delay(1500);
            if (_closing) return;
            if (!_host.FirewallRulePresent || !_host.NetworkCategory.Equals("Private", StringComparison.OrdinalIgnoreCase))
            {
                AppendEvent("网络未放行，等待你点「一键准备」");
                RefreshUi();
                return;
            }
            await RestartReceiverAsync();
            AppendEvent("已按新网络重新开启接收端");
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"网络恢复异常：{ex.Message}"); }
    }
    private async Task PollNetworkAsync()
    {
        if (_closing) return;
        try
        {
            var (lan, category, firewall, wlan) = await Task.Run(() =>
            {
                var l = NetInfo.GetLanInterface();
                string c = l is null ? "Unknown" : NetInfo.GetNetworkCategory(l.Name);
                bool f = NetInfo.HasFirewallRuleFor(NetworkFixer.RulePrefix);
                NetInfo.WlanLink? w = NetInfo.ReadWlanLink();
                return (l, c, f, w);
            });
            string? hotspot = await Task.Run(NetInfo.DetectHotspotConflict);
            if (_lastSsid.Length == 0 && lan is not null)
            {
                string? ssid = await Task.Run(NetInfo.GetWlanSsid);
                if (!string.IsNullOrEmpty(ssid)) _lastSsid = ssid;
            }
            await Dispatcher.InvokeAsync(() =>
            {
                string key = $"{lan?.Address}|{category}|{firewall}";
                bool changed = _lastLanKey.Length > 0 && _lastLanKey != key;
                _lastLanKey = key;
                _host.ApplyNetworkSnapshot(lan, category, firewall, wlan);
                _host.ApplyHotspotConflict(hotspot);
                RefreshUi();

                // 换了 Wi-Fi / 网络位置变了：自动重新放行并重启接收端，用户不用手动折腾
                if (changed && !_closing && !_busyPrepare)
                {
                    AppendEvent("检测到网络变化，正在自动恢复…");
                    _ = AutoRecoverAfterNetworkChangeAsync();
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"网络轮询异常：{ex.Message}"); }
    }

    // ------------------------------------------------------------------ 界面刷新

    private void OnUiTick()
    {
        var now = _tickWatch.Elapsed;
        double deltaMs = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;
        if (deltaMs > 1500 && !_closing && !_tutorialOpen && now.TotalSeconds > 10)
            _host.Log.Warn("ui", $"界面线程停顿 {deltaMs:F0}ms");

        // 多设备并排：补挂还没挂上画面的那几路（宿主建好之前挂不上），并刷新每格的状态
        UpdateSessionLiveness();
        EnsureSessionHostsAttached();
        SyncMultiView("定时补挂");
        RefreshTileStates();

        // 电脑控制手机：连上后每 5 秒确认一次手机还在不在
        // （功能关闭时不碰 adb：那是唯一一处"无条件起 adb 进程"的轮询）
        if (AdbEnabled && ++_adbPollTicks % 5 == 0)
        {
            _ = _adb.RefreshAsync(full: false);
        }

        // 还没连上就按"先快后慢"的节奏重试（刚投屏上来那几秒最要紧）
        TickPhoneControl();

        // 开着控制时每 4 秒重读一次屏幕尺寸/旋转：手机转了以后坐标才不会一直按旧尺寸算
        if (_adbMouseOn && _adb.IsConnected && _adbPollTicks % 4 == 0)
        {
            _ = _adb.RefreshScreenAsync();
        }

        RefreshAdbUi();

        UpdateStats();
        RefreshUi();
    }

    /// <summary>每秒采集一次播放管道状态，刷新右上角的通讯状态浮层。</summary>
    private void UpdateStats()
    {
        try
        {
            bool mirroring = AnyMirroring;
            var session = PrimaryHost.Player?.PlaybackSession;
            if (mirroring && session is not null)
            {
                _statsData.Sample(session.PlaybackState, session.Position);
            }
            else if (!mirroring)
            {
                _statsData.Reset();
                _statsIssueSince = DateTime.MinValue;
            }

            string verdict = _statsData.Verdict;
            bool healthy = verdict == "ok";
            if (!healthy && mirroring) _statsIssueSince = DateTime.UtcNow;

            // 显示策略：
            //   ShowStats=false            → 不显示
            //   ShowStats=true + 只卡顿时  → 画面正常时完全不出现（全屏不再挡画面），一旦卡顿/停滞就浮出来，
            //                                恢复正常后再多留 6 秒，方便看清刚才发生了什么
            //   ShowStats=true + 始终显示  → 老行为
            bool recentlyUnhealthy = _statsIssueSince != DateTime.MinValue
                                     && (DateTime.UtcNow - _statsIssueSince).TotalSeconds < 6;
            bool show = WindowState != WindowState.Minimized
                        && mirroring
                        && _host.Settings.ShowStats
                        && (App.StatsPreview
                            || !_host.Settings.StatsOnlyWhenLagging
                            || !healthy
                            || recentlyUnhealthy);
            if (!show)
            {
                if (_stats is not null && _stats.IsVisible) _stats.Hide();
                return;
            }

            if (_stats is null)
            {
                _stats = new StatsOverlayWindow { Owner = this };
                _stats.Closed += (_, _) => _stats = null;
            }
            _stats.Update(_statsData.Verdict, _statsData.Summary, _statsData.Detail(_host.Miracast.StreamInfo, (int)PrimaryHost.Volume, PrimaryHost.IsMuted));
            UpdateMetricsChart();
            _stats.Topmost = _fullscreen;   // 只有全屏才置顶；窗口模式跟随主窗口，不会压到别的程序上
            if (!_stats.IsVisible) _stats.Show();
            PositionStats();
        }
        catch (Exception ex) { _host.Log.Debug("ui", $"状态浮层异常：{ex.Message}"); }
    }

    /// <summary>
    /// 把状态浮层贴到画面区右上角（跟随窗口移动/缩放/全屏）。
    ///
    /// 「不遮挡内容」的做法：先看画面上方那条**黑边**放不放得下 —— 放得下就放进黑边里
    /// （黑边本来就没内容），放不下才退回画面内的右上角。用户反馈过浮层压住画面。
    /// </summary>
    private void PositionStats()
    {
        if (_stats is null || !_stats.IsVisible) return;
        try
        {
            var topLeft = StageCard.PointToScreen(new Point(0, 0));
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double w = StageCard.ActualWidth * dpi, h = StageCard.ActualHeight * dpi;

            double overlayH = _stats.ActualHeight > 0 ? _stats.ActualHeight : 60;
            double top = 12;

            // 状态卡是浮在**画面区**上的，而画面区从舞台顶栏（40px）下面才开始 ——
            // 坐标却按 StageCard 顶边算，于是连接后它正好压在右上角那排按钮上。
            // 顶栏可见就把这 40px 加回去（全屏时顶栏收起，高度自然为 0）。
            double stageBarH = StageTopBar.Visibility == Visibility.Visible ? StageTopBar.ActualHeight : 0;
            double videoH = Math.Max(1, h - stageBarH);

            // 画面在画面区里实际占的那块（字母框）：上边距就是可用的黑边高度
            double bar = 0;
            try
            {
                VideoBox box = PrimaryVideoBox();
                bar = box.Y * dpi;
            }
            catch { bar = 0; }

            if (bar >= overlayH + 8)
            {
                // 放进上黑边：垂直居中在黑边里，完全不压画面
                top = Math.Max(4, (bar - overlayH) / 2);
            }

            double clampedTop = Math.Clamp(top, 4, Math.Max(4, videoH - overlayH - 4));
            _stats.Left = topLeft.X + w - _stats.ActualWidth - 12;
            _stats.Top = topLeft.Y + stageBarH + clampedTop;
        }
        catch { }
    }

    private void RefreshUi()
    {
        if (_closing) return;

        // 托盘提示跟着设备名走：以前只在"断开"那一条路径里刷过，
        // 于是设置里改了名字之后，托盘上一直挂着旧名字。
        _tray?.UpdateTooltip($"投屏Window · {_host.Settings.DeviceName}");

        bool listening = _host.Miracast.IsListening;
        bool mirroring = AnyMirroring;
        bool pending = _mirrorStatus.ListeningStatus.Contains("Pending", StringComparison.OrdinalIgnoreCase);
        bool networkReady = _host.NetworkCategory.Equals("Private", StringComparison.OrdinalIgnoreCase)
                            && _host.FirewallRulePresent;
        bool hotspot = !string.IsNullOrEmpty(_host.HotspotConflict);

        // 「当前设置」卡片已按需求删除：设备名称 / Wi-Fi 频段与速率 / 加密库状态 / 键盘回传
        // 都改在设置窗口里看（TxtSetDevice、TxtSetWifi、TxtSetVault、TxtSetKeyboard 已从 MainWindow.xaml 移除）。

        // 空闲时给一个"刷新广播"入口（手机搜不到设备时点它，等价于重启程序那一下）
        BtnRefreshBroadcast.Visibility = (!mirroring && listening && !_mirrorBusy)
            ? Visibility.Visible : Visibility.Collapsed;

        BtnPrepare.Visibility = networkReady ? Visibility.Collapsed : Visibility.Visible;
        BtnGuidePrepare.Visibility = networkReady ? Visibility.Collapsed : Visibility.Visible;

        _suppressToggle = true;
        SwMirror.IsChecked = listening;
        _suppressToggle = false;

        string state, sub;
        string dotKey;
        if (_mirrorBusy) { state = _busyText; sub = ""; dotKey = "B.Warn"; }
        else if (mirroring) { state = "镜像中"; sub = ""; dotKey = "B.Ok"; }
        else if (pending) { state = "手机正在连接"; sub = ""; dotKey = "B.Warn"; }
        else if (listening) { state = "等待手机连接"; sub = ""; dotKey = "B.TextDim"; }
        else if (networkReady) { state = "接收端未开启"; sub = ""; dotKey = "B.Warn"; }
        else { state = "需要放行网络"; sub = ""; dotKey = "B.Warn"; }
        // 用资源引用而不是取一次 Brush：否则切换主题时颜色不会跟着变
        DotState.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
        if (TxtStateBig.Text != state) TxtStateBig.Text = state;

        // ---- 顶栏的"活状态"与舞台顶栏（一眼可见的一句话，不必先看侧栏） ----
        bool session = mirroring || _demoCast;
        string headerState = _mirrorBusy ? "正在准备"
            : mirroring ? "投屏中"
            : pending ? "正在连接"
            : listening ? "等待设备"
            : networkReady ? "接收端已关闭"
            : "需要放行网络";
        if (TxtHeaderState.Text != headerState)
        {
            TxtHeaderState.Text = headerState;
            Motion.FadeIn(TxtHeaderState, 280);
        }
        DotHeader.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);

        string deviceLabel = CurrentDeviceLabel();
        string stageTitle = session ? deviceLabel : "本机画面";
        string stageSub = session
            ? (string.IsNullOrEmpty(_activeProtocol) ? "已连接 · 实时镜像" : $"已连接 · {_activeProtocol}")
            : pending ? "正在建立连接…" : "等待设备投屏";
        if (TxtStageTitle.Text != stageTitle) TxtStageTitle.Text = stageTitle;
        if (TxtStageSub.Text != stageSub)
        {
            TxtStageSub.Text = stageSub;
            Motion.FadeIn(TxtStageSub, 260);
        }
        DotStage.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            session ? "B.Ok" : pending ? "B.Warn" : "B.TextFaint");
        UpdateStageChrome();

        // ---- 只在真的需要时才给出说明文字与动作按钮，正常状态保持干净 ----
        double pendingSeconds = 0;
        if (!mirroring && pending)
        {
            _connectingSince ??= DateTime.UtcNow;
            pendingSeconds = (DateTime.UtcNow - _connectingSince.Value).TotalSeconds;
        }
        else _connectingSince = null;

        bool latencyBad = mirroring && (_statsData.Verdict != "ok"
                                        || (_statsData.HasRatio && _statsData.RealTimeRatio < 0.92)
                                        || _statsData.LagSeconds > 2.5);

        string fixText = "", fixHint = "";
        _fixKind = FixKind.None;
        if (!networkReady)
        {
            _fixKind = FixKind.PrepareNetwork;
            fixText = "一键准备";
            fixHint = "放行防火墙并允许被发现（只需一次）";
        }
        else if (!listening && _host.Settings.AutoStartMiracast && !_mirrorBusy)
        {
            _fixKind = FixKind.RestartReceiver;
            fixText = "重新开启接收端";
            fixHint = "接收端意外停止了";
        }
        else if (pendingSeconds > 20)
        {
            _fixKind = FixKind.RestartReceiver;
            fixText = "重启接收端";
            fixHint = $"手机连接超时（已等待 {pendingSeconds:F0} 秒）";
        }
        else if (!mirroring && !string.IsNullOrEmpty(_mirrorStatus.LastError))
        {
            _fixKind = FixKind.RestartReceiver;
            fixText = "重启接收端";
            fixHint = "上次连接失败";
        }
        else if (latencyBad)
        {
            fixHint = _statsData.LagSeconds > 2.5 ? $"画面滞后 {_statsData.LagSeconds:F1} 秒" : "画面推进偏慢";
        }

        if (mirroring && _mirrorStatus.ConnectionCount > 1)
            fixHint = $"已连接 {_mirrorStatus.ConnectionCount} 台手机（画面显示最近连接的一台）";
        if (TxtStateSub.Text != fixHint) TxtStateSub.Text = fixHint;
        TxtStateSub.Visibility = fixHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DotState.Visibility = TxtStateSub.Visibility;

        bool showFix = _fixKind != FixKind.None;
        BtnFixAction.Visibility = showFix ? Visibility.Visible : Visibility.Collapsed;
        BtnFixAction.Content = fixText;
        BtnLatency.Visibility = latencyBad ? Visibility.Visible : Visibility.Collapsed;
        ActionsRow.Visibility = (showFix || latencyBad) ? Visibility.Visible : Visibility.Collapsed;

        TxtHotspot.Visibility = (hotspot && !mirroring) ? Visibility.Visible : Visibility.Collapsed;
        TxtHotspot.Text = "移动热点占用了 Wi-Fi Direct，可能连不上（可在设置里关闭热点）";
        TxtHotspot.ToolTip = _host.HotspotConflict;

        MiracastHost.Visibility = (mirroring && !_masked && !_introActive && !_introNative && WindowState != WindowState.Minimized)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyStageVisibility();      // 各路并排宿主一起按"有画面/没遮挡/没最小化"决定显示
        PrivacyMask.Visibility = (_masked && !_closing) ? Visibility.Visible : Visibility.Collapsed;
        bool connecting = !mirroring && pending;
        if (StageConnecting.Visibility != (connecting ? Visibility.Visible : Visibility.Collapsed))
        {
            StageConnecting.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            if (connecting)
            {
                Motion.FadeIn(StageConnecting, 300);
                Motion.Spin(ConnSpin, true, 1200);
            }
            else
            {
                Motion.Spin(ConnSpin, false);
            }
        }
        bool showGuide = !mirroring && !pending;
        if (showGuide)
        {
            // 回到待机：如果上一次的"连接收尾"还在淡出，直接取消并恢复引导
            bool wasExiting = _guideExiting;
            _guideEpoch++;
            _guideExiting = false;
            if (wasExiting || StageGuide.Visibility != Visibility.Visible)
            {
                Motion.Stop(StageGuide);
                StageGuide.Opacity = 1;
            }
            if (StageGuide.Visibility != Visibility.Visible)
            {
                StageGuide.Visibility = Visibility.Visible;
                GuideModel.ResetForIdle();
                GuideModel.PlayEntrance();
                Motion.FadeIn(StageGuide, 360);
            }
            if (!_masked && WindowState != WindowState.Minimized) GuideModel.Start();
            else GuideModel.Stop();
        }
        else if (StageGuide.Visibility == Visibility.Visible && !_guideExiting)
        {
            // 连接中 / 投屏中：手机先转回正面、轻微放大上浮，然后整块引导淡出
            _guideExiting = true;
            int epoch = ++_guideEpoch;
            GuideModel.PlayConnectTransition();
            Motion.FadeOut(StageGuide, 520, () =>
            {
                if (epoch != _guideEpoch)
                {
                    StageGuide.Opacity = 1;
                    return;
                }
                StageGuide.Visibility = Visibility.Collapsed;
                StageGuide.Opacity = 1;
                _guideExiting = false;
                GuideModel.Stop();
            });
        }

        if (connecting)
            TxtConnectingHint.Text = pendingSeconds < 20
                ? $"已等待 {pendingSeconds:F0} 秒"
                : $"已等待 {pendingSeconds:F0} 秒，仍未连上";

        // 右侧卡片：未连接时轮播「上手引导」，连上后换成「本次投屏」。
        // 只在状态真正翻转时播一次交叉淡入，否则每秒刷新都会重播动画。
        if (_lastSessionUi != session)
        {
            _lastSessionUi = session;
            if (session)
            {
                TipsCard.Visibility = Visibility.Collapsed;
                SessionCard.Visibility = Visibility.Visible;
                Motion.Reveal(SessionCard, SessionSlideIn, 0, 10, 360, 40);
                // 刚连上的一下：状态点轻轻跳一下，是"连上了"的即时反馈
                Motion.Pulse(DotStageScale, 1.9, 420);
                Motion.Pulse(DotSessionScale, 1.6, 420);
            }
            else
            {
                SessionCard.Visibility = Visibility.Collapsed;
                TipsCard.Visibility = Visibility.Visible;
                Motion.Reveal(TipsCard, TipsSlideIn, 0, 10, 360, 40);
            }
        }
        if (session)
        {
            if (_sessionSince is null) { _sessionSince = DateTime.UtcNow; _sessionShots = 0; _sessionRecords = 0; }
            UpdateSessionCard();
        }
        else
        {
            _sessionSince = null;
            var now2 = DateTime.UtcNow;
            if (now2 - _tipSince > TimeSpan.FromSeconds(5))
            {
                _tipSince = now2;
                ShowTipPage(_tipIndex + 1, animate: true);
            }
        }

        // 加密库状态：归属账号（卡片上的「加密库」一行已随「当前设置」卡片一起删除，
        // 解锁状态与归属在看库/绑定时给出，这里只保留状态卡里的「内容归属」）
        if (_vault is not null)
        {
            string owner = _vault.ActiveAccount?.DisplayName
                           ?? (_vault.HasAccount ? "未绑定设备" : "未创建账号");
            TxtOwner.Text = owner;
        }

        // 托盘提示跟着状态走（只在变化时更新，避免每秒调一次 Shell_NotifyIcon）
        string trayTip = mirroring ? "投屏Window · ● 投屏中"
                       : listening ? "投屏Window · 等待手机连接"
                       : "投屏Window · 接收端已关闭";
        if (trayTip != _lastTrayTip) { _lastTrayTip = trayTip; _tray?.UpdateTooltip(trayTip); }

        SyncBar();
    }

    // ------------------------------------------------------------------ 右侧卡片

    private void BuildTipDots()
    {
        TipDots.Children.Clear();
        for (int i = 0; i < TipPages.Length; i++)
        {
            int index = i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = (System.Windows.Media.Brush)FindResource(i == 0 ? "B.Accent" : "B.Line"),
            };

            // 圆点做成按钮：可以点它直接翻到对应那一页
            var button = new System.Windows.Controls.Button
            {
                Content = dot,
                Width = 24,
                Height = 20,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"第 {i + 1} 页：{TipPages[i].Title}",
                Focusable = false,
            };
            var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var presenter = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            presenter.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = presenter;
            button.Template = template;
            button.Click += (_, _) =>
            {
                _tipSince = DateTime.UtcNow;
                ShowTipPage(index, animate: true);
            };
            TipDots.Children.Add(button);
        }
    }

    /// <summary>切换引导页：淡出上移 → 换内容 → 淡入下落。</summary>
    private void ShowTipPage(int index, bool animate)
    {
        int count = TipPages.Length;
        index = ((index % count) + count) % count;
        _tipIndex = index;
        var page = TipPages[index];

        void Apply()
        {
            try { TipIcon.Data = System.Windows.Media.Geometry.Parse(page.Icon); } catch { }
            TxtTipTitle.Text = page.Title;
            TxtTipBody.Text = page.Body;
            for (int i = 0; i < TipDots.Children.Count; i++)
                if (TipDots.Children[i] is System.Windows.Controls.Button { Content: System.Windows.Shapes.Ellipse dot })
                    dot.Fill = (System.Windows.Media.Brush)FindResource(i == index ? "B.Accent" : "B.Line");
        }

        if (!animate || TipPageHost.Visibility != Visibility.Visible)
        {
            Apply();
            return;
        }

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fadeOut.Completed += (_, _) =>
        {
            Apply();
            TipPageHost.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
            TipSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(240)));
        };
        TipPageHost.BeginAnimation(OpacityProperty, fadeOut);
        TipSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>舞台/顶栏里显示"现在是谁在投"：优先 AirPlay 聚焦会话的真实设备名。</summary>
    private string CurrentDeviceLabel()
    {
        try
        {
            AirPlayService.AirPlaySession? focused = _host.AirPlay.FocusedSession;
            if (focused is not null) return focused.Label;
        }
        catch { }
        return _activeIdentity?.ShortLabel() ?? "手机画面";
    }

    /// <summary>已连接时显示本次投屏的实时信息。</summary>
    private void UpdateSessionCard()
    {
        var cfg = _host.Settings;
        TxtSessionQuality.Text = _host.AirPlay.SessionCount > 0
            ? _host.AirPlay.Quality.ShortText
            : cfg.QualityPreset switch
            {
                "HighQuality" => "高画质 1080p",
                "Balanced" => "均衡 1080p",
                _ => "低延迟 720p",
            };

        string verdict = _statsData.Verdict;
        TxtSessionHealth.Text = verdict switch
        {
            "ok" => _statsData.HasRatio ? $"流畅 {_statsData.RealTimeRatio:P0}" : "流畅",
            "warn" => "偏慢",
            "bad" => "已停滞",
            _ => "等待画面",
        };
        DotSession.Fill = (System.Windows.Media.Brush)FindResource(verdict switch
        {
            "ok" => "B.Ok",
            "warn" => "B.Warn",
            "bad" => "B.Bad",
            _ => "B.TextFaint",
        });

        TxtSessionTime.Text = _sessionSince is { } since
            ? (DateTime.UtcNow - since).ToString(@"hh\:mm\:ss")
            : "--:--:--";
        TxtSessionSaved.Text = $"截图 {_sessionShots} · 录像 {_sessionRecords}";
    }

    /// <summary>元素淡入（160ms）：状态切换更顺滑，不做夸张动画。</summary>
    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>当前需要用户做的修复动作（决定侧栏按钮该显示什么）。</summary>
    private enum FixKind { None, PrepareNetwork, RestartReceiver }
    private FixKind _fixKind = FixKind.None;

    private async void BtnFixAction_Click(object sender, RoutedEventArgs e)
    {
        if (_fixKind == FixKind.PrepareNetwork) await PrepareNetworkAsync();
        else if (_fixKind == FixKind.RestartReceiver) await RestartReceiverAsync();
    }

    /// <summary>最近的动态（设置窗口的"动态"页会读取）。</summary>
    public string EventsText => _eventBuffer.ToString();

    private void AppendEvent(string message)
    {
        if (_closing) return;
        _eventBuffer.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}\n");
        if (_eventBuffer.Length > 12_000) _eventBuffer.Length = 8_000;
        _eventDirty = true;
    }

    // ------------------------------------------------------------------ 全屏（沉浸 + 悬浮控制条）

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { SetFullscreen(!_fullscreen); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; }
    }

    private void SetFullscreen(bool on)
    {
        if (_fullscreen == on || _closing) return;
        _fullscreen = on;

        if (on)
        {
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            _prevTopmost = Topmost;
            _prevState = WindowState;
            _prevBounds = new Rect(Left, Top, Width, Height);   // 退出全屏要精确恢复到这里

            HeaderBar.Visibility = Visibility.Collapsed;
            StatusBarRow.Visibility = Visibility.Collapsed;
            SideColumnDef.Width = new GridLength(0);
            // 关键：行是固定高度，光隐藏内容仍会各留出 56/34 的空白带，必须把行高归零
            RowHeader.Height = new GridLength(0);
            RowStatus.Height = new GridLength(0);
            // 舞台顶栏也收起来：全屏只留画面，别在画面上留一条工具条
            StageTopBar.Visibility = Visibility.Collapsed;
            StageBarRow.Height = new GridLength(0);
            RootFrame.BorderThickness = new Thickness(0);   // 去掉左右那 1px 描边
            MainArea.Margin = new Thickness(0);
            StageCard.Margin = new Thickness(0);

            // 真·全屏：铺满整个显示器（含任务栏区域），而不是只填工作区
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            TryFillMonitor();

            // 注意：不再自动改成"铺满"。用户明确选过就尊重他的选择；
            // 只有从未选过（首次使用）时，才在全屏时给一次"铺满"的默认建议。
            if (!_fitExplicit && _fit == Interop.CompositionVideoHost.VideoFit.Fit && _zoom <= 1.0)
            {
                _fit = Interop.CompositionVideoHost.VideoFit.Fill;
                ApplyFillMode();
            }

            if (_bar is not null) _bar.Topmost = true;
            ShowBar();
            _cursorTimer.Start();
            if (_host.Settings.PreventSleep) { WindowEffects.SetKeepAwake(true); _keepAwake = true; }
            WindowEffects.SetCursorHidden(false);
            AppendEvent("已进入全屏");
        }
        else
        {
            _cursorTimer.Stop();
            if (_keepAwake) { WindowEffects.SetKeepAwake(false); _keepAwake = false; }
            WindowEffects.SetCursorHidden(false);
            ShowBar();             // 回到窗口模式：控制条继续常驻（取消置顶）
            UpdateWindowedBar();

            WindowState = WindowState.Normal;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            Topmost = _prevTopmost;
            if (_prevBounds.Width > 200 && _prevBounds.Height > 200)
            {
                // 恢复进入全屏前的窗口位置与大小（否则会保持铺满屏幕、盖住任务栏）
                Left = _prevBounds.Left;
                Top = _prevBounds.Top;
                Width = _prevBounds.Width;
                Height = _prevBounds.Height;
            }
            if (_prevState == WindowState.Maximized) WindowState = WindowState.Maximized;

            HeaderBar.Visibility = Visibility.Visible;
            StatusBarRow.Visibility = Visibility.Visible;
            // 侧栏宽度/外边距/舞台间距一律交回密度策略，避免"紧凑模式退出全屏后尺寸跳回默认"
            SideColumnDef.Width = new GridLength(300);
            RowHeader.Height = new GridLength(50);
            RowStatus.Height = new GridLength(32);
            StageTopBar.Visibility = Visibility.Visible;
            StageBarRow.Height = new GridLength(40);
            RootFrame.BorderThickness = new Thickness(1);
            ApplyDensity(_host.Settings.Density);

        }
        // 全屏切换会改窗口大小/位置，布局完成后再把控制条摆正（否则要拖动窗口才归位）
        Dispatcher.BeginInvoke(() => PositionBar(), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() => { PositionBar(); UpdateWindowedBar(); }, DispatcherPriority.ApplicationIdle);
        _ = DelayedRepositionAsync();

        RefreshUi();
    }

    /// <summary>窗口尺寸/位置变化后，稍等一拍再摆一次控制条与浮层（等布局稳定）。</summary>
    private async Task DelayedRepositionAsync()
    {
        await Task.Delay(220);
        if (_closing) return;
        PositionBar();
        PositionStats();
        await Task.Delay(400);
        if (_closing) return;
        PositionBar();
        PositionStats();
    }

    /// <summary>
    /// 把窗口铺满所在显示器的完整边界（覆盖任务栏），实现看片式的沉浸全屏。
    /// 多显示器时优先回到上次全屏用的那块屏。
    /// </summary>
    private void TryFillMonitor()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr monitor = IntPtr.Zero;

            if (TryParseMonitorRect(_host.Settings.FullscreenMonitor, out int mx, out int my))
                monitor = FindMonitorByOrigin(mx, my);
            if (monitor == IntPtr.Zero) monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                Left = info.rcMonitor.Left;
                Top = info.rcMonitor.Top;
                Width = info.rcMonitor.Right - info.rcMonitor.Left;
                Height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                // 记住这次用的是哪块屏（多显示器时下次全屏回到同一块）
                _host.Settings.FullscreenMonitor =
                    $"{info.rcMonitor.Left},{info.rcMonitor.Top},{info.rcMonitor.Right - info.rcMonitor.Left},{info.rcMonitor.Bottom - info.rcMonitor.Top}";
                _host.Settings.Save();
                return;
            }
        }
        catch { }
        WindowState = WindowState.Maximized;
    }

    private static bool TryParseMonitorRect(string text, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    private static IntPtr FindMonitorByOrigin(int x, int y)
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    if (rect.Left == x && rect.Top == y) { found = monitor; return false; }
                    return true;
                }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    // ------------------------------------------------------------------ 窗口位置记忆

    /// <summary>还原上次的窗口位置/大小；离屏（比如拔了显示器）就退回居中。</summary>
    private void RestoreWindowPlacement()
    {
        try
        {
            var s = _host.Settings;
            if (s.WindowWidth is not > 400 || s.WindowHeight is not > 300) return;
            if (s.WindowLeft is null || s.WindowTop is null) return;

            double width = Math.Min(s.WindowWidth.Value, SystemParameters.VirtualScreenWidth);
            double height = Math.Min(s.WindowHeight.Value, SystemParameters.VirtualScreenHeight);
            double left = s.WindowLeft.Value, top = s.WindowTop.Value;

            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                         SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(virtualScreen, new Rect(left, top, width, height));
            if (visible.IsEmpty || visible.Width < 120 || visible.Height < 120) return;   // 完全离屏：不还原

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        catch { }
    }

    /// <summary>
    /// 把初始尺寸限制在工作区内。
    /// 起因：窗口默认 1120×720，而 WPF 的尺寸单位是 DIP —— 1366×768 在 150% 缩放下只有 910×512 DIP，
    /// 1920×1080 在 200% 缩放下只有 960×540 DIP，默认尺寸会整块超出屏幕，右上角关闭键点不到。
    /// 最小尺寸本身也已下调（MinWidth 1024→880 / MinHeight 576→520），否则钳制会被 Min 值顶回去。
    /// </summary>
    private void ClampInitialSizeToWorkArea()
    {
        try
        {
            Rect work = SystemParameters.WorkArea;
            Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, work.Width));
            Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, work.Height));
        }
        catch { }
    }

    /// <summary>退出时记住窗口位置/大小（最大化时记还原后的尺寸）。</summary>
    private void SaveWindowPlacement()
    {
        try
        {
            if (_fullscreen) return;     // 全屏尺寸不算窗口尺寸
            var s = _host.Settings;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.Width < 200 || bounds.Height < 200) return;
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.Save();
        }
        catch { }
    }

    private void ShowBar()
    {
        // 入场动画期间不显示控制条（它是独立的置顶小窗，会飘在动画上面，破坏沉浸感）。
        // 动画收场时再补上 —— 用户明确要求：动画结束后不该再看到任何"等待/加载"的东西。
        if (_introActive)
        {
            _barPendingIntro = true;
            return;
        }

        if (_bar is null)
        {
            _bar = new OverlayBarWindow { Owner = this };
            // 条上只留"这里才有"的动作：截屏 / 录制 / 小窗（全屏时多一个"退出全屏"）。
            // 全屏、适配、缩放都长在舞台顶栏上，条里不再重复一份。
            _bar.ScreenshotRequested += TakeScreenshot;
            _bar.RecordRequested += ToggleRecording;
            _bar.MiniWindowRequested += ToggleMiniWindow;
            _bar.ExitFullscreenRequested += () => SetFullscreen(false);
            // 展开 / 收起会改窗口尺寸：贴底重摆一次，按钮才不会跟着列表一起往上跑
            _bar.LayoutChanged += () =>
            {
                _lastBarActivity = DateTime.UtcNow;
                Dispatcher.BeginInvoke(() => PositionBar(), DispatcherPriority.Loaded);
            };
            if (_host.Settings.AntiCapture) WindowProtection.SetProtection(_bar, true);
        }
        SyncBar();
        _bar.Topmost = _fullscreen;   // 只有全屏才置顶，窗口模式跟随主窗口层级
        _bar.Show();
        PositionBar();
        _lastBarActivity = DateTime.UtcNow;
    }

    private void HideBar()
    {
        try { _bar?.Hide(); } catch { }
    }

    private void SyncBar()
    {
        if (_bar is null) return;
        _bar.SyncState(_fullscreen, CanCapture());
    }

    private void PositionBar()
    {
        if (_bar is null) return;
        try
        {
            if (_fullscreen)
            {
                // 全屏：贴在屏幕底部中间
                var area = SystemParameters.WorkArea;
                _bar.Left = area.Left + (area.Width - _bar.ActualWidth) / 2;
                _bar.Top = area.Bottom - _bar.ActualHeight - 28;
            }
            else
            {
                // 窗口模式：浮在画面区底部（不占布局空间，因为它是独立顶层窗口）
                var topLeft = StageCard.PointToScreen(new Point(0, 0));
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double w = StageCard.ActualWidth * dpi, h = StageCard.ActualHeight * dpi;
                _bar.Left = topLeft.X + (w - _bar.ActualWidth) / 2;
                _bar.Top = topLeft.Y + h - _bar.ActualHeight - 14;
            }
        }
        catch { }
    }

    /// <summary>全屏时：鼠标靠近屏幕底部（或悬停控制条上）才显示，否则自动隐藏以保持沉浸感。</summary>
    private void OnCursorTick()
    {
        if (!_fullscreen || _bar is null) return;
        try
        {
            var area = SystemParameters.WorkArea;
            bool overBar = false;
            if (GetCursorPos(out var p))
            {
                if (_lastCursor.X != p.X || _lastCursor.Y != p.Y) { _lastCursor = p; _lastMouseMove = DateTime.UtcNow; }
                overBar = p.X >= _bar.Left && p.X <= _bar.Left + _bar.ActualWidth
                          && p.Y >= _bar.Top && p.Y <= _bar.Top + _bar.ActualHeight;
                if (overBar || p.Y > area.Bottom - 120) _lastBarActivity = DateTime.UtcNow;
            }
            // 全屏无操作 3 秒后隐藏鼠标指针，动一下就恢复
            if (_host.Settings.HideCursorInFullscreen)
            {
                bool idle = (DateTime.UtcNow - _lastMouseMove).TotalSeconds > 3;
                WindowEffects.SetCursorHidden(idle && !overBar);
            }
            bool shouldShow = (DateTime.UtcNow - _lastBarActivity).TotalSeconds < 2.6;
            if (shouldShow && !_bar.IsVisible) { _bar.Show(); PositionBar(); }
            else if (!shouldShow && _bar.IsVisible && !overBar) _bar.Hide();
        }
        catch { }
    }

    /// <summary>窗口模式：控制条常驻；最小化或被其它程序挡住时隐藏。</summary>
    private void UpdateWindowedBar()
    {
        if (_closing) return;
        bool shouldShow = !_fullscreen && WindowState != WindowState.Minimized;
        try
        {
            if (shouldShow)
            {
                if (!_bar!.IsVisible) _bar.Show();
                PositionBar();
            }
            else if (_bar is not null && _bar.IsVisible) _bar.Hide();
        }
        catch { }
    }



    private void ApplyFillMode() => ApplyFillModeToHosts();

    /// <summary>
    /// 适配方式与缩放只作用于**主画面那一路**；并排的格子一律"适应"、不缩放 ——
    /// 小格子里裁切或放大都只会让人看不清，而且会让"铺满"这个设置的含义含混。
    /// </summary>
    private void ApplyFillModeToHosts()
    {
        try
        {
            Interop.CompositionVideoHost primary = PrimaryHost;
            MiracastHost.Fit = ReferenceEquals(primary, MiracastHost) ? _fit : Interop.CompositionVideoHost.VideoFit.Fit;
            MiracastHost.Zoom = ReferenceEquals(primary, MiracastHost) ? _zoom : 1.0;

            foreach (MultiViewTile tile in _tiles.Values)
            {
                bool isPrimary = ReferenceEquals(primary, tile.Host);
                tile.Host.Fit = isPrimary ? _fit : Interop.CompositionVideoHost.VideoFit.Fit;
                tile.Host.Zoom = isPrimary ? _zoom : 1.0;
            }
        }
        catch { }
    }

    private static string FitName(Interop.CompositionVideoHost.VideoFit fit) => fit switch
    {
        Interop.CompositionVideoHost.VideoFit.Fill => "铺满",
        Interop.CompositionVideoHost.VideoFit.Stretch => "拉伸",
        Interop.CompositionVideoHost.VideoFit.Native => "原始",
        _ => "适应",
    };

    private static Interop.CompositionVideoHost.VideoFit ParseFit(string? name) => name switch
    {
        "Fill" => Interop.CompositionVideoHost.VideoFit.Fill,
        "Stretch" => Interop.CompositionVideoHost.VideoFit.Stretch,
        "Native" => Interop.CompositionVideoHost.VideoFit.Native,
        _ => Interop.CompositionVideoHost.VideoFit.Fit,
    };

    /// <summary>切换适配方式（舞台顶栏与设置窗口共用）。</summary>
    public void SetFit(Interop.CompositionVideoHost.VideoFit fit)
    {
        _fit = fit;
        _fitExplicit = _zoomExplicit = true;   // 用户明确选过，之后任何情况都不再自动改
        _fillMode = fit == Interop.CompositionVideoHost.VideoFit.Fill;
        ApplyFillMode();
        _host.Settings.MirrorFit = fit.ToString();
        _host.Settings.Save();
        TxtHint.Text = $"画面：{FitName(fit)}" + (_zoom != 1.0 ? $" · {_zoom * 100:F0}%" : "") + " · F11 进入全屏";
        UpdateStageChrome();
    }

    /// <summary>设置缩放倍率。</summary>
    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5, 2.0);
        _zoomExplicit = true;
        ApplyFillMode();
        _host.Settings.MirrorZoom = _zoom;
        _host.Settings.Save();
        TxtHint.Text = $"画面：{FitName(_fit)}" + (_zoom != 1.0 ? $" · {_zoom * 100:F0}%" : "") + " · F11 进入全屏";
        UpdateStageChrome();
    }

    /// <summary>舞台顶栏上的"正在看什么/怎么缩放"，跟着适配与缩放实时更新。</summary>
    private void UpdateStageChrome()
    {
        try
        {
            if (BtnStageFit is not null) BtnStageFit.Content = FitName(_fit);
            if (TxtStageZoom is not null) TxtStageZoom.Text = $"{_zoom * 100:F0}%";
        }
        catch { }
    }

    private void BtnStageFit_Click(object sender, RoutedEventArgs e)
    {
        CycleFit();
        _lastBarActivity = DateTime.UtcNow;
    }

    private void BtnStageZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.1);

    private void BtnStageZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.1);

    private void BtnStageFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void CycleFit()
    {
        var next = _fit switch
        {
            Interop.CompositionVideoHost.VideoFit.Fit => Interop.CompositionVideoHost.VideoFit.Fill,
            Interop.CompositionVideoHost.VideoFit.Fill => Interop.CompositionVideoHost.VideoFit.Stretch,
            Interop.CompositionVideoHost.VideoFit.Stretch => Interop.CompositionVideoHost.VideoFit.Native,
            _ => Interop.CompositionVideoHost.VideoFit.Fit,
        };
        SetFit(next);
        TxtStatus.Text = $"画面适配：{FitName(next)}";
    }

    // ------------------------------------------------------------------ 动作

    public async Task PrepareNetworkAsync()
    {
        if (_busyPrepare) return;
        _busyPrepare = true;
        ShowBusy("正在放行网络…", "请在 UAC 弹窗中点「是」");
        try
        {
            var (ok, message) = await Task.Run(() =>
            {
                bool r = NetworkFixer.RequestElevation(out string m);
                return (r, m);
            });
            await PollNetworkAsync();
            _host.Publish(ok ? "网络已放行" : "网络放行未完成");
            if (!ok && message.Length > 0)
                MessageBox.Show(this, message, "网络一键准备", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!_host.Miracast.IsListening) await StartMirrorAsync(showError: false);
        }
        finally
        {
            _busyPrepare = false;
            HideBusy();
            RefreshUi();
        }
    }

    private async void BtnPrepare_Click(object sender, RoutedEventArgs e) => await PrepareNetworkAsync();

    /// <summary>重启接收端：清理可能卡住的无线显示进程后重新监听。</summary>
    public async Task RestartReceiverAsync()
    {
        if (_mirrorBusy) return;
        ShowBusy("正在重启接收端…", "清理卡住的进程后重新监听");
        try
        {
            await StopMirrorAsync();
            var (_, detail) = await Task.Run(MiracastService.RecoverWirelessDisplay);
            AppendEvent(detail);
            await Task.Delay(500);
            await StartMirrorAsync(showError: false);
        }
        finally
        {
            await PollMirrorStatusAsync();
            HideBusy();
        }
    }

    private void BtnLatency_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        s.LowLatency = !s.LowLatency;
        s.Save();
        _host.Miracast.LowLatency = s.LowLatency;
        BtnLatency.Content = s.LowLatency ? "延迟优先" : "画质优先";
        if (_host.Miracast.ConnectionCount > 0) _ = _host.Miracast.ReapplyStreamPreferencesAsync();
        TxtStatus.Text = s.LowLatency ? "已切到延迟优先（重连后完全生效）" : "已切到画质优先（重连后完全生效）";
    }

    private async void SwMirror_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _closing) return;
        if (SwMirror.IsChecked == true) await StartMirrorAsync();
        else await StopMirrorAsync();
        RefreshUi();
    }

    // ------------------------------------------------------------------ 多设备并排（③）· 放大切换（④）

    /// <summary>每路 AirPlay 会话对应的一格（会话 id → 格子）。</summary>
    private readonly Dictionary<string, MultiViewTile> _tiles = new();

    /// <summary>已经挂上画面的会话（挂一次就够；宿主还没建好时要等下一轮再试）。</summary>
    private readonly HashSet<string> _attachedSessions = new();

    /// <summary>上一秒每路的帧数：用来判断这一格还在不在推进（标题栏状态点）。</summary>
    private readonly Dictionary<string, long> _sessionFrameSeen = new();

    /// <summary>这一秒里画面确实在推进的那几路。</summary>
    private readonly HashSet<string> _sessionLive = new();

    /// <summary>上一次看到的会话数与聚焦 id：用来区分"新的一路开始"和"只是换了一路放大"。</summary>
    private int _airPlayCountSeen;
    private string _airPlayFocusSeen = "";

    /// <summary>正在重排画面区（Grid 属性变化会再次触发布局，防重入）。</summary>
    private bool _stageLayoutBusy;

    /// <summary>上一次排布的签名：没变化就不重排，避免每秒清一次行定义。</summary>
    private string _stageSignature = "";

    /// <summary>格子列的基准宽度（按窗口高度不够时会整体等比缩小）。</summary>
    private const double TileBaseWidth = 240;

    /// <summary>
    /// 「主画面」宿主：Miracast 有画面时是 <see cref="MiracastHost"/>，否则是当前被放大的那一路的宿主。
    ///
    /// 截屏 / 录制 / 反截屏 / 状态浮层 / 小窗 / 画质档位统统跟着它走 —— 也就是"跟着当前聚焦的那一路"，
    /// 语义与"多设备时截屏只截聚焦那一路"的约定一致（不把宫格合成一张图，避免动那三条已加固的链路）。
    /// </summary>
    private Interop.CompositionVideoHost PrimaryHost
    {
        get
        {
            if (MiracastPrimary)
            {
                return MiracastHost;
            }

            string? focused = _host.AirPlay.FocusedSessionId;
            if (focused is not null && _tiles.TryGetValue(focused, out MultiViewTile? tile))
            {
                return tile.Host;
            }

            return MiracastHost;
        }
    }

    /// <summary>Miracast（安卓）这一路是不是主画面：它有画面就算（演示模式也算）。</summary>
    private bool MiracastPrimary => MiracastHost.SurfaceBrush is not null || _demoCast;

    /// <summary>会话列表变化（新增 / 结束 / 聚焦变化）时的统一处理。</summary>
    private void OnAirPlaySessionsChanged()
    {
        if (_closing) return;
        try
        {
            List<AirPlayService.AirPlaySession> sessions = _host.AirPlay.Sessions.ToList();
            int count = sessions.Count;
            string focused = _host.AirPlay.FocusedSessionId ?? "";
            bool started = count > 0 && _airPlayCountSeen == 0;
            bool focusChanged = count > 0 && !started && focused.Length > 0 && focused != _airPlayFocusSeen;
            _airPlayCountSeen = count;
            _airPlayFocusSeen = focused;

            // 主画面是哪一台，界面上的设备名/协议就跟着哪一台（多设备时不能只留最后一条）
            AirPlayService.AirPlaySession? focusedSession = focused.Length > 0 ? _host.AirPlay.Find(focused) : null;
            if (focusedSession?.Identity is not null) _activeIdentity = focusedSession.Identity;

            SyncMultiView("会话变化");
            EnsureSessionHostsAttached();
            RefreshTileStates();

            if (started)
            {
                _activeProtocol = "AirPlay";
                ApplyVolumeToHosts();
                AttachMiniPicture();
                ApplyAudioSwitchPreference();
                ResetMetrics();
                _hadCast = true;
                AppendEvent("已开始镜像（AirPlay）");
                ApplyAntiCapture();
                ProbeAntiCaptureAsync();
                if (_host.Settings.AutoFullscreen && !_fullscreen && !_autoFullscreenDone)
                {
                    _autoFullscreenDone = true;
                    SetFullscreen(true);
                }
            }
            else if (focusChanged)
            {
                _activeProtocol = "AirPlay";
                AttachMiniPicture();      // 主画面换了一个宿主：小窗要重新接（旧画刷已随宿主换掉）
                ResetMetrics();
                RefreshMiniState();
            }

            RefreshUi();
        }
        catch (Exception ex)
        {
            _host.Log.Debug("ui", $"并排画面同步异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 重排画面区：主画面占左列，其余各路排右列格子。
    ///
    /// 每一路都有自己的宿主与解码器，所以这里**只改 Grid 行列与尺寸**：
    /// 既不搬动 MediaSource（实测搬动后新宿主会长时间停在 Buffering/黑屏，
    /// 见 tools/ui-spike/move-spike 的结论），也不换父节点（HwndHost 换父节点会销毁重建子窗口）。
    /// </summary>
    private void SyncMultiView(string reason)
    {
        if (_closing || _stageLayoutBusy) return;
        _stageLayoutBusy = true;
        try
        {
            List<AirPlayService.AirPlaySession> sessions = _host.AirPlay.Sessions.ToList();
            var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

            // 1) 已结束的会话：先摘画面，再把格子从视觉树上拿掉（否则会留着一块已经释放的表面）
            foreach (string stale in _tiles.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                MultiViewTile tile = _tiles[stale];
                _tiles.Remove(stale);
                _attachedSessions.Remove(stale);
                _sessionFrameSeen.Remove(stale);
                _sessionLive.Remove(stale);
                try { tile.Host.Detach(); } catch { }
                try { StageLayout.Children.Remove(tile); } catch { }
            }

            // 2) 新会话：先建格子放进视觉树（可见才建得出子窗口），画面等下一次补挂
            bool addedTile = false;
            foreach (AirPlayService.AirPlaySession session in sessions)
            {
                if (_tiles.ContainsKey(session.Id)) continue;
                var tile = new MultiViewTile(session.Id);
                tile.FocusRequested += OnTileFocusRequested;
                _tiles[session.Id] = tile;
                StageLayout.Children.Add(tile);
                addedTile = true;
            }

            // 新格子要等这一轮布局跑完，它的子窗口才存在，画面才挂得上（小窗那次"一片黑"的教训）
            if (addedTile)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    EnsureSessionHostsAttached();
                    SyncMultiView("布局就绪");
                    RefreshTileStates();
                }, DispatcherPriority.Loaded);
            }

            // 3) 谁是主画面：Miracast 有画面就是它，否则是当前聚焦那一路
            string mainId = MiracastPrimary ? "" : (_host.AirPlay.FocusedSessionId ?? "");
            var tileSessions = sessions.Where(s => !string.Equals(s.Id, mainId, StringComparison.Ordinal)).ToList();

            // 只有 1 路（或只有 Miracast）时：右列必须完全收起，否则画面区会白留一条
            bool needsLayout = tileSessions.Count > 0;
            int rows = Math.Max(1, tileSessions.Count + 1);
            double available = StageLayout.ActualHeight > 0 ? StageLayout.ActualHeight : Math.Max(0, ActualHeight);
            double scale = TileScale(tileSessions, available);
            double tileWidth = Math.Max(120, TileBaseWidth * scale);

            // 签名里带上每一路画面的宽高比：解码器报出真实尺寸后格子的高度要跟着变
            var parts = new List<string>(sessions.Count);
            foreach (AirPlayService.AirPlaySession session in sessions)
            {
                string size = _tiles.TryGetValue(session.Id, out MultiViewTile? hostTile)
                    ? $"{hostTile.Host.SurfaceWidth}x{hostTile.Host.SurfaceHeight}"
                    : "0x0";
                parts.Add($"{session.Id}:{size}");
            }
            string signature = string.Join(",", parts) + "|" + mainId + "|" +
                               $"{(int)available}|{(int)tileWidth}|{rows}";
            if (signature == _stageSignature)
            {
                ApplyStageVisibility();
                return;
            }
            _stageSignature = signature;

            // 4) 行：每个格子一行（Auto），最后一行用星号把主画面撑满整块区域
            StageLayout.RowDefinitions.Clear();
            for (int i = 0; i < tileSessions.Count; i++)
            {
                StageLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            StageLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            MiracastHost.SetValue(Grid.ColumnProperty, 0);
            MiracastHost.SetValue(Grid.RowProperty, 0);
            MiracastHost.SetValue(Grid.RowSpanProperty, rows);

            for (int i = 0; i < sessions.Count; i++)
            {
                AirPlayService.AirPlaySession session = sessions[i];
                MultiViewTile tile = _tiles[session.Id];
                bool isMain = string.Equals(session.Id, mainId, StringComparison.Ordinal);
                if (isMain)
                {
                    tile.SetFocused(true);
                    tile.SetValue(Grid.ColumnProperty, 0);
                    tile.SetValue(Grid.RowProperty, 0);
                    tile.SetValue(Grid.RowSpanProperty, rows);
                }
                else
                {
                    int row = tileSessions.FindIndex(s => string.Equals(s.Id, session.Id, StringComparison.Ordinal));
                    tile.SetFocused(false, VideoHeightFor(tile.Host, tileWidth));
                    tile.SetValue(Grid.ColumnProperty, 1);
                    tile.SetValue(Grid.RowProperty, Math.Max(0, row));
                    tile.SetValue(Grid.RowSpanProperty, 1);
                }
            }

            StageTileColumn.Width = needsLayout
                ? new GridLength(tileWidth + MultiViewTile.Gap)
                : new GridLength(0);

            ApplyFillModeToHosts();
            ApplyStageVisibility();
        }
        catch (Exception ex)
        {
            _host.Log.Debug("ui", $"并排布局失败（{reason}）：{ex.Message}");
        }
        finally
        {
            _stageLayoutBusy = false;
        }
    }

    /// <summary>把每一路的画面挂到它自己的宿主上；宿主还没建好（最小化/尚未布局）就等下一个周期。</summary>
    private void EnsureSessionHostsAttached()
    {
        if (_closing) return;
        try
        {
            foreach (AirPlayService.AirPlaySession session in _host.AirPlay.Sessions)
            {
                if (_attachedSessions.Contains(session.Id)) continue;
                if (!_tiles.TryGetValue(session.Id, out MultiViewTile? tile)) continue;

                Interop.CompositionVideoHost host = tile.Host;
                if (!host.IsReady) continue;                 // 子窗口还没建出来：此时挂画面会静默失败
                Windows.Media.Core.MediaSource? source = session.MediaSource;
                if (source is null) continue;

                host.AttachSource(source, _host.AirPlay.VideoWidth, _host.AirPlay.VideoHeight, realTime: true);
                host.Fit = Interop.CompositionVideoHost.VideoFit.Fit;
                host.Volume = _host.Settings.Volume;
                _attachedSessions.Add(session.Id);
                _host.Log.Debug("airplay", $"第 {_host.AirPlay.Sessions.Count} 路画面已挂到自己的宿主上");
            }

            if (_attachedSessions.Count > 0)
            {
                _attachedSessions.RemoveWhere(id => _host.AirPlay.Find(id) is null);
            }
        }
        catch (Exception ex)
        {
            _host.Log.Debug("airplay", $"挂载并排画面异常：{ex.Message}");
        }
    }

    /// <summary>每秒对比一次帧数，得出"这一路还活着"的结论（标题栏状态点用）。</summary>
    private void UpdateSessionLiveness()
    {
        foreach (AirPlayService.AirPlaySession session in _host.AirPlay.Sessions)
        {
            long last = _sessionFrameSeen.TryGetValue(session.Id, out long seen) ? seen : -1;
            bool live = session.FrameCount != last;
            _sessionFrameSeen[session.Id] = session.FrameCount;
            if (live) _sessionLive.Add(session.Id);
            else _sessionLive.Remove(session.Id);
        }
    }

    /// <summary>刷新每一格的标题栏（名字 / 第几路 / 状态点）。</summary>
    private void RefreshTileStates()
    {
        try
        {
            List<AirPlayService.AirPlaySession> sessions = _host.AirPlay.Sessions.ToList();
            if (sessions.Count == 0) return;
            for (int i = 0; i < sessions.Count; i++)
            {
                AirPlayService.AirPlaySession session = sessions[i];
                if (!_tiles.TryGetValue(session.Id, out MultiViewTile? tile)) continue;
                tile.SetState(SessionLabel(session), i + 1, sessions.Count, _sessionLive.Contains(session.Id));
            }
        }
        catch { }
    }

    /// <summary>点某一格＝把这一路放大（④）。</summary>
    private void OnTileFocusRequested(string sessionId)
    {
        try
        {
            if (!_host.AirPlay.FocusSession(sessionId))
            {
                return;
            }

            AppendEvent($"已放大第 {_host.AirPlay.FocusedIndex} 路画面（共 {_host.AirPlay.SessionCount} 路）");
            RefreshMiniState();
        }
        catch (Exception ex)
        {
            _host.Log.Debug("ui", $"切换放大画面失败：{ex.Message}");
        }
    }

    /// <summary>格子标题文字：认出型号就用型号，认不出退回原始名字。</summary>
    private static string SessionLabel(AirPlayService.AirPlaySession session) => session.Label;

    /// <summary>按画面宽高比算格子里的画面高度（比例拿不到时按 16:9）。</summary>
    private static double VideoHeightFor(Interop.CompositionVideoHost host, double width)
    {
        double w = host.SurfaceWidth > 0 ? host.SurfaceWidth : 16;
        double h = host.SurfaceHeight > 0 ? host.SurfaceHeight : 9;
        double ratio = h / w;
        if (ratio is < 0.3 or > 3.5) ratio = 9.0 / 16.0;
        return width * ratio;
    }

    /// <summary>格子整体缩放：窗口太矮时等比缩小，保证每一格都还看得见。</summary>
    private double TileScale(List<AirPlayService.AirPlaySession> tiles, double available)
    {
        if (tiles.Count == 0) return 1.0;
        double total = 0;
        foreach (AirPlayService.AirPlaySession session in tiles)
        {
            Interop.CompositionVideoHost host = _tiles.TryGetValue(session.Id, out MultiViewTile? tile)
                ? tile.Host : MiracastHost;
            total += MultiViewTile.HeaderHeight + VideoHeightFor(host, TileBaseWidth) + MultiViewTile.Gap;
        }

        if (available <= 60 || total <= available) return 1.0;
        return Math.Clamp(available / total, 0.35, 1.0);
    }

    /// <summary>
    /// 每一路宿主什么时候该显示的唯一判据。
    ///
    /// Miracast 宿主额外一条：**没画面时不能显示** —— HwndHost 是空域元素，空着的它是一块黑矩形，
    /// WPF 的待机引导文字盖不住它（老行为就是这个判据，这里保持）。
    /// 并排的各格则相反，**没有画面也要保持可见**：HwndHost 只有参与布局之后才建得出子窗口，
    /// 而画面要等子窗口建好才能挂 —— 收起一个还没出画的格子会让它永远挂不上画面。
    /// </summary>
    private void ApplyStageVisibility()
    {
        try
        {
            // 入场动画期间必须把画面宿主收起来：HwndHost 是**原生子窗口**，
            // 它永远盖在 WPF 内容之上（本项目实测过的空域限制），
            // 不收起来，盖在窗口里那层入场遮罩就会被它挡成一片黑。
            bool hidden = _masked || _introActive || _introNative || WindowState == WindowState.Minimized || _closing;
            bool miracastNeeded = MiracastHost.SurfaceBrush is not null
                                  || _demoCast
                                  || _mirrorStatus.ConnectionCount > 0
                                  || _mirrorStatus.ListeningStatus.Contains("Pending", StringComparison.OrdinalIgnoreCase);
            MiracastHost.Visibility = (!hidden && miracastNeeded) ? Visibility.Visible : Visibility.Collapsed;

            foreach (MultiViewTile tile in _tiles.Values)
            {
                tile.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                tile.Host.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch { }
    }

    /// <summary>音量对每一路都生效（小格子里的声音也是这一路的声音）。</summary>
    private void ApplyVolumeToHosts()
    {
        try
        {
            MiracastHost.Volume = _host.Settings.Volume;
            foreach (MultiViewTile tile in _tiles.Values)
            {
                tile.Host.Volume = _host.Settings.Volume;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 画面上的鼠标（④ 与「电脑控制手机」共用）
    //
    // 这两个功能的鼠标入口是**同一处**：画面是 HwndHost 子窗口，它对命中测试是透过的 ——
    // 鼠标消息既不到控件的 WndProc，也不变成控件的 WPF 事件（实测，见 CompositionInterop 的说明），
    // 只有**窗口级**事件收得到，所以在构造函数里一次性挂在窗口上。
    // 判优先级：点在并排的其它格子上＝切那一路（④）；点在主画面上＝控制手机。

    /// <summary>按下时点在某一格上：抬起时把这一路放大（④）。</summary>
    private string? _pendingTileFocus;

    /// <summary>
    /// 画面上的鼠标按下：点到并排的某一格＝把这一路放大（④）；
    /// 点在主画面上＝记下起点，供"点按 vs 拖动"判断（电脑控制手机）。
    /// </summary>
    /// <summary>
    /// 待机页：鼠标在手机附近移动时，左右直接映射成 -25°…+25° 的转台角度，
    /// 上下给一点视差。移出手机范围就交还给自动摇摆，避免在侧栏上划鼠标手机也乱转。
    /// </summary>
    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        try
        {
            if (StageGuide.Visibility != Visibility.Visible || _masked || WindowState == WindowState.Minimized) return;

            Point p = e.GetPosition(GuideModel);
            double w = Math.Max(1, GuideModel.ActualWidth);
            double h = Math.Max(1, GuideModel.ActualHeight);
            double nx = (p.X / w - 0.5) * 2;
            double ny = (p.Y / h - 0.5) * 2;

            // 稍微放宽 25%：在手机边缘附近也能触发，离开就回自动摇摆
            if (nx is < -1.25 or > 1.25 || ny is < -1.25 or > 1.25)
            {
                GuideModel.ResetPointer();
                return;
            }

            GuideModel.SetPointer(Math.Clamp(nx, -1, 1), Math.Clamp(ny, -1, 1));
        }
        catch { }
    }

    private void OnStagePointerDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            _adbDownPoint = null;

            // 先看是不是点在某个"非主画面"的格子上（那时是切换，不是控制手机）
            foreach ((string id, MultiViewTile tile) in _tiles)
            {
                if (tile.IsMain) continue;
                if (InsideHost(tile.Host, e.GetPosition(tile.Host)))
                {
                    _pendingTileFocus = id;
                    return;
                }
            }

            if (!PhoneControlReady(out _, out _)) return;
            if (!InsideHost(MiracastHost, e.GetPosition(MiracastHost))) return;

            _adbDownPoint = e.GetPosition(MiracastHost);
            _adbDownAt = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>鼠标抬起：格子＝放大该路；主画面＝点按或滑动（拖动）。</summary>
    private void OnStagePointerUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_pendingTileFocus is { } pending)
            {
                _pendingTileFocus = null;
                OnTileFocusRequested(pending);
                return;
            }

            if (_adbDownPoint is not { } down) return;
            _adbDownPoint = null;

            if (!PhoneControlReady(out AndroidInput? input, out AndroidScreen? screen)) return;
            if (!InsideHost(MiracastHost, e.GetPosition(MiracastHost))) return;

            VideoBox box = PrimaryVideoBox();
            Point up = e.GetPosition(MiracastHost);
            HandleAdbClickOrDrag(input!, screen!, box, down, up);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _host.Log.Debug("adb", $"鼠标抬起处理异常：{ex.Message}");
        }
    }

    /// <summary>滚轮：在画面上滚动＝手机上滑动一下。</summary>
    private void OnStagePointerWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            if (!PhoneControlReady(out AndroidInput? input, out AndroidScreen? screen)) return;
            Point at = e.GetPosition(MiracastHost);
            if (!InsideHost(MiracastHost, at)) return;

            HandleAdbScroll(input!, screen!, PrimaryVideoBox(), at, e.Delta / 120);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _host.Log.Debug("adb", $"滚轮处理异常：{ex.Message}");
        }
    }

    /// <summary>某个位置是不是落在这个画面宿主里（宿主尺寸为 0 时一律算"不在"）。</summary>
    private static bool InsideHost(Interop.CompositionVideoHost host, Point point)
        => host.ActualWidth > 1 && host.ActualHeight > 1
           && point.X >= 0 && point.Y >= 0 && point.X <= host.ActualWidth && point.Y <= host.ActualHeight;

    // 注意：下面这两个原来是 ⑧ 用的，现在**保持生效**（状态浮层靠它算画面黑边，
    // 好把浮层放进黑边里、不压住画面）。恢复 ⑧ 时不要重复定义它们。
    /// <summary>主画面在窗口里实际占的那块矩形（按当前适配方式与缩放算）。</summary>
    private VideoBox PrimaryVideoBox()
    {
        double dpi = VisualTreeHelper.GetDpi(MiracastHost).DpiScaleX;
        if (dpi <= 0) dpi = 1;
        return ScreenCoordinateMapper.ComputeVideoBox(
            FitModeOf(_fit), MiracastHost.ActualWidth, MiracastHost.ActualHeight,
            MiracastHost.SurfaceWidth, MiracastHost.SurfaceHeight, _zoom, dpi);
    }

    /// <summary>界面上的适配方式 → 纯数学模块里的枚举。</summary>
    private static VideoFitMode FitModeOf(Interop.CompositionVideoHost.VideoFit fit) => fit switch
    {
        Interop.CompositionVideoHost.VideoFit.Fill => VideoFitMode.Fill,
        Interop.CompositionVideoHost.VideoFit.Stretch => VideoFitMode.Stretch,
        Interop.CompositionVideoHost.VideoFit.Native => VideoFitMode.Native,
        _ => VideoFitMode.Fit,
    };

    // =========================================================================================
    // 电脑控制手机（安卓 · adb）
    //
    // 一条链路：投屏画面告诉我们手机屏幕长什么样 → 鼠标坐标换算成手机坐标（ScreenCoordinateMapper）
    //          → 通过 adb 把点按/滑动/按键/文字注入手机；截屏是反方向（exec-out screencap）。
    //
    // 连接怎么做到"不麻烦"（上一版 ⑧ 就是因为"连接这一步太麻烦"被停用的）：
    //   1) **插着数据线** → `adb devices` 里直接就有，不需要配对（最快的一条路）；
    //   2) 无线调试 → mDNS 自动发现 + 自动连接，用户一个地址都不用填；
    //   3) 从没配对过 → 直接把配对窗口打开（IP 与配对端口已自动填好），只差手机上那 6 位码。
    // =========================================================================================

    private readonly AdbWirelessSession _adb;

    /// <summary>
    /// 「电脑控制手机」是否可用 = 产品里保留了该功能（<see cref="Features.AndroidRemoteControl"/>）
    /// **且**用户在设置里打开了（<c>CastSettings.AdbControlEnabled</c>，默认关闭）。
    ///
    /// 所有会驱动 adb 或显示相关界面的地方都只看这一个属性，
    /// 于是"用户在设置里开/关"就是唯一的开关，且关掉后程序完全不碰 adb。
    /// </summary>
    private bool AdbEnabled => Features.AndroidRemoteControl && _host.Settings.AdbControlEnabled;

    /// <summary>「电脑控制手机」总开关（从设置读，改动即存）。</summary>
    private bool _adbMouseOn;

    /// <summary>键盘直通：键盘上敲的英文/数字直接进手机（中文请用控制条上的输入框）。</summary>
    private bool _phoneKeyboardOn;

    /// <summary>鼠标左键按下的位置（宿主 DIP 坐标）与时刻：用来区分"点击"和"拖动"。</summary>
    private Point? _adbDownPoint;
    private DateTime _adbDownAt;

    /// <summary>自动重连节流（每 5 秒最多查一次设备在不在）。</summary>
    private int _adbPollTicks;

    /// <summary>连接窗口是否开着（避免开出两个）。</summary>
    private AdbConnectWindow? _adbWindow;

    /// <summary>已经提示过一次配对引导（避免每次刷新都弹）。</summary>
    private bool _pairPromptShown;

    private bool _suppressAdbToggle;
    private bool _suppressPhoneKeyboardToggle;

    // 注入**排队**执行：打字过程中按下回车，绝不能出现"先回车、后字"这种顺序错乱
    // （每个注入都是一次独立的 adb 进程，不排队就会各跑各的）。
    private readonly object _phoneInputGate = new();
    private Task _phoneInputChain = Task.CompletedTask;

    /// <summary>键盘直通的字符缓冲：攒一小会儿合成一条 input text（一次 adb 进程几十毫秒，逐字发会明显卡）。</summary>
    private readonly StringBuilder _typingBuffer = new();
    private DispatcherTimer? _typingFlushTimer;
    private bool _typingFlushWired;

    // ------------------------------------------------------------------ 界面刷新

    /// <summary>
    /// 按总开关决定「电脑控制手机」那几块界面出不出现。
    /// 关闭时**整块不占位**（Collapsed 会把它从布局里摘掉，侧栏不会留空档）。
    /// </summary>
    private void ApplyAdbFeatureVisibility()
    {
        if (AdbCard is null || PhoneBar is null || BtnAdbConnect is null) return;
        Visibility visibility = AdbEnabled ? Visibility.Visible : Visibility.Collapsed;
        AdbCard.Visibility = visibility;
        // 外层包一块：只收"电脑控制手机"这一块，**不要**收整个侧栏
        if (AdbSection is not null) AdbSection.Visibility = visibility;
        BtnAdbConnect.Visibility = visibility;
        // 控制条本来就要"连上手机才出现"，关闭时永远收起
        PhoneBar.Visibility = AdbEnabled && _phoneBarShown
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>界面刷新：状态点、状态文字、注入计数、控制条显隐。</summary>
    private void RefreshAdbUi()
    {
        if (_closing || DotAdb is null) return;

        // 功能关闭：三块界面直接不出现，也不再读任何 adb 状态。
        // 用户是"刚把开关关掉"的情况在这里收尾（停注入、停键盘直通、断开 adb），
        // 这样关掉开关就真的不碰手机了，而不是留一条半活的连接。
        if (!AdbEnabled)
        {
            if (_adbMouseOn || _phoneKeyboardOn)
            {
                _adbMouseOn = false;
                _phoneControlRefusedByUser = false;
                StopPhoneKeyboard("「电脑控制手机」已关闭");
                _ = _adb.DisconnectAsync();
                AppendEvent("「电脑控制手机」已关闭（可在 设置 → 安全 里重新打开）");
            }
            ApplyAdbFeatureVisibility();
            return;
        }

        try
        {
            // 用户刚在设置里把开关打开时，这块界面要自己冒出来（下面只改控制条）
            ApplyAdbFeatureVisibility();

            string dot = _adb.State switch
            {
                AdbLinkState.Connected => "B.Ok",
                AdbLinkState.Unsupported => "B.Bad",
                AdbLinkState.Failed => "B.Bad",
                AdbLinkState.Pairing or AdbLinkState.Connecting => "B.Warn",
                _ => "B.TextFaint",
            };
            DotAdb.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
            TxtAdbState.Text = _adb.StatusText;

            AndroidInput? input = _adb.Input;
            int taps = input?.TapCount ?? 0, swipes = input?.SwipeCount ?? 0;
            int keys = input?.KeyCount ?? 0, texts = input?.TextCount ?? 0;
            string counts = $"已发送 {taps + swipes + keys + texts} 次（点按 {taps} · 滑动 {swipes} · 按键 {keys} · 文字 {texts}）";
            if (!_adbMouseOn) counts += " · 开关已关";
            else if (!_adb.IsConnected) counts += " · 未连接手机";
            TxtAdbInjected.Text = counts;

            // 中文能不能发，是用户最先会问的事：连上后就把结论写在这里，别让他试出来
            TxtAdbHint.Text = !_adb.IsConnected
                ? "投屏后在画面上点击＝点按手机；这里还能发文字和系统键"
                : _adb.AdbKeyboardInstalled
                    ? "画面上点击＝点按手机；中文与英文都能直接发到手机"
                    : "画面上点击＝点按手机；英文数字可直接发，中文需要手机装 ADBKeyboard 输入法";

            _suppressAdbToggle = true;
            SwAdbMouse.IsChecked = _adbMouseOn;
            _suppressAdbToggle = false;

            // 控制条：开关开着 + 连上手机 + 主画面是安卓这一路，才出现
            bool barVisible = _adbMouseOn && _adb.IsConnected && MiracastPrimary && !_masked;
            PhoneBar.Visibility = barVisible ? Visibility.Visible : Visibility.Collapsed;
            if (barVisible != _phoneBarShown)
            {
                // 控制条出现/消失会改变画面区高度，浮在画面底部的控制条要跟着挪，
                // 否则两条会叠在一起（实测：文字输入框与「全屏/适应」那条重叠，谁都点不到）
                _phoneBarShown = barVisible;
                Dispatcher.BeginInvoke(() => { PositionBar(); RelayoutToasts(); }, DispatcherPriority.Loaded);
            }
            if (barVisible)
            {
                string name = _adb.Device?.Model ?? _adb.Device?.Serial ?? "手机";
                TxtPhoneBarState.Text = $"{name} · {(_adb.IsUsbConnection ? "数据线" : "无线")}";
            }

            _suppressPhoneKeyboardToggle = true;
            SwPhoneKeyboard.IsChecked = _phoneKeyboardOn && barVisible;
            _suppressPhoneKeyboardToggle = false;

            string adbButtonText = _adb.IsConnected ? "连接设置 / 断开…" : "配对 / 其它手机…";
            BtnAdbConnect.Content = adbButtonText;
            // WCAG 2.5.3：名字跟着可见文字走（两个状态都要能被语音控制叫到）
            System.Windows.Automation.AutomationProperties.SetName(BtnAdbConnect, adbButtonText);

            // 发现了手机但连不上、而且从来没配对过：直接把人领到配对那一步
            // （没配对时手机一定拒绝连接，继续"正在连接→失败"地转圈只会让人以为程序坏了）
            if (_adb.NeedsPairing && !_adb.IsConnected && !_pairPromptShown)
            {
                _pairPromptShown = true;
                AppendEvent("这台手机还没和本机配对过：请在手机上点「无线调试 → 使用配对码配对设备」");
                ShowToast("还差一次配对", "在手机「无线调试」里点「使用配对码配对设备」，把 6 位配对码填进来即可（IP 与端口已自动填好）。只需一次。",
                    "打开配对窗口", () => OpenAdbWindow(focusPairingCode: true));
                OpenAdbWindow(focusPairingCode: true);
            }
            else if (_adb.IsConnected)
            {
                _pairPromptShown = false;
            }

            // 正在自动连手机时，把进度写到底部状态栏：用户盯着画面等，得知道程序在忙什么
            if (_adbMouseOn && !_adb.IsConnected && !_closing && TxtStatus is not null && AnyMirroring)
            {
                TxtStatus.Text = "手机控制：" + _adb.StatusText;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 开关与连接

    private void SwAdbMouse_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAdbToggle || _closing) return;
        if (!AdbEnabled) { _adbMouseOn = false; ApplyAdbFeatureVisibility(); return; }
        _adbMouseOn = SwAdbMouse.IsChecked == true;
        _host.Settings.AdbMouseControl = _adbMouseOn;
        _host.Settings.Save();
        AppendEvent(_adbMouseOn ? "已开启「电脑控制手机」" : "已关闭「电脑控制手机」");

        if (!_adbMouseOn)
        {
            _phoneControlRefusedByUser = true;      // 手动关掉：本次投屏不再自动打开
            StopPhoneKeyboard("控制已关闭");
        }
        else
        {
            _phoneControlRefusedByUser = false;
            if (!_adb.IsConnected)
            {
                // 打开开关就自动找手机（无线）：手机先开「无线调试」，程序一个地址都不用问
                AppendEvent("正在自动发现手机（手机上要打开 设置 → 开发者选项 → 无线调试）…");
                _adbRetryCountdown = 2;
                _adbAttempts = 0;
                _ = TryAutoConnectAdbAsync();
            }
        }

        RefreshAdbUi();
    }

    private void BtnAdbConnect_Click(object sender, RoutedEventArgs e) => OpenAdbWindow();

    private void OpenAdbWindow(bool focusPairingCode = false)
    {
        // 功能关闭：连配对窗口都不该被打开（入口按钮也已隐藏，这里是第二道闸）
        if (!AdbEnabled) return;
        try
        {
            if (_adbWindow is not null)
            {
                _adbWindow.Activate();
                if (focusPairingCode) _adbWindow.FocusPairingCode();
                return;
            }

            _adbWindow = new AdbConnectWindow(_adb, _host.Settings) { Owner = this };
            _adbWindow.Closed += (_, _) =>
            {
                _adbWindow = null;
                RefreshAdbUi();
            };
            _adbWindow.Show();
            if (focusPairingCode) _adbWindow.FocusPairingCode();
        }
        catch (Exception ex)
        {
            _host.Log.Debug("adb", $"打开连接窗口失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 手机一投上来就把「电脑控制手机」准备好：**自动打开开关 + 立刻自动发现并连接**。
    ///
    /// 这是用户明确的诉求："手机端只管投屏，电脑端所有操作都自动，在连上的那一刻就做好"。
    /// 尊重手动选择：用户自己把开关关掉之后，本次投屏就不再自动打开（下次手机重新投上来才会再自动准备）。
    /// </summary>
    private async Task AutoStartPhoneControlAsync()
    {
        if (!Features.AndroidRemoteControl) return;   // 功能已整体下线
        try
        {
            if (_closing) return;
            if (!_host.Settings.AdbAutoConnect)
            {
                _host.Log.Debug("adb", "「投屏时自动连接手机控制」是关的，不自动连手机");
                RefreshAdbUi();
                return;
            }

            if (_phoneControlRefusedByUser)
            {
                // 用户在这次投屏里手动关掉了控制开关：别硬给他打开（下次手机重新投上来才会再自动准备）
                _host.Log.Debug("adb", "用户本次投屏已手动关闭「电脑控制手机」，不再自动打开");
                RefreshAdbUi();
                return;
            }

            if (!_adbMouseOn)
            {
                _adbMouseOn = true;
                _host.Settings.AdbMouseControl = true;
                _host.Settings.Save();
                AppendEvent("已自动开启「电脑控制手机」");
                _host.Log.Info("adb", "手机已投屏：自动开启「电脑控制手机」并开始连手机");
            }

            RefreshAdbUi();
            await TryAutoConnectAdbAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Debug("adb", $"自动准备手机控制失败：{ex.Message}");
        }
    }

    /// <summary>用户在本次投屏里手动关掉了控制开关：本次不再自动打开。</summary>
    private bool _phoneControlRefusedByUser;

    /// <summary>
    /// 自动连一次手机（**无线**）。顺序：mDNS 自动发现 → 记过的地址兜底。
    ///
    /// 为什么把发现放在前面：无线调试的**端口每次都会变**，记过的那个多半已经过期；
    /// 先去连一个过期端口要等到 TCP 超时（几秒），而 `adb mdns services` 是查本机缓存，通常几百毫秒。
    /// </summary>
    private async Task TryAutoConnectAdbAsync()
    {
        if (!AdbEnabled) return;   // 功能关闭：不起 adb、不做发现
        if (_adbAutoConnecting) return;
        _adbAutoConnecting = true;
        try
        {
            if (_closing || _adb.IsConnected || _adb.Client is null) return;
            if (_adb.State is AdbLinkState.Connecting or AdbLinkState.Pairing) return;
            if (!_adbMouseOn || !_host.Settings.AdbAutoConnect) return;

            // 1) mDNS 自动发现 + 连接（拿的是手机此刻真实的端口，用户什么都不用填）
            await _adb.AutoConnectAsync();
            if (_adb.IsConnected) return;

            // 2) 兜底：设置里记过的地址（手机上关过又开了无线调试时，端口变了，这一步多半也没用）
            string host = _host.Settings.AdbHost;
            bool hasPort = int.TryParse(_host.Settings.AdbConnectPort, out int port) && port > 0;
            if (host.Length > 0 && hasPort)
            {
                await _adb.ConnectAsync(host, port);
            }
        }
        catch (Exception ex)
        {
            _host.Log.Debug("adb", $"自动连接失败：{ex.Message}");
        }
        finally
        {
            _adbAutoConnecting = false;
            if (!_closing) RefreshAdbUi();
        }
    }

    /// <summary>自动连接是否正在跑（别让定时器和投屏事件叠着跑两份）。</summary>
    private bool _adbAutoConnecting;

    /// <summary>「电脑控制手机」控制条当前是不是显示着（变化时要把浮动控制条挪一下）。</summary>
    private bool _phoneBarShown;

    private int _adbRetryCountdown = 2;
    private int _adbAttempts;

    /// <summary>
    /// 还没连上时的重试节奏：**刚投上来的那几秒最要紧**（用户就盯着画面看能不能用），每 2 秒试一次；
    /// 试了几次还不成（多半是手机端没开「无线调试」）就退回 10 秒一次，别一直占着 adb。
    /// 没在投屏时不重试 —— 手机没投上来的时候连它没有意义。
    /// </summary>
    private void TickPhoneControl()
    {
        if (_closing || _adbAutoConnecting) return;
        if (!AdbEnabled) return;   // 功能关闭：不重试、不碰 adb

        if (!_adbMouseOn || _adb.IsConnected || !_host.Settings.AdbAutoConnect)
        {
            _adbRetryCountdown = 2;
            _adbAttempts = 0;
            return;
        }

        if (!AnyMirroring && !_demoCast) return;

        if (--_adbRetryCountdown > 0) return;
        _adbAttempts++;
        // 前几次 2 秒一次（刚投上来那几秒最要紧），之后 5 秒一次。
        // 不给到 10 秒以上：用户很可能在投屏之后才去手机上打开「无线调试」，
        // 那之后最多等 5 秒就连上，不用让人以为程序没反应。
        _adbRetryCountdown = _adbAttempts <= 4 ? 2 : 5;
        _ = TryAutoConnectAdbAsync();
    }

    /// <summary>「电脑控制手机」当前是不是可用（开关开着、adb 连着、主画面是安卓这一路）。</summary>
    private bool PhoneControlReady(out AndroidInput? input, out AndroidScreen? screen)
    {
        input = null;
        screen = null;

        if (!AdbEnabled) return false;   // 功能关闭
        if (_closing || _masked) return false;
        if (!_adbMouseOn) { LogPhoneIgnored("开关没开"); return false; }
        if (!MiracastPrimary) { LogPhoneIgnored("当前主画面不是安卓这一路（AirPlay 不提供控制）"); return false; }

        input = _adb.Input;
        if (input is null || !_adb.IsConnected) { LogPhoneIgnored("还没连上手机：" + _adb.StatusText); return false; }

        screen = _adb.Screen;
        if (screen is null || !screen.IsValid) { LogPhoneIgnored("还没拿到手机屏幕尺寸"); return false; }
        if (MiracastHost.SurfaceBrush is null) { LogPhoneIgnored("画面还没出来"); return false; }

        return true;
    }

    private DateTime _phoneIgnoredLoggedAt = DateTime.MinValue;
    private string _phoneIgnoredReason = "";

    /// <summary>
    /// 「点了没反应」要能查到原因：把被拦下的原因写进日志与状态栏
    /// （同一原因 10 秒内只写一条，避免刷屏）。
    /// </summary>
    private void LogPhoneIgnored(string reason)
    {
        if (reason == _phoneIgnoredReason && (DateTime.UtcNow - _phoneIgnoredLoggedAt).TotalSeconds < 10)
        {
            return;
        }

        _phoneIgnoredReason = reason;
        _phoneIgnoredLoggedAt = DateTime.UtcNow;
        _host.Log.Info("adb", "画面上的动作被忽略：" + reason);
        if (!_closing && TxtStatus is not null)
        {
            TxtStatus.Text = "没生效：" + reason;
        }
    }

    /// <summary>把一次注入排进队列执行（顺序有保证；异常只记日志，不打断界面）。</summary>
    private void EnqueuePhoneInput(Action<AndroidInput> action)
    {
        lock (_phoneInputGate)
        {
            _phoneInputChain = _phoneInputChain.ContinueWith(_ =>
            {
                try
                {
                    if (_adb.Input is { } input) action(input);
                }
                catch (Exception ex)
                {
                    _host.Log.Debug("adb", $"注入失败：{ex.Message}");
                }
            }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ 画面上的鼠标 → 手机触摸

    private void HandleAdbClickOrDrag(AndroidInput input, AndroidScreen screen, VideoBox box, Point downPoint, Point upPoint)
    {
        ScreenMapResult up = ScreenCoordinateMapper.Map(upPoint.X, upPoint.Y, box, screen, MiracastHost.SurfaceWidth, MiracastHost.SurfaceHeight);
        if (!up.Inside)
        {
            return;                     // 落在黑边上：不动手机
        }

        // 按下点到抬起点距离很小 → 点按；拖了一段 → 滑动（拖动/长按拖动都走这条）
        ScreenMapResult from = ScreenCoordinateMapper.Map(downPoint.X, downPoint.Y, box, screen, MiracastHost.SurfaceWidth, MiracastHost.SurfaceHeight);
        double distance = Math.Sqrt(Math.Pow(upPoint.X - downPoint.X, 2) + Math.Pow(upPoint.Y - downPoint.Y, 2));
        if (from.Inside && distance >= 8)
        {
            int duration = (int)Math.Clamp((DateTime.UtcNow - _adbDownAt).TotalMilliseconds, 120, 1200);
            int fx = from.PhoneX, fy = from.PhoneY, ux = up.PhoneX, uy = up.PhoneY;
            EnqueuePhoneInput(i => i.Swipe(fx, fy, ux, uy, duration));
            TxtStatus.Text = $"已滑动：手机 ({fx},{fy}) → ({ux},{uy})";
            _host.Log.Info("adb", $"鼠标拖动 → 手机滑动 ({fx},{fy})→({ux},{uy})");
            RefreshAdbUi();
            return;
        }

        int x = up.PhoneX, y = up.PhoneY;
        EnqueuePhoneInput(i => i.Tap(x, y));
        TxtStatus.Text = $"已点按：手机 ({x},{y})" + (up.Note is null ? "" : " · " + up.Note);
        _host.Log.Info("adb", $"鼠标点击 → 手机点按 ({x},{y})，画面坐标 u={up.U:F3} v={up.V:F3}");
        RefreshAdbUi();
    }

    private void HandleAdbScroll(AndroidInput input, AndroidScreen screen, VideoBox box, Point at, int notches)
    {
        if (notches == 0) return;
        ScreenMapResult point = ScreenCoordinateMapper.Map(at.X, at.Y, box, screen, MiracastHost.SurfaceWidth, MiracastHost.SurfaceHeight);
        if (!point.Inside) return;

        (int dx, int dy) = ScreenCoordinateMapper.ScrollVector(notches, screen);
        int fromX = point.PhoneX, fromY = point.PhoneY;
        int toX = Math.Clamp(fromX + dx, 0, screen.CurrentWidth - 1);
        int toY = Math.Clamp(fromY + dy, 0, screen.CurrentHeight - 1);
        int steps = Math.Clamp(Math.Abs(notches), 1, 5);
        EnqueuePhoneInput(i =>
        {
            for (int step = 0; step < steps; step++)
            {
                i.Swipe(fromX, fromY, toX, toY, 160);
            }
        });
        TxtStatus.Text = $"已滚动：手机 ({fromX},{fromY}) → ({toX},{toY})";
        _host.Log.Info("adb", $"鼠标滚轮 → 手机滑动 ({fromX},{fromY})→({toX},{toY})");
        RefreshAdbUi();
    }

    // ------------------------------------------------------------------ 系统键

    private void BtnPhoneKey_Click(object sender, RoutedEventArgs e)
    {
        if (!PhoneControlReady(out AndroidInput? input, out _)) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse(tag, ignoreCase: true, out AndroidKey key)) return;

        AndroidInput ready = input!;
        EnqueuePhoneInput(i => i.Key(key));
        TxtStatus.Text = $"已按下手机上的：{AndroidKeys.Label(key)}";
        _host.Log.Info("adb", $"按键 → {AndroidKeys.Label(key)}（{AndroidKeys.Wire(key)}）");
        RefreshAdbUi();
    }

    // ------------------------------------------------------------------ 文字（英文数字直发；中文要 ADBKeyboard）

    private void TxtPhoneText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        _ = SendPhoneTextAsync(TxtPhoneText.Text);
    }

    private async void BtnPhoneSend_Click(object sender, RoutedEventArgs e)
        => await SendPhoneTextAsync(TxtPhoneText.Text);

    private async Task SendPhoneTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!PhoneControlReady(out AndroidInput? input, out _)) return;

        try
        {
            AndroidTextPlan plan = await _adb.BuildTextPlanAsync(text);
            if (!plan.Ok)
            {
                // 送不了就明说原因（中文要装 ADBKeyboard），绝不假装成功、也不静默丢字
                TxtStatus.Text = "文字没有发出去：" + plan.Note;
                ShowToast("这段文字发不到手机", plan.Note, null, null, ok: false);
                return;
            }

            AndroidInput ready = input!;
            await Task.Run(() => ready.SendText(plan));
            if (ready.LastResult is { Ok: true })
            {
                TxtPhoneText.Clear();
                TxtStatus.Text = $"已发送到手机（{plan.ModeLabel}）";
                _host.Log.Info("adb", $"文字已发送到手机（{plan.ModeLabel}，{plan.Commands.Count} 条命令）");
            }
            else
            {
                TxtStatus.Text = "发送失败：" + (ready.LastResult?.Text ?? "（无输出）");
            }

            RefreshAdbUi();
        }
        catch (Exception ex)
        {
            _host.Log.Warn("adb", $"发送文字失败：{ex.Message}");
            TxtStatus.Text = "发送失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 手机截屏（加密入库）

    private async void BtnPhoneShot_Click(object sender, RoutedEventArgs e)
    {
        if (!PhoneControlReady(out _, out _)) return;
        BtnPhoneShot.IsEnabled = false;
        try
        {
            AndroidCapture capture = await _adb.ScreenshotAsync();
            if (!capture.Ok)
            {
                TxtStatus.Text = capture.Describe();
                ShowToast("没能截到手机的屏", capture.Error ?? "未知原因", null, null, ok: false);
                return;
            }

            // 与电脑截屏同一套归属流程：只需要账号（公钥），**不需要密码**；查看时才要密码
            if (!EnsureCaptureAccount()) return;
            var saved = _capture.SavePhoneScreenshot(capture.Png!, capture.Width, capture.Height);
            if (!saved.Ok)
            {
                ShowToast("入库失败", saved.Error ?? "未知原因", null, null, ok: false);
                return;
            }

            _sessionShots++;
            TxtStatus.Text = capture.Describe() + " · 已加密入库";
            ShowToast("手机截屏已保存", capture.Describe() + "：加密存进「我的库」，查看要账号密码。",
                "打开我的库", OpenVault);
            _host.Publish("手机截屏已加密入库");
            _host.Log.Event("adb", $"手机截屏已加密入库（{capture.Width}×{capture.Height}）");
            RefreshUi();
        }
        catch (Exception ex)
        {
            _host.Log.Warn("adb", $"手机截屏失败：{ex.Message}");
            TxtStatus.Text = "手机截屏失败：" + ex.Message;
        }
        finally
        {
            BtnPhoneShot.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ 键盘直通

    private void SwPhoneKeyboard_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPhoneKeyboardToggle || _closing) return;
        if (!AdbEnabled) { SetPhoneKeyboardFlag(false); return; }

        if (SwPhoneKeyboard.IsChecked != true)
        {
            StopPhoneKeyboard("已关闭键盘直通");
            return;
        }

        if (!PhoneControlReady(out _, out _))
        {
            SetPhoneKeyboardFlag(false);
            TxtStatus.Text = "键盘直通要先连上手机";
            return;
        }

        SetPhoneKeyboardFlag(true);
        AppendEvent("键盘直通已开启：敲的字直接进手机（英文数字；回车＝确认、退格＝删除、Esc＝返回；中文用控制条的输入框）");
        TxtStatus.Text = "键盘直通已开启：现在敲的字直接进手机（Ctrl+Alt+K 可关闭）";
    }

    private void SetPhoneKeyboardFlag(bool on)
    {
        _phoneKeyboardOn = on;
        _suppressPhoneKeyboardToggle = true;
        SwPhoneKeyboard.IsChecked = on;
        _suppressPhoneKeyboardToggle = false;
    }

    private void StopPhoneKeyboard(string why)
    {
        FlushTyping();
        if (_phoneKeyboardOn) AppendEvent(why);
        SetPhoneKeyboardFlag(false);
    }

    /// <summary>键盘直通当前生不生效（别跟程序自己的输入框/密码框抢键）。</summary>
    private bool PhoneKeyboardActive()
    {
        if (!_phoneKeyboardOn || !_adbMouseOn || _closing) return false;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;
        if (Keyboard.FocusedElement is PasswordBox) return false;
        return true;
    }

    private void OnStageKeyDown(object sender, KeyEventArgs e)
    {
        // 功能关闭时 Ctrl+Alt+K 完全不存在：不抢这个键，也不提示"要先连上手机"
        if (!AdbEnabled) return;

        // Ctrl+Alt+K：随时开关「键盘直通」。
        // 必须有这么一个明确的出口：直通开着时 Esc 被当成手机的"返回"，界面上就只剩鼠标能关它。
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            if (_phoneKeyboardOn)
            {
                StopPhoneKeyboard("已关闭键盘直通（Ctrl+Alt+K）");
                TxtStatus.Text = "键盘直通已关闭";
            }
            else if (PhoneControlReady(out _, out _))
            {
                SetPhoneKeyboardFlag(true);
                AppendEvent("键盘直通已开启（Ctrl+Alt+K）");
                TxtStatus.Text = "键盘直通已开启：现在敲的字直接进手机";
            }
            else
            {
                TxtStatus.Text = "键盘直通要先连上手机";
            }

            return;
        }

        if (!PhoneKeyboardActive()) return;

        // 带 Ctrl/Alt 的组合留给程序自己（Ctrl+Alt+F 全屏、Ctrl+Alt+K 关直通…）
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

        // 1) 特殊键：立即发（发之前先把攒着的字冲出去，顺序才不会乱）
        if (AndroidKeys.FromVirtualKeyName(e.Key.ToString()) is { } key)
        {
            e.Handled = true;
            FlushTyping();
            EnqueuePhoneInput(i => i.Key(key));
            TxtStatus.Text = $"已按下手机上的：{AndroidKeys.Label(key)}";
            return;
        }

        // 2) 可打印字符：自己查表（不依赖"焦点在能收文字的控件上"）
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (AndroidText.CharFromVirtualKeyName(e.Key.ToString(), shift) is { } ch)
        {
            e.Handled = true;
            QueueTypedChar(ch);
        }
    }

    /// <summary>攒字符：短暂停顿或攒够一批就发一条 input text（一次 adb 进程几十毫秒，逐字发会卡）。</summary>
    private void QueueTypedChar(char ch)
    {
        _typingBuffer.Append(ch);

        _typingFlushTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        if (!_typingFlushWired)
        {
            _typingFlushWired = true;
            _typingFlushTimer.Tick += (_, _) => FlushTyping();
        }

        _typingFlushTimer.Stop();
        _typingFlushTimer.Start();

        if (_typingBuffer.Length >= 60) FlushTyping();
    }

    /// <summary>把攒下的字符合成一条命令发出去（送不了的字符会如实说，不静默丢）。</summary>
    private void FlushTyping()
    {
        _typingFlushTimer?.Stop();
        if (_typingBuffer.Length == 0) return;

        string typed = _typingBuffer.ToString();
        _typingBuffer.Clear();

        if (!PhoneControlReady(out AndroidInput? input, out _)) return;

        AndroidTextPlan plan = AndroidText.BuildPlan(typed, _adb.AdbKeyboardInstalled);
        if (!plan.Ok)
        {
            TxtStatus.Text = "键盘直通：这段字符发不出去 —— " + plan.Note;
            return;
        }

        AndroidInput ready = input!;
        EnqueuePhoneInput(i => i.SendText(plan));
        TxtStatus.Text = $"键盘直通：已发送 {typed.Length} 个字符";
    }

    // ------------------------------------------------------------------ 小窗（画中画 / 最小化继续看）

    private MiniWindow? _mini;

    /// <summary>小窗是否由「主窗口最小化」自动触发；主窗口恢复时随之收起。</summary>
    private bool _miniAuto;

    /// <summary>当前这路投屏用的协议名（小窗状态文字用）。</summary>
    private string _activeProtocol = "";

    /// <summary>识别出的设备信息（⑦）。认不出就为 null，界面退化成显示原始名字。</summary>
    private DeviceIdentity? _activeIdentity;

    /// <summary>
    /// 记下识别结果并同步到界面。同一个设备重复上报（比如重连）时不重复刷提示。
    /// </summary>
    private void SetActiveIdentity(DeviceIdentity identity)
    {
        string described = identity.Describe();
        bool changed = _activeIdentity?.Describe() != described;
        _activeIdentity = identity;

        if (changed)
        {
            AppendEvent("识别到设备：" + described);
        }

        RefreshMiniState();
        RefreshUi();
    }

    /// <summary>当前是否有一路画面在跑（不区分来自 Miracast 还是 AirPlay；多设备时看主画面那一路）。</summary>
    private bool IsMirroringActive => PrimaryHost.SurfaceBrush is not null;

    /// <summary>
    /// 截图 / 录制 / 反截屏要抓的那个画面宿主。
    ///
    /// 多设备并排时每路画面各有自己的子窗口，这三条链路按约定**跟着当前聚焦的那一路**走：
    /// 所以这里返回的就是主画面宿主（<see cref="PrimaryHost"/>）。
    /// 刻意不把宫格合成一张图：那要引入帧服务器自绘路径，会动到截屏、录制、反截屏三条
    /// 已经加固过的链路，代价与风险都不划算。
    ///
    /// 实测依据（tools/ui-spike/move-spike）：对任意一路的宿主子窗口做 PrintWindow
    /// 都能拿到真实画面像素（1020/1023 采样点为亮），与主宿主完全同一条路径。
    /// </summary>
    private Interop.CompositionVideoHost CaptureHost => PrimaryHost;

    /// <summary>
    /// 界面判断"是否在投屏"的统一入口：Miracast 会话、AirPlay 会话、演示模式都算。
    ///
    /// 之前这里只看了 Miracast 的连接数，于是 AirPlay 投屏时主窗口的画面被判成"没在镜像"
    /// 而收起来（画面其实是有的），状态浮层也一直不显示 —— 正是这个 bug 让 AirPlay 的画面看不见。
    /// </summary>
    private bool AnyMirroring =>
        _mirrorStatus.ConnectionCount > 0
        || _host.AirPlay.SessionCount > 0
        || _demoCast;

    /// <summary>悬浮条上的「小窗」按钮：开着就关，关着就开（手动 = 画中画，不怕主窗口恢复）。</summary>
    private void ToggleMiniWindow()
    {
        if (_mini is not null)
        {
            _miniAuto = false;
            HideMiniWindow();
        }
        else
        {
            ShowMiniWindow(auto: false);
        }
    }

    /// <summary>
    /// 显示小窗。<paramref name="auto"/> = true 表示因主窗口最小化而自动弹出（恢复时收起）；
    /// 手动打开则一直保留，直到用户自己关掉。
    /// </summary>
    private void ShowMiniWindow(bool auto)
    {
        if (_mini is null)
        {
            CreateMiniWindow();
        }

        _miniAuto = auto;
        _mini?.SetAutoShown(auto);

        // 顺序很重要：必须先 Show，窗口的 BuildWindowCore 才会跑出合成器，
        // 之后 MirrorFrom 才有地方挂画面。反过来做会静默失败 —— 小窗出来是一片黑。
        if (_mini is not null && !_mini.IsVisible)
        {
            _mini.Show();
        }

        AttachMiniPicture();
        UpdateBarMiniState();
    }

    private void CreateMiniWindow()
    {
        var window = new MiniWindow
        {
            Width = Math.Max(220, _host.Settings.MiniWidth),
            Height = Math.Max(150, _host.Settings.MiniHeight),
            Pinned = _host.Settings.MiniPinned,
        };

        if (!double.IsNaN(_host.Settings.MiniLeft) && !double.IsNaN(_host.Settings.MiniTop))
        {
            window.Left = _host.Settings.MiniLeft;
            window.Top = _host.Settings.MiniTop;
        }
        else
        {
            // 默认落在主窗口右侧下方，尽量不压住主画面
            Rect work = SystemParameters.WorkArea;
            window.Left = Math.Max(work.Left + 24, Math.Min(work.Right - window.Width - 24, Left + Width - window.Width - 48));
            window.Top = Math.Max(work.Top + 24, Math.Min(work.Bottom - window.Height - 24, Top + 96));
        }
        window.EnsureOnScreen();

        // HwndHost 的子窗口与合成器要等布局才建好，Show 之后可能还没就绪，
        // 所以在 Loaded 之后再补接一次画面（接不上只是没画面，不会报错）。
        window.Loaded += (_, _) => Dispatcher.BeginInvoke(AttachMiniPicture, DispatcherPriority.Loaded);

        window.ReturnToMainRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _miniAuto = false;
            HideMiniWindow();
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });

        window.ScreenshotRequested += () => Dispatcher.BeginInvoke(TakeScreenshot);

        // 多设备切换（④）：在小窗里轮换呈现哪一路
        window.SwitchDeviceRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _host.AirPlay.CycleFocus();
            RefreshMiniState();
        });

        window.Dismissed += () => Dispatcher.BeginInvoke(() =>
        {
            // 自动弹出的小窗被用户关掉 = 「我不想最小化就弹小窗」。
            // 以前这里只隐藏小窗、不动设置，于是下次一最小化它又弹回来 ——
            // 用户唯一的出路是退出程序。现在把这份意愿记进设置（设置 → 画面 里有同名开关）。
            bool wasAuto = _miniAuto;
            bool closedWhileMinimized = WindowState == WindowState.Minimized;
            _miniAuto = false;
            HideMiniWindow();

            // 判据用「自动弹出的」或「主窗口正最小化时关掉的」两者之一：
            // 后者是为了不依赖 _miniAuto 的时序（只要你在最小化状态下把小窗关掉，
            // 意思就很明确：最小化时不要给我弹小窗）。
            if ((wasAuto || closedWhileMinimized) && _host.Settings.MiniOnMinimize)
            {
                _host.Settings.MiniOnMinimize = false;
                _host.Settings.Save();
                // 此刻主窗口通常正最小化：提示条是按主窗口坐标摆的，这时会摆到屏幕外看不见，
                // 所以这里走托盘气泡（最小化时也看得见）。
                _tray?.Balloon("已关闭「最小化时自动显示小窗」",
                    "以后最小化主窗口不再自动弹小窗。需要时点悬浮控制条上的「小窗」，或在 设置 → 画面 里重新打开。");
            }

            // 不再强制把主窗口叫回来：最小化是用户自己的选择，关小窗就只是关小窗。
            // （主窗口最小化时仍可从任务栏或托盘图标恢复。）
        });

        window.PinChanged += pinned =>
        {
            _host.Settings.MiniPinned = pinned;
            _host.Settings.Save();
        };

        window.PlacementChanged += (left, top, width, height) =>
        {
            _host.Settings.MiniLeft = left;
            _host.Settings.MiniTop = top;
            _host.Settings.MiniWidth = width;
            _host.Settings.MiniHeight = height;
            _host.Settings.Save();
        };

        _mini = window;
        UpdateBarMiniState();
    }

    private void HideMiniWindow()
    {
        MiniWindow? window = _mini;
        _mini = null;
        if (window is not null)
        {
            try { window.FadeOutAndClose(); }
            catch { try { window.Close(); } catch { } }
        }
        UpdateBarMiniState();
    }

    /// <summary>
    /// 把小窗接到主画面上（共享同一块画刷，不额外解码）。
    /// 主画面换了宿主（多设备切换放大哪一路）之后必须重新接一次——旧宿主那块的画刷已经不同了。
    /// </summary>
    private void AttachMiniPicture()
    {
        if (_mini is null)
        {
            return;
        }

        Interop.CompositionVideoHost primary = PrimaryHost;
        if (primary.SurfaceBrush is null)
        {
            // 一路画面都没有了：小窗留着只会是一片黑
            HideMiniWindow();
            return;
        }

        bool ok = _mini.Host.MirrorFrom(primary);
        _mini.SetCaptureEnabled(ok);
        RefreshMiniState();
    }

    private void RefreshMiniState()
    {
        if (_mini is null)
        {
            return;
        }

        // 优先用识别出来的型号；认不出才退回原始设备名，最后才用本机名兜底
        string device = _activeIdentity?.ShortLabel()
                        ?? (string.IsNullOrWhiteSpace(_currentTransmitterName)
                            ? _host.Settings.DeviceName
                            : _currentTransmitterName);
        string protocol = _activeProtocol.Length > 0 ? _activeProtocol : "投屏";

        // 多台设备同时投屏时把路数写出来，否则用户会以为"只连上了一台"
        int sessions = _host.AirPlay.SessionCount;
        int focusIndex = _host.AirPlay.FocusedIndex;
        string suffix = sessions > 1 ? $" · {focusIndex}/{sessions}" : "";
        _mini.SetStateText($"{device} · {protocol}{suffix}");
        _mini.SetSessionSwitching(sessions > 1, focusIndex, sessions);
    }

    private void UpdateBarMiniState() => _bar?.SetMiniActive(_mini is not null);

    // ------------------------------------------------------------------ 音频输出自动切换（⑥）

    /// <summary>
    /// 投屏开始时按设置切换音频输出。
    /// 失败只记日志 —— 切不了音频设备不该拖累投屏本身。
    /// </summary>
    private void ApplyAudioSwitchPreference()
    {
        try
        {
            if (!_host.Settings.AudioSwitchEnabled)
            {
                return;
            }

            if (_host.Audio.Apply(_host.Settings.AudioSwitchTargetId, _host.Settings.AudioSwitchRestore))
            {
                AppendEvent("已切换音频输出设备");
            }
        }
        catch (Exception ex)
        {
            _host.Log.Warn("audio", "切换音频输出异常：" + ex.Message);
        }
    }

    /// <summary>投屏结束时还原音频输出（只有真的切过才还）。</summary>
    private void RestoreAudioSwitch()
    {
        try
        {
            if (_host.Audio.Restore())
            {
                AppendEvent("已还原音频输出设备");
            }
        }
        catch (Exception ex)
        {
            _host.Log.Warn("audio", "还原音频输出异常：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 码率 / 延迟图表（⑤）

    private MirrorMetrics? _metrics;
    private long _lastBytesReceived;
    private DateTime _lastMetricsAt = DateTime.UtcNow;

    /// <summary>
    /// 每次界面刷新（1 秒）采一个点。
    /// 码率的来源按协议区分，且**含义不同**：
    /// AirPlay 是实测吞吐（收到的字节 ÷ 实际经过时间），Miracast 是平台协商值。
    /// </summary>
    private void UpdateMetricsChart()
    {
        if (_stats is null)
        {
            return;
        }

        _metrics ??= new MirrorMetrics();

        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _lastMetricsAt).TotalSeconds;
        _lastMetricsAt = now;

        double kbps;
        long bytes = _host.AirPlay.IsRunning ? _host.AirPlay.BytesReceived : 0;
        if (bytes > 0)
        {
            long delta = bytes - _lastBytesReceived;
            _lastBytesReceived = bytes;
            kbps = MirrorMetrics.ToKbps(delta, elapsed);
        }
        else
        {
            // AirPlay 没在收流（或还没开始）时退回平台协商值；拿不到就是 0
            kbps = _host.Miracast.CurrentBitrateKbps;
        }

        _metrics.Add(kbps, _statsData.LagSeconds * 1000);

        string kbpsText = kbps >= 1000 ? $"{kbps / 1000:F1}Mb" : $"{kbps:F0}kb";
        _stats.UpdateCharts(_metrics.Kbps, _metrics.LagMs, kbpsText, $"{_statsData.LagSeconds:F1}s");
    }

    /// <summary>换源/新会话时清空曲线，避免把上一次投屏的数据和新会话接成一条线。</summary>
    private void ResetMetrics()
    {
        _metrics?.Reset();
        _lastBytesReceived = _host.AirPlay.BytesReceived;
        _lastMetricsAt = DateTime.UtcNow;
    }

    private async Task StartMirrorAsync(bool showError = true)
    {
        _mirrorBusy = true;
        _busyText = "正在开启接收端…";
        RefreshUi();
        try
        {
            var result = await _host.StartMiracastAsync();
            if (!result.Success)
            {
                if (showError)
                    MessageBox.Show(this, result.Message + "\n\n" + result.Detail, "接收端启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else AppendEvent("接收端启动失败：" + result.Message);
            }

            // AirPlay 与 Miracast 并行：安卓走 Miracast，iPhone/iPad 走 AirPlay，互不影响
            if (_host.Settings.AutoStartAirPlay) _host.StartAirPlay();
        }
        finally
        {
            _mirrorBusy = false;
            await PollMirrorStatusAsync();
            RefreshUi();
        }
    }

    private async Task StopMirrorAsync()
    {
        _mirrorBusy = true;
        _busyText = "正在关闭接收端…";
        RefreshUi();
        try
        {
            await _host.StopMiracastAsync();
            _host.StopAirPlay();
            MiracastHost.Detach();
            // 关掉接收端就等于本次投屏结束：会话状态必须一起归零，否则「本次投屏」会一直挂着计时
            EndSession();
        }
        finally
        {
            _mirrorBusy = false;
            await PollMirrorStatusAsync();
            RefreshUi();
        }
    }

    /// <summary>结束本次投屏会话：清计时、清本次保存计数、清画面统计，并隐藏会话卡。</summary>
    private void EndSession()
    {
        _sessionSince = null;
        _sessionShots = 0;
        _sessionRecords = 0;
        _statsData.Reset();
        _statsIssueSince = DateTime.MinValue;
        if (SessionCard is not null) SessionCard.Visibility = Visibility.Collapsed;
        if (TipsCard is not null) TipsCard.Visibility = Visibility.Visible;
        if (_stats is not null && _stats.IsVisible) _stats.Hide();
    }

    // ------------------------------------------------------------------ 自检用的渲染通道探测

    private async Task<SelfTestItem> RenderChannelProbeAsync(CancellationToken ct)
    {
        const string group = "渲染链路";
        const string name = "Composition 渲染通道（与镜像同一链路）";
        try
        {
            var inner = await Dispatcher.InvokeAsync(async () =>
            {
                if (!MiracastHost.IsReady)
                    return new SelfTestItem(group, name, SelfTestOutcome.Failed, "视频宿主未就绪：" + (MiracastHost.LastError ?? "未初始化"));

                string asset = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp4");
                if (!File.Exists(asset))
                    return new SelfTestItem(group, name, SelfTestOutcome.Failed, "缺少样例视频：" + asset);

                var wasHidden = MiracastHost.Visibility;
                MiracastHost.Visibility = Visibility.Visible;
                var source = MediaSource.CreateFromUri(new Uri(asset));
                MiracastHost.AttachSource(source, 1280, 720, realTime: false);

                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    var session = MiracastHost.Player?.PlaybackSession;
                    if (session is not null && session.NaturalVideoWidth > 0 &&
                        session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                    {
                        int w = (int)session.NaturalVideoWidth, h = (int)session.NaturalVideoHeight;
                        bool surfaceOk = MiracastHost.SurfaceWidth == w && MiracastHost.SurfaceHeight == h;
                        MiracastHost.SetFillMode(true);
                        string fillOn = MiracastHost.StretchModeDebug;
                        MiracastHost.SetFillMode(false);
                        string fillOff = MiracastHost.StretchModeDebug;
                        MiracastHost.Fit = Interop.CompositionVideoHost.VideoFit.Stretch;
                        string stretch = MiracastHost.StretchModeDebug;
                        MiracastHost.Zoom = 1.5;
                        double zoomApplied = MiracastHost.ZoomDebug;
                        MiracastHost.Zoom = 1.0;
                        MiracastHost.Fit = Interop.CompositionVideoHost.VideoFit.Fit;
                        bool ok = fillOn == "UniformToFill" && fillOff == "Uniform" && stretch == "Fill"
                                  && Math.Abs(zoomApplied - 1.5) < 0.01;
                        MiracastHost.Detach();
                        MiracastHost.Visibility = wasHidden;
                        return new SelfTestItem(group, name, surfaceOk && ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                            $"已解码 {w}×{h}；表面={MiracastHost.SurfaceWidth}×{MiracastHost.SurfaceHeight}（{(surfaceOk ? "已匹配" : "未匹配")}）；" +
                            $"铺满→{fillOn}，适应→{fillOff}，拉伸→{stretch}，缩放 150%→{zoomApplied:P0}");
                    }
                    await Task.Delay(250, ct);
                }
                string err = MiracastHost.LastError ?? "15 秒内未进入 Playing";
                MiracastHost.Detach();
                MiracastHost.Visibility = wasHidden;
                return new SelfTestItem(group, name, SelfTestOutcome.Failed, err);
            });
            return await inner;
        }
        catch (Exception ex)
        {
            return new SelfTestItem(group, name, SelfTestOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
