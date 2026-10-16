using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Security;
using Windows.Media.Core;
using Windows.Media.Miracast;

namespace CastReceiver.Core.Miracast;

public sealed record MiracastStartResult(bool Success, string Message, string Detail);

/// <summary>
/// 改设备名的结果。<c>Applied=false</c> 表示"已经记下，但要等条件满足才真正生效"
/// （例如正在投屏时不能重建广播，否则会打断用户）。
/// </summary>
public sealed record DeviceNameApplyResult(bool Applied, string Message);

public sealed record MiracastStatus(
    bool Listening,
    string ListeningStatus,
    string WiFiStatus,
    int ConnectionCount,
    string DeviceName,
    string? LastError);

public sealed record MiracastConnectionEventArgs(string Pin, string TransmitterName, string MacAddress, string Fingerprint);

/// <summary>
/// 系统投屏（Miracast / Wi-Fi Display）接收服务。
/// 依赖 Windows 11 的 Windows.Media.Miracast 平台 API；已实测在“未打包的普通 WPF 进程”中可以启动监听
/// （无需 MSIX 包标识，前提：网卡与显卡驱动均支持 Wireless Display，且为 x64 进程）。
/// </summary>
public sealed class MiracastService : IDisposable
{
    private readonly AppLog _log;
    private MiracastReceiver? _receiver;
    private MiracastReceiverSession? _session;
    private CancellationTokenSource? _cts;
    private Task? _watchdog;
    private int _connectionCount;
    private bool _keyboardTransmitLogged;
    private string? _lastError;
    private bool _wantListening;
    private MiracastStatus _lastStatus = new(false, "NotListening", "Unknown", 0, "", null);
    private string? _cachedWifiStatus;
    private int _recoveryDepth;
    private readonly List<MiracastReceiverConnection> _activeConnections = new();

    public MiracastService(AppLog log) => _log = log;

    public event Action<string>? Notice;
    public event EventHandler<MiracastStatus>? StatusChanged;
    public event EventHandler<MiracastConnectionEventArgs>? ConnectionCreated;
    public event EventHandler<MediaSource>? MediaSourceCreated;
    public event EventHandler<string>? Disconnected;

    public bool IsListening => _session is not null && _wantListening;

    /// <summary>延迟优先：向手机申请更低的分辨率/码率（镜像更跟手，清晰度略降）。</summary>
    public bool LowLatency { get; set; } = true;

    /// <summary>画质档位：LowLatency(720p@4M) / Balanced(1080p@8M) / HighQuality(不限制分辨率, 高码率)。</summary>
    public string QualityPreset { get; set; } = "LowLatency";

    /// <summary>镜像静音（省掉音频同步缓冲，可进一步降低延迟）。</summary>
    public bool MuteAudio { get; set; }

    /// <summary>当前协商到的视频码率（kbps）。Miracast 能从平台直接拿到这个值。</summary>
    public int CurrentBitrateKbps { get; private set; }

    /// <summary>
    /// 键盘回传（UIBC）：开启后把本机键盘的按键转发给正在投屏的手机。
    /// 方向是「电脑 → 手机」（官方语义：接收端把输入发给发送端），连接建立时应用，改了需要重连才生效。
    /// </summary>
    public bool TransmitKeyboard { get; set; }

    /// <summary>键盘回传当前状态，供界面显示：未连接 / 未开启 / 手机未请求 / 已开启 / 正在发送。</summary>
    public string KeyboardBackchannelState { get; private set; } = "未连接";

    /// <summary>
    /// 给界面看的最终状态。没连接时也要如实反映「开关是开还是关」，
    /// 否则用户打开这个功能后界面上什么都看不出来，没法判断它到底生效没有。
    /// </summary>
    public string EffectiveKeyboardState => _connectionCount == 0
        ? (TransmitKeyboard ? "等待连接" : "未开启")
        : KeyboardBackchannelState;

    /// <summary>
    /// 隐私模式：日志与动态里不出现设备名 / MAC / PIN，投屏会话事件不写日志文件。
    /// 需求：投屏内容、开始时间、结束时间不能让本人之外的人知道。
    /// </summary>
    public bool PrivacyLog { get; set; } = true;

    /// <summary>允许新设备抢占正在进行的投屏（默认关闭：别人不能把你的投屏顶掉）。</summary>
    public bool AllowConnectionTakeover { get; set; }

    /// <summary>配对方式：Confirm（本机确认）/ Pin（要求手机输入本机显示的 PIN）。</summary>
    public string PairingMode { get; set; } = "Confirm";

    /// <summary>启动监听时清空系统记录的「已知无线显示设备」历史。</summary>
    public bool ForgetDeviceHistory { get; set; }

    /// <summary>空闲（无连接）时每隔多少分钟静默重建一次广播，0 = 不自动刷新。</summary>
    public int IdleBroadcastRefreshMinutes { get; set; } = 10;

    private DateTime _sessionStartedUtc = DateTime.UtcNow;
    private DateTime _connectedAtUtc = DateTime.UtcNow;
    private DateTime? _mediaSourceAtUtc;
    private int _staleStreak;
    private bool _lastRequireConfirm = true;

    /// <summary>正在应用新设备名（重建广播）：防止设置界面的连续输入与看门狗重启互相踩。</summary>
    private bool _applyingName;

    /// <summary>当前实际使用的视频流参数（供界面显示）。</summary>
    public string StreamInfo { get; private set; } = "未知";
    public string DeviceName { get; private set; } = "";

    /// <summary>
    /// 宿主写入的"当前想要的设备名"（设置里改了名字就写这里）。
    ///
    /// 为什么必须单独有它：<see cref="DeviceName"/> 是**上一次 StartAsync 的快照**，
    /// 而空闲刷新 / 看门狗自动恢复 / 手动「刷新广播」这些内部重启都要重新应用一次名字 ——
    /// 早先它们读的就是那个快照，于是"在设置里改了名"会被这些重启**悄悄改回旧名字**
    /// （现象：改完名字再点「刷新广播」，手机上还是旧名字；放着不管十分钟后也被改回去）。
    /// </summary>
    public string DesiredDeviceName { get; set; } = "";

    public int ConnectionCount => _connectionCount;

    /// <summary>这次重启真正要用的名字：以宿主写入的"想要的"为准，没写过才退回上次的快照。</summary>
    private string EffectiveDeviceName()
        => string.IsNullOrWhiteSpace(DesiredDeviceName) ? DeviceName : DesiredDeviceName.Trim();

    /// <summary>本机 Miracast 硬件/驱动能力（MiracastSupported 表示可用）。结果会被缓存。</summary>
    public string QueryWifiStatus()
    {
        if (_cachedWifiStatus is not null) return _cachedWifiStatus;
        try
        {
            var receiver = _receiver ?? new MiracastReceiver();
            var status = receiver.GetStatus();
            _cachedWifiStatus = status.WiFiStatus.ToString();
            return _cachedWifiStatus;
        }
        catch (Exception ex)
        {
            _log.Error("miracast", "查询无线显示能力失败", ex);
            return "Unknown";
        }
    }

    /// <summary>
    /// 读取接收端状态。注意：未开启时直接返回缓存值——
    /// 每次新建 MiracastReceiver 都会触发 WinRT 激活与无线查询（几十到上百毫秒），
    /// 早期版本把它放在界面定时器里，导致点几下就“应用程序未响应”。
    /// </summary>
    public MiracastStatus QueryStatus()
    {
        if (_receiver is null)
            return _lastStatus with { ConnectionCount = _connectionCount, DeviceName = DeviceName };

        try
        {
            var status = _receiver.GetStatus();
            _cachedWifiStatus = status.WiFiStatus.ToString();
            _lastStatus = new MiracastStatus(
                status.ListeningStatus == MiracastReceiverListeningStatus.Listening,
                status.ListeningStatus.ToString(),
                status.WiFiStatus.ToString(),
                _connectionCount,
                DeviceName,
                _lastError);
            return _lastStatus;
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _lastStatus = new MiracastStatus(false, "Unknown", "Unknown", _connectionCount, DeviceName, _lastError);
            return _lastStatus;
        }
    }

    /// <summary>
    /// 修复无线显示通道：结束可能已经卡死的 CastSrv 进程（Windows 无线显示/Cast 的后台进程）。
    /// 背景：应用被强制结束或异常退出后，该进程偶尔会一直占着 Wi-Fi Direct 接收端，
    /// 之后任何 StartAsync 都会失败甚至永久挂住；实测结束它之后接收端可立即恢复正常。
    /// 该进程按需自动重启，因此在“没有正在投屏”的前提下结束它是安全的。
    /// </summary>
    public static (bool Ok, string Detail) RecoverWirelessDisplay()
    {
        var killed = new List<int>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("CastSrv"))
            {
                try { int id = p.Id; p.Kill(); p.WaitForExit(3000); killed.Add(id); }
                catch (Exception ex) { return (false, $"结束 CastSrv(PID {p.Id}) 失败：{ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }

        return (true, killed.Count == 0
            ? "未发现残留的无线显示进程（CastSrv），无需处理"
            : $"已结束卡住的无线显示进程 CastSrv（PID {string.Join(",", killed)}），Windows 会按需重新启动它");
    }

    /// <summary>
    /// 给 WinRT 异步调用加超时。实测当 Wi-Fi Direct 处于异常状态（例如被移动热点/残留 P2P 占用）时，
    /// MiracastReceiverSession.StartAsync 可能既不成功也不失败、直接挂住不返回，
    /// 界面就会长时间停在“未开启”而看不到任何原因，因此必须主动超时并给出可操作的提示。
    /// </summary>
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, int seconds, string what)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))).ConfigureAwait(false);
        if (finished != task)
            throw new TimeoutException($"{what} 超过 {seconds} 秒没有返回（Wi-Fi Direct 可能处于异常状态）");
        return await task.ConfigureAwait(false);
    }

    public async Task<MiracastStartResult> StartAsync(string deviceName, bool requireConfirm, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _lastError = null;
        _lastRequireConfirm = requireConfirm;
        _cts = new CancellationTokenSource();

        try
        {
            _receiver = new MiracastReceiver();
            DeviceName = deviceName;

            // 清掉历史配对记录：手机端曾“忘记”或授权状态残留时，会让连接被静默拒绝
            if (ForgetDeviceHistory)
            {
                try { _receiver.ClearKnownTransmitters(); } catch { }
            }

            var settings = _receiver.GetDefaultSettings();
            settings.FriendlyName = deviceName;
            settings.ModelName = "投屏Window 桌面版";
            settings.ModelNumber = "1.0";
            bool wantPin = string.Equals(PairingMode, "Pin", StringComparison.OrdinalIgnoreCase);
            settings.AuthorizationMethod = wantPin
                ? MiracastReceiverAuthorizationMethod.PinDisplayRequired
                : requireConfirm ? MiracastReceiverAuthorizationMethod.ConfirmConnection
                                 : MiracastReceiverAuthorizationMethod.None;
            settings.RequireAuthorizationFromKnownTransmitters = false;

            var applied = await WithTimeoutAsync(_receiver.DisconnectAllAndApplySettingsAsync(settings).AsTask(ct), 15, "应用接收端设置")
                .ConfigureAwait(false);
            if (applied.Status != MiracastReceiverApplySettingsStatus.Success && wantPin)
            {
                // 有些手机/驱动不支持 PIN 配对：退回「本机确认」，不因此让用户投不上屏
                _log.Warn("miracast", $"要求 PIN 配对失败（{applied.Status}），已退回「本机确认」方式");
                settings.AuthorizationMethod = MiracastReceiverAuthorizationMethod.ConfirmConnection;
                applied = await WithTimeoutAsync(_receiver.DisconnectAllAndApplySettingsAsync(settings).AsTask(ct), 15, "应用接收端设置")
                    .ConfigureAwait(false);
            }
            if (applied.Status != MiracastReceiverApplySettingsStatus.Success)
            {
                string detail = Describe(applied.ExtendedError);
                _log.Warn("miracast", $"应用接收端设置失败：{applied.Status} {detail}");
            }
            else
            {
                _log.Info("miracast", $"设备名已设为“{deviceName}”，配对方式={settings.AuthorizationMethod}");
            }

            var current = await WithTimeoutAsync(_receiver.GetCurrentSettingsAsync().AsTask(ct), 10, "读取接收端设置").ConfigureAwait(false);
            // 读回校验：平台可能不接受这个名字（长度/字符限制等）。早先这里直接采信读回值，
            // 而上面已经打过"设备名已设为…"，于是"改了名字但手机上没变"完全无从判断。
            string actual = string.IsNullOrWhiteSpace(current.FriendlyName) ? deviceName : current.FriendlyName.Trim();
            if (!string.Equals(actual, deviceName, StringComparison.Ordinal))
            {
                _log.Warn("miracast", $"系统未接受设备名“{deviceName}”，实际使用“{actual}”");
                Notice?.Invoke($"系统未接受设备名“{deviceName}”，实际为“{actual}”");
            }
            DeviceName = actual;

            _session = await WithTimeoutAsync(_receiver.CreateSessionAsync(null).AsTask(ct), 15, "创建接收会话").ConfigureAwait(false);
            if (_session is null)
                return Fail("无法创建 Miracast 接收会话", "CreateSessionAsync 返回 null");

            _session.MaxSimultaneousConnections = 1;
            _session.AllowConnectionTakeover = AllowConnectionTakeover;   // 默认 false：别的设备不能顶掉你正在进行的投屏
            _session.ConnectionCreated += OnConnectionCreated;
            _session.MediaSourceCreated += OnMediaSourceCreated;
            _session.Disconnected += OnDisconnected;
            _receiver.StatusChanged += OnReceiverStatusChanged;

            _wantListening = true;
            var startResult = await WithTimeoutAsync(_session.StartAsync().AsTask(ct), 20, "启动监听会话").ConfigureAwait(false);
            if (startResult.Status != MiracastReceiverSessionStartStatus.Success)
            {
                _wantListening = false;
                // 自动修复一次：结束可能卡死的无线显示进程后重试
                var retry = await TryRecoverAndRetryAsync(deviceName, requireConfirm, ct, startResult.Status.ToString())
                    .ConfigureAwait(false);
                if (retry is not null) return retry;
                string detail = Describe(startResult.ExtendedError);
                string hint = startResult.Status switch
                {
                    MiracastReceiverSessionStartStatus.MiracastNotSupported =>
                        "本机无线网卡/显卡驱动未启用“无线显示(Wireless Display)”接收能力",
                    MiracastReceiverSessionStartStatus.AccessDenied =>
                        "系统策略或权限禁止启用 Miracast 接收（企业策略/组策略可能已禁用）",
                    _ => "系统返回未知失败，请查看日志",
                };
                // Wi-Fi Direct 被占用是最常见的原因：手机端会报“共享通道被占用”，本机则表现为状态错误(0x8007139F)
                string? hotspot = NetInfo.DetectHotspotConflict();
                if (hotspot is not null)
                    hint = "检测到移动热点/连接共享正在占用 Wi-Fi Direct 通道（" + hotspot +
                           "）——这会直接导致 Miracast 无法工作。请先关闭移动热点，再点「重启接收端」。";
                if (startResult.ExtendedError is not null && startResult.ExtendedError.HResult == unchecked((int)0x8007139F))
                    hint = "Wi-Fi Direct 无线状态不正确（0x8007139F）" +
                           (hotspot is null
                               ? "：通常是上一次投屏留下的残留连接，请点「重启接收端」重试；仍失败可关闭再打开 Wi-Fi。"
                               : "，且检测到移动热点/连接共享占用 Wi-Fi Direct：请先关闭热点，再点「重启接收端」。");
                _lastError = $"{startResult.Status}：{hint}（{detail}）";
                _log.Error("miracast", $"启动监听失败：{_lastError}");
                RaiseStatus();
                return Fail($"启动系统投屏监听失败：{startResult.Status}", _lastError);
            }

            var status = _receiver.GetStatus();
            _log.Info("miracast", $"系统投屏监听已启动：{status.ListeningStatus} / 无线能力={status.WiFiStatus}");
            Notice?.Invoke("系统投屏已开启");
            RaiseStatus();
            _sessionStartedUtc = DateTime.UtcNow;
            _staleStreak = 0;
            _watchdog = Task.Run(() => WatchdogLoopAsync(_cts.Token));

            return new MiracastStartResult(true,
                "系统投屏已开启",
                $"设备名“{DeviceName}”，ListeningStatus={status.ListeningStatus}, WiFiStatus={status.WiFiStatus}");
        }
        catch (Exception ex)
        {
            _lastError = Describe(ex);
            _log.Error("miracast", "启动系统投屏异常", ex);
            var retry = await TryRecoverAndRetryAsync(deviceName, requireConfirm, ct, ex.GetType().Name).ConfigureAwait(false);
            if (retry is not null) return retry;
            RaiseStatus();
            return Fail("启动系统投屏异常", _lastError);
        }
    }

    /// <summary>
    /// 启动失败（尤其是超时/0x8007139F）时自动修复一次：结束卡死的 CastSrv 后重新尝试。
    /// 返回 null 表示不再重试（已经重试过或修复失败）。
    /// </summary>
    private async Task<MiracastStartResult?> TryRecoverAndRetryAsync(string deviceName, bool requireConfirm,
                                                                     CancellationToken ct, string reason)
    {
        if (_recoveryDepth > 0) return null;
        _recoveryDepth++;
        try
        {
            var (ok, detail) = RecoverWirelessDisplay();
            _log.Warn("miracast", $"启动失败（{reason}），自动修复：{detail}");
            Notice?.Invoke(ok ? "无线显示通道异常，已自动清理并重试…" : $"自动修复失败：{detail}");

            await StopAsync().ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            if (!ok) return null;
            return await StartAsync(deviceName, requireConfirm, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("miracast", "自动修复重试失败", ex);
            return null;
        }
        finally { _recoveryDepth--; }
    }

    /// <summary>重新向当前连接的手机申请视频流参数（切换“延迟优先/画质优先”时用，无需重连）。</summary>
    public Task ReapplyStreamPreferencesAsync()
    {
        foreach (var connection in _activeConnections.ToArray())
        {
            try { ApplyStreamPreferences(connection); } catch { }
        }
        RaiseStatus();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _wantListening = false;
        try { _cts?.Cancel(); } catch { }
        try { if (_watchdog is not null) await Task.WhenAny(_watchdog, Task.Delay(1000)).ConfigureAwait(false); } catch { }
        _watchdog = null;

        // 停止监听 = 本次投屏结束：连接计数必须归零，否则界面会一直以为还在投屏
        _connectionCount = 0;
        _activeConnections.Clear();
        StreamInfo = "未知";

        if (_session is not null)
        {
            try { _session.ConnectionCreated -= OnConnectionCreated; } catch { }
            try { _session.MediaSourceCreated -= OnMediaSourceCreated; } catch { }
            try { _session.Disconnected -= OnDisconnected; } catch { }
            try { _session.Dispose(); } catch { }
            _session = null;
        }
        if (_receiver is not null)
        {
            try { _receiver.StatusChanged -= OnReceiverStatusChanged; } catch { }
        }
        _log.Info("miracast", "系统投屏监听已停止");
        RaiseStatus();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 看门狗：让接收端"自愈"。
    /// 实测现象：偶发投屏不上，重启一下程序就好了——根因是 Wi-Fi Direct 会话进入异常状态
    /// （监听状态丢失、或手机一直停在"正在连接"而本机收不到连接）。这里主动发现并重建会话，
    /// 用户不需要再重启程序。
    /// </summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        DateTime? pendingSince = null;
        int recoveries = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_wantListening || _receiver is null) return;

            try
            {
                var status = _receiver.GetStatus();
                bool pending = status.ListeningStatus == MiracastReceiverListeningStatus.ConnectionPending;
                if (pending) pendingSince ??= DateTime.UtcNow; else pendingSince = null;

                // ① 监听状态丢失；② 手机卡在"正在连接"超过 25 秒而始终没连上
                bool lostListening = status.ListeningStatus != MiracastReceiverListeningStatus.Listening
                                     && _connectionCount == 0;
                bool pendingStuck = pendingSince is not null
                                    && (DateTime.UtcNow - pendingSince.Value).TotalSeconds > 25;

                // ③ 连上了但 10 秒还没收到画面（媒体源）——"假连接"：手机显示已投屏，电脑这边一片黑
                bool noMedia = _connectionCount > 0 && _mediaSourceAtUtc is null
                               && (DateTime.UtcNow - _connectedAtUtc).TotalSeconds > 10;
                // ④ 平台已经不再报告 Connected，但本地还记着连接 —— 手机那边其实早就断了，
                //    这种残留状态会让手机重连时被拒（表现为"点连接要等很久，重启程序才能秒连"）
                bool staleCount = _connectionCount > 0 && !pending
                                  && status.ListeningStatus == MiracastReceiverListeningStatus.Listening
                                  && (DateTime.UtcNow - _connectedAtUtc).TotalSeconds > 6;

                if (noMedia || staleCount) _staleStreak++;
                else if (!lostListening && !pendingStuck) _staleStreak = 0;

                if (_staleStreak >= 4)
                {
                    _staleStreak = 0;
                    _log.Warn("miracast", "连接反复异常，提示用户复位手机端");
                    Notice?.Invoke("连接状态反复异常：请在手机上关闭再打开「无线投屏」，或关掉手机 Wi-Fi 重开");
                    _connectionCount = 0;
                    _mediaSourceAtUtc = null;
                }

                // ⑤ 空闲太久：静默重建一次广播。
                //    "手机搜不到设备、重启程序就好" 的本质就是重建广播；这里让它自动发生。
                bool idleRefresh = IdleBroadcastRefreshMinutes > 0
                                   && _connectionCount == 0 && !pending && _mediaSourceAtUtc is null
                                   && status.ListeningStatus == MiracastReceiverListeningStatus.Listening
                                   && (DateTime.UtcNow - _sessionStartedUtc).TotalMinutes >= IdleBroadcastRefreshMinutes;

                if (!lostListening && !pendingStuck && !noMedia && !staleCount && !idleRefresh)
                {
                    recoveries = 0;
                    continue;
                }

                if (idleRefresh && !lostListening && !pendingStuck && !noMedia && !staleCount)
                {
                    // 空闲刷新：不打扰用户，只记日志
                    _log.Info("miracast", $"空闲超过 {IdleBroadcastRefreshMinutes} 分钟，自动重建广播以保持可被发现");
                    var idleName = EffectiveDeviceName();
                    _wantListening = false;
                    try { if (_session is not null) _session.Dispose(); } catch { }
                    _session = null;
                    await Task.Delay(400, ct).ConfigureAwait(false);
                    _recoveryDepth = 0;
                    var idleResult = await StartAsync(idleName, _lastRequireConfirm, ct).ConfigureAwait(false);
                    if (!idleResult.Success) _log.Warn("miracast", "空闲刷新广播失败：" + idleResult.Message);
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }

                if (recoveries >= 5)
                {
                    _log.Warn("miracast", "自动恢复多次失败，停止重试；可在界面上点「重启接收端」");
                    return;
                }
                recoveries++;
                pendingSince = null;

                string why = lostListening ? $"监听状态异常（{status.ListeningStatus}）"
                           : pendingStuck ? "手机连接卡住"
                           : noMedia ? "已连接但收不到画面"
                           : "连接状态已失效（手机端可能还记着旧连接）";
                _log.Warn("miracast", $"{why}，第 {recoveries} 次自动恢复");
                Notice?.Invoke(noMedia ? "已连接但没收到画面，正在重建接收端…" : "接收端无响应，正在自动恢复…");

                var name = EffectiveDeviceName();
                _wantListening = false;
                try { if (_session is not null) _session.Dispose(); } catch { }
                _session = null;
                try { _connectionCount = 0; } catch { }
                _mediaSourceAtUtc = null;
                // 若上次是进程异常退出留下的卡死状态，一并清理无线显示后台进程
                if (recoveries >= 2) { try { RecoverWirelessDisplay(); } catch { } }

                await Task.Delay(700 + recoveries * 400, ct).ConfigureAwait(false);
                _recoveryDepth = 0;   // 允许 StartAsync 内部再自愈一次
                var result = await StartAsync(name, _lastRequireConfirm, ct).ConfigureAwait(false);
                if (result.Success) Notice?.Invoke("接收端已自动恢复");
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Debug("miracast", $"看门狗异常：{ex.Message}"); }
        }
    }
    /// <summary>
    /// 向发送端申请视频流参数——接收端能做的最直接的延迟优化：
    /// 分辨率/码率越低，手机编码、无线传输、本机解码三段耗时都会下降。
    /// </summary>
    public void ApplyStreamPreferences(MiracastReceiverConnection? connection)
    {
        try
        {
            var control = connection?.StreamControl;
            if (control is null) return;
            control.MuteAudio = MuteAudio;
            if (LowLatency || QualityPreset != "HighQuality")
            {
                // 该类型不可自行 new（无公共构造函数），必须拿平台给的对象再改
                var settings = control.GetVideoStreamSettings();
                if (settings is null) { StreamInfo = "平台未返回可改的流参数"; return; }
                (int w, int h, int bitrate, string name) = QualityPreset switch
                {
                    "HighQuality" => (1920, 1080, 20_000_000, "高画质 1920×1080@20Mbps"),
                    "Balanced" => (1920, 1080, 8_000_000, "均衡 1920×1080@8Mbps"),
                    _ => (1280, 720, 4_000_000, "低延迟 1280×720@4Mbps"),
                };
                settings.Size = new Windows.Graphics.SizeInt32 { Width = w, Height = h };
                settings.Bitrate = bitrate;
                control.SuggestVideoStreamSettings(settings);
                var applied = control.GetVideoStreamSettings();
                CurrentBitrateKbps = (applied?.Bitrate ?? 0) / 1000;
                StreamInfo = name +
                             (applied is null ? "" : $"（当前 {applied.Size.Width}×{applied.Size.Height}@{applied.Bitrate / 1000}kbps）") +
                             (MuteAudio ? " 静音" : "");
                _log.Info("miracast", "已申请流参数：" + StreamInfo);
            }
            else
            {
                try
                {
                    var cur = control.GetVideoStreamSettings();
                    CurrentBitrateKbps = (cur?.Bitrate ?? 0) / 1000;
                    StreamInfo = cur is null ? "画质优先（手机自定）" : $"画质优先 {cur.Size.Width}×{cur.Size.Height}@{cur.Bitrate / 1000}kbps";
                }
                catch { StreamInfo = "画质优先（手机自定）"; }
                _log.Info("miracast", StreamInfo);
            }
        }
        catch (Exception ex) { _log.Warn("miracast", $"应用视频流参数失败：{ex.Message}"); }
    }

    /// <summary>
    /// 手动刷新广播：重建接收会话（手机搜不到设备时用，等价于"重启程序"的那一下）。
    /// 有设备连着时不动，避免打断投屏。
    /// </summary>
    /// <param name="deviceName">
    /// 这次广播要用的名字。**界面上的「刷新广播」必须把设置里的当前名字传进来** ——
    /// 早先这里读的是 <see cref="DeviceName"/> 快照，于是改完名字点刷新仍然是旧名字。
    /// </param>
    public async Task<bool> RefreshBroadcastAsync(string? deviceName = null)
    {
        if (_connectionCount > 0) return false;
        string name = string.IsNullOrWhiteSpace(deviceName) ? EffectiveDeviceName() : deviceName.Trim();
        if (name.Length == 0) return false;
        DesiredDeviceName = name;
        DeviceName = name;      // 界面立刻显示新名字，不等这次重建跑完
        var confirm = _lastRequireConfirm;
        Notice?.Invoke("正在刷新接收广播…");
        await StopAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        var result = await StartAsync(name, confirm).ConfigureAwait(false);
        Notice?.Invoke(result.Success ? "接收广播已刷新，手机上重新搜索本机即可" : "刷新失败：" + result.Message);
        return result.Success;
    }

    /// <summary>
    /// 应用新的设备名（设置里改完名字后由宿主调用）。
    ///
    /// 三种情况：
    /// <list type="bullet">
    /// <item>没在监听：记下来，下次开启接收端时生效；</item>
    /// <item>有设备正在投屏：**不重建广播**（那会把用户的投屏打断），只记下新名字，
    /// 断开后的下一次重启（空闲刷新/看门狗）会自动用上它；</item>
    /// <item>空闲：立刻重建广播，手机上重新搜索即可看到新名字。</item>
    /// </list>
    /// </summary>
    public async Task<DeviceNameApplyResult> ApplyDeviceNameAsync(string deviceName)
    {
        string name = (deviceName ?? "").Trim();
        if (name.Length == 0)
        {
            return new DeviceNameApplyResult(false, "设备名不能为空，已保持原来的名字");
        }

        DesiredDeviceName = name;
        DeviceName = name;

        if (!IsListening)
        {
            return new DeviceNameApplyResult(true, "设备名已保存，下次开启接收端时生效");
        }
        if (_connectionCount > 0)
        {
            return new DeviceNameApplyResult(false, "已有设备正在投屏：设备名将在断开后自动生效");
        }
        if (_applyingName)
        {
            return new DeviceNameApplyResult(false, "正在应用上一个设备名，请稍候");
        }

        _applyingName = true;
        try
        {
            bool ok = await RefreshBroadcastAsync(name).ConfigureAwait(false);
            return ok
                ? new DeviceNameApplyResult(true, $"设备名已生效（已按“{name}”重新广播）")
                : new DeviceNameApplyResult(false, "重新广播失败，可在主界面点「刷新广播」重试");
        }
        finally
        {
            _applyingName = false;
        }
    }

    private void OnConnectionCreated(MiracastReceiverSession sender, MiracastReceiverConnectionCreatedEventArgs args)
    {
        try
        {
            ApplyStreamPreferences(args.Connection);
            ApplyInputDevicePolicy(args.Connection);
            if (args.Connection is not null) _activeConnections.Add(args.Connection);
            _connectionCount++;
            _connectedAtUtc = DateTime.UtcNow;
            _mediaSourceAtUtc = null;
            _staleStreak = 0;
            string name = args.Connection?.Transmitter?.Name ?? "未知设备";
            string mac = args.Connection?.Transmitter?.MacAddress ?? "";
            string fingerprint = VaultCrypto.DeviceFingerprint(name, mac);

            // 隐私模式：不把设备名/MAC/PIN 写进日志；会话事件也不落盘（见 AppLog.Event）
            if (PrivacyLog) _log.Event("miracast", "手机已连接（设备信息已按隐私设置隐藏）");
            else _log.Info("miracast", $"手机已连接：{name} ({mac}) PIN={args.Pin}");

            Notice?.Invoke("手机已连接");
            ConnectionCreated?.Invoke(this, new MiracastConnectionEventArgs(args.Pin ?? "", name, mac, fingerprint));
            RaiseStatus();
        }
        catch (Exception ex) { _log.Debug("miracast", $"连接事件处理异常：{ex.Message}"); }
    }

    /// <summary>
    /// 应用「输入设备转发」策略（Miracast UIBC）。
    ///
    /// 方向必须先说清楚：这套 API 的语义是「接收端（本机）把输入采集起来发给发送端（手机）」——
    /// 官方文档 MiracastReceiverInputDevices 的原文就是 "devices from which the Miracast Receiver
    /// can collect input and transmit to the Miracast Transmitter"。它**不是**「手机反向控制本机」，
    /// 不存在那个安全风险。早先版本按后者理解并一律关闭，等于关掉了唯一一条「电脑键盘 → 手机」的官方通道。
    ///
    /// 现在：键盘按设置开关决定是否转发；手柄仍然一律关闭（暂不做手柄映射）。
    /// 能否真正生效取决于手机是否愿意接收（IsRequestedByTransmitter），这一点本机无法单方面决定。
    /// </summary>
    private void ApplyInputDevicePolicy(MiracastReceiverConnection? connection)
    {
        try
        {
            var inputs = connection?.InputDevices;
            if (inputs is null) return;

            var keyboard = inputs.Keyboard;
            if (keyboard is not null)
            {
                bool requested = false;
                try { requested = keyboard.IsRequestedByTransmitter; } catch { }
                try
                {
                    keyboard.TransmitInput = TransmitKeyboard;
                    if (TransmitKeyboard)
                    {
                        _log.Event("miracast", requested
                            ? "键盘回传已开启：手机表示愿意接收键盘输入 —— 保持本窗口在前，直接敲键盘试试"
                            : "键盘回传已开启，但手机没有表示愿意接收键盘输入（很可能不支持 UIBC）");
                    }
                    else if (requested)
                    {
                        _log.Info("miracast", "手机愿意接收键盘输入，但「键盘回传」未开启（设置 → 安全）");
                    }
                }
                catch (Exception ex) { _log.Warn("miracast", $"设置键盘回传失败：{ex.Message}"); }

                try { keyboard.Changed -= OnKeyboardDeviceChanged; keyboard.Changed += OnKeyboardDeviceChanged; } catch { }
                _keyboardTransmitLogged = false;
                UpdateKeyboardState(requested, transmitting: false);
            }

            var gameController = inputs.GameController;
            if (gameController is not null)
            {
                bool requested = false;
                try { requested = gameController.IsRequestedByTransmitter; } catch { }
                gameController.TransmitInput = false;      // 手柄映射暂未实现，保持关闭
                if (requested) _log.Info("miracast", "手机请求了手柄输入转发；暂不支持，已保持关闭");
            }
        }
        catch (Exception ex) { _log.Debug("miracast", $"应用输入设备策略失败：{ex.Message}"); }
    }

    /// <summary>设置改动后对已连接的会话重新应用一次输入设备策略，免得用户非要重连才生效。</summary>
    public void ReapplyInputDevicePolicy()
    {
        foreach (var connection in _activeConnections.ToArray())
        {
            try { ApplyInputDevicePolicy(connection); } catch { }
        }
    }

    /// <summary>手机侧会主动改这个通道的状态（刚连上、锁屏等），只在状态真的变化时记日志。</summary>
    private void OnKeyboardDeviceChanged(MiracastReceiverKeyboardDevice sender, object args)
    {
        try
        {
            bool requested = false, transmitting = false;
            try { requested = sender.IsRequestedByTransmitter; } catch { }
            try { transmitting = sender.IsTransmittingInput; } catch { }
            UpdateKeyboardState(requested, transmitting);
        }
        catch (Exception ex) { _log.Debug("miracast", $"键盘通道状态读取失败：{ex.Message}"); }
    }

    /// <summary>刷新给界面看的状态文字；「已开启 / 正在发送」之间会随每次按键来回跳，只记第一次。</summary>
    private void UpdateKeyboardState(bool requested, bool transmitting)
    {
        string state = !TransmitKeyboard ? "未开启"
            : !requested ? "手机未请求"
            : transmitting ? "正在发送" : "已开启";
        if (state == KeyboardBackchannelState) return;
        string previous = KeyboardBackchannelState;
        KeyboardBackchannelState = state;

        bool bothTransmitSide = (state is "已开启" or "正在发送") && (previous is "已开启" or "正在发送");
        if (bothTransmitSide)
        {
            if (state == "正在发送" && !_keyboardTransmitLogged)
            {
                _keyboardTransmitLogged = true;
                _log.Event("miracast", "键盘回传状态：正在发送（本机按键已转发到手机）");
            }
            return;
        }
        _log.Event("miracast", $"键盘回传状态：{state}");
        RaiseStatus();
    }

    private void OnMediaSourceCreated(MiracastReceiverSession sender, MiracastReceiverMediaSourceCreatedEventArgs args)
    {
        try
        {
            var source = args.MediaSource;
            _log.Info("miracast", $"已收到手机画面媒体源：{(source is null ? "null" : "MediaSource")}");
            if (source is null) return;
            _mediaSourceAtUtc = DateTime.UtcNow;
            // 光标通道：手机端若有鼠标指针也可显示
            try
            {
                var cursor = args.CursorImageChannelSettings;
                if (cursor is not null) cursor.IsEnabled = false;
            }
            catch { }
            MediaSourceCreated?.Invoke(this, source);
        }
        catch (Exception ex) { _log.Error("miracast", "媒体源事件处理异常", ex); }
    }

    private void OnDisconnected(MiracastReceiverSession sender, MiracastReceiverDisconnectedEventArgs args)
    {
        try
        {
            _connectionCount = Math.Max(0, _connectionCount - 1);
            if (args.Connection is not null) _activeConnections.Remove(args.Connection);
            _mediaSourceAtUtc = null;
            _staleStreak = 0;
            if (_connectionCount == 0)
            {
                _keyboardTransmitLogged = false;
                KeyboardBackchannelState = "未连接";
            }
            string reason = args.Connection is null ? "未知" : "正常断开";
            _log.Event("miracast", $"手机已断开连接（{reason}）");
            Notice?.Invoke("手机已断开投屏");
            Disconnected?.Invoke(this, reason);
            RaiseStatus();
        }
        catch (Exception ex) { _log.Debug("miracast", $"断开事件处理异常：{ex.Message}"); }
    }

    private void OnReceiverStatusChanged(MiracastReceiver sender, object args)
    {
        try { RaiseStatus(); } catch { }
    }

    public void DisconnectAll()
    {
        try { _receiver?.ClearKnownTransmitters(); } catch { }
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(this, QueryStatus()); } catch { }
    }

    private static MiracastStartResult Fail(string message, string detail) => new(false, message, detail);

    private static string Describe(Exception? ex)
        => ex is null ? "无附加信息" : $"{ex.GetType().Name}(0x{ex.HResult:X8}): {ex.Message}";

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}