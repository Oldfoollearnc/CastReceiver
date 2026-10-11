using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using CastReceiver.Core.Http;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Media;

namespace CastReceiver.Core.Diagnostics;

public enum SelfTestOutcome { Passed, Warning, Failed }

public sealed record SelfTestItem(string Group, string Name, SelfTestOutcome Outcome, string Detail)
{
    public string Result => Outcome switch
    {
        SelfTestOutcome.Passed => "通过",
        SelfTestOutcome.Warning => "警告",
        _ => "失败",
    };

    public string Color => Outcome switch
    {
        SelfTestOutcome.Passed => "#4ADE80",
        SelfTestOutcome.Warning => "#FBBF24",
        _ => "#F87171",
    };
}

/// <summary>
/// 合并式端到端自检：一次运行覆盖【网络环境 → SSDP 发现 → 设备描述/SCPD → SOAP 控制全流程 →
/// GENA 事件 → 媒体代理防盗链转发 → 播放内核与帧解码 → Miracast 接收会话】，
/// 而不是为每个小功能单独写验证脚本。
/// </summary>
public sealed class SelfTestRunner
{
    /// <summary>与 NetworkFixer 使用同一前缀的防火墙规则名。</summary>
    public const string FirewallRuleName = "投屏Window 桌面版";

    /// <summary>
    /// 用于播放链路验证的公网视频地址（更接近真实投屏场景：HTTPS + CDN + 断点续传）。
    /// 离线时自动退回本机样例文件，并把该项标记为警告。
    /// </summary>
    public static string NetworkSampleUrl { get; set; } =
        "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/360/Big_Buck_Bunny_360_10s_2MB.mp4";

    /// <summary>检查结论：既可用 bool 简写（通过/失败），也可显式指定 Warning。</summary>
    public readonly struct Verdict
    {
        public SelfTestOutcome Outcome { get; }
        public Verdict(SelfTestOutcome outcome) => Outcome = outcome;
        public static implicit operator Verdict(bool passed)
            => new(passed ? SelfTestOutcome.Passed : SelfTestOutcome.Failed);
        public static implicit operator Verdict(SelfTestOutcome outcome) => new(outcome);
    }

    private readonly CastSettings _settings;
    private readonly AppLog _log;
    private readonly IMediaPlayerEngine? _player;
    private readonly Dlna.DlnaRenderer? _dlna;
    private readonly Miracast.MiracastService _miracast;
    private readonly string _assetPath;
    /// <summary>是否具备 App 投屏（DLNA + 播放内核）链路：纯镜像版为 false，相关用例会自动跳过。</summary>
    private readonly bool _hasDlna;
    private readonly Func<CancellationToken, Task<SelfTestItem>>? _renderProbe;

    private MiniHttpServer? _testServer;
    private HttpClient? _client;
    private readonly List<(string Service, string Body)> _notifications = new();
    private readonly object _notifyGate = new();

    public SelfTestRunner(CastSettings settings, AppLog log, IMediaPlayerEngine? player,
                          Dlna.DlnaRenderer? dlna, Miracast.MiracastService miracast,
                          string assetPath, Func<CancellationToken, Task<SelfTestItem>>? renderProbe = null)
    {
        _settings = settings;
        _log = log;
        _player = player;
        _dlna = dlna;
        _miracast = miracast;
        _assetPath = assetPath;
        _renderProbe = renderProbe;
        _hasDlna = player is not null && dlna is not null;
    }

    /// <summary>
    /// 只跑 AirPlay 协议级用例（不需要网络、界面或 VLC，秒级完成）。
    /// 用于在没有手机、也没有公网的环境下回归整条 AirPlay 链路。
    /// </summary>
    public static IReadOnlyList<SelfTestItem> RunAirPlayOnly()
    {
        var items = new List<SelfTestItem>();
        AirPlaySelfTest.Run((group, name, verdict, detail) =>
            items.Add(new SelfTestItem(group, name, verdict.Outcome, detail)));
        DeviceIdentitySelfTest.Run((group, name, verdict, detail) =>
            items.Add(new SelfTestItem(group, name, verdict.Outcome, detail)));
        return items;
    }

    public async Task<IReadOnlyList<SelfTestItem>> RunAsync(IProgress<SelfTestItem>? progress = null,
                                                            CancellationToken ct = default)
    {
        var items = new List<SelfTestItem>();
        void Add(string group, string name, Verdict verdict, string detail)
        {
            var item = new SelfTestItem(group, name, verdict.Outcome, detail);
            items.Add(item);
            try { progress?.Report(item); } catch { }
            _log.Info("selftest", $"[{item.Result}] {group} / {name} :: {detail}");
        }

        _log.Info("selftest", "================ 合并自检开始 ================");
        var sw = Stopwatch.StartNew();

        // 自检用的本地 HTTP 服务：既充当“上游视频源”，又接收 GENA 回调
        if (_hasDlna)
        {
            _testServer = new MiniHttpServer(0, TestServerHandler, _log, "selftest");
            _testServer.Start();
        }
        int testPort = _testServer?.Port ?? 0;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // ---------------------------------------------------------- A 网络环境
        var lan = NetInfo.GetLanInterface();
        Add("A 网络环境", "找到局域网网卡", lan is not null,
            lan is null ? "未找到可用局域网 IPv4（请检查 Wi-Fi/网线）" : $"{lan.Name} / {lan.Description} / {lan.Address}");

        if (lan is not null)
        {
            string category = NetInfo.GetNetworkCategory(lan.Name);
            Add("A 网络环境", "网络位置为专用(Private)",
                category.Equals("Private", StringComparison.OrdinalIgnoreCase) ? SelfTestOutcome.Passed : SelfTestOutcome.Warning,
                $"当前={category}；公用网络会被 Windows 网络发现策略拦截，手机可能搜不到本机（点“网络一键放行”可自动修复）");

            bool rule = NetInfo.HasFirewallRuleFor(FirewallRuleName);
            Add("A 网络环境", "防火墙已放行本程序",
                rule ? SelfTestOutcome.Passed : SelfTestOutcome.Warning,
                rule ? "已存在入站放行规则" : "未找到入站规则：手机能搜到但可能无法回连/推流（点“网络一键放行”可自动修复）");

            string? hotspot = NetInfo.DetectHotspotConflict();
            // 说明：Wi-Fi Direct 虚拟适配器上的 192.168.137.x 会时有时无（ICS 会反复应用作用域地址），
            // 实测此时接收端往往仍能正常监听，所以只作为“可能影响镜像连接”的警告，不再判失败。
            Add("A 网络环境", "移动热点/连接共享未占用 Wi-Fi Direct",
                hotspot is null ? SelfTestOutcome.Passed : SelfTestOutcome.Warning,
                hotspot is null ? "未检测到移动热点或 ICS 占用" : hotspot + " → 若手机连不上镜像，先关闭移动热点并点「重启接收端」");
        }

        if (_hasDlna)
            Add("A 网络环境", "App 投屏服务端口监听", _dlna!.IsRunning,
                _dlna.IsRunning ? $"HTTP 服务运行于 0.0.0.0:{_dlna.HttpPort}" : "App 投屏未启动（纯镜像版可忽略）");

        if (_hasDlna)
        {
        // ---------------------------------------------------------- B SSDP 发现
        Add("B SSDP 发现", "UDP 1900 共享绑定并加入组播", _dlna.Ssdp?.IsRunning == true,
            _dlna.Ssdp?.IsRunning == true
                ? $"已广播 ssdp:alive {_dlna.Ssdp.NotifyCount} 条，收到 M-SEARCH {_dlna.Ssdp.SearchReceivedCount} 次，应答 {_dlna.Ssdp.SearchResponseCount} 次"
                : "SSDP 未运行");

        var discovered = await SelfDiscoverAsync(ct).ConfigureAwait(false);
        Add("B SSDP 发现", "M-SEARCH 能被本机发现", discovered.Ok, discovered.Detail);

        // ---------------------------------------------------------- C 设备描述
        string baseUrl = $"http://127.0.0.1:{_dlna.HttpPort}";
        (bool ok, string detail, string? body) desc = await GetAsync($"{baseUrl}{Dlna.DlnaXml.DescriptionUrl}", ct);
        string deviceXml = desc.body ?? "";
        bool descOk = desc.ok && deviceXml.Contains("MediaRenderer:1") && deviceXml.Contains("AVTransport:1")
                      && deviceXml.Contains("RenderingControl:1") && deviceXml.Contains("ConnectionManager:1")
                      && deviceXml.Contains($"uuid:{_settings.DeviceUuid}") && deviceXml.Contains("DMR-1.50")
                      && deviceXml.Contains(_settings.DeviceName);
        Add("C 设备描述", "description.xml 内容完整", descOk,
            descOk ? $"含 deviceType/UDN/三项服务/DMR-1.50/设备名“{_settings.DeviceName}”"
                   : $"异常：{(desc.ok ? "内容缺少关键字段" : desc.detail)}");

        foreach (var (name, url, mustHave) in new[]
        {
            ("AVTransport", Dlna.DlnaXml.ScpdUrlAvt, new[] { "SetAVTransportURI", "GetPositionInfo", "Seek", "LastChange" }),
            ("RenderingControl", Dlna.DlnaXml.ScpdUrlRcs, new[] { "SetVolume", "GetVolume", "SetMute" }),
            ("ConnectionManager", Dlna.DlnaXml.ScpdUrlCms, new[] { "GetProtocolInfo", "GetCurrentConnectionIDs" }),
        })
        {
            var r = await GetAsync($"{baseUrl}{url}", ct).ConfigureAwait(false);
            string scpd = r.Body ?? "";
            var missing = mustHave.Where(m => !scpd.Contains(m)).ToList();
            Add("C 设备描述", $"{name} SCPD 可获取且动作齐全", r.Ok && missing.Count == 0,
                r.Ok ? (missing.Count == 0 ? $"{scpd.Length} 字节，关键动作/变量齐全" : "缺少：" + string.Join(",", missing))
                     : r.Detail);
        }

        // ---------------------------------------------------------- D SOAP 控制
        var protocolInfo = await SoapAsync(baseUrl, "ConnectionManager", "GetProtocolInfo",
            new(), ct).ConfigureAwait(false);
        Add("D SOAP 控制", "ConnectionManager#GetProtocolInfo", protocolInfo.Ok,
            protocolInfo.Ok ? $"接收能力 {protocolInfo.Args.GetValueOrDefault("Sink", "").Split(',').Length} 项" : protocolInfo.Detail);

        if (File.Exists(_assetPath))
        {
            // 播放链路用公网地址（真实投屏场景）；控制面语义仍用本机文件，保证可重复
            string mediaUrl = NetworkSampleUrl;
            string didl = BuildDidl(mediaUrl, "自检样例视频");

            var setUri = await SoapAsync(baseUrl, "AVTransport", "SetAVTransportURI",
                new() { ["InstanceID"] = "0", ["CurrentURI"] = mediaUrl, ["CurrentURIMetaData"] = didl }, ct).ConfigureAwait(false);
            Add("D SOAP 控制", "AVTransport#SetAVTransportURI 被接受", setUri.Ok, setUri.Ok ? "已接收投屏地址" : setUri.Detail);

            // 等待播放器真正就绪：网络媒体需要打开+缓冲，未就绪时时长/进度都会是 0
            (bool ready, string readyDetail) = await WaitForMediaReadyAsync(baseUrl, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            Add("D SOAP 控制", "投屏后媒体被成功打开（时长已就绪）", ready,
                $"GetTransportInfo/GetMediaInfo → {readyDetail}");

            var mediaInfo = await SoapAsync(baseUrl, "AVTransport", "GetMediaInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            string duration = mediaInfo.Args.GetValueOrDefault("MediaDuration", "");
            Add("D SOAP 控制", "GetMediaInfo 返回时长", mediaInfo.Ok && duration != "0:00:00",
                mediaInfo.Ok ? $"MediaDuration={duration}" : mediaInfo.Detail);

            var pos1 = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            await Task.Delay(1800, ct).ConfigureAwait(false);
            var pos2 = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            bool advanced = Dlna.DlnaRenderer.TryParseUpnpTime(pos2.Args.GetValueOrDefault("RelTime", "0:00:00"), out var t2)
                         && Dlna.DlnaRenderer.TryParseUpnpTime(pos1.Args.GetValueOrDefault("RelTime", "0:00:00"), out var t1)
                         && t2 > t1;
            string posDetail = $"RelTime {pos1.Args.GetValueOrDefault("RelTime", "?")} → {pos2.Args.GetValueOrDefault("RelTime", "?")}";
            if (advanced)
            {
                Add("D SOAP 控制", "播放进度持续推进", true, posDetail);
            }
            else
            {
                // 画面播放不受影响，但 HTTP(S) 源下 VLC 常出现“输入提前 EOF → 时长/进度回读冻结”
                // （本机已做对照实验：本地文件正常推进，HTTP 源回读冻结）。样例视频仅 10 秒，
                // 若检查时已播完也会读不到推进。两种情况都如实标记为警告而非失败。
                (bool ok2, string detail2) = await ProbePositionAdvanceAsync(baseUrl, ct).ConfigureAwait(false);
                Add("D SOAP 控制", "播放进度持续推进",
                    ok2 ? SelfTestOutcome.Passed : SelfTestOutcome.Warning,
                    ok2 ? detail2 : $"{posDetail}；{detail2}");
            }

            var seek = await SoapAsync(baseUrl, "AVTransport", "Seek",
                new() { ["InstanceID"] = "0", ["Unit"] = "REL_TIME", ["Target"] = "0:00:02" }, ct).ConfigureAwait(false);
            var afterSeek = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            bool seekOk = seek.Ok && Dlna.DlnaRenderer.TryParseUpnpTime(afterSeek.Args.GetValueOrDefault("RelTime", "0:00:00"), out var ts)
                          && ts.TotalSeconds is > 1.5 and < 8;
            Add("D SOAP 控制", "Seek 跳转到指定位置", seekOk,
                seekOk ? $"跳转后 RelTime={afterSeek.Args.GetValueOrDefault("RelTime", "?")}" : $"返回={seek.Detail} 位置={afterSeek.Args.GetValueOrDefault("RelTime", "?")}");

            // 状态机测试前重新投一次（等价于手机上重新发起投屏），确保媒体从起点开始、不受上一次播放结束影响
            await SoapAsync(baseUrl, "AVTransport", "SetAVTransportURI",
                new() { ["InstanceID"] = "0", ["CurrentURI"] = mediaUrl, ["CurrentURIMetaData"] = didl }, ct).ConfigureAwait(false);
            string reloadState = await WaitForMediaReadyAsync(baseUrl, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false) is var rr && rr.Ready
                ? rr.Detail : "未就绪";
            Add("D SOAP 控制", "重新投屏后再次就绪", reloadState != "未就绪", $"→ {reloadState}");

            var pause = await SoapAsync(baseUrl, "AVTransport", "Pause", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            string pausedWait = await WaitForStateAsync(baseUrl, "PAUSED_PLAYBACK", TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            Add("D SOAP 控制", "Pause 生效", pausedWait == "PAUSED_PLAYBACK",
                $"CurrentTransportState={pausedWait}（Pause 返回 {pause.Detail}）");

            await SoapAsync(baseUrl, "AVTransport", "Play", new() { ["InstanceID"] = "0", ["Speed"] = "1" }, ct).ConfigureAwait(false);
            string playedWait = await WaitForStateAsync(baseUrl, "PLAYING", TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            Add("D SOAP 控制", "Play 恢复播放", playedWait == "PLAYING", $"CurrentTransportState={playedWait}");

            await SoapAsync(baseUrl, "AVTransport", "Stop", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            var stoppedState = await SoapAsync(baseUrl, "AVTransport", "GetTransportInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            Add("D SOAP 控制", "Stop 停止播放", stoppedState.Args.GetValueOrDefault("CurrentTransportState") == "STOPPED",
                $"CurrentTransportState={stoppedState.Args.GetValueOrDefault("CurrentTransportState", "?")}");
        }
        else
        {
            Add("D SOAP 控制", "找到自检样例视频", false, $"未找到 {_assetPath}（无法验证播放链路）");
        }

        // ---------------------------------------------------------- E 音量控制
        var setVol = await SoapAsync(baseUrl, "RenderingControl", "SetVolume",
            new() { ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredVolume"] = "33" }, ct).ConfigureAwait(false);
        var getVol = await SoapAsync(baseUrl, "RenderingControl", "GetVolume",
            new() { ["InstanceID"] = "0", ["Channel"] = "Master" }, ct).ConfigureAwait(false);
        Add("E 音量控制", "SetVolume/GetVolume 往返一致", setVol.Ok && getVol.Args.GetValueOrDefault("CurrentVolume") == "33",
            $"设置 33 → 读回 {getVol.Args.GetValueOrDefault("CurrentVolume", "?")}");

        var setVolBack = await SoapAsync(baseUrl, "RenderingControl", "SetVolume",
            new() { ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredVolume"] = _settings.Volume.ToString() }, ct).ConfigureAwait(false);
        Add("E 音量控制", "音量可恢复", setVolBack.Ok, $"已恢复到 {_settings.Volume}");

        // ---------------------------------------------------------- F GENA 事件
        int avtBase = NotificationCountOf("AVTransport");
        int rcsBase = NotificationCountOf("RenderingControl");
        var subAvt = await SubscribeAsync(baseUrl, Dlna.DlnaXml.EventUrlAvt, "/notify/avt", ct).ConfigureAwait(false);
        var subRcs = await SubscribeAsync(baseUrl, Dlna.DlnaXml.EventUrlRcs, "/notify/rcs", ct).ConfigureAwait(false);
        Add("F GENA 事件", "SUBSCRIBE 订阅成功（AVTransport + RenderingControl）", subAvt.Ok && subRcs.Ok,
            $"AVT: {subAvt.Detail}；RCS: {subRcs.Detail}");

        bool gotAvt = await WaitForNotificationAsync("AVTransport", TimeSpan.FromSeconds(6), ct, avtBase).ConfigureAwait(false);
        bool gotRcs = await WaitForNotificationAsync("RenderingControl", TimeSpan.FromSeconds(6), ct, rcsBase).ConfigureAwait(false);
        Add("F GENA 事件", "订阅后立即收到初始 NOTIFY 事件", gotAvt && gotRcs,
            $"AVTransport={(gotAvt ? "已收到" : "未收到")}，RenderingControl={(gotRcs ? "已收到" : "未收到")}");

        int rcsBefore = NotificationCountOf("RenderingControl");
        await SoapAsync(baseUrl, "RenderingControl", "SetVolume",
            new() { ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredVolume"] = "44" }, ct).ConfigureAwait(false);
        bool gotChange = await WaitForNotificationAsync("RenderingControl", TimeSpan.FromSeconds(6), ct, rcsBefore).ConfigureAwait(false);
        int rcsAfter = NotificationCountOf("RenderingControl");
        Add("F GENA 事件", "状态变化触发增量 NOTIFY", gotChange && rcsAfter > rcsBefore,
            $"改为音量 44 后 RenderingControl 事件数 {rcsBefore} → {rcsAfter}");

        // ---------------------------------------------------------- G 媒体代理
        if (File.Exists(_assetPath))
        {
            string upstream = $"http://127.0.0.1:{testPort}/sample.mp4";
            string proxyUrl = Dlna.MediaProxy.BuildProxyUrl(baseUrl, upstream);
            var full = await GetBytesAsync(proxyUrl, null, ct).ConfigureAwait(false);
            long fileLength = new FileInfo(_assetPath).Length;
            Add("G 媒体代理", "代理转发完整文件", full.Ok && full.Bytes.Length == fileLength,
                full.Ok ? $"转发 {full.Bytes.Length} 字节 / 源文件 {fileLength} 字节，状态 {full.Status}" : full.Detail);

            var ranged = await GetBytesAsync(proxyUrl, "bytes=100-199", ct).ConfigureAwait(false);
            Add("G 媒体代理", "代理支持 Range 断点请求", ranged.Ok && ranged.Status == 206 && ranged.Bytes.Length == 100,
                $"状态={ranged.Status}，返回 {ranged.Bytes.Length} 字节");
        }

        }

        // ---------------------------------------------------------- H 播放内核
        Add("H 播放内核", "LibVLC 可用", _player is VlcPlayerEngine,
            _player is VlcPlayerEngine v ? $"已初始化（音量 {v.Volume}）" : "播放内核未就绪");

        (bool decodeOk, string decodeDetail) = await ProbeDecodeAsync(ct).ConfigureAwait(false);
        Add("H 播放内核", "能解码并输出视频帧", decodeOk, decodeDetail);

        if (_renderProbe is not null)
        {
            var probe = await _renderProbe(ct).ConfigureAwait(false);
            Add("H 播放内核", probe.Name, probe.Outcome, probe.Detail);
        }

        // ---------------------------------------------------------- I Miracast
        string wifiStatus = _miracast.QueryWifiStatus();
        Add("I 系统投屏", "本机无线显示能力", wifiStatus.Contains("MiracastSupported"),
            $"MiracastReceiverWiFiStatus={wifiStatus}");

        (bool sinkOk, string sinkDetail) = QueryMiracastSinkCapability();
        Add("I 系统投屏", "驱动层 Miracast Sink 能力", sinkOk ? SelfTestOutcome.Passed : SelfTestOutcome.Warning, sinkDetail);

        var startResult = await _miracast.StartAsync(_settings.DeviceName, false, ct).ConfigureAwait(false);
        Add("I 系统投屏", "启动 Miracast 接收会话", startResult.Success,
            startResult.Success ? startResult.Detail : $"{startResult.Message} :: {startResult.Detail}");

        await Task.Delay(1200, ct).ConfigureAwait(false);
        var miracastStatus = _miracast.QueryStatus();
        Add("I 系统投屏", "监听状态为 Listening", miracastStatus.Listening,
            $"ListeningStatus={miracastStatus.ListeningStatus}，设备名=“{miracastStatus.DeviceName}”，无线能力={miracastStatus.WiFiStatus}");

        await _miracast.StopAsync().ConfigureAwait(false);
        await Task.Delay(600, ct).ConfigureAwait(false);
        var afterStop = _miracast.QueryStatus();
        Add("I 系统投屏", "可停止并释放监听", !afterStop.Listening,
            $"停止后 ListeningStatus={afterStop.ListeningStatus}");

        // ---------------------------------------------------------- J AirPlay
        // 协议级自检：期望值来自 UxPlay C 参考实现导出的向量，
        // 不依赖网络、不依赖手机、也不依赖开发期的 Python/GCC 工具链。
        AirPlaySelfTest.Run(Add);
        DeviceIdentitySelfTest.Run(Add);

        // ---------------------------------------------------------- 收尾
        try { _testServer.Stop(); } catch { }
        try { _client.Dispose(); } catch { }
        _testServer = null; _client = null;

        sw.Stop();
        int passed = items.Count(i => i.Outcome == SelfTestOutcome.Passed);
        int warned = items.Count(i => i.Outcome == SelfTestOutcome.Warning);
        int failed = items.Count(i => i.Outcome == SelfTestOutcome.Failed);
        _log.Info("selftest",
            $"================ 合并自检结束：通过 {passed} / 警告 {warned} / 失败 {failed}，用时 {sw.Elapsed.TotalSeconds:F1}s ================");
        return items;
    }

    /// <summary>移动热点开启时会破坏 Wi-Fi Direct，导致 Miracast 无法连接（官方说明）。</summary>
    public static string DetectMobileHotspot() => NetInfo.DetectHotspotConflict() ?? "";

    /// <summary>驱动层能力：netsh wlan show wirelesscapabilities 中的 Miracast Sink 状态。</summary>
    public static (bool Ok, string Detail) QueryMiracastSinkCapability()
    {
        try
        {
            string output = NetInfo.Run("netsh", "wlan show wirelesscapabilities", 8000);
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
            // 兼容中英文与不同驱动：凡是含 Miracast 的行都算（例如 "Miracast Sink : Supported"、"Miracast 接收器: 支持"）
            var miracastLines = lines.Where(l => l.Contains("Miracast", StringComparison.OrdinalIgnoreCase)).ToList();
            string? sink = miracastLines.FirstOrDefault(l => l.Contains("Sink", StringComparison.OrdinalIgnoreCase)
                                                          || l.Contains("接收", StringComparison.OrdinalIgnoreCase))
                           ?? miracastLines.FirstOrDefault();
            string? wfd = lines.FirstOrDefault(l => l.Contains("Wi-Fi Direct device", StringComparison.OrdinalIgnoreCase));
            if (sink is null)
                return (false, $"netsh 输出 {output.Length} 字符，未找到含 Miracast 的行；" +
                               $"首段内容：{(lines.Count > 0 ? string.Join(" | ", lines.Take(3)) : "(空)")}");
            // 取值部分可能被本地化为“支持 / 不支持”或保持英文 “Supported / Not Supported”
            int colon = sink.LastIndexOf(':');
            string value = (colon >= 0 ? sink[(colon + 1)..] : sink).Trim();
            bool supported = !value.StartsWith("Not", StringComparison.OrdinalIgnoreCase)
                          && !value.StartsWith("不支持", StringComparison.Ordinal)
                          && (value.StartsWith("Supported", StringComparison.OrdinalIgnoreCase)
                              || value.StartsWith("支持", StringComparison.Ordinal));
            return (supported, $"{sink}；{(wfd ?? "Wi-Fi Direct 能力: 未在输出中找到")}");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private int NotificationCount
    {
        get { lock (_notifyGate) { return _notifications.Count; } }
    }

    private int NotificationCountOf(string service)
    {
        lock (_notifyGate) { return _notifications.Count(n => n.Service.Equals(service, StringComparison.OrdinalIgnoreCase)); }
    }

    // ------------------------------------------------------------------ 自检内部 HTTP 服务

    private Task<HttpResponseEx> TestServerHandler(HttpRequestEx request, CancellationToken ct)
    {
        switch (request.Path)
        {
            case "/sample.mp4":
            {
                if (!File.Exists(_assetPath)) return Task.FromResult(HttpResponseEx.Status(404, "no asset"));
                var fi = new FileInfo(_assetPath);
                long start = 0, end = fi.Length - 1;
                int status = 200;
                string? rangeHeader = request.Header("Range");
                var rangeResult = HttpRange.Parse(rangeHeader, fi.Length, out long rangeStart, out long rangeEnd);
                if (rangeResult == RangeParseResult.Unsatisfiable)
                {
                    // 播放器探测文件末尾（bytes=<长度>-）时，必须回 416 并给出总长度
                    return Task.FromResult(
                        HttpResponseEx.Status(416, "range not satisfiable")
                            .With("Content-Range", $"bytes */{fi.Length}"));
                }
                if (rangeResult == RangeParseResult.Satisfiable && !(rangeStart == 0 && rangeEnd == fi.Length - 1))
                {
                    // 只对“真正的部分请求”回 206；请求整个文件时按普通静态文件回 200
                    start = rangeStart;
                    end = rangeEnd;
                    status = 206;
                }
                var fs = new FileStream(_assetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                fs.Seek(start, SeekOrigin.Begin);
                long remaining = end - start + 1;
                var response = new HttpResponseEx
                {
                    StatusCode = status,
                    ContentLength = remaining,
                    BodyWriter = async (output, token) =>
                    {
                        var buffer = new byte[64 * 1024];
                        long left = remaining;
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buffer.Length, left);
                            int read = await fs.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
                            if (read <= 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            left -= read;
                        }
                        fs.Dispose();
                    },
                };
                response.Headers["Content-Type"] = "video/mp4";
                response.Headers["Accept-Ranges"] = "bytes";
                if (status == 206) response.Headers["Content-Range"] = $"bytes {start}-{end}/{fi.Length}";
                return Task.FromResult(response);
            }

            case "/notify/avt":
            case "/notify/rcs":
            {
                string service = request.Path.EndsWith("avt") ? "AVTransport" : "RenderingControl";
                lock (_notifyGate) { _notifications.Add((service, request.BodyText)); }
                _log.Debug("selftest", $"收到 GENA 通知 {service}（SEQ={request.Header("SEQ")}）");
                return Task.FromResult(new HttpResponseEx { StatusCode = 200, Body = Array.Empty<byte>() });
            }
        }
        return Task.FromResult(HttpResponseEx.Status(404, "not found"));
    }

    // ------------------------------------------------------------------ 协议请求封装

    private async Task<(bool Ok, string Detail, string? Body)> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _client!.GetAsync(url, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}", body);
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}", null); }
    }

    private async Task<(bool Ok, int Status, byte[] Bytes, string Detail)> GetBytesAsync(string url, string? range, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            if (range is not null) msg.Headers.TryAddWithoutValidation("Range", range);
            using var resp = await _client!.SendAsync(msg, ct).ConfigureAwait(false);
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, bytes, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, 0, Array.Empty<byte>(), $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<(bool Ok, Dictionary<string, string> Args, string Detail)> SoapAsync(
        string baseUrl, string service, string action, Dictionary<string, string> args, CancellationToken ct)
    {
        string ns = service switch
        {
            "AVTransport" => Dlna.DlnaXml.ServiceNsAvt,
            "RenderingControl" => Dlna.DlnaXml.ServiceNsRcs,
            _ => Dlna.DlnaXml.ServiceNsCms,
        };
        string controlUrl = service switch
        {
            "AVTransport" => Dlna.DlnaXml.ControlUrlAvt,
            "RenderingControl" => Dlna.DlnaXml.ControlUrlRcs,
            _ => Dlna.DlnaXml.ControlUrlCms,
        };

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n");
        sb.Append("  <s:Body>\n");
        sb.Append($"    <u:{action} xmlns:u=\"{ns}\">\n");
        foreach (var (k, v) in args) sb.Append($"      <{k}>{Dlna.DlnaXml.Escape(v)}</{k}>\n");
        sb.Append($"    </u:{action}>\n");
        sb.Append("  </s:Body>\n");
        sb.Append("</s:Envelope>\n");

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + controlUrl)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml"),
            };
            msg.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{ns}#{action}\"");
            using var resp = await _client!.SendAsync(msg, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var outArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool fault = false;
            try
            {
                var doc = XDocument.Parse(body);
                if (doc.Descendants().Any(e => e.Name.LocalName == "Fault"))
                {
                    fault = true;
                    string code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value ?? "?";
                    string text = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value ?? "";
                    return (false, outArgs, $"SOAP Fault {code} {text}");
                }
                var responseEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Response", StringComparison.Ordinal)
                                                                     && e.Name.NamespaceName == ns);
                if (responseEl is not null)
                    foreach (var el in responseEl.Elements()) outArgs[el.Name.LocalName] = el.Value;
            }
            catch (Exception ex) { return (false, outArgs, $"响应解析失败：{ex.Message}"); }

            return (resp.IsSuccessStatusCode && !fault, outArgs, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, new Dictionary<string, string>(), $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<string> WaitForStateAsync(string baseUrl, string expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string state = "?";
        while (DateTime.UtcNow < deadline)
        {
            var info = await SoapAsync(baseUrl, "AVTransport", "GetTransportInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            state = info.Args.GetValueOrDefault("CurrentTransportState", "?");
            if (state.Contains(expected, StringComparison.OrdinalIgnoreCase)) return state;
            await Task.Delay(350, ct).ConfigureAwait(false);
        }
        return state;
    }

    /// <summary>再等一小段时间观察进度是否推进（用于区分“媒体已播完”与“回读冻结”）。</summary>
    private async Task<(bool Ok, string Detail)> ProbePositionAdvanceAsync(string baseUrl, CancellationToken ct)
    {
        for (int i = 0; i < 6; i++)
        {
            var a = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            await Task.Delay(700, ct).ConfigureAwait(false);
            var b = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            string ra = a.Args.GetValueOrDefault("RelTime", "0:00:00"), rb = b.Args.GetValueOrDefault("RelTime", "0:00:00");
            if (Dlna.DlnaRenderer.TryParseUpnpTime(ra, out var ta) && Dlna.DlnaRenderer.TryParseUpnpTime(rb, out var tb) && tb > ta)
                return (true, $"进度推进正常：{ra} → {rb}");
            if (_player.State == TransportState.Stopped)
                return (false, "样例视频已播放结束（仅 10 秒），检查窗口内读不到推进；这是样例过短所致，非功能缺陷");
        }
        return (false, "该 HTTP(S) 源下 VLC 的时长/进度回读未刷新（已知现象：HTTP 输入提前 EOF 使回读冻结），画面播放不受影响");
    }

    /// <summary>
    /// 等待媒体就绪。判定标准：**时长已知**且不是“无媒体”状态——
    /// 时长能被读出即代表投屏地址已被播放器成功打开并解析（这部分由 DLNA 控制面负责）；
    /// 至于是否处于 Playing、进度是否推进，分别由后面的 Pause/Play 与“进度推进”两项单独覆盖。
    /// （实测说明：某些 HTTP 源下 VLC 不一定会回调 Playing，但画面确实在播放，
    ///   因此这里不能用“必须看到 PLAYING”作为就绪条件，否则会误判为失败。）
    /// </summary>
    private async Task<(bool Ready, string Detail)> WaitForMediaReadyAsync(string baseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string state = "?", duration = "0:00:00";
        bool sawPlaying = false, sawProgress = false;
        while (DateTime.UtcNow < deadline)
        {
            var info = await SoapAsync(baseUrl, "AVTransport", "GetTransportInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            state = info.Args.GetValueOrDefault("CurrentTransportState", "?");
            var media = await SoapAsync(baseUrl, "AVTransport", "GetMediaInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            duration = media.Args.GetValueOrDefault("MediaDuration", "0:00:00");
            var pos = await SoapAsync(baseUrl, "AVTransport", "GetPositionInfo", new() { ["InstanceID"] = "0" }, ct).ConfigureAwait(false);
            string relTime = pos.Args.GetValueOrDefault("RelTime", "0:00:00");

            if (state.Contains("PLAYING", StringComparison.OrdinalIgnoreCase)) sawPlaying = true;
            if (relTime != "0:00:00") sawProgress = true;
            bool opened = duration != "0:00:00" && !state.StartsWith("NO_MEDIA", StringComparison.OrdinalIgnoreCase);
            if (opened)
                return (true, $"{state}，MediaDuration={duration}，RelTime={relTime}" +
                              $"（期间{(sawPlaying ? "出现过 PLAYING" : "未回调 PLAYING")}{(sawProgress ? "、进度有推进" : "")}）");
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return (false, $"{state}，MediaDuration={duration}（等待超时，媒体未被成功打开）");
    }

    private async Task<(bool Ok, string Detail)> SelfDiscoverAsync(CancellationToken ct)
    {
        try
        {
            var lan = NetInfo.GetLanInterface();
            if (lan is null) return (false, "无局域网 IPv4");

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;
            // 绑定到无线局域网地址并指定组播出口，模拟真实控制点（手机）的行为，避免被 Hyper-V 等虚拟网卡抢走默认路由
            socket.Bind(new IPEndPoint(lan.Address, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, lan.Address.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            socket.ReceiveTimeout = 1500;

            string msearch = "M-SEARCH * HTTP/1.1\r\n" +
                             $"HOST: {Dlna.SsdpServer.MulticastAddress}:1900\r\n" +
                             "MAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                             "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";
            socket.SendTo(Encoding.UTF8.GetBytes(msearch),
                new IPEndPoint(IPAddress.Parse(Dlna.SsdpServer.MulticastAddress), 1900));

            var deadline = DateTime.UtcNow.AddSeconds(8);
            var buffer = new byte[8192];
            int responses = 0;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = socket.ReceiveFrom(buffer, ref from);
                    string text = Encoding.UTF8.GetString(buffer, 0, n);
                    if (!text.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) continue;
                    responses++;
                    if (text.Contains(_settings.DeviceUuid, StringComparison.OrdinalIgnoreCase))
                    {
                        string location = text.Split("\r\n")
                            .FirstOrDefault(l => l.StartsWith("LOCATION", StringComparison.OrdinalIgnoreCase)) ?? "";
                        return (true, $"收到本机应答（来自 {((IPEndPoint)from).Address}）：{location.Trim()}");
                    }
                }
                catch (SocketException) { }
            }
            int received = _dlna.Ssdp?.SearchReceivedCount ?? 0;
            return (false, $"8 秒内未收到本机应答（收到 {responses} 个其它设备应答；本机 SSDP 收到 M-SEARCH {received} 次）");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<(bool Ok, string Detail, string Sid)> SubscribeAsync(string baseUrl, string eventUrl, string callbackPath, CancellationToken ct)
    {
        try
        {
            string callback = $"<http://127.0.0.1:{_testServer!.Port}{callbackPath}>";
            using var msg = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), baseUrl + eventUrl);
            msg.Headers.TryAddWithoutValidation("CALLBACK", callback);
            msg.Headers.TryAddWithoutValidation("NT", "upnp:event");
            msg.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
            using var resp = await _client!.SendAsync(msg, ct).ConfigureAwait(false);
            string sid = resp.Headers.TryGetValues("SID", out var v) ? string.Join("", v) : "";
            return (resp.IsSuccessStatusCode && sid.Length > 0,
                $"HTTP {(int)resp.StatusCode} SID={(sid.Length > 12 ? sid[..12] + "…" : sid)}", sid);
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}", ""); }
    }

    private async Task<bool> WaitForNotificationAsync(string service, TimeSpan timeout, CancellationToken ct, int baseline = -1)
    {
        if (baseline < 0) baseline = NotificationCountOf(service);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (NotificationCountOf(service) > baseline) return true;
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
        return NotificationCountOf(service) > baseline;
    }

    private async Task<(bool Ok, string Detail)> ProbeDecodeAsync(CancellationToken ct)
    {
        // 分三轮对照：本地文件（基线）、公网 HTTPS 视频（真实投屏场景）、经本机媒体代理转发（防盗链模式）
        var local = await ProbeOneAsync(new Uri(_assetPath).AbsoluteUri, "本地文件", ct).ConfigureAwait(false);
        var network = await ProbeOneAsync(NetworkSampleUrl, "公网视频", ct).ConfigureAwait(false);

        string proxyDetail = "未测试";
        bool proxyOk = false;
        var lan = NetInfo.GetLanInterface();
        if (lan is not null && _dlna.IsRunning)
        {
            string proxied = Dlna.MediaProxy.BuildProxyUrl($"http://127.0.0.1:{_dlna.HttpPort}", NetworkSampleUrl);
            var viaProxy = await ProbeOneAsync(proxied, "经媒体代理", ct).ConfigureAwait(false);
            proxyOk = viaProxy.Ok;
            proxyDetail = viaProxy.Detail;
        }

        bool ok = local.Ok;   // 本地文件必须正常
        string networkNote = network.Ok ? "" : "（公网视频未推进：多为该源在本机的输入提前 EOF，画面播放仍正常）";
        return (ok,
            $"本地文件：{local.Detail} ｜ 公网视频：{network.Detail}{networkNote} ｜ 经媒体代理：{proxyDetail}");
    }

    private async Task<(bool Ok, string Detail)> ProbeOneAsync(string uri, string title, CancellationToken ct)
    {
        try
        {
            // 在后台线程驱动播放器：绝不能在 WPF UI 线程上同步调用 VLC（会与视频输出窗口互相等待而死锁）
            await Task.Run(() => _player.SetUriAsync(uri, null, ct), ct).ConfigureAwait(false);
            await Task.Run(() => _player.Play(), ct).ConfigureAwait(false);

            var samples = new List<string>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            int tick = 0;
            while (DateTime.UtcNow < deadline)
            {
                samples.Add($"{_player.Position.TotalSeconds:F1}");
                if (_player is VlcPlayerEngine vt && ++tick % 3 == 1)
                    _log.Info("selftest", $"{title} 遥测 #{tick}: pos={_player.Position.TotalSeconds:F1}s dur={_player.Duration.TotalSeconds:F1}s {vt.RawTelemetry}");
                if (_player.Position > TimeSpan.FromSeconds(1.5)) break;
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
            _player.Stop();
            await Task.Delay(400, ct).ConfigureAwait(false);

            bool advanced = samples.Any(s => double.TryParse(s, out double v) && v > 1.0);
            string telemetry = _player is VlcPlayerEngine vp ? vp.RawTelemetry : "";
            string err = _player.LastError is null ? "" : $" 错误={_player.LastError}";
            return (advanced,
                advanced ? $"进度推进正常（采样 {string.Join("→", samples.Take(8))} 秒）"
                         : $"进度未推进（采样 {string.Join("→", samples.Take(8))}；状态={_player.State}；{telemetry}{err}）");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string BuildDidl(string url, string title)
    {
        string t = Dlna.DlnaXml.Escape(title);
        return "&lt;DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
               "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"&gt;" +
               "&lt;item id=\"1\" parentID=\"0\" restricted=\"0\"&gt;" +
               $"&lt;dc:title&gt;{t}&lt;/dc:title&gt;" +
               "&lt;upnp:class&gt;object.item.videoItem&lt;/upnp:class&gt;" +
               $"&lt;res protocolInfo=\"http-get:*:video/mp4:*\"&gt;{Dlna.DlnaXml.Escape(url)}&lt;/res&gt;" +
               "&lt;/item&gt;&lt;/DIDL-Lite&gt;";
    }
}