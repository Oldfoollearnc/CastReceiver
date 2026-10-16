using System.Windows;
using CastReceiver.App.Services;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Security;

namespace CastReceiver.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 网络修复这类"工具模式"不启动界面，直接做完就退出
        if (args.Any(a => a.Equals("--fix-network", StringComparison.OrdinalIgnoreCase)))
        {
            bool setPrivate = args.Any(a => a.Equals("--set-private", StringComparison.OrdinalIgnoreCase));
            bool disableHotspot = args.Any(a => a.Equals("--disable-hotspot", StringComparison.OrdinalIgnoreCase));
            bool cleanupFirewall = args.Any(a => a.Equals("--cleanup-firewall", StringComparison.OrdinalIgnoreCase));
            string iface = "";
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--iface", StringComparison.OrdinalIgnoreCase)) iface = args[i + 1];
            return NetworkFixer.RunElevatedFix(setPrivate, iface, disableHotspot, cleanupFirewall);
        }

        // 可选：把加密库/配置放到指定目录（自检与界面冒烟用；也方便做「绿色版」）
        App.DemoCast = args.Any(a => a.Equals("--demo-cast", StringComparison.OrdinalIgnoreCase));
        // 跳过启动入场动画（自检/截图/自动化用；用户想关也可以在设置里关）
        App.NoIntro = args.Any(a => a.Equals("--no-intro", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--vault-root", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                VaultPaths.Root = args[i + 1];
            if (args[i].Equals("--settings-root", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                CastSettings.RootOverride = args[i + 1];
            if (args[i].Equals("--instance-suffix", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                App.InstanceSuffix = args[i + 1];
            // 自检用：把「mDNS 发现结果」换成文件内容（不设则走真实的 adb mdns services）。
            // 用途：真机上"发现 → 连接"整条链路没法在无人值守的自检里跑，这个口子让它能跑。
            if (args[i].Equals("--adb-discovery", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                App.AdbDiscoveryFile = args[i + 1];
            // 自检用：adb 可执行文件路径（指到一个桩程序上，就能在没有真机时端到端验"命令发对了没有"）
            if (args[i].Equals("--adb-path", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                CastReceiver.Core.Android.AdbClient.PathOverride = args[i + 1];
        }

        Bootstrap.Log("Main 入口");

        // 入场动画：**在这里就起播**（WPF 还没初始化）。
        // 用户实测"点开之后要黑等一会儿才出画面"，那段是 WPF 的固定开销（解析 XAML + 显示第一个窗口），
        // 所以画面必须在 WPF 之前放出来 —— 走原生窗口 + Composition，一两百毫秒就能出画。
        StartIntroEarly();

        var app = new App();
        app.InitializeComponent();
        Bootstrap.Log("App.InitializeComponent 完成");
        return app.Run();
    }

    /// <summary>
    /// 尽早起播入场动画（失败就静默跳过，回退到 WPF 内部那层遮罩）。
    /// 位置/大小按"上次关闭时的窗口样子"来，与主窗口严丝合缝，交接时不会跳。
    /// </summary>
    private static void StartIntroEarly()
    {
        try
        {
            if (App.NoIntro) return;

            CastSettings settings = CastSettings.Load();
            if (!settings.ShowIntro) return;

            string video = IntroClips.ResolvePath(settings.IntroSource, AppContext.BaseDirectory);
            if (video.Length == 0) return;

            (int x, int y, int w, int h) = IntroWindowBounds(settings);
            var style = new NativeIntroWindow.Style(
                settings.IntroStretch, settings.IntroAlignH, settings.IntroAlignV,
                settings.IntroVolume, settings.IntroFadeInMs, settings.IntroFadeOutMs, settings.IntroFlash,
                settings.IntroCropEffective.Top, settings.IntroCropEffective.Bottom,
                settings.IntroCropEffective.Left, settings.IntroCropEffective.Right);
            bool ok = NativeIntroWindow.Start(video, settings.IntroStart, settings.IntroEnd, settings.IntroExtend, x, y, w, h, style);
            Bootstrap.Log(ok ? "入场动画已提前起播" : "入场动画未提前起播，回退到界面内遮罩");
        }
        catch (Exception ex)
        {
            Bootstrap.Log("提前起播入场动画失败：" + ex.Message);
        }
    }

    /// <summary>入场动画窗口占地：优先用上次关闭时的窗口矩形，没有就居中 1120×720。</summary>
    internal static (int X, int Y, int W, int H) IntroWindowBounds(CastSettings settings)
    {
        try
        {
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (settings.WindowWidth is > 400 && settings.WindowHeight is > 300 &&
                settings.WindowLeft is not null && settings.WindowTop is not null)
            {
                // 画面大小可编辑：设了就用它，没设才跟随主窗口；并限制最小值（太小就只剩黑边）
                double wantW = settings.IntroWidth > 200 ? settings.IntroWidth : Math.Max(settings.WindowWidth.Value, 1024);
                double wantH = settings.IntroHeight > 150 ? settings.IntroHeight : Math.Max(settings.WindowHeight.Value, 576);
                int w = (int)Math.Clamp(wantW, 1024, vw);
                int h = (int)Math.Clamp(wantH, 576, vh);
                int x = (int)Math.Clamp(settings.WindowLeft.Value, 0, Math.Max(0, vw - w));
                int y = (int)Math.Clamp(settings.WindowTop.Value, 0, Math.Max(0, vh - h));
                return (x, y, w, h);
            }

            int dw = (int)Math.Min(1120, vw), dh = (int)Math.Min(720, vh);
            return ((int)((vw - dw) / 2), (int)((vh - dh) / 2), dw, dh);
        }
        catch { return (200, 120, 1120, 720); }
    }
}
