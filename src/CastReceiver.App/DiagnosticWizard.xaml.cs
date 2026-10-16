using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CastReceiver.App.Services;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Miracast;

namespace CastReceiver.App;

/// <summary>
/// 一键连接诊断：按顺序跑一组检查，每项都给出人话结论与建议。
/// 目的是把"手机搜不到/连不上"这类问题变成一条明确的下一步动作。
/// </summary>
public partial class DiagnosticWizard : Window
{
    private readonly CastAppHost _host;
    private readonly List<(string Name, Func<Task<(bool Ok, string Detail, string? Fix)>> Check)> _checks;
    private readonly DispatcherTimer _timer;
    private int _index;

    public DiagnosticWizard(CastAppHost host)
    {
        InitializeComponent();
        _host = host;

        _checks = new List<(string, Func<Task<(bool, string, string?)>>)>
        {
            ("接收端是否在监听", async () =>
            {
                var s = await Task.Run(() => host.Miracast.QueryStatus());
                return s.Listening
                    ? (true, $"正在监听（{s.ListeningStatus}）", null)
                    : (false, "接收端没有在监听", "点「重启接收端」");
            }),
            ("无线显示能力", async () =>
            {
                await Task.Yield();
                var s = host.Miracast.QueryStatus();
                bool ok = s.WiFiStatus.Contains("Miracast", StringComparison.OrdinalIgnoreCase);
                return ok
                    ? (true, s.WiFiStatus, null)
                    : (false, s.WiFiStatus, "驱动不支持 Miracast 接收，请更新无线网卡驱动");
            }),
            ("网卡与地址", async () =>
            {
                var lan = await Task.Run(NetInfo.GetLanInterface);
                return lan is null
                    ? (false, "没有找到可用的局域网网卡", "确认已连接 Wi-Fi")
                    : (true, $"{lan.Name} · {lan.Address}", null);
            }),
            ("网络位置（专用/公用）", async () =>
            {
                var lan = await Task.Run(NetInfo.GetLanInterface);
                if (lan is null) return (false, "未联网", "先连接 Wi-Fi");
                string category = await Task.Run(() => NetInfo.GetNetworkCategory(lan.Name));
                return category.Equals("Private", StringComparison.OrdinalIgnoreCase)
                    ? (true, "专用网络", null)
                    : (false, $"当前是 {category}", "点「一键准备」把网络设为专用");
            }),
            ("防火墙放行", async () =>
            {
                bool ok = await Task.Run(() => NetInfo.HasFirewallRuleFor(NetworkFixer.RulePrefix));
                return ok ? (true, "已放行本程序", null) : (false, "尚未放行", "点「一键准备」放行（需同意一次 UAC）");
            }),
            ("移动热点占用", async () =>
            {
                string? conflict = await Task.Run(NetInfo.DetectHotspotConflict);
                return conflict is null
                    ? (true, "未检测到占用", null)
                    : (false, conflict, "关闭移动热点后点「重启接收端」");
            }),
            ("手机是否在同一网段", async () =>
            {
                await Task.Yield();
                var lan = host.Lan;
                if (lan is null) return (false, "本机未联网", "先连接 Wi-Fi");
                string prefix = string.Join('.', lan.Address.ToString().Split('.').Take(3));
                return (true, $"本机网段 {prefix}.x —— 手机应连接同一个 Wi-Fi", null);
            }),
            ("网络延迟（本机连通性）", async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync("223.5.5.5", 2000);
                    return reply.Status == IPStatus.Success
                        ? (true, $"外网延迟 {reply.RoundtripTime} ms（参考值）", null)
                        : (false, "外网不通（不影响局域网投屏）", null);
                }
                catch { return (false, "无法测试外网（不影响局域网投屏）", null); }
            }),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _timer.Tick += async (_, _) => await StepAsync();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private async Task StepAsync()
    {
        if (_index >= _checks.Count)
        {
            _timer.Stop();
            BtnClose.Content = "完成";
            // WCAG 2.5.3：可访问名必须包含可见文字，否则语音控制说「点击完成」会失败
            System.Windows.Automation.AutomationProperties.SetName(BtnClose, "完成");
            TxtProgress.Text = "诊断完成";
            return;
        }

        var (name, check) = _checks[_index];
        _index++;
        TxtProgress.Text = $"正在检查 {_index}/{_checks.Count}…";

        bool ok; string detail; string? fix;
        try { (ok, detail, fix) = await check(); }
        catch (Exception ex) { ok = false; detail = ex.Message; fix = null; }

        var row = BuildRow(name, ok, detail, fix);
        Results.Children.Add(row);
        FadeIn(row);
        Scroll.ScrollToEnd();

        if (_index >= _checks.Count)
        {
            int bad = Results.Children.OfType<FrameworkElement>().Count(e => e.Tag as string == "bad");
            TxtVerdict.Text = bad == 0
                ? "全部正常：现在用手机下拉面板点「无线投屏」并选择本机即可。"
                : $"发现 {bad} 项需要处理，按每条的提示操作即可（多数情况点「一键准备」就能解决）。";
            TxtVerdict.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement BuildRow(string name, bool ok, string detail, string? fix)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource(ok ? "B.Ok" : "B.Warn"),
        });
        head.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = name, Margin = new Thickness(9, 0, 0, 0), FontSize = 12.5,
            Foreground = (Brush)FindResource("B.Text"),
        });
        panel.Children.Add(head);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detail, Margin = new Thickness(17, 3, 0, 0), FontSize = 11.5,
            Foreground = (Brush)FindResource("B.TextDim"), TextWrapping = TextWrapping.Wrap,
        });
        if (fix is not null)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "→ " + fix, Margin = new Thickness(17, 3, 0, 0), FontSize = 11.5,
                Foreground = (Brush)FindResource("B.Warn"), TextWrapping = TextWrapping.Wrap,
            });
        panel.Tag = ok ? "ok" : "bad";
        return panel;
    }

    private static void FadeIn(UIElement element) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }
}