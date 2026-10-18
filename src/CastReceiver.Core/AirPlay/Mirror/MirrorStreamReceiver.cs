using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace CastReceiver.Core.AirPlay.Mirror;

/// <summary>
/// 一帧镜像视频（Annex-B H.264）的**借用视图**。
///
/// <see cref="Buffer"/> 来自 <see cref="ArrayPool{T}"/>，只在 <c>FrameReady</c> 回调期间有效；
/// 回调返回后接收端会立刻归还。处理器必须在这之前把数据复制走（<c>AirPlayVideoSource</c>
/// 就是在这里复制进 WinRT IBuffer 的），**不要**把它存进队列/字段。
/// 这样 1080p 关键帧不再每帧分配一个几十~几百 KB 的托管数组（大对象堆），
/// 避免 Gen2 回收造成的周期性卡顿。
/// </summary>
public readonly record struct MirrorFrame(byte[] Buffer, int Length, ulong Timestamp, bool IsKeyFrame)
{
    /// <summary>帧内容（仅在回调期间有效）。</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
}

/// <summary>
/// 接收 AirPlay 镜像视频流并还原出 Annex-B 形式的 H.264。
///
/// 连接方向：**发送端（手机）主动连本机**，所以这里是 TCP 监听端；监听端口在
/// RTSP <c>SETUP</c> 的响应里作为 <c>dataPort</c> 报给手机。
///
/// 报文格式（参考实现 <c>raop_rtp_mirror.c</c>）：反复出现的
/// <c>[128 字节头][payload_size 字节载荷]</c>，头里字段全为小端：
/// <list type="bullet">
/// <item><c>[0:4]</c> payload_size（只算载荷，不含头）</item>
/// <item><c>[4]</c> 类型：0x00 加密视频、0x01 未加密 SPS/PPS、0x02 旧协议心跳、0x05 每秒报表</item>
/// <item><c>[5]</c> 子类型（0x00 非 IDR / 0x10 IDR）</item>
/// <item><c>[6]</c> option 低字节（0x16/0x56/0x1e/0x5e 区分正常与"视频停止"）</item>
/// <item><c>[8:16]</c> 时间戳（LE u64，Q32.32）</item>
/// </list>
///
/// 只有 <c>0x00</c> 的载荷是加密的，而且所有 <c>0x00</c> 载荷**共用一条连续的
/// AES-128-CTR 密钥流**（见 <see cref="Aes128Ctr"/>）；明文包不推进密钥流。
///
/// 解密后是 AVCC 形式（每段 <c>[BE32 长度][NAL]</c>），需要把 4 字节长度就地改写成
/// <c>00 00 00 01</c>，让解码器拿到 Annex-B；SPS/PPS 由本端按时间戳前缀注入。
/// </summary>
public sealed class MirrorStreamReceiver : IDisposable
{
    /// <summary>载荷上限（防御性：真实载荷远小于此，用来避免被坏数据撑爆内存）。</summary>
    private const int MaxPayloadSize = 8 * 1024 * 1024;

    private const byte TypeVideo = 0x00;
    private const byte TypeCodecConfig = 0x01;
    private const byte TypeOldKeepAlive = 0x02;
    private const byte TypeStreamingReport = 0x05;

    /// <summary>直播流的接收缓冲：大一点能吸收 Wi-Fi 的突发，避免应用层被小包唤醒太频繁。</summary>
    private const int ReceiveBufferBytes = 512 * 1024;

    private readonly byte[] _aesKey;
    private readonly byte[] _aesIv;
    private readonly Action<string>? _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private byte[]? _configAnnexB;
    private ulong _configTimestamp;
    private bool _configPending;

    public MirrorStreamReceiver(byte[] aesKey, byte[] aesIv, Action<string>? log = null)
    {
        _aesKey = aesKey ?? throw new ArgumentNullException(nameof(aesKey));
        _aesIv = aesIv ?? throw new ArgumentNullException(nameof(aesIv));
        _log = log;
    }

    /// <summary>监听端口（RTSP SETUP 响应里的 dataPort）。</summary>
    public int Port { get; private set; }

    /// <summary>
    /// 已接收的视频帧（Annex-B 字节 + 原始时间戳 + 是否关键帧）。
    /// 回调是**同步**的、在接收线程上执行；帧内存在回调返回后即被回收，必须当场复制。
    /// </summary>
    public event Action<MirrorFrame>? FrameReady;

    /// <summary>已生成的 Annex-B 帧数。</summary>
    public int FrameCount { get; private set; }
    /// <summary>解析失败的载荷数（解密结果不像 H.264 时计数）。</summary>
    public int InvalidPayloadCount { get; private set; }

    /// <summary>从流里实际收到的字节数（含包头）。码率图表按这个算实测吞吐。</summary>
    public long BytesReceived { get; private set; }

    public bool IsConnected { get; private set; }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        _log?.Invoke($"镜像流监听已启动，dataPort={Port}");
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
        IsConnected = false;
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

            // 直播流：关掉 Nagle。默认开着时小包会被攒着发、还要等对端 ACK，
            // 与延迟 ACK 叠加可平白加上几十毫秒 —— 镜像会明显"发木"。
            try
            {
                client.NoDelay = true;
                client.ReceiveBufferSize = ReceiveBufferBytes;
            }
            catch { }

            IsConnected = true;
            _log?.Invoke("手机已连上镜像流端口");
            try
            {
                await ReadStreamAsync(client, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"镜像流中断：{ex.Message}");
            }
            finally
            {
                IsConnected = false;
                try
                {
                    client.Dispose();
                }
                catch
                {
                    // 忽略
                }
                _log?.Invoke($"镜像流结束，共 {FrameCount} 帧，无效载荷 {InvalidPayloadCount} 个");
            }
        }
    }

    private async Task ReadStreamAsync(TcpClient client, CancellationToken token)
    {
        NetworkStream stream = client.GetStream();
        var header = new byte[128];
        using var cipher = new Aes128Ctr(_aesKey, _aesIv);

        while (!token.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, 0, 128, token).ConfigureAwait(false))
            {
                return;
            }

            uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            byte type = header[4];
            byte optionLow = header[6];
            ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));

            // 实际收到的字节数（含 128 字节包头）：码率图表用的是**实测吞吐**，
            // 而不是协商值 —— 卡顿时两者会差很远，只有实测值能反映真实情况。
            BytesReceived += 128 + payloadSize;

            if (payloadSize > MaxPayloadSize)
            {
                _log?.Invoke($"载荷长度异常（{payloadSize}），断开镜像流");
                return;
            }

            // 载荷走 ArrayPool：1080p 关键帧常有 100–300KB，直接 new 会落进大对象堆，
            // 每秒几十次分配/回收就是周期性卡顿的典型来源。
            byte[] payload = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, payloadSize));
            try
            {
                if (payloadSize > 0
                    && !await ReadExactAsync(stream, payload, 0, (int)payloadSize, token).ConfigureAwait(false))
                {
                    return;
                }

                switch (type)
                {
                    case TypeVideo:
                        HandleVideo(cipher, payload, (int)payloadSize, timestamp);
                        break;

                    case TypeCodecConfig:
                        HandleCodecConfig(payload, (int)payloadSize, optionLow, timestamp);
                        break;

                    case TypeOldKeepAlive:
                    case TypeStreamingReport:
                        // 心跳/报表：明文，读完即丢（不推进密钥流）
                        break;

                    default:
                        _log?.Invoke($"收到未处理的镜像包类型 0x{type:x2}（长度 {payloadSize}）");
                        break;
                }
            }
            finally
            {
                // 事件是同步的：到这里处理器已经复制完（见 FrameReady 的约定）
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>加密视频包：CTR 解密 → AVCC 转 Annex-B → 按需前缀 SPS/PPS。</summary>
    private void HandleVideo(Aes128Ctr cipher, byte[] payload, int length, ulong timestamp)
    {
        if (length == 0)
        {
            return;
        }

        cipher.Transform(payload.AsSpan(0, length));

        if (!TryConvertAvccToAnnexB(payload, length, out int outLength))
        {
            InvalidPayloadCount++;
            _log?.Invoke("解密结果不像合法的 H.264（长度前缀越界或 forbidden bit 置位）");
            return;
        }

        bool keyFrame = IsKeyFrame(payload.AsSpan(0, outLength));

        // SPS/PPS 要跟着关键帧走：解码器每次重新起解（会话开始、重同步、表面重建）都需要
        // 先拿到参数集，否则那一小段就是花屏。缓存里的配置很小（几十字节），
        // 每个 IDR 前面都补一份比"指望时间戳刚好对上"稳得多。
        bool injectCachedConfig = keyFrame
                                  && _configAnnexB is not null
                                  && !StartsWithSps(payload.AsSpan(0, outLength));

        if (injectCachedConfig || (_configPending && _configAnnexB is not null && _configTimestamp == timestamp))
        {
            // 前缀需要一块连续内存，只好再借一块（同样来自池，不落大对象堆）。
            byte[] config = _configAnnexB!;
            byte[] combined = ArrayPool<byte>.Shared.Rent(config.Length + outLength);
            try
            {
                Array.Copy(config, 0, combined, 0, config.Length);
                Array.Copy(payload, 0, combined, config.Length, outLength);
                _configPending = false;

                FrameCount++;
                FrameReady?.Invoke(new MirrorFrame(combined, config.Length + outLength, timestamp, keyFrame));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(combined);
            }
            return;
        }

        // 这里**不要**再切一份：TryConvertAvccToAnnexB 成功时 outLength 恒等于 length
        // （它就是"每段长度前缀都合法、加起来正好等于总长"的判据），所以 payload 本身就是最终帧。
        FrameCount++;
        FrameReady?.Invoke(new MirrorFrame(payload, outLength, timestamp, keyFrame));
    }

    /// <summary>未加密的 SPS/PPS 包：解析出配置，缓存到下一个时间戳匹配的视频包前。</summary>
    private void HandleCodecConfig(byte[] payload, int length, byte optionLow, ulong timestamp)
    {
        // 0x56/0x5e 表示"视频停止"（手机锁屏/切走），此时没有配置可解析
        if (optionLow is 0x56 or 0x5e)
        {
            _configPending = false;
            _log?.Invoke("手机侧视频已暂停");
            return;
        }

        // 0x1e 是 HEVC 配置：本程序不广播 bit 42，故不会协商 H.265，这里只记一笔
        if (optionLow == 0x1e)
        {
            _log?.Invoke("收到 HEVC(VPS/SPS/PPS) 配置，但本程序未协商 H.265，忽略");
            return;
        }

        if (length < 11)
        {
            return;
        }

        int spsSize = (payload[6] << 8) | payload[7];
        int spsOffset = 8;
        if (spsSize <= 0 || spsOffset + spsSize + 3 > length)
        {
            _log?.Invoke("SPS 长度越界，忽略该配置包");
            return;
        }

        int ppsSizeOffset = spsOffset + spsSize + 1;
        int ppsSize = (payload[ppsSizeOffset] << 8) | payload[ppsSizeOffset + 1];
        int ppsOffset = spsOffset + spsSize + 3;
        if (ppsSize <= 0 || ppsOffset + ppsSize > length)
        {
            _log?.Invoke("PPS 长度越界，忽略该配置包");
            return;
        }

        var config = new byte[spsSize + ppsSize + 8];
        int p = 0;
        config[p++] = 0; config[p++] = 0; config[p++] = 0; config[p++] = 1;
        Array.Copy(payload, spsOffset, config, p, spsSize);
        p += spsSize;
        config[p++] = 0; config[p++] = 0; config[p++] = 0; config[p++] = 1;
        Array.Copy(payload, ppsOffset, config, p, ppsSize);

        _configAnnexB = config;
        _configTimestamp = timestamp;
        _configPending = true;
        _log?.Invoke($"已缓存 H.264 配置：SPS {spsSize} 字节 + PPS {ppsSize} 字节");
    }

    /// <summary>
    /// AVCC（<c>[BE32 长度][NAL]</c> 重复）就地改写为 Annex-B（<c>00 00 00 01</c> + NAL）。
    /// 校验与参考实现一致：长度不能越界，且每个 NAL 的 forbidden_zero_bit 必须为 0。
    /// </summary>
    internal static bool TryConvertAvccToAnnexB(byte[] buffer, int length, out int outLength)
    {
        int pos = 0;
        while (pos + 4 <= length)
        {
            int naluLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(pos));

            // 必须用 64 位算：naluLength 可以是接近 int.MaxValue 的值，
            // 于是 pos + 4 + naluLength 会溢出成负数，越界检查失效、下标变负，
            // 坏数据会把接收端直接打成异常而不是"判为无效"。
            long end = (long)pos + 4 + naluLength;
            if (naluLength <= 0 || end > length)
            {
                outLength = 0;
                return false;
            }

            if ((buffer[pos + 4] & 0x80) != 0)
            {
                outLength = 0;
                return false;   // forbidden_zero_bit
            }

            buffer[pos] = 0;
            buffer[pos + 1] = 0;
            buffer[pos + 2] = 0;
            buffer[pos + 3] = 1;
            pos = (int)end;
        }

        outLength = pos;
        return pos == length;
    }

    /// <summary>
    /// 判断一个 Annex-B 帧里是否含 IDR 关键帧（NAL 类型 5）。
    ///
    /// 用途：解码跟不上时**必须从关键帧重新起解**。盲丢最旧的那几帧会让解码器拿到
    /// "引用了不存在帧"的数据，参考链一断就是花屏/卡顿，直到下一个关键帧才恢复。
    /// 注意注入过 SPS/PPS 的帧前面还有 7/8，所以必须逐段扫、不能只看第一段。
    ///
    /// 扫描是安全的：H.264 的 emulation prevention 保证载荷内部不会出现 <c>00 00 00 01</c>。
    /// </summary>
    internal static bool IsKeyFrame(ReadOnlySpan<byte> annexB)
    {
        int pos = 0;
        while (pos + 4 <= annexB.Length)
        {
            // 本程序产出的帧恒为 4 字节起始码（见 TryConvertAvccToAnnexB 与配置注入）
            if (annexB[pos] != 0 || annexB[pos + 1] != 0 || annexB[pos + 2] != 0 || annexB[pos + 3] != 1)
            {
                return false;
            }

            int headerIndex = pos + 4;
            if (headerIndex >= annexB.Length)
            {
                return false;
            }
            if ((annexB[headerIndex] & 0x1F) == 5)
            {
                return true;   // IDR
            }

            int next = IndexOfStartCode(annexB, headerIndex + 1);
            if (next < 0)
            {
                return false;
            }
            pos = next;
        }
        return false;
    }

    /// <summary>这一帧是不是以 SPS（NAL 类型 7）开头（已经带参数集，不必再补）。</summary>
    internal static bool StartsWithSps(ReadOnlySpan<byte> annexB)
        => annexB.Length >= 5
           && annexB[0] == 0 && annexB[1] == 0 && annexB[2] == 0 && annexB[3] == 1
           && (annexB[4] & 0x1F) == 7;

    /// <summary>找下一个 4 字节起始码的位置；没有则返回 -1。</summary>
    private static int IndexOfStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (int i = Math.Max(0, from); i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), token).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }
}
