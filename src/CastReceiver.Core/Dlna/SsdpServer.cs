using System.Net;
using System.Net.Sockets;
using System.Text;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Dlna;

/// <summary>
/// SSDP 服务：监听 239.255.255.250:1900 的 M-SEARCH 并单播应答，同时周期性多播 NOTIFY ssdp:alive。
/// 关键点：系统 SSDPSRV 已占用 1900 端口，必须以 SO_REUSEADDR 共享绑定并从正确的网卡应答。
/// </summary>
public sealed class SsdpServer : IDisposable
{
    public const string MulticastAddress = "239.255.255.250";
    public const int MulticastPort = 1900;

    private readonly string _uuid;
    private readonly Func<IPAddress, string> _locationFor;
    private readonly AppLog _log;
    private readonly List<Socket> _receiveSockets = new();
    private readonly Dictionary<IPAddress, Socket> _sendSockets = new();
    private readonly Dictionary<int, IPAddress> _interfaceIndexToAddress = new();
    private readonly List<IPAddress> _interfaces = new();
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _notifyLoop;
    private int _bootId = 1;
    private int _searchReceived;
    private int _receiveErrors;

    public SsdpServer(string uuid, Func<IPAddress, string> locationFor, AppLog log)
    {
        _uuid = uuid;
        _locationFor = locationFor;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public int NotifyCount { get; private set; }
    public int SearchResponseCount { get; private set; }
    public int SearchReceivedCount { get; private set; }

    public event Action<string>? Notice;
    public event Action<IPAddress, string>? SearchReceived;

    public static string ServerHeader => $"Windows/10.0 UPnP/1.0 投屏Window/1.0";

    private IReadOnlyList<string> Targets()
    {
        var list = new List<string>
        {
            "upnp:rootdevice",
            $"uuid:{_uuid}",
            "urn:schemas-upnp-org:device:MediaRenderer:1",
            "urn:schemas-upnp-org:device:MediaRenderer:2",
            "urn:schemas-upnp-org:service:AVTransport:1",
            "urn:schemas-upnp-org:service:AVTransport:2",
            "urn:schemas-upnp-org:service:RenderingControl:1",
            "urn:schemas-upnp-org:service:ConnectionManager:1",
        };
        return list;
    }

    private static string UsnFor(string uuid, string target)
        => target.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? target : $"uuid:{uuid}::{target}";

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _interfaces.Clear();
        _interfaceIndexToAddress.Clear();
        _interfaces.AddRange(NetInfo.GetAllLanIPv4());

        // 建立“接口索引 → 本机地址”映射：接收组播时用它判断报文是从哪块网卡进来的
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                int index;
                try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { index = -1; }
                if (index < 0) continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    _interfaceIndexToAddress[index] = ua.Address;
                }
            }
        }
        catch (Exception ex) { _log.Debug("ssdp", $"接口索引映射构建失败：{ex.Message}"); }

        var primary = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        primary.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        primary.ExclusiveAddressUse = false;
        primary.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        try
        {
            primary.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        }
        catch (Exception ex) { _log.Debug("ssdp", $"PacketInformation 不可用：{ex.Message}"); }

        foreach (var ip in _interfaces)
        {
            try
            {
                primary.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(IPAddress.Parse(MulticastAddress), ip));
                _log.Info("ssdp", $"已加入组播组 {MulticastAddress} @ {ip}");
            }
            catch (Exception ex) { _log.Warn("ssdp", $"在 {ip} 加入组播失败：{ex.Message}"); }

            try
            {
                var send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                send.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                send.Bind(new IPEndPoint(ip, 0));
                send.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                send.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                _sendSockets[ip] = send;
            }
            catch (Exception ex) { _log.Warn("ssdp", $"为 {ip} 创建发送套接字失败：{ex.Message}"); }
        }

        _receiveSockets.Add(primary);
        IsRunning = true;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(primary, _cts.Token));
        _notifyLoop = Task.Run(() => NotifyLoopAsync(_cts.Token));
        _log.Info("ssdp", $"SSDP 服务已启动（UUID={_uuid}，网卡数={_interfaces.Count}）");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { SendNotify(byebye: true); } catch { }
        try { _cts?.Cancel(); } catch { }
        foreach (var s in _receiveSockets) { try { s.Close(); } catch { } }
        foreach (var s in _sendSockets.Values) { try { s.Close(); } catch { } }
        _receiveSockets.Clear();
        _sendSockets.Clear();
        _log.Info("ssdp", "SSDP 服务已停止");
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                SocketFlags flags = SocketFlags.None;
                var result = await socket.ReceiveMessageFromAsync(new ArraySegment<byte>(buffer), flags, remote, ct)
                                         .ConfigureAwait(false);
                if (result.ReceivedBytes <= 0) continue;

                // 关键：远端地址取自返回值 result.RemoteEndPoint（入参在异步重载里不会被回填），
                // 本地入口地址由 result.PacketInformation.Interface（接口索引）推导——
                // PacketInformation.Address 是报文的目的地址（组播组地址），不能当本机地址用。
                var remoteEndpoint = result.RemoteEndPoint as IPEndPoint;
                var localAddress = ResolveLocalAddress(result.PacketInformation.Interface);
                string text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                if (!text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
                Interlocked.Increment(ref _searchReceived);
                SearchReceivedCount = _searchReceived;
                if (remoteEndpoint is null || remoteEndpoint.Port == 0 || localAddress is null)
                {
                    if (_receiveErrors++ < 5)
                        _log.Warn("ssdp", $"M-SEARCH 来源解析异常：remote={result.RemoteEndPoint} ifIndex={result.PacketInformation.Interface} local={localAddress}");
                    continue;
                }
                string st = ParseHeaders(text).GetValueOrDefault("ST", "ssdp:all");
                if (_searchReceived <= 8) _log.Info("ssdp", $"收到 M-SEARCH（来自 {remoteEndpoint}，入口网卡 {localAddress}，ST={st}）");
                try { SearchReceived?.Invoke(remoteEndpoint.Address, st); } catch { }
                _ = Task.Run(() => HandleSearchAsync(text, remoteEndpoint, localAddress, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                if (_receiveErrors++ < 3) _log.Warn("ssdp", $"接收异常：{ex.SocketErrorCode} {ex.Message}");
            }
            catch (Exception ex)
            {
                if (_receiveErrors++ < 3) _log.Warn("ssdp", $"接收循环异常：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private async Task HandleSearchAsync(string request, IPEndPoint remote, IPAddress localAddress, CancellationToken ct)
    {
        var headers = ParseHeaders(request);
        if (!headers.TryGetValue("MAN", out var man) || !man.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase))
            return;
        if (!headers.TryGetValue("ST", out var st)) st = "ssdp:all";
        st = st.Trim();

        // 轻微随机延迟，避免多设备同时应答造成风暴
        try { await Task.Delay(Random.Shared.Next(5, 60), ct).ConfigureAwait(false); } catch { }

        var targets = new List<string>();
        if (st.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase)) targets.AddRange(Targets());
        else if (st.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase)) targets.Add(st);
        else if (Targets().Any(t => t.Equals(st, StringComparison.OrdinalIgnoreCase))) targets.Add(st);
        else
        {
            // 兼容模式：部分国内视频 App 会使用非标准 ST 搜索，这里同样回一个标准 MediaRenderer 描述，
            // 让它们在设备列表里能看到本机（标准控制点会忽略不匹配的应答，无副作用）。
            if (!ShouldAnswerUnknownSearch(st))
            {
                _log.Debug("ssdp", $"忽略无关搜索 ST={st}");
                return;
            }
            targets.Add(st);
            _log.Info("ssdp", $"收到非标准 ST 搜索：{st}（已按兼容模式应答）");
        }

        RaiseSearchNotice(remote, st);

        if (localAddress.Equals(IPAddress.Any)) localAddress = _interfaces.FirstOrDefault() ?? IPAddress.Loopback;
        var socket = GetOrCreateSendSocket(localAddress);
        if (socket is null)
        {
            _log.Warn("ssdp", $"无法为 {localAddress} 建立应答套接字，跳过本次搜索");
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                string response = BuildSearchResponse(target, localAddress);
                byte[] payload = Encoding.UTF8.GetBytes(response);
                // 同一条应答发两遍：UDP 丢一个包，部分手机 App 就再也不显示设备了
                await socket.SendToAsync(payload, SocketFlags.None, remote, ct).ConfigureAwait(false);
                await Task.Delay(25, ct).ConfigureAwait(false);
                await socket.SendToAsync(payload, SocketFlags.None, remote, ct).ConfigureAwait(false);
                SearchResponseCount++;
                if (SearchResponseCount <= 5)
                    _log.Info("ssdp", $"已应答 {remote} 的搜索 ST={target}（LOCATION={_locationFor(localAddress)}）");
            }
            catch (Exception ex) { _log.Debug("ssdp", $"应答失败：{ex.Message}"); }
        }
    }

    private string BuildSearchResponse(string target, IPAddress localAddress)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("CACHE-CONTROL: max-age=1800\r\n");
        sb.Append("DATE: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        sb.Append("EXT:\r\n");
        sb.Append("LOCATION: ").Append(_locationFor(localAddress)).Append("\r\n");
        sb.Append("SERVER: ").Append(ServerHeader).Append("\r\n");
        sb.Append("ST: ").Append(target).Append("\r\n");
        sb.Append("USN: ").Append(UsnFor(_uuid, target)).Append("\r\n");
        sb.Append("BOOTID.UPNP.ORG: ").Append(_bootId).Append("\r\n");
        sb.Append("CONFIGID.UPNP.ORG: 1\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private async Task NotifyLoopAsync(CancellationToken ct)
    {
        // 启动时先广播一轮，之后每 30 秒重播（远小于 max-age=1800/2）
        try { await Task.Delay(150, ct).ConfigureAwait(false); } catch { }
        SendNotify(byebye: false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            SendNotify(byebye: false);
        }
    }

    /// <summary>手动重新广播：先发 byebye 让控制点丢弃缓存，再发 alive 重新发现（用于手机列表里看不到本机时）。</summary>
    public void Rebroadcast()
    {
        if (!IsRunning) return;
        try { SendNotify(byebye: true); } catch { }
        Thread.Sleep(120);
        try { SendNotify(byebye: false); } catch { }
        _log.Info("ssdp", "已手动重新广播（byebye + alive）");
        Notice?.Invoke("已重新广播，请让手机重新搜索投屏设备");
    }

    private void SendNotify(bool byebye)
    {
        foreach (var (ip, socket) in _sendSockets)
        {
            foreach (var target in Targets())
            {
                var sb = new StringBuilder();
                sb.Append("NOTIFY * HTTP/1.1\r\n");
                sb.Append("HOST: ").Append(MulticastAddress).Append(':').Append(MulticastPort).Append("\r\n");
                sb.Append("CACHE-CONTROL: max-age=1800\r\n");
                sb.Append("LOCATION: ").Append(_locationFor(ip)).Append("\r\n");
                sb.Append("NT: ").Append(target).Append("\r\n");
                sb.Append("NTS: ").Append(byebye ? "ssdp:byebye" : "ssdp:alive").Append("\r\n");
                sb.Append("SERVER: ").Append(ServerHeader).Append("\r\n");
                sb.Append("USN: ").Append(UsnFor(_uuid, target)).Append("\r\n");
                sb.Append("BOOTID.UPNP.ORG: ").Append(_bootId).Append("\r\n");
                sb.Append("CONFIGID.UPNP.ORG: 1\r\n");
                sb.Append("\r\n");
                try
                {
                    socket.SendTo(Encoding.UTF8.GetBytes(sb.ToString()),
                        new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort));
                    NotifyCount++;
                }
                catch (Exception ex) { _log.Debug("ssdp", $"NOTIFY 发送失败 @ {ip}：{ex.Message}"); }
            }
        }
        if (!byebye) _log.Debug("ssdp", $"已发送 ssdp:alive 广播（累计 {NotifyCount} 条）");
    }

    /// <summary>该地址是否是本机自己的网卡地址（用于区分“手机在搜索”与“本机自己在搜索”）。</summary>
    public bool IsLocalAddress(IPAddress address) => _interfaces.Any(ip => ip.Equals(address));

    private IPAddress? ResolveLocalAddress(int interfaceIndex)
    {
        if (interfaceIndex > 0 && _interfaceIndexToAddress.TryGetValue(interfaceIndex, out var addr)) return addr;
        return _interfaces.FirstOrDefault();
    }

    private readonly Dictionary<string, DateTime> _noticeThrottle = new();

    /// <summary>节流上报搜索事件：局域网里可能有设备每秒广播一次，避免刷屏（同一来源+ST 每 10 秒最多提示一次）。</summary>
    private void RaiseSearchNotice(IPEndPoint remote, string st)
    {
        // 忽略本机自己发起的搜索（例如 Windows 定时搜索 InternetGatewayDevice），否则「最近动态」会被刷屏
        if (_interfaces.Any(ip => ip.Equals(remote.Address))) return;

        string key = $"{remote.Address}|{st}";
        lock (_noticeThrottle)
        {
            if (_noticeThrottle.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalSeconds < 10) return;
            _noticeThrottle[key] = DateTime.UtcNow;
            if (_noticeThrottle.Count > 200) _noticeThrottle.Clear();
        }
        Notice?.Invoke($"已应答 {remote.Address} 的{ShortLabel(st)}");
    }

    /// <summary>把冗长的 UPnP 搜索类型换成一眼能看懂的短标签。</summary>
    private static string ShortLabel(string st) => st switch
    {
        "urn:schemas-upnp-org:device:MediaRenderer:1" => "投屏设备搜索",
        "urn:schemas-upnp-org:service:AVTransport:1" => "播放控制搜索",
        "upnp:rootdevice" => "设备搜索",
        "ssdp:all" => "全设备搜索",
        _ => "设备搜索",
    };

    private Socket? GetOrCreateSendSocket(IPAddress localAddress)
    {
        lock (_sendSockets)
        {
            if (_sendSockets.TryGetValue(localAddress, out var existing)) return existing;
            try
            {
                var send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                send.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                send.Bind(new IPEndPoint(localAddress, 0));
                send.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
                send.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                _sendSockets[localAddress] = send;
                _log.Info("ssdp", $"已为 {localAddress} 建立应答套接字");
                return send;
            }
            catch (Exception ex)
            {
                _log.Warn("ssdp", $"为 {localAddress} 建立应答套接字失败：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>非标准 ST 的兼容策略：媒体/投屏类搜索照常应答；DIAL、网关、打印机等无关搜索不回，避免污染局域网设备列表。</summary>
    private static bool ShouldAnswerUnknownSearch(string st)
    {
        string s = st.ToLowerInvariant();
        string[] blocked =
        {
            "dial-multiscreen", "internetgatewaydevice", "printer", "scanner", "wfa:", "urn:schemas-wifialliance",
            "urn:schemas-microsoft-com:service", "xbox", "mediaextender",
        };
        return !blocked.Any(b => s.Contains(b));
    }

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            int idx = t.IndexOf(':');
            if (idx <= 0) continue;
            dict[t[..idx].Trim()] = t[(idx + 1)..].Trim();
        }
        return dict;
    }

    public void Dispose() => Stop();
}