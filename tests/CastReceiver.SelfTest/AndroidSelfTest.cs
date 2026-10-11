using CastReceiver.Core.Android;
using CastReceiver.Core.Diagnostics;

namespace CastReceiver.SelfTest;

/// <summary>
/// 安卓鼠标控制（⑧）的纯逻辑用例：**全都不需要手机**，跑一次几毫秒。
///
/// 为什么把这些单拎出来：坐标换算写错了不会崩、不会报错，只会"点偏"——
/// 那是最难发现的一类问题。所以把 adb 输出解析与坐标换算逐条钉死（用的是真机/模拟器导出的真实输出样本）。
/// 真正连手机注入触摸的部分由 tools\ui-spike\adb-verify.ps1 用安卓模拟器实测（那个必须有设备才能跑）。
/// </summary>
internal static class AndroidSelfTest
{
    private const string Group = "J 安卓鼠标控制（⑧）";

    public static IReadOnlyList<SelfTestItem> RunAll(string? liveEndpoint = null)
    {
        var items = new List<SelfTestItem>();
        ParseDevices(items);
        ParseScreen(items);
        ParseRotation(items);
        ParseMdns(items);
        ParseLinkText(items);
        MapFit(items);
        MapFill(items);
        MapLandscape(items);
        MapTapSpace(items);
        Scroll(items);
        LocateAdb(items);
        AutoConnectLive(items, liveEndpoint);
        return items;
    }

    /// <summary>
    /// 「自动发现 → 自动连接」整条链路的实测。
    ///
    /// 发现结果允许注入：注入的就是 <c>adb mdns services</c> 的真实格式，
    /// 而**连接与读设备信息走真实 adb**。这样没接真机时也能验这条链路。
    /// 没传端点就如实报"未验证"，不假装通过。
    /// </summary>
    private static void AutoConnectLive(List<SelfTestItem> items, string? liveEndpoint)
    {
        const string name = "自动发现 → 自动连接（注入发现结果，连真实端点）";
        if (string.IsNullOrWhiteSpace(liveEndpoint))
        {
            items.Add(new SelfTestItem(Group, name, SelfTestOutcome.Warning,
                "未指定 --adb-endpoint，本项未验证（传 <host:port> 才跑）"));
            return;
        }

        string host = liveEndpoint;
        int port = 5555;
        int colon = liveEndpoint.LastIndexOf(':');
        if (colon > 0 && int.TryParse(liveEndpoint[(colon + 1)..], out int parsed))
        {
            host = liveEndpoint[..colon];
            port = parsed;
        }

        var session = new AdbWirelessSession();
        try
        {
            session.DiscoveryOutputProvider = () => Task.FromResult(
                "List of discovered mdns services\n" +
                $"adb-TESTDEVICE-CONNECT\t_adb-tls-connect._tcp\t{host}:{port}\n");

            AdbAutoConnectReport report = session.AutoConnectAsync().GetAwaiter().GetResult();
            bool ok = report.Connected && session.IsConnected && session.Screen is { IsValid: true };
            Add(items, name, ok,
                ok
                    ? $"已连上 {session.Device?.Serial}：{session.Device?.VersionText}，" +
                      $"屏幕 {session.Screen?.CurrentWidth}×{session.Screen?.CurrentHeight}（旋转 {session.Screen?.Rotation}）"
                    : $"未连上：{report.Detail}（发现 {report.Discovered} 个，试了 {report.Tried} 个）");
        }
        catch (Exception ex)
        {
            Add(items, name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { session.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        }
    }

    private static void Add(List<SelfTestItem> items, string name, bool ok, string detail)
        => items.Add(new SelfTestItem(Group, name, ok ? SelfTestOutcome.Passed : SelfTestOutcome.Failed, detail));

    // ------------------------------------------------------------------ adb 输出解析

    private static void ParseDevices(List<SelfTestItem> items)
    {
        const string sample =
            "List of devices attached\n" +
            "192.168.5.20:37251     device product:panther model:Pixel_7 device:panther transport_id:3\n" +
            "emulator-5554          unauthorized transport_id:1\n" +
            "R58M12ABCDE            offline transport_id:2\n";

        IReadOnlyList<AdbDevice> devices = AdbParsers.ParseDevices(sample);
        bool countOk = devices.Count == 3;
        AdbDevice? wireless = devices.FirstOrDefault(d => d.Serial.Contains(':'));
        bool wirelessOk = wireless is { IsOnline: true, IsWireless: true, Host: "192.168.5.20", Port: 37251, Model: "Pixel_7" };
        AdbDevice? unauthorized = devices.FirstOrDefault(d => d.State == "unauthorized");
        bool statesOk = unauthorized is { IsOnline: false };

        Add(items, "解析 adb devices -l（含无线/未授权/离线三种状态）", countOk && wirelessOk && statesOk,
            $"{devices.Count} 台；无线={wireless?.DisplayName ?? "无"}；未授权条目={(unauthorized is null ? "丢失" : "在")}");

        // 空列表 / 只有 daemon 提示时不能瞎报设备
        bool emptyOk = AdbParsers.ParseDevices("* daemon not running; starting now at tcp:5037\nList of devices attached\n").Count == 0;
        Add(items, "没有任何设备时不编造设备", emptyOk, "只返回空列表");
    }

    private static void ParseScreen(List<SelfTestItem> items)
    {
        (int Width, int Height)? only = AdbParsers.ParseReportedSize("Physical size: 1080x2400\n");
        bool onlyOk = only is { Width: 1080, Height: 2400 };

        // 用户改过分辨率时 Override 才是真正生效的那组，必须优先
        (int Width, int Height)? over = AdbParsers.ParseReportedSize("Physical size: 1080x2400\nOverride size: 720x1600\n");
        bool overOk = over is { Width: 720, Height: 1600 };

        Add(items, "解析 wm size（只有 Physical / 有 Override 时以 Override 为准）", onlyOk && overOk,
            $"Physical→{only?.Width}×{only?.Height}；Override→{over?.Width}×{over?.Height}");

        Add(items, "解析不出尺寸时返回 null（不猜一个默认值）",
            AdbParsers.ParseReportedSize("error: device offline\n") is null, "返回 null");

        // 真实样本（本机 Android 14 模拟器 + wm size 2340x1080 的输出片段）：
        // 逻辑尺寸必须从 dumpsys display 里读，而不是拿 wm size 去 Min/Max 推
        const string displaySample = """
              mViewports=[DisplayViewport{type=INTERNAL, valid=true, displayId=0, orientation=0}]
              DisplayDeviceInfo{"Built-in Screen", 1080 x 2340, modeId 1, touch INTERNAL, rotation 0}
                mCurrentOrientation=0
                mBaseDisplayInfo=DisplayInfo{"Built-in Screen", real 1080 x 2340, rotation 0}
                mOverrideDisplayInfo=DisplayInfo{"Built-in Screen", real 2340 x 1080, rotation 0}
            """;
        (int Width, int Height)? logical = AdbParsers.ParseLogicalSize(displaySample);
        int rotation = AdbParsers.ParseRotation(displaySample);
        bool logicalOk = logical is { Width: 2340, Height: 1080 } && rotation == 0;
        Add(items, "从 dumpsys display 读当前方向的生效尺寸（Override 优先）与旋转", logicalOk,
            $"逻辑尺寸→{logical?.Width}×{logical?.Height}，旋转→{rotation}（真实样本：改了尺寸但旋转为 0）");

        // 上一条正是"不能拿 wm size 去推算"的证据：报的是 2340×1080、旋转 0，
        // 如果按"自然方向一定竖屏"去 Min/Max，会算成 1080×2340，坐标整体错位
        var tricky = new AndroidScreen(2340, 1080, 2340, 1080, 0);
        var guessed = new AndroidScreen(2340, 1080, 0, 0, 0);
        Add(items, "有实测逻辑尺寸时不再按旋转猜（猜会把 2340×1080 算成 1080×2340）",
            tricky.CurrentWidth == 2340 && tricky.CurrentHeight == 1080
            && guessed.CurrentWidth == 1080 && guessed.CurrentHeight == 2340,
            $"实测→{tricky.CurrentWidth}×{tricky.CurrentHeight}；只按旋转猜→{guessed.CurrentWidth}×{guessed.CurrentHeight}");
    }

    private static void ParseRotation(List<SelfTestItem> items)
    {
        int fromInput = AdbParsers.ParseRotation("Input Reader State:\n  SurfaceOrientation: 1\n");
        int fromWindow = AdbParsers.ParseRotation("Display: mDisplayId=0\n  mRotation=2\n");
        int fromDisplay = AdbParsers.ParseRotation("mCurrentOrientation=3\n");
        int junk = AdbParsers.ParseRotation("error: no devices/emulators found\n");

        Add(items, "解析屏幕旋转（SurfaceOrientation / mRotation / orientation 三种写法）",
            fromInput == 1 && fromWindow == 2 && fromDisplay == 3,
            $"dumpsys input→{fromInput}，dumpsys window→{fromWindow}，dumpsys display→{fromDisplay}");
        Add(items, "读不出旋转时返回 -1（由调用方决定退化策略，而不是偷偷当 0）", junk == -1, $"junk→{junk}");
    }

    private static void ParseMdns(List<SelfTestItem> items)
    {
        const string sample =
            "List of discovered mdns services\n" +
            "adb-1A2B3C-XYZ\t_adb-tls-connect._tcp\t192.168.5.20:37251\n" +
            "adb-1A2B3C-ABC\t_adb-tls-pairing._tcp\t192.168.5.20:41235\n";

        IReadOnlyList<AdbMdnsService> found = AdbMdnsParser.Parse(sample);
        AdbMdnsService? connect = found.FirstOrDefault(s => s.IsConnect);
        AdbMdnsService? pairing = found.FirstOrDefault(s => s.IsPairing);
        Add(items, "解析 adb mdns services（自动发现连接端口与配对端口）",
            found.Count == 2 && connect?.Port == 37251 && pairing?.Port == 41235,
            $"{found.Count} 个服务：连接={connect?.Endpoint}，配对={pairing?.Endpoint}");
    }

    private static void ParseLinkText(List<SelfTestItem> items)
    {
        bool ok = AdbParsers.ConnectSucceeded("connected to 192.168.5.20:37251")
                  && AdbParsers.ConnectSucceeded("already connected to 192.168.5.20:37251")
                  && !AdbParsers.ConnectSucceeded("failed to connect to 192.168.5.20:37251")
                  && !AdbParsers.ConnectSucceeded("cannot connect to 192.168.5.20:37251: 由于目标计算机积极拒绝，无法连接。");
        Add(items, "判定 adb connect 是否真的连上（不拿「没报错」当成功）", ok, "connected/already connected 才算成功");

        bool unsupported = AdbParsers.LooksLikePairingUnsupported("unknown command pair")
                           && AdbParsers.LooksLikePairingUnsupported("Failed to pair to 192.168.5.20:41235: protocol fault")
                           && !AdbParsers.LooksLikePairingUnsupported("Successfully paired to 192.168.5.20:41235");
        Add(items, "识别「不支持无线配对」（老 adb / 老系统）以便界面明说", unsupported, "unknown command / failed to pair / protocol fault");
    }

    // ------------------------------------------------------------------ 坐标换算

    private static void MapFit(List<SelfTestItem> items)
    {
        // 16:9 画面放进正方形窗口（适应）→ 上下留黑边
        VideoBox box = ScreenCoordinateMapper.ComputeVideoBox(VideoFitMode.Fit, 400, 400, 1920, 1080);
        var screen = new AndroidScreen(1080, 2400, 1080, 2400, 0);

        bool boxOk = Math.Abs(box.Width - 400) < 0.01 && Math.Abs(box.Height - 225) < 0.01 && Math.Abs(box.Y - 87.5) < 0.01;
        ScreenMapResult top = ScreenCoordinateMapper.Map(200, 20, box, screen);          // 黑边上
        ScreenMapResult center = ScreenCoordinateMapper.Map(200, 200, box, screen);      // 画面中心
        ScreenMapResult leftTop = ScreenCoordinateMapper.Map(box.X, box.Y, box, screen);
        ScreenMapResult rightBottom = ScreenCoordinateMapper.Map(box.X + box.Width, box.Y + box.Height, box, screen);

        bool centerOk = center.Inside && Math.Abs(center.U - 0.5) < 0.01 && Math.Abs(center.V - 0.5) < 0.01;
        Add(items, "适应模式：算出画面矩形，并判定「点在黑边上」",
            boxOk && !top.Inside && centerOk && leftTop.Inside && rightBottom.Inside,
            $"矩形=({box.X:F0},{box.Y:F0}) {box.Width:F0}×{box.Height:F0}；黑边上的点 Inside={top.Inside}；中心→手机({center.PhoneX},{center.PhoneY})");

        Add(items, "归一化坐标映射到手机屏幕（中心→屏幕中心）",
            center.PhoneX == 540 && center.PhoneY == 1200, $"手机({center.PhoneX},{center.PhoneY})，屏幕 1080×2400");
    }

    private static void MapFill(List<SelfTestItem> items)
    {
        // 铺满：窗口比画面更"高"，左右会被裁掉
        VideoBox box = ScreenCoordinateMapper.ComputeVideoBox(VideoFitMode.Fill, 400, 400, 1920, 1080);
        var screen = new AndroidScreen(1080, 2400, 1080, 2400, 0);
        ScreenMapResult center = ScreenCoordinateMapper.Map(200, 200, box, screen);
        ScreenMapResult left = ScreenCoordinateMapper.Map(0, 200, box, screen);

        // 裁切后窗口左边界对应视频里的一小段（u > 0），但仍应落在手机屏幕内
        bool ok = center.Inside && left.Inside && center.PhoneX == 540 && left.PhoneX >= 0 && left.PhoneX < 1080 && left.U > 0;
        Add(items, "铺满模式：算出被裁掉的部分（左边界不是 u=0）",
            ok, $"矩形 {box.Width:F0}×{box.Height:F0}；左边界 u={left.U:F3}→手机 x={left.PhoneX}；中心→{center.PhoneX}");
    }

    private static void MapLandscape(List<SelfTestItem> items)
    {
        var portrait = new AndroidScreen(1080, 2400, 1080, 2400, 0);
        var landscape = new AndroidScreen(1080, 2400, 2400, 1080, 1);
        bool sizesOk = portrait.CurrentWidth == 1080 && portrait.CurrentHeight == 2400
                       && landscape.CurrentWidth == 2400 && landscape.CurrentHeight == 1080;
        Add(items, "旋转 90° 时屏幕宽高互换", sizesOk,
            $"竖屏 {portrait.CurrentWidth}×{portrait.CurrentHeight}；横屏 {landscape.CurrentWidth}×{landscape.CurrentHeight}");

        // 横屏：画面也是横的，点在画面右侧中间 → 手机最右边中间
        VideoBox box = ScreenCoordinateMapper.ComputeVideoBox(VideoFitMode.Fit, 400, 200, 2400, 1080);
        ScreenMapResult right = ScreenCoordinateMapper.Map(box.X + box.Width, box.Y + box.Height / 2, box, landscape);
        Add(items, "横屏下换算到手机坐标（画面右中→屏幕右中）",
            right.Inside && right.PhoneX == 2399 && right.PhoneY == 540,
            $"手机({right.PhoneX},{right.PhoneY})，屏幕 2400×1080");
    }

    private static void MapTapSpace(List<SelfTestItem> items)
    {
        var screen = new AndroidScreen(1080, 2400, 2400, 1080, 1);
        VideoBox box = new(0, 0, 100, 100);
        ScreenMapResult point = ScreenCoordinateMapper.Map(100, 100, box, screen);

        TapCoordinateSpace saved = ScreenCoordinateMapper.TapSpace;
        try
        {
            ScreenCoordinateMapper.TapSpace = TapCoordinateSpace.Natural;
            ScreenMapResult natural = ScreenCoordinateMapper.Map(100, 100, box, screen);
            Add(items, "点按坐标系可切换（当前方向 / 自然方向），换算是显式而不是靠默认值",
                point.PhoneX == 2399 && point.PhoneY == 1079 && natural.PhoneX == 1079 && natural.PhoneY == 2399,
                $"当前方向→({point.PhoneX},{point.PhoneY})；自然方向→({natural.PhoneX},{natural.PhoneY})");
        }
        finally
        {
            ScreenCoordinateMapper.TapSpace = saved;
        }
    }

    private static void Scroll(List<SelfTestItem> items)
    {
        var screen = new AndroidScreen(1080, 2400, 1080, 2400, 0);
        (int upDx, int upDy) = ScreenCoordinateMapper.ScrollVector(1, screen);
        (int downDx, int downDy) = ScreenCoordinateMapper.ScrollVector(-1, screen);
        bool ok = upDx == 0 && upDy > 0 && downDy < 0 && Math.Abs(upDy) == Math.Abs(downDy) && Math.Abs(upDy) <= 900;
        Add(items, "滚轮方向换算（向上滚＝手机手指往下滑，距离按屏幕高度取且有上限）",
            ok, $"向上滚→dy={upDy}，向下滚→dy={downDy}");
    }

    private static void LocateAdb(List<SelfTestItem> items)
    {
        string? path = AdbClient.Locate();
        Add(items, "能在本机找到可用的 adb（自带优先，其次系统 PATH / SDK）",
            path is not null, path is null ? "没找到 adb" : path);
    }
}
