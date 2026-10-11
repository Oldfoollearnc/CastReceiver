using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CastReceiver.Core.Infrastructure;

public sealed record LanInterface(string Name, string Description, IPAddress Address, NetworkInterfaceType Type)
{
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>局域网与系统网络状态探测（选网卡、查网络位置、查防火墙）。</summary>
public static class NetInfo
{
    /// <summary>挑选用于投屏的局域网 IPv4：优先无线，其次有线；排除虚拟网卡/169.254。</summary>
    public static LanInterface? GetLanInterface()
    {
        var candidates = new List<(int score, LanInterface item)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            string desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
            bool virtualLike = desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                            || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                            || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                            || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;

                int score = 0;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 100;
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 60;
                if (hasGateway) score += 20;
                if (virtualLike) score -= 200;

                candidates.Add((score, new LanInterface(nic.Name ?? "", nic.Description ?? "", ua.Address, nic.NetworkInterfaceType)));
            }
        }

        return candidates.Where(c => c.score > 0).OrderByDescending(c => c.score).Select(c => c.item).FirstOrDefault();
    }

    public static string? GetWlanSsid()
    {
        try
        {
            string output = Run("netsh", "wlan show interfaces", 4000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = t.IndexOf(':');
                    if (idx > 0) return t[(idx + 1)..].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>当前无线链路的关键信息（频段/信道/协商速率/信号），用于判断"为什么卡"。</summary>
    public sealed record WlanLink(string Ssid, double BandGhz, int Channel, int RxMbps, int TxMbps,
                                  int SignalPercent, string RadioType)
    {
        public bool Is24G => BandGhz > 0 && BandGhz < 3;
        public bool Is5G => BandGhz >= 3;

        public string BandText => BandGhz <= 0 ? "未知频段" : $"{BandGhz:0.#}GHz";

        /// <summary>侧栏一行显示的摘要。</summary>
        public string ShortText
        {
            get
            {
                string rate = RxMbps > 0 ? $" · {RxMbps}Mbps" : "";
                return $"{BandText}{rate}";
            }
        }

        public string DetailText
            => $"{Ssid}\n频段 {BandText} · 信道 {Channel} · {RadioType}\n" +
               $"接收 {RxMbps}Mbps / 发送 {TxMbps}Mbps · 信号 {SignalPercent}%" +
               (Is24G ? "\n\n⚠ 当前是 2.4GHz：投屏延迟会明显偏高，建议把手机和电脑都连到 5GHz 频段。" : "");
    }

    /// <summary>
    /// 读取当前 Wi-Fi 链路信息（解析 netsh 输出，中英文都能认）。
    /// 只用一次子进程调用，调用方应放在后台线程并做缓存。
    /// </summary>
    public static WlanLink? ReadWlanLink()
    {
        try
        {
            string output = Run("netsh", "wlan show interfaces", 4000);
            if (string.IsNullOrWhiteSpace(output)) return null;

            string ssid = "";
            double band = 0;
            int channel = 0, rx = 0, tx = 0, signal = 0;
            string radio = "";
            bool connected = false;

            foreach (var raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    int i = line.IndexOf(':');
                    if (i > 0) ssid = line[(i + 1)..].Trim();
                    continue;
                }
                if (line.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("已连接", StringComparison.Ordinal)) connected = true;

                var bandMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)\s*GHz");
                if (bandMatch.Success && double.TryParse(bandMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b))
                    band = b;

                var channelMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:Channel|信道)\s*:\s*(\d+)");
                if (channelMatch.Success) int.TryParse(channelMatch.Groups[1].Value, out channel);

                var rxMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:Receive rate|接收速率)[^:]*:\s*(\d+)");
                if (rxMatch.Success) int.TryParse(rxMatch.Groups[1].Value, out rx);

                var txMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:Transmit rate|传输速率)[^:]*:\s*(\d+)");
                if (txMatch.Success) int.TryParse(txMatch.Groups[1].Value, out tx);

                var signalMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:Signal|信号)\s*:\s*(\d+)");
                if (signalMatch.Success) int.TryParse(signalMatch.Groups[1].Value, out signal);

                if (line.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("无线电类型", StringComparison.Ordinal))
                {
                    int i = line.IndexOf(':');
                    if (i > 0) radio = line[(i + 1)..].Trim();
                }
            }

            if (!connected && ssid.Length == 0) return null;
            return new WlanLink(ssid, band, channel, rx, tx, signal, radio);
        }
        catch { return null; }
    }

    /// <summary>读取 Windows 防火墙里的网络位置类别（Public/Private/DomainAuthenticated）。</summary>
    public static string GetNetworkCategory(string interfaceAlias)
    {
        try
        {
            string ps = $"$p = Get-NetConnectionProfile -InterfaceAlias '{interfaceAlias}' -ErrorAction SilentlyContinue; " +
                        "if ($p) { ($p | Select-Object -First 1).NetworkCategory } else { 'Unknown' }";
            string outp = Run("powershell", $"-NoProfile -NonInteractive -Command \"{ps}\"", 8000).Trim();
            return string.IsNullOrWhiteSpace(outp) ? "Unknown" : outp;
        }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// 检测“移动热点 / Internet 连接共享”是否占用 Wi-Fi Direct 通道。这是 Miracast 最常见的杀手之一：
    /// Windows 官方明确说明移动热点会破坏 Wi-Fi Direct，表现就是手机端报“共享通道被占用”而本机收不到任何连接事件。
    /// 判据（已按实测校准，避免误报）：
    ///   ① Wi-Fi Direct 虚拟适配器处于 Up 且持有 ICS 默认网段地址（192.168.137.x / 192.168.173.x）——真正的占用；
    ///   ② Windows 移动热点服务(icssvc) 正在运行。
    /// 注意：仅 SharedAccess(ICS) 服务处于 Running 不构成冲突（实测此时 Miracast 仍可正常工作），不再据此报警。
    /// </summary>
    public static string? DetectHotspotConflict()
    {
        var reasons = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            string desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
            bool wfdVirtual = desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                              || desc.Contains("本地连接*", StringComparison.OrdinalIgnoreCase);
            if (!wfdVirtual) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = ua.Address.ToString();
                if (ip.StartsWith("192.168.137.") || ip.StartsWith("192.168.173."))
                    reasons.Add($"Wi-Fi Direct 虚拟适配器「{nic.Name}」已启用并占用 {ip}（移动热点/连接共享正在使用 Wi-Fi Direct 通道）");
            }
        }

        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"(Get-Service icssvc -ErrorAction SilentlyContinue).Status\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                string s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(4000);
                if (s.Equals("Running", StringComparison.OrdinalIgnoreCase))
                    reasons.Add("Windows 移动热点服务(icssvc) 正在运行（移动热点处于开启状态）");
            }
        }
        catch { }

        return reasons.Count == 0 ? null : string.Join("；", reasons);
    }

    /// <summary>是否已存在针对本程序的入站放行规则。</summary>
    public static bool HasFirewallRuleFor(string ruleNamePrefix)
    {
        // 优先直读注册表（进程内、毫秒级）；早期版本用 netsh 枚举全部规则，
        // 每次要几百毫秒并会拉起一个进程，放在界面轮询里会明显吃 CPU。
        try
        {
            if (TryReadFirewallRulesFromRegistry(ruleNamePrefix, out bool found)) return found;
        }
        catch { }

        try
        {
            string outp = Run("netsh", "advfirewall firewall show rule name=all dir=in", 8000);
            return outp.Contains(ruleNamePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TryReadFirewallRulesFromRegistry(string ruleNamePrefix, out bool found)
    {
        found = false;
        const string key = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
        if (baseKey is null) return false;
        foreach (var valueName in baseKey.GetValueNames())
        {
            if (baseKey.GetValue(valueName) is not string rule) continue;
            if (!rule.Contains(ruleNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rule.Contains("Dir=In", StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Contains("Active=FALSE", StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            return true;
        }
        return true;   // 注册表可读，但确实没有规则
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>本机所有可用于回调的 IPv4（DLNA 控制点会从其中任意一个发起连接）。</summary>
    public static IReadOnlyList<IPAddress> GetAllLanIPv4()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                if (list.Any(x => x.Equals(ua.Address))) continue;
                list.Add(ua.Address);
            }
        }
        return list;
    }

    public static string Run(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        // 直接读二进制再自行解码：系统工具的输出可能是 UTF-8（本机 netsh）也可能是控制台代码页 GBK，
        // 用固定编码解码会要么吞掉 ASCII 关键字、要么把中文显示成乱码。
        using var ms = new MemoryStream();
        try { p.StandardOutput.BaseStream.CopyTo(ms); } catch { }
        try { p.StandardError.BaseStream.CopyTo(Stream.Null); } catch { }
        p.WaitForExit(timeoutMs);
        return Decode(ms.ToArray());
    }

    /// <summary>UTF-8 优先、失败再退 GBK、最后退 Latin1 的宽松解码。</summary>
    public static string Decode(byte[] raw)
    {
        if (raw.Length == 0) return "";
        string utf8 = new System.Text.UTF8Encoding(false, false).GetString(raw);
        if (!utf8.Contains('\uFFFD')) return utf8;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            string gbk = System.Text.Encoding.GetEncoding(936).GetString(raw);
            if (!gbk.Contains('\uFFFD')) return gbk;
        }
        catch { }
        return System.Text.Encoding.Latin1.GetString(raw);
    }
}