using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CastReceiver.App.Services;

namespace CastReceiver.App;

public partial class App : Application
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const int SwRestore = 9;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>「请退出，新实例要接管」的广播消息号（RegisterWindowMessage 全局唯一）。</summary>
    public static uint TakeOverMessageId { get; private set; }

    private Mutex? _singleInstance;
    private bool _isInstanceOwner;

    public CastAppHost Host { get; private set; } = null!;

    /// <summary>诊断开关：--stats-preview 时强制显示通讯状态浮层（便于截图/排障）。</summary>
    public static bool StatsPreview { get; private set; }

    /// <summary>单实例互斥名的后缀（--instance-suffix）：供界面冒烟测试并行跑一个隔离实例。</summary>
    public static string InstanceSuffix { get; set; } = "";

    /// <summary>
    /// 自检用（--adb-discovery &lt;文件&gt;）：把「电脑控制手机」的 mDNS 发现结果换成文件里的内容
    /// （不设则走真实的 <c>adb mdns services</c>）。
    /// 存在的理由：真机上"只打开开关就自动连上"这条链路在无人值守的自检里跑不了
    /// （发现的是真实手机，脚本没法保证环境），注入一段真实格式的发现结果就能把这条链路验完。
    /// </summary>
    public static string? AdbDiscoveryFile { get; set; }

    /// <summary>
    /// 演示模式（--demo-cast）：用本地样例视频冒充手机镜像，
    /// 这样「截屏 / 录制 → 加密入库」这条链路在没有手机的情况下也能端到端自检。
    /// </summary>
    public static bool DemoCast { get; set; }

    /// <summary>跳过启动入场动画（--no-intro；设置里也能关）。</summary>
    public static bool NoIntro { get; set; }

    /// <summary>本次启动要不要放入场动画（命令行优先，其次看设置）。</summary>
    public static bool ShowIntro
    {
        get
        {
            if (NoIntro) return false;
            try { return (Current as App)?.Host?.Settings.ShowIntro ?? true; }
            catch { return true; }
        }
    }

    /// <summary>主题切换后触发（界面需要重新取色时用）。</summary>
    public static event Action? ThemeChanged;

    /// <summary>当前实际生效的主题文件名：Dark / Light。</summary>
    public static string CurrentThemeFile { get; private set; } = "Dark";

    /// <summary>把 "System" 解析成实际主题（读 Windows 的应用主题设置）。</summary>
    public static string ResolveTheme(string theme)
    {
        if (!string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase))
            return string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return value is int i && i == 0 ? "Dark" : "Light";
        }
        catch { return "Dark"; }
    }

    /// <summary>应用主题（运行时即时切换，无需重启）。</summary>
    public static void ApplyTheme(string theme, bool persist = true)
    {
        string file = ResolveTheme(theme);
        try
        {
            var dict = new ResourceDictionary { Source = new Uri($"Themes/{file}.xaml", UriKind.Relative) };
            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);
            CurrentThemeFile = file;
        }
        catch { }
        // 主题换了之后要把强调色覆盖层重新算一遍：弱化色（Soft/Line/FocusRing）是
        // 从强调色现算的，不重算的话切主题后聚焦环/选中底会停在上一套的 alpha 上。
        try { ApplyAccentCore((Current as App)?.Host?.Settings.Accent ?? "Mint"); } catch { }
        if (persist && Current is App app && app.Host is not null)
        {
            app.Host.Settings.Theme = theme;
            app.Host.Settings.Save();
        }
        ThemeChanged?.Invoke();
    }

    /// <summary>强调色预设（覆盖主题里的 B.Accent / B.AccentText）。</summary>
    private static readonly Dictionary<string, (string Accent, string Text)> Accents = new()
    {
        ["Mint"] = ("#3DDC97", "#08221A"),
        ["Amber"] = ("#F0B45E", "#261A06"),
        ["Violet"] = ("#A78BFA", "#1A1230"),
        ["Rose"] = ("#F472B6", "#2A0F1E"),
        ["Blue"] = ("#60A5FA", "#0A1D33"),
    };

    /// <summary>强调色预设里"当前这套"的 #RRGGBB（原生开屏动画要用它画细线）。</summary>
    public static string AccentHex(string accent)
    {
        (string hex, _) = accent switch
        {
            "System" => FromSystemAccent(),
            _ => Accents.TryGetValue(accent, out var pair) ? pair : Accents["Mint"],
        };
        return hex;
    }

    /// <summary>应用强调色（Mint/Amber/Violet/Rose/Blue/System）。</summary>
    public static void ApplyAccent(string accent)
    {
        ApplyAccentCore(accent);
        ThemeChanged?.Invoke();
    }

    /// <summary>真正写覆盖层的那一步（不触发事件；主题切换时复用，避免递归）。</summary>
    private static void ApplyAccentCore(string accent)
    {
        try
        {
            var merged = Current.Resources.MergedDictionaries;
            // 最后一本固定是强调色覆盖层；没有就补一本
            ResourceDictionary overlay;
            if (merged.Count >= 3 && merged[merged.Count - 1].Contains("B.AccentOverlay"))
                overlay = merged[merged.Count - 1];
            else
            {
                overlay = new ResourceDictionary();
                overlay["B.AccentOverlay"] = true;
                merged.Add(overlay);
            }

            (string accentColor, string textColor) = accent switch
            {
                "System" => FromSystemAccent(),
                _ => Accents.TryGetValue(accent, out var pair) ? pair : Accents["Mint"],
            };
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentColor);
            overlay["B.Accent"] = new System.Windows.Media.SolidColorBrush(color);
            overlay["B.AccentText"] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(textColor));
            // 强调色的弱化版本：选中底色 / 描边 / 聚焦环都用它，改强调色时整套跟着变
            overlay["B.AccentSoft"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x24, color.R, color.G, color.B));
            overlay["B.AccentLine"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x70, color.R, color.G, color.B));
            // 焦点环光晕：这个 alpha 直接决定键盘焦点可见度，太淡等于没有
            overlay["B.FocusRing"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x99, color.R, color.G, color.B));
        }
        catch { }
    }

    private static (string, string) FromSystemAccent()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null);
            if (value is int argb)
            {
                var color = System.Windows.Media.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
                bool light = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 150;
                return ($"#{color.R:X2}{color.G:X2}{color.B:X2}", light ? "#16181C" : "#FFFFFF");
            }
        }
        catch { }
        return ("#3DDC97", "#08221A");
    }

    /// <summary>取主题里的颜色字符串（原生闪屏要用）。</summary>
    public static string ThemeColor(string key, string fallback)
    {
        try { return Current.TryFindResource(key) as string ?? fallback; }
        catch { return fallback; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StatsPreview = e.Args.Any(a => a.Equals("--stats-preview", StringComparison.OrdinalIgnoreCase));
        Bootstrap.Log("OnStartup 进入");
        base.OnStartup(e);

        // 注册「新实例接管」广播消息（全局唯一，同名消息在所有进程里是同一个号）
        try { TakeOverMessageId = RegisterWindowMessage("CastReceiver.App.TakeOverInstance.v1"); } catch { }

        // 已经有一个实例在跑：让旧的退出，由本次启动接管（新版替换旧版）
        if (!TryAcquireSingleInstance())
        {
            bool replaced = TakeOverExistingInstance();
            if (replaced)
            {
                for (int i = 0; i < 30 && !_isInstanceOwner; i++)
                {
                    try { _singleInstance?.Dispose(); } catch { }
                    _singleInstance = null;
                    if (TryAcquireSingleInstance()) break;
                    Thread.Sleep(150);
                }
            }
            if (!_isInstanceOwner)
            {
                FocusExistingInstance();
                Shutdown();
                return;
            }
            Bootstrap.Log("已替换上一个实例，本次启动接管");
        }

        // 开屏：三种情况
        //   ① 用户选了视频且已起播 → 让开，别盖住它；
        //   ② 选了"品牌动画"（默认）→ 用原生窗口自绘一段 3.2 秒的品牌开场；
        //   ③ 没开开屏动画 → 一块静态品牌闪屏，挡住 WPF 启动那一小段。
        if (NativeIntroWindow.IsPlaying)
        {
            Bootstrap.Log("入场动画已在播放，跳过原生黑幕");
        }
        else
        {
            // 注意：这里 **Host 还没创建**（下面才 new），所以不能引用 Host.Settings ——
            // 直接轻量读一次配置（缓存过，不会重复读盘）。
            CastReceiver.Core.Infrastructure.CastSettings startup = CastReceiver.Core.Infrastructure.CastSettings.Load();
            (int sx, int sy, int sw, int sh) = Program.IntroWindowBounds(startup);

            if (ShowIntro && CastReceiver.Core.Infrastructure.IntroClips.IsBrand(startup.IntroSource))
            {
                Bootstrap.Log("开始品牌开场动画");
                NativeSplash.ShowBrandIntro(
                    ThemeColor("T.SplashBg", "#0B0C0E"),
                    ThemeColor("T.SplashFg", "#ECEDEF"),
                    ThemeColor("T.SplashDim", "#A0A6AE"),
                    ThemeColor("T.SplashLine", "#262A30"),
                    AccentHex(startup.Accent),
                    sx, sy, sw, sh);
            }
            else
            {
                Bootstrap.Log("开始创建原生黑幕");
                NativeSplash.Show("正在启动…",
                    ThemeColor("T.SplashBg", "#0B0C0E"),
                    ThemeColor("T.SplashFg", "#ECEDEF"),
                    ThemeColor("T.SplashDim", "#A0A6AE"),
                    ThemeColor("T.SplashLine", "#262A30"),
                    sx, sy, sw, sh);
            }
        }

        // 宿主很轻（配置 + 日志 + Miracast 服务对象）
        Host = new CastAppHost();
        Motion.SetEnabled(Host.Settings.UiMotion);
        ApplyTheme(Host.Settings.Theme, persist: false);   // 先按用户选择换肤，再显示主界面
        ApplyAccent(Host.Settings.Accent);
        Host.Log.Info("startup", $"启动阶段：宿主就绪 {SinceProcessStart():F0}ms");
        NativeSplash.SetStep("正在准备界面…");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { Host?.Log.Error("app", "未处理异常", args.ExceptionObject as Exception ?? new Exception("unknown")); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { Host?.Log.Error("app", "未观察的任务异常", args.Exception); } catch { }
            args.SetObserved();
        };

        // 先把窗口显示出来（内含启动动画遮罩），重活全部留到窗口出现之后
        Bootstrap.Log("开始创建主窗口");
        var window = new MainWindow();
        Bootstrap.Log("主窗口对象已创建");
        Host.Log.Info("startup", $"启动阶段：窗口对象就绪 {SinceProcessStart():F0}ms");
        MainWindow = window;   // 主窗口成为 MainWindow，随后关闭闪屏不会导致退出
        window.ContentRendered += (_, _) =>
        {
            Host.Log.Info("startup", $"启动阶段：主窗口已渲染 {SinceProcessStart():F0}ms");
            // 品牌开场是自己演完再退场的（约 3.2 秒），首帧渲染完就关会把动画砍掉
            if (!NativeSplash.BrandRunning)
            {
                try { NativeSplash.Close(); } catch { }
            }
            if (window is MainWindow main) main.FadeInWindow();
        };
        window.Show();
        Host.Log.Info("startup", $"启动阶段：窗口已显示 {SinceProcessStart():F0}ms");
        Host.Publish("投屏Window 已启动");
    }

    /// <summary>从进程启动算起的毫秒数（用于定位启动耗时）。</summary>
    private static double SinceProcessStart()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch { return -1; }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            // 注意：命名互斥量已经存在时，构造函数的 initiallyOwned 参数是无效的，
            // 必须用 WaitOne(0) 真正去抢；上一个实例崩溃留下的废弃互斥量要当成"可接管"。
            _singleInstance = new Mutex(false, "CastReceiver.App.SingleInstance" + InstanceSuffix);
            try { _isInstanceOwner = _singleInstance.WaitOne(0); }
            catch (AbandonedMutexException) { _isInstanceOwner = true; }
            return _isInstanceOwner;
        }
        catch { _isInstanceOwner = true; return true; }
    }

    /// <summary>
    /// 已经有实例在跑时：请求它退出，然后由本次启动接管。
    /// 这样「装了新版本、但旧版本还在托盘里」时，双击新版会真的换成新版，
    /// 而不是把旧版本的窗口唤到前台。
    /// </summary>
    private static bool TakeOverExistingInstance()
    {
        try
        {
            if (TakeOverMessageId != 0)
                PostMessage(HwndBroadcast, TakeOverMessageId, IntPtr.Zero, IntPtr.Zero);

            string? exe = Environment.ProcessPath;
            string name = exe is null ? "投屏Window" : Path.GetFileNameWithoutExtension(exe);
            int self = Environment.ProcessId;

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                var others = Process.GetProcessesByName(name).Where(p => p.Id != self).ToArray();
                if (others.Length == 0) return true;
                bool alive = false;
                foreach (var p in others)
                {
                    try { if (!p.HasExited) alive = true; }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (!alive) return true;
                Thread.Sleep(200);
            }

            // 旧实例不响应（例如它还是没有接管逻辑的旧版本）：直接结束它
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(3000); }
                catch { }
                finally { p.Dispose(); }
            }
            Thread.Sleep(500);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把已经在运行的那个实例的窗口唤到前台（用户再次双击图标时的期望行为）。</summary>
    private static void FocusExistingInstance()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            string name = exe is null ? "投屏Window" : Path.GetFileNameWithoutExtension(exe);
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == self) continue;
                IntPtr handle = p.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    p.Refresh();
                    handle = p.MainWindowHandle;
                }
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                break;
            }
        }
        catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { Host?.Log.Error("app", "界面线程异常：" + e.Exception, e.Exception); } catch { }
        MessageBox.Show("发生异常：" + e.Exception.Message + "\n\n程序会继续运行，详细信息见日志。",
            "投屏Window", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Dispose(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}