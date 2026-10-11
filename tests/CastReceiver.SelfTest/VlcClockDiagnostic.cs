using System.IO;
using System.Windows;
using CastReceiver.Core.Http;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Media;

namespace CastReceiver.SelfTest;

/// <summary>
/// 播放时钟对照诊断：同一段视频分别用【本地文件】【本机 HTTP 流(带/不带缓存)】播放，
/// 每 500ms 打印一次 VLC 的 Time/Length/Position，用于判定“进度不推进”是来源问题还是播放器配置问题。
/// 这是诊断工具而非功能验证脚本，功能验证统一走合并自检（不带参数运行）。
/// </summary>
internal static class VlcClockDiagnostic
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { Console.WriteLine("[异常] " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return 0;
    }

    private static async Task RunAsync()
    {
        string asset = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp4");
        Console.WriteLine("样例文件：" + asset + " 存在=" + File.Exists(asset));
        if (!File.Exists(asset)) return;

        var log = new AppLog { EchoToConsole = false, MinLevel = LogLevel.Warn };
        VlcPlayerEngine.EnableVlcLog = true;
        var settings = new CastSettings { StreamUserAgent = "", StreamReferer = "" };
        using var engine = new VlcPlayerEngine(settings, log);
        var mediaPlayer = engine.Raw;
        engine.VlcMessage += m =>
        {
            if (m.Contains("aout", StringComparison.OrdinalIgnoreCase)
                || m.Contains("audio", StringComparison.OrdinalIgnoreCase)
                || m.Contains("clock", StringComparison.OrdinalIgnoreCase)
                || m.Contains("vout", StringComparison.OrdinalIgnoreCase)
                || m.Contains("error", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                || m.Contains("pause", StringComparison.OrdinalIgnoreCase)
                || m.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                || m.Contains("end of", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("   [VLC] " + m.Replace("\n", "\n         "));
        };

        // 供 HTTP 对照用的极简文件服务：两种语义 —— A) 永远 200 + 整文件；B) 标准 Range（含 416）
        var serverPlain = MakeServer(asset, log, "diag-plain", properRange: false);
        var serverRange = MakeServer(asset, log, "diag-range", properRange: true);
        serverPlain.Start();
        serverRange.Start();
        string httpPlain = $"http://127.0.0.1:{serverPlain.Port}/sample.mp4";
        string httpRange = $"http://127.0.0.1:{serverRange.Port}/sample.mp4";

        // 播放窗口：给 VLC 一个真实视频输出；并同时放一个 Composition 视频宿主，复刻自检/正式界面的窗口构成
        var view = new LibVLCSharp.WPF.VideoView();
        var compHost = new CastReceiver.App.Interop.CompositionVideoHost();
        var layout = new System.Windows.Controls.Grid();
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        System.Windows.Controls.Grid.SetColumn(view, 0);
        System.Windows.Controls.Grid.SetColumn(compHost, 1);
        layout.Children.Add(view);
        layout.Children.Add(compHost);

        var window = new Window
        {
            Title = "VLC 时钟诊断", Width = 800, Height = 400,
            WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false, Content = layout,
        };
        window.Show();
        view.MediaPlayer = mediaPlayer;
        await Task.Delay(500);

        await ProbeAsync("0. 本地文件（基准对照）", new Uri(asset).AbsoluteUri, engine, sync: true);
        await ProbeAsync("1A. HTTP 流 · 调用线程同步执行", httpPlain, engine, sync: true);
        await ProbeAsync("1B. HTTP 流 · 串行后台队列执行", httpPlain, engine, sync: false);

        // 复刻 DLNA 会话序列：投放 → 停止 → 再投放 → 暂停 → 播放 → 停止
        Console.WriteLine();
        Console.WriteLine("=== 2. 复刻 DLNA 会话序列（SetURI/Stop/SetURI/Pause/Play/Stop）===");
        await engine.SetUriAsync(httpRange, null); engine.Play();
        await Task.Delay(2500);
        engine.Stop();
        await Task.Delay(600);
        await engine.SetUriAsync(httpRange, null); engine.Play();
        await Task.Delay(2500);
        engine.Pause();
        await Task.Delay(800);
        engine.Play();
        await Task.Delay(1200);
        engine.Stop();
        await Task.Delay(800);
        Console.WriteLine("  序列完成");

        await ProbeAsync("3. 序列之后再播一次 HTTP 流（标准 Range 服务器）", httpRange, engine);

        // 对照：换成正式设置里的 User-Agent / Referer，验证是否是 HTTP 选项导致的进度不推进
        var realSettings = CastSettings.Load();
        Console.WriteLine();
        Console.WriteLine($"=== 4. 使用正式设置的 UA（{realSettings.StreamUserAgent[..Math.Min(40, realSettings.StreamUserAgent.Length)]}…）===");
        using (var engine2 = new VlcPlayerEngine(realSettings, log))
        {
            view.MediaPlayer = engine2.Raw;
            await Task.Delay(300);
            await ProbeAsync("4. 正式设置 UA + HTTP 流", httpRange, engine2);
        }

        try { serverPlain.Stop(); } catch { }
        try { serverRange.Stop(); } catch { }
        try { window.Close(); } catch { }
    }

    private static MiniHttpServer MakeServer(string asset, AppLog log, string name, bool properRange)
        => new(0, (req, ct) =>
        {
            var fi = new FileInfo(asset);
            long start = 0, end = fi.Length - 1;
            int status = 200;
            if (properRange)
            {
                var parsed = HttpRange.Parse(req.Header("Range"), fi.Length, out long rs, out long re);
                if (parsed == RangeParseResult.Unsatisfiable)
                    return Task.FromResult(HttpResponseEx.Status(416, "Range Not Satisfiable")
                        .With("Content-Range", $"bytes */{fi.Length}"));
                if (parsed == RangeParseResult.Satisfiable && !(rs == 0 && re == fi.Length - 1))
                {
                    start = rs; end = re; status = 206;
                }
            }
            var fs = new FileStream(asset, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            fs.Seek(start, SeekOrigin.Begin);
            long remaining = end - start + 1;
            var resp = new HttpResponseEx
            {
                StatusCode = status,
                ContentLength = remaining,
                BodyWriter = async (output, token) =>
                {
                    var buffer = new byte[64 * 1024];
                    long left = remaining;
                    while (left > 0)
                    {
                        int read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), token);
                        if (read <= 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        left -= read;
                    }
                    fs.Dispose();
                },
            };
            resp.Headers["Content-Type"] = "video/mp4";
            resp.Headers["Accept-Ranges"] = "bytes";
            if (status == 206) resp.Headers["Content-Range"] = $"bytes {start}-{end}/{fi.Length}";
            return Task.FromResult(resp);
        }, log, name);

    private static async Task ProbeAsync(string title, string uri, VlcPlayerEngine engine, bool zeroCache = false, bool sync = false)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title}：{uri} ===");
        VlcPlayerEngine.SynchronousControl = sync;
        await engine.SetUriAsync(uri, null);
        engine.Play();
        for (int i = 1; i <= 8; i++)
        {
            await Task.Delay(600);
            Console.WriteLine($"  #{i} {engine.RawTelemetry}  pos={engine.Position.TotalSeconds:F1}s dur={engine.Duration.TotalSeconds:F1}s");
        }
        engine.Stop();
        await Task.Delay(600);
    }
}