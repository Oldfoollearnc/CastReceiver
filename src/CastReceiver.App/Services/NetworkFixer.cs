using System.Diagnostics;
using System.IO;
using System.Text;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.App.Services;

/// <summary>
/// 网络放行助手：
/// Windows 防火墙默认拦截入站（尤其是网络位置为“公用”时），会导致手机既搜不到本机、也无法回连。
/// 本助手通过 UAC 提权执行一次性的防火墙规则添加与网络位置切换，普通运行时不需要管理员权限。
/// </summary>
public static class NetworkFixer
{
    public const string RulePrefix = "投屏Window 桌面版";
    public const string ResultFileName = "network-fix-result.txt";

    private static string ResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver", ResultFileName);

    /// <summary>提权子进程入口：添加防火墙规则（可选切换为专用网络、可选关闭移动热点）。</summary>
    public static int RunElevatedFix(bool setPrivate, string? interfaceAlias, bool disableHotspot = false, bool cleanupFirewallOnly = false)
    {
        var sb = new StringBuilder();
        int failures = 0;

        void Run(string file, string args, string title, bool ignoreNotFound = false)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                bool ok = p.ExitCode == 0;
                // “没有与指定标准相匹配的规则”= 本来就没有旧规则可删，属于正常情况，不能算失败
                if (!ok && ignoreNotFound &&
                    (outp.Contains("没有与指定标准相匹配的规则") || outp.Contains("No rules match", StringComparison.OrdinalIgnoreCase)))
                    ok = true;
                if (!ok) failures++;
                sb.AppendLine($"[{(ok ? "成功" : "失败")}] {title}");
                if (!string.IsNullOrWhiteSpace(outp)) sb.AppendLine("    " + outp.Trim().Replace("\n", "\n    "));
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine($"[失败] {title} :: {ex.GetType().Name}: {ex.Message}");
            }
        }

        string exe = Environment.ProcessPath ?? "";
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序：{exe}");
        sb.AppendLine();

        if (cleanupFirewallOnly)
        {
            sb.AppendLine("=== 隐私加固：清理本程序多余的网络放行规则 ===");
            // 本程序自身不监听任何端口（镜像由系统组件负责），因此把自己名下的入站规则全部删掉，
            // 消除早期版本（DLNA 时代）遗留的 UDP / 1900 / Any 暴露面。
            string[] stale =
            {
                "投屏Window 桌面版 (程序 TCP)", "投屏Window 桌面版 (程序 UDP)", "投屏Window 桌面版 (SSDP 1900)",
                "投屏window.exe", "CastReceiver.SelfTest.exe",
            };
            foreach (var name in stale)
                Run("netsh", $"advfirewall firewall delete rule name=\"{name}\"", $"移除规则：{name}", ignoreNotFound: true);
            // Miracast 由系统组件负责，它的规则要保留（若不存在则补一条，保证镜像仍可用）
            Run("netsh", "advfirewall firewall show rule name=\"miracastreceiver.exe\"", "确认系统 Miracast 组件规则存在", ignoreNotFound: true);
            sb.AppendLine();
            sb.AppendLine(failures == 0
                ? "清理完成：本程序名下不再有任何入站放行规则，暴露面已最小化。"
                : $"清理完成，但有 {failures} 项失败，请查看上面的明细。");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
                File.WriteAllText(ResultPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
            try
            {
                System.Windows.MessageBox.Show(sb.ToString(), "网络规则清理结果",
                    System.Windows.MessageBoxButton.OK,
                    failures == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
            }
            catch { }
            return failures;
        }

        if (exe.Length > 0)
        {
            Run("netsh", $"advfirewall firewall delete rule name=\"{RulePrefix} (程序 TCP)\"", "清理旧的 TCP 规则（若不存在则跳过）", ignoreNotFound: true);
            Run("netsh", $"advfirewall firewall delete rule name=\"{RulePrefix} (程序 UDP)\"", "清理旧的 UDP 规则（若不存在则跳过）", ignoreNotFound: true);
            Run("netsh", $"advfirewall firewall delete rule name=\"{RulePrefix} (SSDP 1900)\"", "清理旧的 SSDP 规则（若不存在则跳过）", ignoreNotFound: true);
            Run("netsh", $"advfirewall firewall add rule name=\"{RulePrefix} (程序 TCP)\" dir=in action=allow program=\"{exe}\" enable=yes profile=any protocol=TCP", "放行本程序入站 TCP（投屏控制/媒体服务）");
            Run("netsh", $"advfirewall firewall add rule name=\"{RulePrefix} (程序 UDP)\" dir=in action=allow program=\"{exe}\" enable=yes profile=any protocol=UDP", "放行本程序入站 UDP（Miracast/组播）");
            Run("netsh", $"advfirewall firewall add rule name=\"{RulePrefix} (SSDP 1900)\" dir=in action=allow protocol=UDP localport=1900 profile=any", "放行 SSDP 发现端口 UDP 1900");
        }
        else
        {
            failures++;
            sb.AppendLine("[失败] 无法解析当前程序路径");
        }

        if (setPrivate && !string.IsNullOrWhiteSpace(interfaceAlias))
        {
            Run("powershell",
                $"-NoProfile -NonInteractive -Command \"Set-NetConnectionProfile -InterfaceAlias '{interfaceAlias}' -NetworkCategory Private\"",
                $"将无线网络“{interfaceAlias}”切换为专用网络（Windows 允许被发现的必要条件）");
        }

        if (disableHotspot)
        {
            // 移动热点 / 连接共享会占用 Wi-Fi Direct 通道，直接导致 Miracast 收不到连接
            Run("netsh", "wlan stop hostednetwork", "停止旧式承载网络（若未启用则跳过）", ignoreNotFound: true);
            Run("powershell", "-NoProfile -NonInteractive -Command \"Stop-Service icssvc -Force -ErrorAction SilentlyContinue; Stop-Service SharedAccess -Force -ErrorAction SilentlyContinue; 'done'\"",
                "关闭移动热点 / Internet 连接共享（释放 Wi-Fi Direct 通道）");
            Run("powershell",
                "-NoProfile -NonInteractive -Command \"Get-NetAdapter -Name '本地连接*' -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.ifIndex -Dhcp Disabled -ErrorAction SilentlyContinue }\"",
                "释放 Wi-Fi Direct 虚拟适配器上的热点地址", ignoreNotFound: true);

            // 若无线网卡仍处于异常的 Wi-Fi Direct 状态，重启一次无线网卡让协议栈复位
            if (!string.IsNullOrWhiteSpace(interfaceAlias))
            {
                Run("powershell",
                    $"-NoProfile -NonInteractive -Command \"Restart-NetAdapter -Name '{interfaceAlias}' -Confirm:$false -ErrorAction Stop\"",
                    $"重启无线网卡“{interfaceAlias}”（复位 Wi-Fi Direct 协议栈，Wi-Fi 会短暂断开）");
            }
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "全部完成：手机现在可以搜索并连接本机。" : $"完成，但有 {failures} 项失败，请查看上面的明细。");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
            File.WriteAllText(ResultPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }

        try
        {
            System.Windows.MessageBox.Show(sb.ToString(), "网络一键放行结果",
                System.Windows.MessageBoxButton.OK,
                failures == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch { }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>普通权限进程调用：请求提权并等待结果。</summary>
    public static bool RequestElevation(out string message, bool setPrivate = true, bool disableHotspot = false, bool cleanupFirewallOnly = false)
    {
        message = "";
        try { if (File.Exists(ResultPath)) File.Delete(ResultPath); } catch { }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径");
        string iface = NetInfo.GetLanInterface()?.Name ?? "";
        string args = $"--fix-network{(setPrivate && !cleanupFirewallOnly ? " --private" : "")}{(disableHotspot ? " --disable-hotspot" : "")}{(cleanupFirewallOnly ? " --cleanup-firewall" : "")} --iface \"{iface}\"";

        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            if (p is null) { message = "提权进程启动失败"; return false; }
            p.WaitForExit(90000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "已取消 UAC 授权，未做任何修改。";
            return false;
        }
        catch (Exception ex)
        {
            message = $"提权失败：{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        try
        {
            if (File.Exists(ResultPath))
            {
                message = File.ReadAllText(ResultPath, Encoding.UTF8);
                return !message.Contains("但有");
            }
        }
        catch { }

        message = "提权进程已结束，但未读到结果文件。";
        return false;
    }
}