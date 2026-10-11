using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CastReceiver.Core.AirPlay.Discovery;

/// <summary>一个要广播的 DNS-SD 服务。</summary>
public sealed class MdnsService
{
    /// <summary>服务类型，例如 <c>_airplay._tcp.local</c>。</summary>
    public required string ServiceType { get; init; }

    /// <summary>实例名（含服务类型后缀），例如 <c>PC@DESKTOP._airplay._tcp.local</c>。</summary>
    public required string Instance { get; init; }

    public required int Port { get; init; }

    /// <summary>TXT 键值（顺序即报文顺序，保持与参考实现一致）。</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Txt { get; init; }
}

/// <summary>
/// 极简 mDNS（RFC 6762 / DNS-SD）应答器：只做 AirPlay 发现需要的那部分。
///
/// 与参考实现对齐的几个要点：
/// <list type="bullet">
/// <item>组播地址 <c>224.0.0.251:5353</c>，Windows 下只用 IPv4（参考实现同样在 Windows 关掉 IPv6）。</item>
/// <item>响应只放在 ANSWER 段，<c>QDCOUNT=0</c>、标志 <c>0x8400</c>（QR|AA）。</item>
/// <item>SRV/TXT/A 带 cache-flush 位（class 最高位），PTR 不带。</item>
/// <item>TTL：PTR/TXT = 4500 秒，SRV/A = 120 秒；下线时发 TTL=0 的"告别"。</item>
/// <item>不做探测/冲突检测（参考实现也没有）。</item>
/// </list>
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private const string MulticastAddressV4 = "224.0.0.251";
    private const int MulticastPort = 5353;
    private const int TtlService = 4500;
    private const int TtlHost = 120;

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort TypeAny = 255;

    private readonly IPAddress _localAddress;
    private readonly string _hostName;
    private readonly IReadOnlyList<MdnsService> _services;
    private readonly Action<string>? _log;

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MdnsResponder(IPAddress localAddress, string hostName, IReadOnlyList<MdnsService> services, Action<string>? log = null)
    {
        _localAddress = localAddress ?? throw new ArgumentNullException(nameof(localAddress));
        _hostName = hostName ?? throw new ArgumentNullException(nameof(hostName));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _log = log;
    }

    /// <summary>当前绑定的本地端口（固定 5353，便于自检断言）。</summary>
    public int BoundPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public void Start()
    {
        if (_socket is not null)
        {
            return;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // Windows 上 5353 常被系统自带的 mDNS 解析器占用；不独占才能一起收组播
            ExclusiveAddressUse = false,
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(IPAddress.Parse(MulticastAddressV4), _localAddress));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
            _localAddress.GetAddressBytes());

        _socket = socket;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));

        Announce();
        _log?.Invoke($"mDNS 已启动：{_localAddress}:{MulticastPort}，主机名 {_hostName}");
    }

    public void Stop()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            SendGoodbye();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"发送 mDNS 告别失败：{ex.Message}");
        }

        try
        {
            _cts?.Cancel();
            _socket.Close();
        }
        catch
        {
            // 关闭期间的异常无意义
        }

        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- 发送

    /// <summary>主动广播一遍全部记录（启动时、以及空闲刷新时调用）。</summary>
    public void Announce()
    {
        foreach (MdnsService service in _services)
        {
            byte[] packet = BuildResponse(service, includePtr: true, ttlOverride: null);
            SendMulticast(packet);
        }

        _log?.Invoke("已发送 mDNS 广播通告");
    }

    /// <summary>下线时把 TTL 置 0，让客户端立刻忘掉本机。</summary>
    private void SendGoodbye()
    {
        foreach (MdnsService service in _services)
        {
            SendMulticast(BuildResponse(service, includePtr: true, ttlOverride: 0));
        }
    }

    private void SendMulticast(byte[] packet)
    {
        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        socket.SendTo(packet, new IPEndPoint(IPAddress.Parse(MulticastAddressV4), MulticastPort));
    }

    private void SendUnicast(byte[] packet, IPEndPoint target)
    {
        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.SendTo(packet, target);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"mDNS 单播响应失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 接收

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, any, token).ConfigureAwait(false);

                if (result.ReceivedBytes <= 0)
                {
                    continue;
                }

                var remote = (IPEndPoint)result.RemoteEndPoint;
                HandlePacket(buffer, result.ReceivedBytes, remote);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // 瞬时错误（例如网卡切换）忽略，继续监听
            }
        }
    }

    private void HandlePacket(byte[] packet, int length, IPEndPoint remote)
    {
        if (length < 12)
        {
            return;
        }

        ushort flags = ReadU16(packet, 2);
        if ((flags & 0x8000) != 0)
        {
            return;   // 这是别人的响应，不是查询
        }

        int questionCount = ReadU16(packet, 4);
        if (questionCount == 0)
        {
            return;
        }

        int offset = 12;
        var matched = new List<(MdnsService Service, bool WantPtr)>();

        for (int i = 0; i < questionCount; i++)
        {
            string name = ReadName(packet, ref offset);
            if (offset + 4 > length)
            {
                return;
            }

            ushort qtype = ReadU16(packet, offset);
            ushort qclass = ReadU16(packet, offset + 2);
            offset += 4;

            bool unicastRequested = (qclass & 0x8000) != 0;
            bool typeMatches = qtype == TypeAny;
            string lower = name.ToLowerInvariant();
            int matchedBefore = matched.Count;

            // _services._dns-sd._udp.local 的 PTR 查询：把两个服务类型都报回去
            // （必须 includePtr=true，否则不会发出 "_services -> 服务类型" 这条 PTR）
            if (lower == "_services._dns-sd._udp.local" && (typeMatches || qtype == TypePtr))
            {
                foreach (MdnsService s in _services)
                {
                    matched.Add((s, true));
                }
            }
            else
            {
                foreach (MdnsService service in _services)
                {
                    string type = service.ServiceType.ToLowerInvariant();
                    string instance = service.Instance.ToLowerInvariant();

                    if (lower == type && (typeMatches || qtype == TypePtr || qtype == TypeSrv || qtype == TypeTxt))
                    {
                        matched.Add((service, true));
                    }
                    else if (lower == instance && (typeMatches || qtype == TypeSrv || qtype == TypeTxt))
                    {
                        matched.Add((service, false));
                    }
                    else if (lower == _hostName.ToLowerInvariant() && (typeMatches || qtype == TypeA))
                    {
                        matched.Add((service, false));
                    }
                }
            }

            // 传统查询（源端口不是 5353）或置了 QU 位：除组播外再单播回一份。
            // 这一步必须对**所有**匹配分支生效 —— 客户端通常绑定在临时端口上，
            // 只发组播（目标是 5353）它根本收不到。
            if (matched.Count > matchedBefore && (unicastRequested || remote.Port != MulticastPort))
            {
                foreach (MdnsService s in _services)
                {
                    SendUnicast(BuildResponse(s, includePtr: true, ttlOverride: null), remote);
                }
                return;
            }
        }

        foreach ((MdnsService service, bool wantPtr) in matched)
        {
            SendMulticast(BuildResponse(service, includePtr: wantPtr, ttlOverride: null));
        }

        if (matched.Count > 0)
        {
            _log?.Invoke($"应答复来自 {remote.Address} 的 mDNS 查询（{matched.Count} 项）");
        }
    }

    // ---------------------------------------------------------------- 报文构造

    /// <summary>
    /// 构造一个响应：PTR（可选）+ SRV + TXT + A，全部放在 ANSWER 段。
    /// 参考实现就是这么发的（不用 ADDITIONAL 段）。
    /// </summary>
    private byte[] BuildResponse(MdnsService service, bool includePtr, int? ttlOverride)
    {
        int ttlService = ttlOverride ?? TtlService;
        int ttlHost = ttlOverride ?? TtlHost;

        var answers = new List<byte[]>();
        if (includePtr)
        {
            answers.Add(BuildPtr(service.ServiceType, service.Instance, ttlService));
            answers.Add(BuildPtr("_services._dns-sd._udp.local", service.ServiceType, ttlService));
        }
        answers.Add(BuildSrv(service.Instance, service.Port, ttlHost));
        answers.Add(BuildTxt(service.Instance, service.Txt, ttlService));
        answers.Add(BuildAddress(_hostName, ttlHost));

        var packet = new List<byte>(512);
        WriteU16(packet, 0);                       // 响应里 ID 固定为 0
        WriteU16(packet, 0x8400);                  // QR | AA
        WriteU16(packet, 0);                       // QDCOUNT
        WriteU16(packet, answers.Count);           // ANCOUNT
        WriteU16(packet, 0);                       // NSCOUNT
        WriteU16(packet, 0);                       // ARCOUNT
        foreach (byte[] answer in answers)
        {
            packet.AddRange(answer);
        }
        return packet.ToArray();
    }

    private static byte[] BuildPtr(string name, string target, int ttl)
    {
        var r = new List<byte>();
        WriteName(r, name);
        WriteU16(r, TypePtr);
        WriteU16(r, 1);              // class IN，PTR 不带 cache-flush
        WriteU32(r, (uint)ttl);
        var rdata = new List<byte>();
        WriteName(rdata, target);
        WriteU16(r, rdata.Count);
        r.AddRange(rdata);
        return r.ToArray();
    }

    private byte[] BuildSrv(string instance, int port, int ttl)
    {
        var r = new List<byte>();
        WriteName(r, instance);
        WriteU16(r, TypeSrv);
        WriteU16(r, 0x8001);         // cache-flush | IN
        WriteU32(r, (uint)ttl);
        var rdata = new List<byte>();
        WriteU16(rdata, 0);          // priority
        WriteU16(rdata, 0);          // weight
        WriteU16(rdata, (ushort)port);
        WriteName(rdata, _hostName);
        WriteU16(r, rdata.Count);
        r.AddRange(rdata);
        return r.ToArray();
    }

    private static byte[] BuildTxt(string instance, IReadOnlyList<KeyValuePair<string, string>> txt, int ttl)
    {
        var r = new List<byte>();
        WriteName(r, instance);
        WriteU16(r, TypeTxt);
        WriteU16(r, 0x8001);         // cache-flush | IN
        WriteU32(r, (uint)ttl);
        var rdata = new List<byte>();
        foreach (var pair in txt)
        {
            byte[] item = Encoding.ASCII.GetBytes($"{pair.Key}={pair.Value}");
            if (item.Length > 255)
            {
                continue;
            }
            rdata.Add((byte)item.Length);
            rdata.AddRange(item);
        }
        if (rdata.Count == 0)
        {
            rdata.Add(0);            // 空 TXT 也要有一个长度 0 的条目
        }
        WriteU16(r, rdata.Count);
        r.AddRange(rdata);
        return r.ToArray();
    }

    private byte[] BuildAddress(string hostName, int ttl)
    {
        var r = new List<byte>();
        WriteName(r, hostName);
        WriteU16(r, TypeA);
        WriteU16(r, 0x8001);         // cache-flush | IN
        WriteU32(r, (uint)ttl);
        byte[] addr = _localAddress.GetAddressBytes();
        WriteU16(r, (ushort)addr.Length);
        r.AddRange(addr);
        return r.ToArray();
    }

    // ---------------------------------------------------------------- DNS 编码

    private static void WriteName(List<byte> buffer, string name)
    {
        // DNS-SD 的实例名允许 UTF-8（设备名可能是中文），所以按 UTF-8 写标签
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                bytes = bytes[..63];
            }
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
    }

    private static void WriteU16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteU32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static ushort ReadU16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    /// <summary>解析 DNS 名称，支持压缩指针（查询报文里客户端可能压缩）。</summary>
    private static string ReadName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();
        int pos = offset;
        bool jumped = false;
        int jumps = 0;

        while (pos < data.Length)
        {
            byte length = data[pos];
            if (length == 0)
            {
                pos++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length)
                {
                    break;
                }
                int pointer = ((length & 0x3F) << 8) | data[pos + 1];
                if (!jumped)
                {
                    offset = pos + 2;
                    jumped = true;
                }
                pos = pointer;
                if (++jumps > 32)
                {
                    break;   // 防指针环
                }
                continue;
            }

            pos++;
            if (pos + length > data.Length)
            {
                break;
            }
            if (sb.Length > 0)
            {
                sb.Append('.');
            }
            sb.Append(Encoding.UTF8.GetString(data, pos, length));
            pos += length;
        }

        if (!jumped)
        {
            offset = pos;
        }
        return sb.ToString();
    }
}
