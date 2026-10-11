// 合并式端到端验证（开发工具）。一次运行覆盖：
//   网络环境 / SSDP 发现 / 设备描述与 SCPD / SOAP 控制全流程 / GENA 事件 / 媒体代理 /
//   VLC 解码 / Composition 渲染通道（与镜像同一链路）/ Miracast 接收会话。
// 说明：产品本身已精简为“纯手机镜像接收”，DLNA 相关链路只在命令行工具里保留用于回归验证。
// 用法：CastReceiver.SelfTest.exe [--report <路径>] [--verbose] [--diag-vlc]
using System.IO;
using System.Text;
using System.Windows;
using CastReceiver.App.Interop;
using CastReceiver.Core.Diagnostics;
using CastReceiver.Core.Dlna;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Media;
using CastReceiver.Core.Miracast;

namespace CastReceiver.SelfTest;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (args.Any(a => a.Equals("--diag-vlc", StringComparison.OrdinalIgnoreCase)))
            return VlcClockDiagnostic.Run();

        // 音频输出设备枚举与切换诊断（默认只做空操作切换，不打扰正在播放的声音）
        if (args.Any(a => a.Equals("--audio-endpoints", StringComparison.OrdinalIgnoreCase)))
            return AudioEndpointDiagnostic.Run(realSwitch: args.Any(a => a.Equals("--switch", StringComparison.OrdinalIgnoreCase)));

        // 手工把默认播放设备设成指定 id（救急用，例如验证脚本中途失败时还原）
        int setIndex = Array.FindIndex(args, a => a.Equals("--audio-set", StringComparison.OrdinalIgnoreCase));
        if (setIndex >= 0 && setIndex + 1 < args.Length)
            return AudioEndpointDiagnostic.SetDefault(args[setIndex + 1]);

        // 只跑加密库用例组（不需要界面/网络/VLC，秒级完成）
        if (args.Any(a => a.Equals("--vault-only", StringComparison.OrdinalIgnoreCase)))
        {
            var vaultItems = VaultSelfTest.RunAll();
            foreach (var item in vaultItems)
            {
                Console.WriteLine($"[{item.Outcome}] {item.Name}");
                Console.WriteLine($"        {item.Detail}");
            }
            int bad = vaultItems.Count(i => i.Outcome == SelfTestOutcome.Failed);
            Console.WriteLine($"\n加密库用例：{vaultItems.Count} 项，失败 {bad} 项");
            return bad;
        }

        // 只跑 AirPlay 协议级用例组（不需要界面/网络/手机，秒级完成）
        if (args.Any(a => a.Equals("--airplay-only", StringComparison.OrdinalIgnoreCase)))
        {
            var airplayItems = SelfTestRunner.RunAirPlayOnly();
            foreach (var item in airplayItems)
            {
                Console.WriteLine($"[{item.Result}] {item.Name}");
                Console.WriteLine($"        {item.Detail}");
            }
            int apBad = airplayItems.Count(i => i.Outcome == SelfTestOutcome.Failed);
            Console.WriteLine($"\nAirPlay / 设备识别 用例：{airplayItems.Count} 项，失败 {apBad} 项");
            return apBad;
        }

        // 只跑安卓鼠标控制（⑧）的纯逻辑用例组（不需要手机/adb 在线，毫秒级完成）
        // 可选 --adb-endpoint <host:port>：额外跑一次"自动发现 → 自动连接"的真实连接
        if (args.Any(a => a.Equals("--android-only", StringComparison.OrdinalIgnoreCase)))
        {
            int endpointIndex = Array.FindIndex(args, a => a.Equals("--adb-endpoint", StringComparison.OrdinalIgnoreCase));
            string? liveEndpoint = endpointIndex >= 0 && endpointIndex + 1 < args.Length ? args[endpointIndex + 1] : null;

            var androidItems = AndroidSelfTest.RunAll(liveEndpoint);
            foreach (var item in androidItems)
            {
                Console.WriteLine($"[{item.Result}] {item.Name}");
                Console.WriteLine($"        {item.Detail}");
            }
            int adbBad = androidItems.Count(i => i.Outcome == SelfTestOutcome.Failed);
            Console.WriteLine($"\n安卓鼠标控制（⑧）用例：{androidItems.Count} 项，失败 {adbBad} 项");
            return adbBad;
        }

        string reportPath = args.SkipWhile(a => !a.Equals("--report", StringComparison.OrdinalIgnoreCase))
                                .Skip(1).FirstOrDefault()
                            ?? Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");

        int failures = 0;
        bool verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        if (verbose) VlcPlayerEngine.EnableVlcLog = true;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { failures = await RunAsync(reportPath, verbose); }
            catch (Exception ex)
            {
                Console.WriteLine("[致命] " + ex);
                failures = 99;
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return failures;
    }

    private static async Task<int> RunAsync(string reportPath, bool verbose)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("        投屏Window · 合并式端到端自检（镜像 + 协议栈）");
        Console.WriteLine("================================================================");

        var log = new AppLog { EchoToConsole = false };
        log.MinLevel = verbose ? LogLevel.Debug : LogLevel.Info;
        log.EntryWritten += e =>
        {
            if (verbose || e.Level >= LogLevel.Warn) Console.WriteLine("  · " + e);
        };

        var settings = CastSettings.Load();
        using var player = new VlcPlayerEngine(settings, log);
        using var dlna = new DlnaRenderer(settings, player, log);
        using var miracast = new MiracastService(log);
        if (verbose) player.VlcMessage += m => Console.WriteLine("  [VLC] " + m);

        var host = new CompositionVideoHost();
        var libVlcView = new LibVLCSharp.WPF.VideoView();
        var layout = new System.Windows.Controls.Grid();
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        System.Windows.Controls.Grid.SetColumn(libVlcView, 0);
        System.Windows.Controls.Grid.SetColumn(host, 1);
        layout.Children.Add(libVlcView);
        layout.Children.Add(host);

        var window = new Window
        {
            Title = "投屏自检 · 渲染通道宿主（左：VLC / 右：Composition）",
            Width = 900, Height = 460, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false,
            Content = layout, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        await Task.Delay(800);

        libVlcView.MediaPlayer = player.Raw;
        await Task.Delay(300);

        dlna.Start();
        await Task.Delay(600);

        string asset = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp4");
        var runner = new SelfTestRunner(settings, log, player, dlna, miracast, asset,
            ct => RenderProbeAsync(host, asset, ct));

        var results = (await runner.RunAsync(null)).ToList();
        results.AddRange(VaultSelfTest.RunAll());

        Console.WriteLine();
        string? lastGroup = null;
        var report = new StringBuilder();
        report.AppendLine($"投屏Window · 合并自检报告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(new string('=', 100));
        foreach (var item in results)
        {
            if (item.Group != lastGroup)
            {
                Console.WriteLine();
                Console.WriteLine($"【{item.Group}】");
                report.AppendLine($"\n【{item.Group}】");
                lastGroup = item.Group;
            }
            Console.WriteLine($"  [{item.Result}] {item.Name}");
            Console.WriteLine($"          {item.Detail}");
            report.AppendLine($"  [{item.Result}] {item.Name}\n          {item.Detail}");
        }

        int passed = results.Count(r => r.Outcome == SelfTestOutcome.Passed);
        int warned = results.Count(r => r.Outcome == SelfTestOutcome.Warning);
        int failed = results.Count(r => r.Outcome == SelfTestOutcome.Failed);
        string summary = $"总计 {results.Count} 项：通过 {passed} / 警告 {warned} / 失败 {failed}";
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  " + summary);
        Console.WriteLine("================================================================");
        report.AppendLine(new string('=', 100));
        report.AppendLine(summary);

        try
        {
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            Console.WriteLine("报告已写入：" + reportPath);
        }
        catch (Exception ex) { Console.WriteLine("报告写入失败：" + ex.Message); }

        try { dlna.Stop(); } catch { }
        try { window.Close(); } catch { }
        return failed;
    }

    /// <summary>用与镜像完全相同的渲染链路（MediaPlayer → Composition → 子窗口）播放样例视频。</summary>
    private static async Task<SelfTestItem> RenderProbeAsync(CompositionVideoHost host, string asset, CancellationToken ct)
    {
        const string group = "H 渲染链路";
        const string name = "Composition 渲染通道（与镜像同一链路）";
        try
        {
            if (!host.IsReady)
                return new SelfTestItem(group, name, SelfTestOutcome.Failed, "视频宿主未就绪：" + (host.LastError ?? "未初始化"));
            if (!File.Exists(asset))
                return new SelfTestItem(group, name, SelfTestOutcome.Failed, "缺少样例视频：" + asset);

            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(asset));
            host.AttachSource(source, 1280, 720, realTime: false);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var session = host.Player?.PlaybackSession;
                if (session is not null && session.NaturalVideoWidth > 0 &&
                    session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    int w = (int)session.NaturalVideoWidth, h = (int)session.NaturalVideoHeight;
                    bool surfaceOk = host.SurfaceWidth == w && host.SurfaceHeight == h;
                    host.SetFillMode(true);
                    string fillOn = host.StretchModeDebug;
                    host.SetFillMode(false);
                    string fillOff = host.StretchModeDebug;
                    host.Fit = CompositionVideoHost.VideoFit.Stretch;
                    string stretch = host.StretchModeDebug;
                    host.Zoom = 1.5;
                    double zoomApplied = host.ZoomDebug;
                    host.Zoom = 1.0;
                    host.Fit = CompositionVideoHost.VideoFit.Fit;
                    bool ok = fillOn == "UniformToFill" && fillOff == "Uniform" && stretch == "Fill"
                              && Math.Abs(zoomApplied - 1.5) < 0.01;
                    host.Detach();
                    return new SelfTestItem(group, name, surfaceOk && ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed,
                        $"已解码 {w}×{h} 并进入 Playing；渲染表面={host.SurfaceWidth}×{host.SurfaceHeight}（{(surfaceOk ? "已匹配" : "未匹配视频")}）；" +
                        $"铺满→{fillOn}，适应→{fillOff}，拉伸→{stretch}，缩放150%→{zoomApplied:P0}");
                }
                await Task.Delay(250, ct);
            }
            string err = host.LastError ?? "15 秒内未进入 Playing";
            host.Detach();
            return new SelfTestItem(group, name, SelfTestOutcome.Failed, err);
        }
        catch (Exception ex)
        {
            return new SelfTestItem(group, name, SelfTestOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}