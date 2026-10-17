using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CastReceiver.Core.AirPlay.Discovery;
using CastReceiver.Core.AirPlay.FairPlay;
using CastReceiver.Core.AirPlay.Mirror;
using CastReceiver.Core.AirPlay.Plist;
using CastReceiver.Core.Devices;

namespace CastReceiver.Core.AirPlay.Rtsp;

/// <summary>
/// AirPlay 的 RTSP/HTTP 控制通道（一个 TCP 监听端口，同时承载控制与配对握手）。
///
/// 端口不是固定 7000：实际绑定端口要经 mDNS SRV 广播出去，手机连的是广播的那个。
///
/// 实现的端点（照参考实现的语义）：
/// <c>OPTIONS</c>、<c>GET /info</c>、<c>POST /fp-setup</c>、<c>SETUP</c>、
/// <c>RECORD</c>、<c>SET_PARAMETER</c>、<c>GET_PARAMETER</c>、<c>POST /feedback</c>、<c>TEARDOWN</c>。
///
/// 配对（<c>/pair-setup</c>、<c>/pair-verify</c>）**不实现**：因为广播的 features 第 27 位为 0，
/// 客户端不会发起配对（见 <see cref="AirPlayAdvertisement"/>）。若客户端仍然发了，
/// 这里按参考实现在"未配对"时的行为回一个空的 200，让流程继续。
/// </summary>
public sealed class AirPlayRtspServer : IDisposable
{
    private readonly AirPlayOptions _options;
    private readonly Action<string>? _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public AirPlayRtspServer(AirPlayOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>
    /// mDNS 广播的服务定义。要等 <see cref="Start"/> 之后才能确定端口，所以是可后置赋值的。
    /// 目前只被 <c>GET /info</c> 的 TXT 分支用到。
    /// </summary>
    public IReadOnlyList<MdnsService> Services { get; set; } = Array.Empty<MdnsService>();

    public int Port { get; private set; }

    /// <summary>镜像视频帧就绪（Annex-B 字节 + 原始时间戳）。</summary>
    public event Action<string, MirrorFrame>? FrameReady;

    /// <summary>
    /// 某路镜像流开始。第一个参数是**会话 id** —— 必须带上它：
    /// 现在允许多台设备同时连，事件里不带 id 就分不清帧是谁的。
    /// </summary>
    public event Action<string, MirrorStreamReceiver>? MirrorStarted;

    /// <summary>控制通道连接结束（TEARDOWN 或对端断开），界面据此收起画面。</summary>
    public event Action<string>? SessionEnded;

    /// <summary>识别出客户端设备（来自 SETUP 的 plist 与 User-Agent，能得到多少报多少）。</summary>
    public event Action<string, DeviceIdentity>? IdentityReported;

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(5);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        _log?.Invoke($"RTSP 控制通道已启动，端口 {Port}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // 关闭期间异常无意义
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(client, token), CancellationToken.None);
        }
    }

    /// <summary>一个连接上的会话状态。</summary>
    private sealed class Session
    {
        /// <summary>会话 id：多台设备同时连时用来区分各自的帧与控制通道。</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

        public FairPlaySession FairPlay { get; } = new();

        public byte[]? AesKey { get; set; }

        public MirrorStreamReceiver? Mirror { get; set; }

        public UdpClient? Timing { get; set; }

        public UdpClient? AudioData { get; set; }

        public UdpClient? AudioControl { get; set; }

        public double VolumeDb { get; set; }

        /// <summary>识别出的客户端设备（第一次 SETUP 时填一次）。</summary>
        public DeviceIdentity? Identity { get; set; }

        public void Close()
        {
            Mirror?.Dispose();
            Timing?.Dispose();
            AudioData?.Dispose();
            AudioControl?.Dispose();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        var session = new Session();
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        _log?.Invoke($"控制通道新连接：{remote}");

        try
        {
            // 控制通道全是小包（SETUP / 每秒一次的 feedback）：关掉 Nagle，
            // 否则每个来回都要等 ACK，握手与保活会平白慢几十毫秒。
            try { client.NoDelay = true; } catch { }
            NetworkStream stream = client.GetStream();
            while (!token.IsCancellationRequested)
            {
                RtspRequest? request = await RtspRequest.ReadAsync(stream, token).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                RtspResponse? response = Dispatch(session, request);
                if (response is null)
                {
                    continue;   // 没有 CSeq 的请求按参考实现不回（BLE 探测例外，已在 Dispatch 里处理）
                }

                string audioJack = request.Method == "RECORD"
                    ? "connected; type=analog"
                    : "connected; type=digital";
                byte[] bytes = response.Serialize(request.Protocol, request.CSeq, audioJack, AirPlayAdvertisement.SourceVersion);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);

                if (response.CloseConnection)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"控制通道连接结束：{ex.Message}");
        }
        finally
        {
            session.Close();
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略
            }
            _log?.Invoke($"控制通道断开：{remote}");
            SessionEnded?.Invoke(session.Id);
        }
    }

    private RtspResponse? Dispatch(Session session, RtspRequest request)
    {
        string url = request.Url;

        // 没有 CSeq：参考实现直接不回；只有 URL 里点明要 TXT 的探测才应答
        if (!request.HasCSeq)
        {
            if (url.Contains("txtAirPlay", StringComparison.OrdinalIgnoreCase)
                || url.Contains("txtRAOP", StringComparison.OrdinalIgnoreCase))
            {
                return BuildTxtResponse(url.Contains("txtRAOP", StringComparison.OrdinalIgnoreCase));
            }
            return null;
        }

        switch (request.Method)
        {
            case "OPTIONS":
                return new RtspResponse
                {
                    Headers = { new("Public", "SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER") },
                };

            case "GET" when url.Contains("/info", StringComparison.Ordinal):
                return HandleInfo(request);

            case "POST" when url.Contains("/fp-setup", StringComparison.Ordinal):
                return HandleFpSetup(session, request);

            case "POST" when url.Contains("/feedback", StringComparison.Ordinal):
                return new RtspResponse();   // 心跳，空 200

            case "POST" when url.Contains("/pair-setup", StringComparison.Ordinal):
            case "POST" when url.Contains("/pair-verify", StringComparison.Ordinal):
                // 我们广播的是"不支持 legacy pairing"，正常不会走到这里。
                // 真来了就按参考实现"未配对"时的行为回空 200，让客户端继续。
                _log?.Invoke($"收到配对请求 {url}（预期不会发生），按未配对处理");
                return new RtspResponse();

            case "SETUP":
                return HandleSetup(session, request);

            case "RECORD":
                // 参考实现回 <audio_delay_micros * 44100 / 1e6> = 11025
                return new RtspResponse { Headers = { new("Audio-Latency", "11025") } };

            case "SET_PARAMETER":
                return HandleSetParameter(session, request);

            case "GET_PARAMETER":
                return HandleGetParameter(session, request);

            case "FLUSH":
                return new RtspResponse();

            case "TEARDOWN":
                session.Close();
                return new RtspResponse { CloseConnection = true };

            default:
                _log?.Invoke($"未实现的 RTSP 方法：{request.Method} {url}");
                return new RtspResponse { StatusCode = 501, Reason = "Not Implemented" };
        }
    }

    private RtspResponse BuildTxtResponse(bool wantRaop)
    {
        MdnsService? service = Services.FirstOrDefault(s =>
            wantRaop
                ? s.ServiceType.StartsWith("_raop", StringComparison.Ordinal)
                : s.ServiceType.StartsWith("_airplay", StringComparison.Ordinal));
        if (service is null)
        {
            return new RtspResponse { Body = BinaryPlist.Write(new Dictionary<string, object?>()) };
        }

        string key = wantRaop ? "txtRAOP" : "txtAirPlay";
        var dict = new Dictionary<string, object?> { [key] = AirPlayInfoPlist.RawTxt(service) };
        return Plist(dict);
    }

    private RtspResponse HandleInfo(RtspRequest request)
    {
        // 带 Content-Type 的探测：只回 mDNS TXT 原始字节
        if (request.Header("Content-Type") is not null)
        {
            bool wantRaop = false;
            try
            {
                if (BinaryPlist.Read(request.Body) is Dictionary<string, object?> body
                    && body.TryGetValue("qualifier", out object? q) && q is List<object?> list && list.Count > 0
                    && list[0] is string qualifier)
                {
                    wantRaop = qualifier.Contains("RAOP", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                // 体不是 plist 就按默认（airplay）处理
            }
            return BuildTxtResponse(wantRaop);
        }

        Dictionary<string, object?> info = AirPlayInfoPlist.Build(_options, _options.PkHex, Port);
        return Plist(info);
    }

    private RtspResponse HandleFpSetup(Session session, RtspRequest request)
    {
        switch (request.Body.Length)
        {
            case 16:
            {
                var response = new byte[142];
                if (!session.FairPlay.Setup(request.Body, response))
                {
                    return new RtspResponse { StatusCode = 501, Reason = "Not Implemented" };
                }
                return new RtspResponse
                {
                    Body = response,
                    Headers = { new("Content-Type", "application/octet-stream") },
                };
            }

            case 164:
            {
                var response = new byte[32];
                if (!session.FairPlay.Handshake(request.Body, response))
                {
                    return new RtspResponse { StatusCode = 501, Reason = "Not Implemented" };
                }
                return new RtspResponse
                {
                    Body = response,
                    Headers = { new("Content-Type", "application/octet-stream") },
                };
            }

            default:
                _log?.Invoke($"fp-setup 长度异常：{request.Body.Length}");
                return new RtspResponse();
        }
    }

    private RtspResponse HandleSetup(Session session, RtspRequest request)
    {
        Dictionary<string, object?>? body;
        try
        {
            body = BinaryPlist.Read(request.Body) as Dictionary<string, object?>;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"SETUP 的 plist 解析失败：{ex.Message}");
            return new RtspResponse { StatusCode = 400, Reason = "Bad Request" };
        }

        if (body is null)
        {
            return new RtspResponse { StatusCode = 400, Reason = "Bad Request" };
        }

        var response = new Dictionary<string, object?>(StringComparer.Ordinal);

        // 设备识别：第一次 SETUP 的 plist 里带 deviceID / model / name，请求头里有 User-Agent。
        // 这几个字段与 UxPlay 读的是同一批（raop_handlers.h:756-759、816）。
        // 拿不到就是拿不到 —— 不猜，界面会退化成显示原始名字。
        if (session.Identity is null)
        {
            DeviceIdentity identity = DeviceModelCatalog.FromAirPlay(
                GetString(body, "model"), GetString(body, "name"), GetString(body, "deviceID"),
                GetString(body, "osName"), GetString(body, "osVersion"), request.Header("User-Agent"));
            session.Identity = identity;
            _log?.Invoke("识别到设备：" + identity.Describe()
                         + (identity.UserAgent is null ? "" : $"（UA {identity.UserAgent}）"));
            try { IdentityReported?.Invoke(session.Id, identity); } catch { }
        }

        // 第一次 SETUP：ekey/eiv，初始化密钥与时间端口
        if (body.TryGetValue("ekey", out object? ekeyObj) && body.TryGetValue("eiv", out object? eivObj)
            && ekeyObj is byte[] ekey && eivObj is byte[] eiv)
        {
            if (ekey.Length < 72)
            {
                _log?.Invoke($"ekey 长度不足 72（{ekey.Length}）");
                return new RtspResponse { StatusCode = 400, Reason = "Bad Request" };
            }

            var aesKey = new byte[16];
            if (!session.FairPlay.Decrypt(ekey.AsSpan(0, 72).ToArray(), aesKey))
            {
                _log?.Invoke("fairplay_decrypt 失败（可能没先做 fp-setup 握手）");
                return new RtspResponse { StatusCode = 400, Reason = "Bad Request" };
            }
            session.AesKey = aesKey;

            // 本程序不发 NTP 请求，但响应里要给一个端口；eventPort 在镜像模式下恒为 0
            session.Timing ??= new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            response["timingPort"] = (long)((IPEndPoint)session.Timing.Client.LocalEndPoint!).Port;
            response["eventPort"] = 0L;

            _log?.Invoke($"SETUP：已解出 AES 密钥，eiv {eiv.Length} 字节");
        }

        // 第二次 SETUP：streams
        if (body.TryGetValue("streams", out object? streamsObj) && streamsObj is List<object?> streams)
        {
            var resultStreams = new List<object?>();
            foreach (object? item in streams)
            {
                if (item is not Dictionary<string, object?> stream)
                {
                    continue;
                }

                long type = ToLong(stream.GetValueOrDefault("type"));
                if (type == 110)
                {
                    if (session.AesKey is null)
                    {
                        _log?.Invoke("请求镜像流但还没有 AES 密钥（缺少第一次 SETUP）");
                        return new RtspResponse { StatusCode = 400, Reason = "Bad Request" };
                    }

                    long connectionId = ToLong(stream.GetValueOrDefault("streamConnectionID"));
                    (byte[] key, byte[] iv) = DeriveVideoKey(session.AesKey, (ulong)connectionId);

                    var mirror = new MirrorStreamReceiver(key, iv, _log);
                    mirror.FrameReady += frame => FrameReady?.Invoke(session.Id, frame);
                    mirror.Start();
                    session.Mirror = mirror;
                    MirrorStarted?.Invoke(session.Id, mirror);

                    resultStreams.Add(new Dictionary<string, object?>
                    {
                        ["type"] = 110L,
                        ["dataPort"] = (long)mirror.Port,
                    });
                    _log?.Invoke($"SETUP：镜像流已就绪，dataPort={mirror.Port}，streamConnectionID={connectionId}");
                }
                else if (type == 96)
                {
                    // 音频本程序不实现，但仍要报端口，否则客户端可能直接报错
                    session.AudioData ??= new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    session.AudioControl ??= new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    resultStreams.Add(new Dictionary<string, object?>
                    {
                        ["type"] = 96L,
                        ["dataPort"] = (long)((IPEndPoint)session.AudioData.Client.LocalEndPoint!).Port,
                        ["controlPort"] = (long)((IPEndPoint)session.AudioControl.Client.LocalEndPoint!).Port,
                    });
                    _log?.Invoke("SETUP：客户端请求了音频流（本程序不支持音频，仅返回端口）");
                }
                else
                {
                    _log?.Invoke($"SETUP：不支持的流类型 {type}，断开连接");
                    return new RtspResponse { CloseConnection = true };
                }
            }
            response["streams"] = resultStreams;
        }

        return Plist(response);
    }

    private RtspResponse HandleSetParameter(Session session, RtspRequest request)
    {
        string? contentType = request.Header("Content-Type");
        if (contentType is null)
        {
            return new RtspResponse { StatusCode = 451, Reason = "Parameter not understood" };
        }

        if (contentType.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase))
        {
            string text = Encoding.ASCII.GetString(request.Body);
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("volume:", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(line[7..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                {
                    session.VolumeDb = db;
                    _log?.Invoke($"音量设为 {db:0.###} dB");
                }
            }
        }

        return new RtspResponse();
    }

    private RtspResponse HandleGetParameter(Session session, RtspRequest request)
    {
        string? contentType = request.Header("Content-Type");
        if (contentType is null)
        {
            return new RtspResponse { StatusCode = 451, Reason = "Parameter not understood" };
        }

        string text = Encoding.ASCII.GetString(request.Body);
        if (text.Contains("volume", StringComparison.OrdinalIgnoreCase))
        {
            string body = string.Create(CultureInfo.InvariantCulture, $"volume: {session.VolumeDb,9:0.000000}\r\n");
            return new RtspResponse
            {
                Body = Encoding.ASCII.GetBytes(body),
                Headers = { new("Content-Type", "text/parameters") },
            };
        }

        return new RtspResponse();   // 其余当作保活
    }

    private static RtspResponse Plist(Dictionary<string, object?> dict)
        => new()
        {
            Body = BinaryPlist.Write(dict),
            Headers = { new("Content-Type", "application/x-apple-binary-plist") },
        };

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        ulong u => (long)u,
        int i => i,
        _ => 0L,
    };

    /// <summary>从 plist 里取字符串；不是字符串就拿不到（宁可没有，也不要瞎转）。</summary>
    private static string? GetString(Dictionary<string, object?> body, string key)
        => body.TryGetValue(key, out object? value) && value is string text && text.Length > 0 ? text : null;

    /// <summary>
    /// 镜像视频的密钥派生（参考实现 <c>mirror_buffer.c</c>）：
    /// <c>SHA512("AirPlayStreamKey" + 十进制 streamConnectionID ‖ aeskey16)[0:16]</c>，
    /// IV 同理但前缀是 <c>"AirPlayStreamIV"</c>。注意是 SHA-512（源码注释里的 "sha-256" 是错的）。
    /// </summary>
    internal static (byte[] Key, byte[] Iv) DeriveVideoKey(byte[] aesKey, ulong streamConnectionId)
    {
        string id = streamConnectionId.ToString(CultureInfo.InvariantCulture);
        byte[] key = Derive(aesKey, "AirPlayStreamKey" + id);
        byte[] iv = Derive(aesKey, "AirPlayStreamIV" + id);
        return (key, iv);
    }

    private static byte[] Derive(byte[] aesKey, string prefix)
    {
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var material = new byte[prefixBytes.Length + aesKey.Length];
        Array.Copy(prefixBytes, material, prefixBytes.Length);
        Array.Copy(aesKey, 0, material, prefixBytes.Length, aesKey.Length);

        byte[] hash = SHA512.HashData(material);
        return hash[..16];
    }
}
