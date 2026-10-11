// WPF 真机探针：在"未打包 WPF 应用 + 真实 UI 线程"下，矩阵式测试 Miracast 接收会话能否启动。
// 一次运行覆盖多个变体（而非逐个写小脚本），结果同时输出到控制台、窗口和报告文件。
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.Miracast;
using Windows.System;

namespace MiracastProbeWpf;

internal static class Program
{
    private static readonly StringBuilder Report = new();
    private static TextBox _log = null!;

    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        _log = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var win = new Window
        {
            Title = "Miracast 接收端 — WPF 真机探针", Width = 1100, Height = 720, Content = _log,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        win.Loaded += async (_, _) =>
        {
            try { await RunMatrixAsync(); }
            catch (Exception ex) { Say($"[致命] {ex}"); }
            Say("\n探针结束后 6 秒自动关闭……");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) => { t.Stop(); win.Close(); };
            t.Start();
        };
        win.Closed += (_, _) => app.Shutdown();
        app.Run(win);
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "miracast-probe-report.txt"), Report.ToString(), Encoding.UTF8);
            Console.WriteLine(Report.ToString());
        }
        catch { }
    }

    private static void Say(string line)
    {
        Report.AppendLine(line);
        Console.WriteLine(line);
        try { _log.AppendText(line + Environment.NewLine); _log.ScrollToEnd(); } catch { }
    }

    private static async Task RunMatrixAsync()
    {
        Say("=========== Miracast 接收端 WPF 真机探针 ===========");
        Say($"[环境] {Environment.OSVersion}  线程={Environment.CurrentManagedThreadId}  STA={Thread.CurrentThread.GetApartmentState()}");
        Say($"[Wi-Fi Direct 虚拟适配器] {(WfdAdapterPresent() ? "存在" : "未检测到")}");

        // 变体 0：读取基础状态
        var r0 = new MiracastReceiver();
        var st0 = r0.GetStatus();
        Say($"[状态] ListeningStatus={st0.ListeningStatus} WiFiStatus={st0.WiFiStatus} MaxConn={st0.MaxSimultaneousConnections} Takeover={st0.IsConnectionTakeoverSupported}");

        // 变体 A：UI 线程 + DispatcherQueue + null 视图 + StartAsync（不改任何属性）
        await VariantAsync("A. UI线程+DispatcherQueue+StartAsync(默认属性)", ensureDispatcherQueue: true, mutate: false, useAsync: true, onUiThread: true);

        // 变体 B：UI 线程 + 同步 Start()
        await VariantAsync("B. UI线程+同步 Start()", ensureDispatcherQueue: true, mutate: false, useAsync: false, onUiThread: true);

        // 变体 C：UI 线程 + 修改属性 + StartAsync
        await VariantAsync("C. UI线程+改属性(MaxConn=1,Takeover=true)+StartAsync", ensureDispatcherQueue: true, mutate: true, useAsync: true, onUiThread: true);

        // 变体 D：后台线程（无线程消息泵）+ StartAsync，用于对照
        await VariantAsync("D. 后台线程(无消息泵)+StartAsync", ensureDispatcherQueue: false, mutate: false, useAsync: true, onUiThread: false);

        // 变体 E：换用自定义设备名后启动（同时验证设备名可写）
        await VariantAsync("E. 自定义设备名后 StartAsync", ensureDispatcherQueue: true, mutate: false, useAsync: true, onUiThread: true, friendlyName: "投屏Window 桌面版");
    }

    private static async Task VariantAsync(string title, bool ensureDispatcherQueue, bool mutate, bool useAsync, bool onUiThread, string? friendlyName = null)
    {
        Say($"\n--- {title} ---");
        try
        {
            if (ensureDispatcherQueue)
            {
                try
                {
                    var existing = DispatcherQueue.GetForCurrentThread();
                    Say($"    DispatcherQueue: {(existing is null ? "当前线程尚无（WPF 正常情况）" : "已存在 " + existing)}");
                }
                catch (Exception ex) { Say($"    DispatcherQueue 查询异常: {Describe(ex)}"); }
            }

            Func<Task> work = async () =>
            {
                MiracastReceiverSession? session = null;
                try
                {
                    var receiver = new MiracastReceiver();
                    if (friendlyName is not null)
                    {
                        var s = receiver.GetDefaultSettings();
                        s.FriendlyName = friendlyName;
                        s.ModelName = "投屏Window";
                        s.ModelNumber = "1.0";
                        s.AuthorizationMethod = MiracastReceiverAuthorizationMethod.ConfirmConnection;
                        var applied = await receiver.DisconnectAllAndApplySettingsAsync(s);
                        Say($"    应用设置: Status={applied.Status} Err={Describe(applied.ExtendedError)}");
                        var cur = await receiver.GetCurrentSettingsAsync();
                        Say($"    当前设备名: '{cur.FriendlyName}' 授权方式={cur.AuthorizationMethod}");
                    }

                    session = await receiver.CreateSessionAsync(null);
                    Say($"    CreateSessionAsync(null): {(session is null ? "null" : "OK")}");
                    if (session is null) return;

                    if (mutate)
                    {
                        try { session.MaxSimultaneousConnections = 1; Say("    已设 MaxSimultaneousConnections=1"); }
                        catch (Exception ex) { Say($"    设 MaxSimultaneousConnections 失败: {Describe(ex)}"); }
                        try { session.AllowConnectionTakeover = true; Say("    已设 AllowConnectionTakeover=true"); }
                        catch (Exception ex) { Say($"    设 AllowConnectionTakeover 失败: {Describe(ex)}"); }
                    }

                    int connCount = 0, mediaCount = 0;
                    session.ConnectionCreated += (_, e) => { connCount++; Say($"    [事件] ConnectionCreated PIN='{e.Pin}'"); };
                    session.MediaSourceCreated += (_, e) => { mediaCount++; Say($"    [事件] MediaSourceCreated source={(e.MediaSource is null ? "null" : "OK")}"); };
                    session.Disconnected += (_, e) => Say($"    [事件] Disconnected");

                    MiracastReceiverSessionStartResult res;
                    if (useAsync) { res = await session.StartAsync(); }
                    else { res = session.Start(); }
                    Say($"    启动结果: Status={res.Status}  ExtendedError={Describe(res.ExtendedError)}");

                    await Task.Delay(1200);
                    var after = receiver.GetStatus();
                    Say($"    启动后: ListeningStatus={after.ListeningStatus} WiFiStatus={after.WiFiStatus} 事件计数 conn={connCount} media={mediaCount}");

                    if (res.Status == MiracastReceiverSessionStartStatus.Success)
                    {
                        await Task.Delay(2500);
                        var s2 = receiver.GetStatus();
                        Say($"    保持监听 2.5s 后: ListeningStatus={s2.ListeningStatus}");
                    }
                }
                finally { try { session?.Dispose(); } catch { } }
            };

            if (onUiThread) { await work(); }
            else { await Task.Run(work); }
        }
        catch (Exception ex) { Say($"    变体异常: {Describe(ex)}"); }
    }

    private static bool WfdAdapterPresent()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-PnpDevice -FriendlyName '*Wi-Fi Direct*' -ErrorAction SilentlyContinue | Measure-Object).Count\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(4000);
            return int.TryParse(s, out int n) && n > 0;
        }
        catch { return false; }
    }

    private static string Describe(Exception? ex)
        => ex is null ? "无" : $"{ex.GetType().Name}(0x{ex.HResult:X8}): {ex.Message}" + (ex.InnerException is null ? "" : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}