using System.Globalization;
using System.Text.RegularExpressions;
using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Android;

/// <summary>无线调试连接的状态。</summary>
public enum AdbLinkState
{
    /// <summary>本机找不到 adb（程序自带一份，正常不该出现）。</summary>
    NoAdb,
    /// <summary>有 adb，但还没连手机。</summary>
    Idle,
    /// <summary>正在配对。</summary>
    Pairing,
    /// <summary>正在连接。</summary>
    Connecting,
    /// <summary>已连上，可以注入触摸。</summary>
    Connected,
    /// <summary>这台手机/这份 adb 不支持无线调试配对（Android 11 以下，或 adb 太老）。</summary>
    Unsupported,
    /// <summary>失败（失败原因在 <see cref="StatusText"/> 与 <see cref="LastOutput"/> 里）。</summary>
    Failed,
}

/// <summary>手机上探测到的信息。</summary>
public sealed record AndroidDeviceReport(string Serial, string? Model, int Sdk, string? Release, AndroidScreen? Screen)
{
    /// <summary>无线调试配对要 Android 11（API 30）及以上。</summary>
    public bool SupportsWirelessPairing => Sdk >= 30;

    public string VersionText => Release is null
        ? (Sdk > 0 ? $"API {Sdk}" : "系统版本未知")
        : $"Android {Release}" + (Sdk > 0 ? $"（API {Sdk}）" : "");
}

/// <summary>
/// 安卓「无线调试 + 鼠标控制」的状态机。
///
/// 流程与手机上看到的一致：
/// 1) 手机开 开发者选项 → 无线调试（Android 11+ 才有）；
/// 2) 首次要一次配对：把手机上的**配对端口 + 6 位配对码**填进来 → <c>adb pair</c>；
/// 3) 之后连接：把手机上的**连接端口**填进来 → <c>adb connect</c>（两端在同一网段时也能自动发现）。
///
/// 所有 adb 调用都在后台线程上跑（adb 会因为设备离线卡很久），状态变化通过 <see cref="Changed"/> 通知界面。
/// </summary>
public sealed class AdbWirelessSession
{
    private readonly AppLog? _log;
    private readonly object _gate = new();

    public AdbWirelessSession(AppLog? log = null)
    {
        _log = log;
        Client = AdbClient.Create();
        State = Client is null ? AdbLinkState.NoAdb : AdbLinkState.Idle;
        StatusText = Client is null
            ? "本机没有可用的 adb（程序自带一份，缺失时请重新安装）"
            : "未连接（打开开关会自动发现手机）";
        Input = Client is null ? null : new AndroidInput(Client, () => Serial, m => _log?.Debug("adb", m));
    }

    public AdbClient? Client { get; }

    /// <summary>触摸注入器（未连上手机时为 null）。</summary>
    public AndroidInput? Input { get; }

    public AdbLinkState State { get; private set; }

    /// <summary>给用户看的一句话状态。</summary>
    public string StatusText { get; private set; }

    /// <summary>最近一次 adb 的输出（排查用，界面可展开显示）。</summary>
    public string LastOutput { get; private set; } = "";

    /// <summary>当前连接的设备序列号（无线连接就是 IP:端口）。</summary>
    public string? Serial { get; private set; }

    /// <summary>手机信息（连上后才有）。</summary>
    public AndroidDeviceReport? Device { get; private set; }

    /// <summary>手机屏幕信息（连上后才有）。</summary>
    public AndroidScreen? Screen => Device?.Screen;

    /// <summary>这台手机支不支持无线调试**配对**（Android 11+）。连上之前是"未知"。</summary>
    public bool? PairingSupported { get; private set; }

    /// <summary>
    /// 本机与这台手机**配对过**吗（配对成功后由界面记进设置）。
    /// 用途：没配对过时 `adb connect` 会被手机拒掉 —— 这时要直接引导用户去配对，
    /// 而不是一直"正在连接 → 连接失败"地转圈。
    /// </summary>
    public bool EverPaired { get; set; }

    /// <summary>上次自动连接失败，且从来没配对成功过：界面据此弹出配对引导。</summary>
    public bool NeedsPairing { get; private set; }

    /// <summary>最近一次失败的直接原因（adb 的原话，取最后一行）。</summary>
    public string LastFailure { get; private set; } = "";

    public bool IsConnected => State == AdbLinkState.Connected && Serial is not null;

    /// <summary>状态或信息变化。</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ 配对 / 连接

    /// <summary>用配对码配对（手机「使用配对码配对设备」里那一组）。</summary>
    public async Task<AdbResult?> PairAsync(string host, int pairPort, string code)
    {
        if (Client is null) return null;
        SetState(AdbLinkState.Pairing, $"正在与 {host}:{pairPort} 配对…");

        string arguments = $"pair {host}:{pairPort} {code}";
        AdbResult result = await Task.Run(() => Client.Run(arguments, 60000)).ConfigureAwait(false);
        LastOutput = result.Text;

        if (result.Ok && result.Text.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            PairingSupported = true;
            _log?.Info("adb", $"配对成功：{host}:{pairPort}");
            SetState(AdbLinkState.Idle, "配对成功，接着填「连接端口」点连接");
        }
        else if (AdbParsers.LooksLikePairingUnsupported(result.Text))
        {
            PairingSupported = false;
            SetState(AdbLinkState.Unsupported, "这台手机或这份 adb 不支持无线调试配对（需要 Android 11 及以上）");
        }
        else
        {
            SetState(AdbLinkState.Failed, "配对失败：" + FirstLine(result.Text));
        }

        return result;
    }

    /// <summary>连接到手机（无线调试里显示的「IP 地址和端口」）。</summary>
    public async Task<AdbResult?> ConnectAsync(string host, int port, bool remember = true)
    {
        if (Client is null) return null;
        SetState(AdbLinkState.Connecting, $"正在连接 {host}:{port}…");

        string endpoint = $"{host}:{port}";
        AdbResult connect = await Task.Run(() => Client.Run($"connect {endpoint}", 30000)).ConfigureAwait(false);
        LastOutput = connect.Text;

        if (!AdbParsers.ConnectSucceeded(connect.Text))
        {
            SetState(AdbLinkState.Failed, "连接失败：" + FirstLine(connect.Text));
            return connect;
        }

        bool ok = await RefreshCoreAsync(endpoint).ConfigureAwait(false);
        if (!ok)
        {
            SetState(AdbLinkState.Failed, "已发出连接，但设备没就绪（检查手机是否弹了授权确认）");
        }
        else if (remember)
        {
            LastEndpoint = endpoint;
        }

        return connect;
    }

    /// <summary>刷新设备状态与屏幕信息（定时调用即可）。<paramref name="full"/>=false 只查设备在不在。</summary>
    public async Task RefreshAsync(bool full = true)
    {
        if (Client is null) return;
        await RefreshCoreAsync(Serial, full).ConfigureAwait(false);
    }

    /// <summary>
    /// 只重读屏幕尺寸与旋转（不碰设备列表）。
    ///
    /// 为什么需要它：**手机是会转的**。连上时读到的尺寸/旋转一旦过期，
    /// 后面每一次点击都会按旧尺寸换算 —— 表现就是"点哪儿都不对"。
    /// 所以开着鼠标控制时要隔几秒重读一次（两个只读命令，代价很小）。
    /// </summary>
    public async Task RefreshScreenAsync()
    {
        if (Client is null) return;
        string? serial = Serial;
        if (string.IsNullOrWhiteSpace(serial)) return;

        AndroidScreen? screen = await Task.Run(() => ReadScreen(serial)).ConfigureAwait(false);
        if (screen is null || !screen.IsValid) return;

        AndroidDeviceReport? device = Device;
        if (device is null) return;

        // 尺寸或旋转变了才发通知（不然每秒一次的通知会把界面刷爆）
        if (device.Screen is { } old && old.CurrentWidth == screen.CurrentWidth
            && old.CurrentHeight == screen.CurrentHeight && old.Rotation == screen.Rotation)
        {
            return;
        }

        Device = device with { Screen = screen };
        _log?.Info("adb", $"手机画面尺寸/旋转变化：{screen.CurrentWidth}×{screen.CurrentHeight}，旋转 {screen.Rotation * 90}°");
        SetState(State, $"{device.Model ?? device.Serial} · {device.VersionText}");
    }

    /// <summary>断开并清理。</summary>
    public async Task DisconnectAsync()
    {
        if (Client is null) return;
        string? serial = Serial;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            try { await Task.Run(() => Client.Run($"disconnect {serial}", 15000)).ConfigureAwait(false); } catch { }
        }

        Serial = null;
        Device = null;
        SetState(AdbLinkState.Idle, "已断开");
    }

    /// <summary>自动发现同一网段上开了无线调试的手机（<c>adb mdns services</c>）。</summary>
    public async Task<IReadOnlyList<AdbMdnsService>> DiscoverAsync()
    {
        if (Client is null) return Array.Empty<AdbMdnsService>();
        AdbResult result = await Task.Run(() => Client.Run("mdns services", 20000)).ConfigureAwait(false);
        LastOutput = result.Text;
        IReadOnlyList<AdbMdnsService> found = AdbMdnsParser.Parse(result.Text);
        if (found.Count == 0)
        {
            _log?.Debug("adb", "mDNS 没发现无线调试服务（手机可能没开无线调试，或不在同一网段）");
        }

        return found;
    }

    /// <summary>
    /// 自动发现并连接 —— 用户**不需要填任何地址**。
    ///
    /// 为什么可以这样：手机打开「无线调试」后会通过 mDNS 广播服务
    /// （`_adb-tls-connect._tcp` 用于连接、`_adb-tls-pairing._tcp` 用于配对），
    /// adb 自己就能列出来（`adb mdns services`；本机实测可用，且**不需要** Bonjour）。
    /// 手机若跑过 `adb tcpip 5555`，还会广播传统的 `_adb._tcp`，也一并试。
    ///
    /// 只有**第一次**在一台新手机上用时才需要配对码（Android 的安全要求，任何工具都绕不过）；
    /// 配对过一次之后手机记住了这台电脑的密钥，之后就是"打开开关即连上"。
    /// </summary>
    public async Task<AdbAutoConnectReport> AutoConnectAsync()
    {
        if (Client is null)
        {
            return new AdbAutoConnectReport(false, 0, 0, "本机没有可用的 adb");
        }

        if (IsConnected)
        {
            return new AdbAutoConnectReport(true, 0, 0, "已经连接：" + (Device?.Serial ?? Serial ?? ""));
        }

        SetState(AdbLinkState.Connecting, "正在自动发现手机（mDNS）…");
        string listing = await Task.Run(ReadDiscoveryOutputAsync).ConfigureAwait(false);
        LastOutput = listing;

        IReadOnlyList<AdbMdnsService> found = AdbMdnsParser.Parse(listing);
        if (found.Count == 0)
        {
            SetState(AdbLinkState.Idle,
                "没发现开了无线调试的手机：请在手机上打开 设置 → 开发者选项 → 无线调试（首次还要配对一次）");
            return new AdbAutoConnectReport(false, 0, 0, "mDNS 没有发现任何无线调试服务");
        }

        // 优先"无线调试连接口"，其次传统的 adb tcp（跑过 adb tcpip 5555 的手机会广播它）
        var candidates = found
            .Where(s => s.IsConnect || s.ServiceType.StartsWith("_adb._tcp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Endpoint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(s => s.IsConnect)
            .ToList();

        if (candidates.Count == 0)
        {
            SetState(AdbLinkState.Idle, $"发现了 {found.Count} 个服务，但都不是可连接的无线调试端点");
            return new AdbAutoConnectReport(false, found.Count, 0, "只有配对端点，没有连接端点");
        }

        int tried = 0;
        string lastFailure = "";
        foreach (AdbMdnsService candidate in candidates)
        {
            tried++;
            SetState(AdbLinkState.Connecting, $"发现 {candidate.Endpoint}，正在连接…");
            // 单个端点只等 8 秒：连不上的地址本来就要等到 TCP 超时，
            // 等太久会让界面一直停在"正在连接"，而且反正每 10 秒会再试一次
            AdbResult connect = await Task.Run(() => Client.Run($"connect {candidate.Endpoint}", 8000)).ConfigureAwait(false);
            LastOutput = connect.Text;
            if (!AdbParsers.ConnectSucceeded(connect.Text))
            {
                lastFailure = FirstLine(connect.Text);
                continue;
            }

            if (await RefreshCoreAsync(candidate.Endpoint).ConfigureAwait(false))
            {
                LastEndpoint = candidate.Endpoint;
                NeedsPairing = false;
                _log?.Info("adb", $"自动连接成功：{candidate.Endpoint}");
                return new AdbAutoConnectReport(true, found.Count, tried, $"已自动连上 {Device?.Model ?? candidate.Endpoint}");
            }

            lastFailure = "连接被接受但设备没就绪（手机端可能弹了授权确认，或还没配对）";
        }

        LastFailure = lastFailure;
        NeedsPairing = !EverPaired;
        if (NeedsPairing)
        {
            // 这是用户最容易卡住的地方：服务能发现，但没配对就永远连不上。
            // 直接把下一步写清楚，别让人对着"连接失败"猜。
            SetState(AdbLinkState.Idle,
                $"连不上 {candidates[0].Endpoint}：这台手机还没和本机配对过 —— " +
                "请在手机上点「无线调试 → 使用配对码配对设备」，把 6 位配对码填进本程序（IP 与端口已自动填好）");
        }
        else
        {
            SetState(AdbLinkState.Idle,
                $"发现了 {found.Count} 个服务，但都连不上：{lastFailure}（手机与电脑要在同一网络；手机端若弹出授权请点允许）");
        }

        return new AdbAutoConnectReport(false, found.Count, tried, "发现的端点都连不上");
    }

    /// <summary>
    /// 生成一份"连不上时该看什么"的诊断报告：电脑这侧的网络、adb 原话、发现到的服务、配对状态。
    /// 用户点一下就能复制/保存发出来 —— 有了它，远程排查不用来回猜。
    /// </summary>
    public async Task<string> BuildDiagnosticAsync()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("==== 投屏Window · 安卓鼠标控制诊断 ====");
        text.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"adb：{(Client is null ? "未找到" : Client.ExecutablePath + (Client.IsBundled ? "（程序自带）" : "（来自系统）"))}");
        text.AppendLine($"状态：{State} · {StatusText}");
        text.AppendLine($"最近失败原因：{(LastFailure.Length > 0 ? LastFailure : "（无）")}");
        text.AppendLine($"配对过：{(EverPaired ? "是" : "否")}；需要配对：{(NeedsPairing ? "是" : "否")}");
        text.AppendLine($"当前连接：{Serial ?? "（无）"}；上次端点：{LastEndpoint ?? "（无）"}");

        if (NetworkFixer is not null)
        {
            text.AppendLine();
            text.AppendLine("---- 本机网络 ----");
            text.AppendLine(NetworkFixer());
        }

        if (Client is not null)
        {
            AdbResult version = await Task.Run(() => Client.Run("version", 10000)).ConfigureAwait(false);
            text.AppendLine();
            text.AppendLine("---- adb version ----");
            text.AppendLine(version.Text);

            AdbResult devices = await Task.Run(() => Client.Run("devices -l", 15000)).ConfigureAwait(false);
            text.AppendLine("---- adb devices -l ----");
            text.AppendLine(devices.Text);

            AdbResult mdns = await Task.Run(() => Client.Run("mdns services", 20000)).ConfigureAwait(false);
            text.AppendLine("---- adb mdns services ----");
            text.AppendLine(mdns.Text);

            AdbResult mdnsCheck = await Task.Run(() => Client.Run("mdns check", 10000)).ConfigureAwait(false);
            text.AppendLine("---- adb mdns check ----");
            text.AppendLine(mdnsCheck.Text);

            if (!string.IsNullOrWhiteSpace(Serial))
            {
                AdbResult state = await Task.Run(() => Client.RunOn(Serial!, "shell getprop ro.product.model", 10000)).ConfigureAwait(false);
                text.AppendLine("---- 手机型号 ----");
                text.AppendLine(state.Text);
            }
        }

        text.AppendLine();
        text.AppendLine("---- 最近一次 adb 输出 ----");
        text.AppendLine(LastOutput.Length > 0 ? LastOutput : "（无）");
        return text.ToString();
    }

    /// <summary>诊断里要附上的"本机网络"信息来源（由界面注入，Core 不依赖系统命令的实现细节）。</summary>
    public Func<string>? NetworkFixer { get; set; }

    /// <summary>发现服务的原始输出来源。默认跑 <c>adb mdns services</c>；
    /// 留这个口子是为了让自检能**注入一段真实输出**来验证"发现 → 连接"整条链路
    /// （不注入的话，这条链路只有真机才能验）。
    /// </summary>
    public Func<Task<string>>? DiscoveryOutputProvider { get; set; }

    private async Task<string> ReadDiscoveryOutputAsync()
    {
        if (DiscoveryOutputProvider is not null)
        {
            return await DiscoveryOutputProvider().ConfigureAwait(false);
        }

        if (Client is null) return "";
        AdbResult result = Client.Run("mdns services", 20000);
        return result.StdOut.Length > 0 ? result.StdOut : result.Text;
    }

    // ------------------------------------------------------------------ 内部

    private async Task<bool> RefreshCoreAsync(string? wanted, bool full = true)
    {
        if (Client is null) return false;

        // 还没连上任何设备、也没指定要连哪一台：这属于"正在找手机"的状态，
        // 由自动发现那条链路负责报状态；这里若继续跑就会把它刚说的话覆盖掉
        // （实测：自动发现刚报"发现了服务但连不上"，一秒后被刷成"检测到 N 台设备…"）。
        if (string.IsNullOrWhiteSpace(wanted) && string.IsNullOrWhiteSpace(Serial) && !IsConnected)
        {
            return false;
        }

        AdbResult devices = await Task.Run(() => Client.Run("devices -l", 20000)).ConfigureAwait(false);
        IReadOnlyList<AdbDevice> list = AdbParsers.ParseDevices(devices.StdOut.Length > 0 ? devices.StdOut : devices.Text);
        if (list.Count == 0)
        {
            Serial = null;
            Device = null;
            SetState(AdbLinkState.Idle, "没有已连接的安卓设备");
            return false;
        }

        AdbDevice? target = null;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            target = list.FirstOrDefault(d => string.Equals(d.Serial, wanted, StringComparison.OrdinalIgnoreCase));
        }

        // 没指定就用唯一在线的那台；多台且没指定就不猜
        if (target is null)
        {
            var online = list.Where(d => d.IsOnline).ToList();
            target = online.Count == 1 ? online[0] : null;
        }

        if (target is null)
        {
            var online = list.Where(d => d.IsOnline).ToList();
            Serial = null;
            Device = null;
            string why = online.Count > 1
                ? $"检测到 {online.Count} 台设备：请填手机 IP 与连接端口（或用「自动发现同网段手机」）"
                : "设备未就绪：" + list[0].State;
            SetState(AdbLinkState.Idle, why);
            return false;
        }

        if (!target.IsOnline)
        {
            Serial = target.Serial;
            Device = null;
            string why = target.State.ToLowerInvariant() switch
            {
                "unauthorized" => "手机还没允许这台电脑调试：请在手机上点「允许」",
                "offline" => "设备离线（重新连一次无线调试）",
                _ => "设备状态：" + target.State,
            };
            SetState(AdbLinkState.Failed, why);
            return false;
        }

        Serial = target.Serial;

        // 已经连上的设备：轻量轮询时不必每次都去读系统版本与屏幕（dumpsys 不便宜）
        if (!full && Device is not null && string.Equals(Device.Serial, target.Serial, StringComparison.OrdinalIgnoreCase))
        {
            SetState(AdbLinkState.Connected, $"{target.Model ?? target.Serial} · {Device.VersionText}");
            return true;
        }

        // 系统版本 / 屏幕：各查一次，都是纯读取
        AdbResult sdk = await Task.Run(() => Client.RunOn(target.Serial, "shell getprop ro.build.version.sdk", 15000)).ConfigureAwait(false);
        AdbResult release = await Task.Run(() => Client.RunOn(target.Serial, "shell getprop ro.build.version.release", 15000)).ConfigureAwait(false);
        AndroidScreen? screen = await Task.Run(() => ReadScreen(target.Serial)).ConfigureAwait(false);

        int sdkValue = AdbParsers.ParseSdk(sdk.StdOut);
        Device = new AndroidDeviceReport(target.Serial, target.Model, sdkValue,
            AdbParsers.ParseAndroidRelease(release.StdOut), screen);

        PairingSupported ??= sdkValue > 0 ? sdkValue >= 30 : null;

        if (sdkValue > 0 && sdkValue < 30)
        {
            // 连上了但系统太老：鼠标控制照样能用，但"无线配对"这条路走不通，必须如实说
            SetState(AdbLinkState.Connected,
                $"{target.Model ?? target.Serial} · Android {Device.Release}（不支持无线配对，需先用数据线执行一次 adb tcpip 5555）");
            return true;
        }

        SetState(AdbLinkState.Connected, $"{target.Model ?? target.Serial} · {Device.VersionText}");
        return true;
    }

    /// <summary>
    /// 读屏幕：<c>wm size</c> 给"系统报的分辨率"，<c>dumpsys display</c> 给"当前方向的生效尺寸 + 旋转"。
    /// 两者都要 —— 只靠前者推算，遇到"改过分辨率"或"横屏"就会算错（见 AndroidScreen 的说明）。
    /// </summary>
    private AndroidScreen? ReadScreen(string serial)
    {
        if (Client is null) return null;

        // 1) 当前方向的尺寸与旋转（最可靠的一条）
        AdbResult display = Client.RunOn(serial, "shell dumpsys display", 25000);
        int rotation = AdbParsers.ParseRotation(display.StdOut);
        (int Width, int Height)? logical = AdbParsers.ParseLogicalSize(display.StdOut);

        // 2) 老版本没有 mCurrentOrientation：退到 dumpsys input 的 SurfaceOrientation
        if (rotation < 0)
        {
            AdbResult input = Client.RunOn(serial, "shell dumpsys input", 20000);
            rotation = AdbParsers.ParseRotation(input.StdOut);
        }

        // 3) 还读不到就退到"用户设置"（只有关了自动旋转才是权威值）
        if (rotation < 0)
        {
            AdbResult userRotation = Client.RunOn(serial, "shell settings get system user_rotation", 10000);
            int parsed = AdbParsers.ParseRotation("rotation=" + userRotation.StdOut.Trim());
            if (parsed >= 0) rotation = parsed;
        }

        if (rotation < 0)
        {
            rotation = 0;
            _log?.Warn("adb", "没能从 dumpsys 里读出屏幕旋转，按竖屏(0)处理 —— 横屏时坐标会偏，这是已知限制");
        }

        // 4) 系统报的分辨率（wm size，Override 优先）
        AdbResult size = Client.RunOn(serial, "shell wm size", 15000);
        (int Width, int Height)? reported = AdbParsers.ParseReportedSize(size.StdOut);
        if (reported is null)
        {
            _log?.Warn("adb", "没能解析 wm size 的输出：" + FirstLine(size.Text));
            if (logical is null) return null;
            reported = logical;
        }

        return new AndroidScreen(reported.Value.Width, reported.Value.Height,
            logical?.Width ?? 0, logical?.Height ?? 0, rotation);
    }

    private void SetState(AdbLinkState state, string status)
    {
        lock (_gate)
        {
            State = state;
            StatusText = status;
        }

        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>最近一次记住的连接端点（下次启动可以直接重连）。</summary>
    public string? LastEndpoint { get; private set; }

    private static string FirstLine(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "（无输出）" : lines[^1].Trim();
    }
}

/// <summary>mDNS 发现的无线调试服务。</summary>
public sealed record AdbMdnsService(string Name, string ServiceType, string Host, int Port)
{
    public bool IsPairing => ServiceType.Contains("pairing", StringComparison.OrdinalIgnoreCase);
    public bool IsConnect => ServiceType.Contains("connect", StringComparison.OrdinalIgnoreCase);
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");
}

/// <summary>一次"自动发现并连接"的结果（界面与自检都用它，不靠异常传信息）。</summary>
public sealed record AdbAutoConnectReport(bool Connected, int Discovered, int Tried, string Detail);

/// <summary>解析 <c>adb mdns services</c> 的输出（纯函数，样本可自检）。</summary>
public static partial class AdbMdnsParser
{
    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)\s+(\d{1,3}(?:\.\d{1,3}){3}):(\d+)\s*$")]
    public static partial Regex LineRegex();

    /// <summary>
    /// 形如：
    /// <code>
    /// List of discovered mdns services
    /// adb-1A2B3C-XYZ	_adb-tls-connect._tcp	192.168.1.5:37251
    /// adb-1A2B3C-ABC	_adb-tls-pairing._tcp	192.168.1.5:41235
    /// </code>
    /// </summary>
    public static IReadOnlyList<AdbMdnsService> Parse(string? output)
    {
        var list = new List<AdbMdnsService>();
        if (string.IsNullOrWhiteSpace(output)) return list;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase)) continue;

            Match match = LineRegex().Match(line);
            if (!match.Success) continue;

            if (!int.TryParse(match.Groups[4].Value, out int port)) continue;
            list.Add(new AdbMdnsService(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, port));
        }

        return list;
    }
}
