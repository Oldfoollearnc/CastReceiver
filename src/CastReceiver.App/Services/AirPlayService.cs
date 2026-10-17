using CastReceiver.App.Interop;
using CastReceiver.Core.AirPlay;
using CastReceiver.Core.AirPlay.Discovery;
using CastReceiver.Core.AirPlay.Mirror;
using CastReceiver.Core.AirPlay.Rtsp;
using CastReceiver.Core.Devices;
using CastReceiver.Core.Infrastructure;
using Windows.Media.Core;

namespace CastReceiver.App.Services;

/// <summary>
/// AirPlay 接收端的运行期封装（iPhone/iPad 投屏）。
///
/// 把三层串起来：
/// <list type="number">
/// <item>mDNS 广播（<see cref="MdnsResponder"/> + <see cref="AirPlayAdvertisement"/>）——
/// 手机的投屏列表靠它才能看到本机；</item>
/// <item>RTSP 控制通道（<see cref="AirPlayRtspServer"/>）—— 握手、FairPlay、SETUP 交出 dataPort；</item>
/// <item>视频源（<see cref="AirPlayVideoSource"/>）—— 把收到的 H.264 交给现有渲染链路。</item>
/// </list>
///
/// <b>多设备（③）</b>：一台设备一条 <see cref="AirPlaySession"/>，各自拥有**独立的**
/// <see cref="AirPlayVideoSource"/>（即独立的解码器）。这一点是并排显示的前提 ——
/// 早先所有会话共用一块 <c>MediaPlayer</c> 时，两路帧混着推会花屏，所以那时只敢推聚焦那一路；
/// 现在每路各有自己的解码器，就必须**每一路都推**，否则没被聚焦的那几路解码器收不到数据、格子是全黑的。
/// </summary>
public sealed class AirPlayService : IDisposable
{
    private readonly AppLog _log;
    private readonly Action<string>? _publish;

    private MdnsResponder? _responder;
    private AirPlayRtspServer? _server;

    /// <summary>启动时保存的设置/链路引用：改画质档位或网络变化时重算"下一次连接"的画质。</summary>
    private CastSettings? _settings;
    private AirPlayOptions? _options;
    private NetInfo.WlanLink? _link;

    /// <summary>本次/下一次连接向手机请求的画质（写进 /info 的 width/height/maxFPS）。</summary>
    private AirPlayQualityProfile _profile = new(1280, 720, 30, "低延迟", "默认");

    /// <summary>连续多少段健康会话（用于撤销自动降档，避免来回抖动）。</summary>
    private int _goodSessions;

    /// <summary>所有活动会话（一台设备一条），按接入先后排序 —— 界面上的序号就按它来。</summary>
    private readonly List<AirPlaySession> _sessions = new();

    /// <summary>当前「放大呈现」的那一路（多设备时就是主画面那一路）。</summary>
    private string? _activeSessionId;

    /// <summary>
    /// 设备识别结果先到、会话后到 —— 必须这样缓存一下。
    ///
    /// 实测顺序：**第一次 SETUP** 就带 deviceID/model/name（识别在这里上报），
    /// 而**第二次 SETUP** 的 streams 才让镜像流开始（会话在这里才登记）。
    /// 早先只在会话列表里查，于是识别结果每次都查不到、被默默丢掉，
    /// 界面只能显示兜底名字（多设备并排的格子标题就是靠它）。
    /// </summary>
    private readonly Dictionary<string, DeviceIdentity> _pendingIdentities = new();

    public AirPlayService(AppLog log, Action<string>? publish)
    {
        _log = log;
        _publish = publish;
    }

    /// <summary>AirPlay 接收端是否在运行。</summary>
    public bool IsRunning => _server is not null;

    /// <summary>正在接收的会话数（AirPlay 与 Miracast 各自独立）。</summary>
    public int SessionCount
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>当前会话快照（多设备并排的界面用）；顺序即"第几路"。</summary>
    public IReadOnlyList<AirPlaySession> Sessions
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.ToList();
            }
        }
    }

    /// <summary>按 id 取会话；没有则为 null。</summary>
    public AirPlaySession? Find(string sessionId)
    {
        lock (_sessions)
        {
            return _sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>当前正在呈现的会话的帧数（状态文字用）。</summary>
    public long FrameCount { get; private set; }

    /// <summary>所有会话实测收到的字节数合计（码率图表用；不是协商值）。</summary>
    public long BytesReceived
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Sum(s => s.BytesReceived);
            }
        }
    }

    /// <summary>当前正在呈现的会话 id（没有则为 null）。</summary>
    public string? FocusedSessionId => _activeSessionId;

    /// <summary>当前呈现的那一路。</summary>
    public AirPlaySession? FocusedSession => _activeSessionId is null ? null : Find(_activeSessionId);

    /// <summary>
    /// 切换正在呈现的会话（多设备切换 ④）。
    /// 会重新抛出 <see cref="MediaSourceCreated"/>，让界面把"主画面"落到这一路上；
    /// 旧那一路的画面不必销毁 —— 会话本身还在收流，只是不再放大。
    /// </summary>
    public bool FocusSession(string sessionId)
    {
        AirPlaySession? session = Find(sessionId);
        if (session is null || string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        _activeSessionId = sessionId;
        FrameCount = 0;

        _publish?.Invoke($"已切换到 {(session.Identity?.Describe() ?? session.Label)}");
        StatusChanged?.Invoke($"AirPlay 接收中：{session.Label}");
        try
        {
            if (session.MediaSource is { } source)
            {
                MediaSourceCreated?.Invoke(this, source);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("airplay", $"交付 MediaSource 失败：{ex.Message}");
        }

        RaiseSessionsChanged();
        return true;
    }

    /// <summary>在所有会话之间轮换呈现。少于两路时什么都不做（返回 false）。</summary>
    public bool CycleFocus()
    {
        List<AirPlaySession> list;
        lock (_sessions)
        {
            list = _sessions.ToList();
        }

        if (list.Count < 2)
        {
            return false;
        }

        int index = list.FindIndex(s => string.Equals(s.Id, _activeSessionId, StringComparison.Ordinal));
        return FocusSession(list[(index + 1) % list.Count].Id);
    }

    /// <summary>当前呈现的是第几路（从 1 开始；没有会话时为 0）。用于界面显示「1/2」。</summary>
    public int FocusedIndex
    {
        get
        {
            lock (_sessions)
            {
                int index = _sessions.FindIndex(s => string.Equals(s.Id, _activeSessionId, StringComparison.Ordinal));
                return index < 0 ? 0 : index + 1;
            }
        }
    }

    /// <summary>RTSP 控制端口（诊断用）。</summary>
    public int Port => _server?.Port ?? 0;

    /// <summary>当前请求的画质（界面显示用）。</summary>
    public AirPlayQualityProfile Quality => _profile;

    /// <summary>侧栏「画质」一栏的文字，例如 <c>低延迟 1280×720@30</c>。</summary>
    public string DisplayProfileText => _profile.DisplayText;

    /// <summary>本次请求的画面宽度（挂渲染表面时用）。</summary>
    public int VideoWidth => _profile.Width;

    /// <summary>本次请求的画面高度。</summary>
    public int VideoHeight => _profile.Height;

    /// <summary>
    /// 重算画质（只在**下一次连接**生效）：改画质档位、网络频段/速率变化、自动降档后都会调。
    /// 不会踢掉正在投屏的设备 —— 用户体验优先。
    /// </summary>
    public void ApplyQualityProfile(CastSettings settings, NetInfo.WlanLink? link)
    {
        _settings = settings;
        if (link is not null) _link = link;
        if (_options is null) return;

        AirPlayQualityProfile profile = AirPlayQualityProfile.ForSettings(
            settings.QualityPreset, _link, settings.AirPlaySmoothFallback);
        bool changed = profile != _profile;
        _profile = profile;

        _options.Width = profile.Width;
        _options.Height = profile.Height;
        _options.RefreshRate = 60;
        _options.MaxFPS = profile.MaxFPS;
        _options.QualityLabel = profile.Label;

        _log.Debug("airplay", $"AirPlay 画质：{profile.DisplayText}（{profile.Reason}）");
        if (changed)
        {
            _publish?.Invoke($"AirPlay 请求画质：{profile.DisplayText}（{profile.Reason}）");
        }
    }

    /// <summary>画面源就绪 —— 界面据此调用 <c>AttachSource</c>。</summary>
    public event EventHandler<MediaSource>? MediaSourceCreated;

    /// <summary>
    /// 会话列表发生变化（新增 / 结束 / 聚焦变化）—— 多设备宫格据此重建。
    /// 每一次都要**重新读一遍 <see cref="Sessions"/>**，不要缓存：这个事件就是"快照已过期"的信号。
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>最后一路会话也结束了 —— 界面据此收尾。</summary>
    public event EventHandler? Disconnected;

    /// <summary>状态文字（供侧栏/动态显示）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>识别出客户端设备（型号/系统/User-Agent，握手时能拿到多少报多少）。</summary>
    public event Action<DeviceIdentity>? DeviceIdentified;

    public bool Start(CastSettings settings, NetInfo.WlanLink? link = null)
    {
        if (_server is not null)
        {
            return true;
        }

        LanInterface? lan = NetInfo.GetLanInterface();
        if (lan is null)
        {
            _publish?.Invoke("AirPlay 未开启：没有可用的局域网地址");
            return false;
        }

        try
        {
            _settings = settings;
            _link = link;

            var options = new AirPlayOptions
            {
                ServerName = Sanitize(settings.DeviceName),
                MacAddress = string.IsNullOrWhiteSpace(settings.AirPlayMac) ? "7A:1B:2C:3D:4E:5F" : settings.AirPlayMac,
                PkHex = string.IsNullOrWhiteSpace(settings.AirPlayPk) ? new string('b', 64) : settings.AirPlayPk,
            };
            _options = options;
            // 先按"画质档位 + 链路 + 上次健康度"算出请求分辨率，再广播；/info 会直接读这些字段
            ApplyQualityProfile(settings, link);

            // 先起控制通道：端口是动态分配的，广播里要用真实端口
            var server = new AirPlayRtspServer(options, m => _log.Debug("airplay", m));
            server.MirrorStarted += OnMirrorStarted;
            server.FrameReady += OnFrameReady;
            server.SessionEnded += OnSessionEnded;
            server.IdentityReported += OnIdentityReported;
            server.Start();

            IReadOnlyList<MdnsService> services = AirPlayAdvertisement.Build(
                options.ServerName, options.MacAddress, options.PkHex, server.Port);
            server.Services = services;

            var responder = new MdnsResponder(lan.Address, AirPlayAdvertisement.LocalHostName(), services,
                m => _log.Debug("airplay", m));
            responder.Start();

            _server = server;
            _responder = responder;

            _publish?.Invoke($"AirPlay 已开启（iPhone/iPad 可在投屏列表里选「{options.ServerName}」）");
            StatusChanged?.Invoke("AirPlay 等待连接");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("airplay", $"AirPlay 启动失败：{ex.Message}");
            _publish?.Invoke("AirPlay 启动失败：" + ex.Message);
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_server is not null)
            {
                _server.MirrorStarted -= OnMirrorStarted;
                _server.FrameReady -= OnFrameReady;
                _server.SessionEnded -= OnSessionEnded;
                _server.IdentityReported -= OnIdentityReported;
            }
        }
        catch
        {
            // 忽略
        }

        // 每一路都要收干净：只收"当前那一路"会留下仍在解码的孤儿视频源
        List<AirPlaySession> doomed;
        lock (_sessions)
        {
            doomed = _sessions.ToList();
            _sessions.Clear();
        }
        foreach (AirPlaySession session in doomed)
        {
            try { session.Video.Dispose(); } catch { }
        }
        lock (_pendingIdentities)
        {
            _pendingIdentities.Clear();
        }

        try { _responder?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }

        _responder = null;
        _server = null;
        _activeSessionId = null;
        FrameCount = 0;
        RaiseSessionsChanged();
    }

    public void Dispose() => Stop();

    private void OnMirrorStarted(string sessionId, MirrorStreamReceiver receiver)
    {
        // 每一路各自一个解码器：这样两路帧不会互相插帧，非聚焦的那几路也能出画（宫格要用）。
        // 描述符里的分辨率/帧率必须与 /info 向手机请求的一致（否则管线会等帧/按错误尺寸出画）。
        AirPlayQualityProfile profile = _profile;
        var video = new AirPlayVideoSource(profile.Width, profile.Height, capacity: 16, fps: profile.MaxFPS);
        video.Status += m => StatusChanged?.Invoke(m);
        _log.Debug("airplay", $"新会话 {sessionId}：解码器按 {profile.Spec} 建立（{profile.Reason}）");

        var session = new AirPlaySession(sessionId, receiver, video);
        bool becameFocused;
        lock (_sessions)
        {
            // 识别结果比会话先到：这时候把它补给这一路（否则界面只能显示兜底名字）
            if (_pendingIdentities.Remove(sessionId, out DeviceIdentity? pending))
            {
                session.Identity = pending;
            }

            _sessions.Add(session);
            // 已有聚焦那一路时**不抢**：第二台设备接入不应该把用户正在看的主画面换掉
            becameFocused = _activeSessionId is null;
            if (becameFocused)
            {
                _activeSessionId = sessionId;
            }
        }

        FrameCount = 0;
        _publish?.Invoke(SessionCount > 1
            ? $"又一台 iPhone/iPad 接入（共 {SessionCount} 路）"
            : "iPhone/iPad 已连接，正在接收画面…");
        StatusChanged?.Invoke($"AirPlay 接收中：{session.Label}");

        if (becameFocused && session.MediaSource is { } source)
        {
            try
            {
                MediaSourceCreated?.Invoke(this, source);
            }
            catch (Exception ex)
            {
                _log.Warn("airplay", $"交付 MediaSource 失败：{ex.Message}");
            }
        }

        RaiseSessionsChanged();
    }

    private void OnFrameReady(string sessionId, MirrorFrame frame)
    {
        AirPlaySession? session = Find(sessionId);
        if (session is null)
        {
            return;
        }

        // 计数器按会话各自累加（宫格每格要显示自己那一路的状态）
        session.FrameCount++;

        // 关键：**推给这一路自己的解码器**。早先所有会话共用一个播放器，只能推聚焦那一路；
        // 现在每路一个解码器，不推的话它那块格子就永远收不到数据。
        session.Video.Push(frame);
        session.NoteFrame(session.Video.QueuedCount);

        if (string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal))
        {
            FrameCount++;
            if (FrameCount == 1 || FrameCount % 300 == 0)
            {
                // 带上诊断数字：用户报"卡顿"时，这几个值能直接区分瓶颈在 Wi-Fi 带宽还是解码侧
                StatusChanged?.Invoke(
                    $"AirPlay 接收中：{FrameCount} 帧 · 积压 {session.QueuedCount} · 丢弃 {session.DroppedCount}"
                    + (session.WaitingForKeyFrame ? " · 正在等关键帧" : ""));
                _log.Debug("airplay",
                    $"会话 {sessionId}：帧 {session.FrameCount}，积压 {session.QueuedCount}，丢弃 {session.DroppedCount}，"
                    + $"重同步 {session.ResyncCount}，实测 {session.BytesReceived / 1024} KB");
            }
        }
    }

    /// <summary>一路上 AirPlay 镜像会话（一台设备）。</summary>
    public sealed class AirPlaySession
    {
        internal AirPlaySession(string id, MirrorStreamReceiver mirror, AirPlayVideoSource video)
        {
            Id = id;
            Mirror = mirror;
            Video = video;
        }

        /// <summary>会话 id（与服务端事件里的 id 一致）。</summary>
        public string Id { get; }

        internal MirrorStreamReceiver Mirror { get; }

        /// <summary>这路会话的画面源；每一路挂到自己的宿主上（同程序集内可用）。</summary>
        internal AirPlayVideoSource Video { get; }

        /// <summary>交给宿主渲染的媒体源；视频源已经被释放时为 null（此时不要再去挂）。</summary>
        public MediaSource? MediaSource
        {
            get
            {
                try { return Video.MediaSource; }
                catch (ObjectDisposedException) { return null; }
            }
        }

        /// <summary>识别出的设备（可能在会话建立之后才拿到）。</summary>
        public DeviceIdentity? Identity { get; internal set; }

        /// <summary>实测收到的字节数（含包头）。</summary>
        public long BytesReceived => Mirror.BytesReceived;

        /// <summary>这路会话收到的帧数（不管有没有被放大呈现）。</summary>
        public long FrameCount { get; internal set; }

        /// <summary>这路因为跟不上而被丢掉的帧数（含重同步时清空的积压）。</summary>
        public long DroppedCount => Video.DroppedCount;

        /// <summary>这路重同步过几次（&gt;0 说明解码或网络确实跟不上过）。</summary>
        public long ResyncCount => Video.ResyncCount;

        /// <summary>这路还积压着多少帧没被解码器取走。</summary>
        public int QueuedCount => Video.QueuedCount;

        /// <summary>是否正在等关键帧（此时画面会停一下再恢复）。</summary>
        public bool WaitingForKeyFrame => Video.WaitingForKeyFrame;

        /// <summary>本会话开始的时间（健康度按"实际时长"算，短会话不参与判断）。</summary>
        internal DateTime StartedUtc { get; } = DateTime.UtcNow;

        internal long HealthFrames { get; private set; }
        internal double QueueSum { get; private set; }
        internal int QueueSamples { get; private set; }
        internal int PeakQueue { get; private set; }

        /// <summary>每帧记一次积压，供会话结束时评估"持续卡顿还是网络抖动"。</summary>
        internal void NoteFrame(int queued)
        {
            HealthFrames++;
            if (queued > PeakQueue) PeakQueue = queued;
            // 采样上限只是防溢出：实际一段投屏也就几万帧
            if (QueueSamples < 200_000)
            {
                QueueSum += queued;
                QueueSamples++;
            }
        }

        /// <summary>会话结束时算一次健康度（纯本地计数，不向手机要数据）。</summary>
        internal AirPlaySessionHealth HealthSnapshot()
        {
            double seconds = Math.Max(0, (DateTime.UtcNow - StartedUtc).TotalSeconds);
            double averageQueue = QueueSamples > 0 ? QueueSum / QueueSamples : 0;
            return new AirPlaySessionHealth(seconds, HealthFrames, DroppedCount, ResyncCount, averageQueue);
        }

        /// <summary>界面标签：认不出型号就退回原始名字。</summary>
        public string Label => Identity?.ShortLabel() ?? "AirPlay 设备";
    }

    private void OnIdentityReported(string sessionId, DeviceIdentity identity)
    {
        // 记到对应会话上：多台设备时要各自保留自己的名字，不能只留最后一条。
        // 会话还没登记（识别比镜像流先到）就先存起来，等会话建立时补给它。
        AirPlaySession? session = Find(sessionId);
        if (session is not null)
        {
            session.Identity = identity;
            RaiseSessionsChanged();     // 格子的标题要跟着变成真实名字
        }
        else
        {
            lock (_pendingIdentities)
            {
                _pendingIdentities[sessionId] = identity;
            }
        }

        _publish?.Invoke("识别到设备：" + identity.Describe());
        try { DeviceIdentified?.Invoke(identity); } catch { }
    }

    private void OnSessionEnded(string sessionId)
    {
        // 只清理结束的那一路；别的会话还在跑就不能动（早先只处理聚焦那一路，
        // 结果没被聚焦的会话断线后永远留在列表里，路数一直不归零）
        AirPlaySession? removed = Find(sessionId);
        if (removed is not null)
        {
            EvaluateQualityAfterSession(removed);
            lock (_sessions)
            {
                _sessions.Remove(removed);
            }
            try { removed.Video.Dispose(); } catch { }
        }

        bool wasFocused = string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal);
        lock (_pendingIdentities)
        {
            _pendingIdentities.Remove(sessionId);
        }
        AirPlaySession? next = null;
        if (wasFocused)
        {
            lock (_sessions)
            {
                next = _sessions.FirstOrDefault();
            }
            _activeSessionId = next?.Id;
            FrameCount = 0;
            if (next is not null)
            {
                StatusChanged?.Invoke($"AirPlay 接收中：{next.Label}");
                if (next.MediaSource is { } source)
                {
                    try { MediaSourceCreated?.Invoke(this, source); } catch { }
                }
            }
        }

        RaiseSessionsChanged();

        if (SessionCount == 0)
        {
            StatusChanged?.Invoke("AirPlay 等待连接");
            _publish?.Invoke("AirPlay 会话已结束");
            try { Disconnected?.Invoke(this, EventArgs.Empty); } catch { }
        }
        else if (wasFocused && next is not null)
        {
            _publish?.Invoke($"已切换到 {next.Identity?.Describe() ?? next.Label}（共 {SessionCount} 路）");
        }
        else
        {
            _publish?.Invoke($"一台设备已断开（还剩 {SessionCount} 路）");
        }
    }

    /// <summary>
    /// 会话结束后的自动画质调整（迟滞策略，只在**下一次连接**生效）：
    /// 持续卡顿 → 记住"下次降 720p30"；连续两段健康会话 → 撤销降档。
    /// 绝不为了切画质把正在投屏的会话踢掉。
    /// </summary>
    private void EvaluateQualityAfterSession(AirPlaySession session)
    {
        if (_settings is null) return;

        AirPlaySessionHealth health = session.HealthSnapshot();
        if (health.Seconds < 8 || health.Frames == 0)
        {
            return;   // 太短的会话（误触/立刻断开）不参与判断
        }

        bool alreadySmooth = _settings.AirPlaySmoothFallback;
        AirPlayQualityDecision decision = AirPlayQualityProfile.Evaluate(health, alreadySmooth);
        _log.Debug("airplay",
            $"会话健康度：{health.Seconds:F0}s / {health.Frames} 帧 / 丢弃 {health.Dropped} / 重同步 {health.Resync}"
            + $" / 平均积压 {health.AverageQueue:F1} → {decision}");

        if (decision == AirPlayQualityDecision.Downgrade)
        {
            if (!alreadySmooth)
            {
                _settings.AirPlaySmoothFallback = true;
                _settings.Save();
            }
            _goodSessions = 0;
            ApplyQualityProfile(_settings, _link);
            _publish?.Invoke($"检测到投屏卡顿（丢 {health.Dropped} 帧 / 重同步 {health.Resync} 次）：下次连接自动改用 720p30");
        }
        else if (decision == AirPlayQualityDecision.Upgrade)
        {
            _goodSessions++;
            if (_goodSessions >= 2)
            {
                _goodSessions = 0;
                _settings.AirPlaySmoothFallback = false;
                _settings.Save();
                ApplyQualityProfile(_settings, _link);
                _publish?.Invoke("连续两段投屏流畅：AirPlay 画质已恢复自动提升");
            }
        }
    }

    private void RaiseSessionsChanged()
    {
        try { SessionsChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { _log.Debug("airplay", $"SessionsChanged 异常：{ex.Message}"); }
    }

    /// <summary>
    /// 设备名会直接进 DNS-SD 实例名，句点会被当成标签分隔符，这里替换掉；
    /// 其余字符保持原样（DNS-SD 允许 UTF-8，中文设备名可以正常显示）。
    /// </summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "投屏Window";
        }
        return name.Replace('.', '-').Trim();
    }
}
