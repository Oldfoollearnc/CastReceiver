using System.Text.Json;
using System.Text.Json.Serialization;

namespace CastReceiver.Core.Infrastructure;

/// <summary>应用配置：设备名、端口、播放器与 Miracast 选项。持久化到 %LocalAppData%\CastReceiver\settings.json。</summary>
public sealed class CastSettings
{
    /// <summary>手机端投屏列表里显示的名字。</summary>
    public string DeviceName { get; set; } = "PC";   // 隐私优先：中性名称，不在别人手机的投屏列表里暴露身份

    /// <summary>DLNA 内嵌 HTTP 服务端口。</summary>
    public int HttpPort { get; set; } = 2869;

    /// <summary>是否随程序启动自动开启 App 投屏（DLNA）。</summary>
    public bool AutoStartDlna { get; set; }

    /// <summary>是否随程序启动自动开启系统投屏（Miracast）监听。默认开启，用户无需任何点击。</summary>
    public bool AutoStartMiracast { get; set; } = true;

    /// <summary>是否随程序启动自动开启 AirPlay 接收端（让 iPhone/iPad 也能投屏）。默认开启。</summary>
    public bool AutoStartAirPlay { get; set; } = true;

    /// <summary>AirPlay 的设备地址（mDNS 的 deviceid，以及 _raop 实例名前缀）。</summary>
    public string AirPlayMac { get; set; } = "7A:1B:2C:3D:4E:5F";

    /// <summary>
    /// AirPlay 公钥（64 个十六进制字符 = 32 字节）。因为广播的 features 第 27 位为 0，
    /// 客户端不会做配对，这个值不参与密码学运算；但要求**跨次启动稳定**，否则
    /// 客户端可能把它当成另一台设备。默认给一个固定值，首次运行后写回配置。
    /// </summary>
    public string AirPlayPk { get; set; } = "b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7";

    /// <summary>主窗口最小化时自动用小窗继续显示画面（小窗 A）。</summary>
    public bool MiniOnMinimize { get; set; } = true;

    /// <summary>小窗是否置顶显示。</summary>
    public bool MiniPinned { get; set; } = true;

    /// <summary>小窗位置与大小；Left/Top 为 NaN 表示用默认位置（下次打开重新计算）。</summary>
    public double MiniLeft { get; set; } = double.NaN;
    public double MiniTop { get; set; } = double.NaN;
    public double MiniWidth { get; set; } = 384;

    /// <summary>默认高度让画面区域正好接近 16:9（384×216），避免小窗一开就上下留黑边。</summary>
    public double MiniHeight { get; set; } = 218;

    /// <summary>投屏开始时自动把音频输出切到指定设备。</summary>
    public bool AudioSwitchEnabled { get; set; }

    /// <summary>投屏时要切到的音频输出设备 id（空 = 还没选，功能不生效）。</summary>
    public string AudioSwitchTargetId { get; set; } = "";

    /// <summary>投屏结束后把音频输出还原成原来的设备。</summary>
    public bool AudioSwitchRestore { get; set; } = true;

    /// <summary>Miracast 连接是否需要在本机二次确认（手机端会看到配对/PIN 流程）。默认免确认，投屏更顺。</summary>
    public bool MiracastConfirmConnection { get; set; } = true;   // 隐私优先：默认需要本机确认

    /// <summary>拉流时使用的 User-Agent（部分视频 App 的 CDN 会校验）。</summary>
    public string StreamUserAgent { get; set; } =
        "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";

    /// <summary>拉流时附加的 Referer（防盗链场景）。</summary>
    public string StreamReferer { get; set; } = "";

    /// <summary>是否强制经由本地代理转发媒体流（用于注入 header、规避防盗链）。</summary>
    public bool ForceMediaProxy { get; set; }

    /// <summary>播放结束后是否自动回到待机。</summary>
    public bool StopAfterEof { get; set; } = true;

    public int Volume { get; set; } = 80;

    /// <summary>画面适配方式：Fit(适应) / Fill(铺满裁切) / Stretch(拉伸) / Native(原始 1:1)。</summary>
    public string MirrorFit { get; set; } = "Fit";

    /// <summary>画面缩放倍率（0.5 - 2.0），在适配方式之上生效。</summary>
    public double MirrorZoom { get; set; } = 1.0;

    /// <summary>界面主题：Dark / Light / System（跟随系统）。</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>关闭窗口时最小化到托盘而不是退出。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>开机自动启动。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>手机连上后自动进入全屏。</summary>
    public bool AutoFullscreen { get; set; }

    /// <summary>全屏观看时自动隐藏鼠标指针。</summary>
    public bool HideCursorInFullscreen { get; set; } = true;

    /// <summary>全屏观看时阻止屏幕保护与自动休眠。</summary>
    public bool PreventSleep { get; set; } = true;

    /// <summary>连接/断开时播放提示音并弹出气泡通知。</summary>
    public bool NotifyOnConnect { get; set; } = true;

    /// <summary>画质档位：LowLatency / Balanced / HighQuality。</summary>
    public string QualityPreset { get; set; } = "LowLatency";

    /// <summary>强调色：Mint / Amber / Violet / Rose / System。</summary>
    public string Accent { get; set; } = "Mint";

    /// <summary>界面密度：Comfortable / Compact。</summary>
    public string Density { get; set; } = "Comfortable";

    /// <summary>使用 Mica（Win11 半透明材质）背景。</summary>
    public bool UseMica { get; set; }

    /// <summary>全局快捷键开关（Ctrl+Alt+F 全屏、Ctrl+Alt+S 截屏）。</summary>
    public bool GlobalHotkeys { get; set; } = true;

    /// <summary>截图/录像保存目录（留空表示默认：视频\投屏Window）。</summary>
    public string CaptureFolder { get; set; } = "";

    /// <summary>退出程序时自动清空已保存的截图与录像（隐私加固）。</summary>
    public bool ClearCapturesOnExit { get; set; }

    // ---------------- 隐私与加密库（v1.1 起） ----------------

    /// <summary>隐私模式：投屏会话事件（连接/断开/时间）只留在内存，不写日志文件。</summary>
    public bool PrivacyLog { get; set; } = true;

    /// <summary>界面与气泡是否显示手机名称（默认不显示，避免旁人一眼看出你在投什么设备）。</summary>
    public bool ShowTransmitterName { get; set; }

    /// <summary>允许新设备抢占正在进行的投屏（默认关闭：别人不能把你的投屏顶掉）。</summary>
    public bool AllowConnectionTakeover { get; set; }

    /// <summary>
    /// 键盘回传（Miracast UIBC）：把本机键盘的按键转发给正在投屏的手机。
    /// 方向是「电脑 → 手机」—— 官方 API 语义为「接收端把输入发给发送端」，不存在「手机反向控制本机」这回事。
    /// 能否真正生效取决于手机是否主动请求键盘输入（多数安卓机不支持），本机无法单方面决定；默认关闭。
    /// </summary>
    public bool KeyboardBackchannel { get; set; }

    /// <summary>配对方式：Confirm（本机确认）/ Pin（要求手机输入本机显示的 PIN）。</summary>
    public string PairingMode { get; set; } = "Confirm";

    /// <summary>启动/退出时清空系统记录的「已知无线显示设备」历史（隐私优先，代价是每次都要重新配对）。</summary>
    public bool ForgetDeviceHistory { get; set; }

    /// <summary>反截屏保护：其他程序截取/录制本程序窗口时显示为黑屏。</summary>
    public bool AntiCapture { get; set; } = true;

    /// <summary>反截屏与「本程序自己录制」冲突时的降级策略：PauseWhileRecording / BlockRecording。</summary>
    public string AntiCaptureFallback { get; set; } = "PauseWhileRecording";

    /// <summary>录像帧率（5 / 10 / 15 / 20）。帧率越高体积越大。</summary>
    public string RecordFps { get; set; } = "10";

    /// <summary>录像单帧 JPEG 质量（30-100）。</summary>
    public int RecordJpegQuality { get; set; } = 80;

    /// <summary>单条录像时长上限（分钟），到点自动停止并保存。</summary>
    public int RecordMaxMinutes { get; set; } = 30;

    /// <summary>每个账号的加密库软配额（MB），超出时提示并可按策略清理。</summary>
    public long VaultQuotaMB { get; set; } = 2048;

    /// <summary>保留策略：Off / Days30 / Days90。</summary>
    public string VaultRetention { get; set; } = "Off";

    /// <summary>库窗口空闲多少分钟后自动上锁（0 = 不自动上锁）。</summary>
    public int LibraryIdleLockMinutes { get; set; } = 5;

    /// <summary>是否已经询问过「导入旧版本明文文件」。</summary>
    public bool LegacyMigrationPrompted { get; set; }

    // ---------------- 窗口记忆与接收端刷新 ----------------

    /// <summary>上次退出时的窗口位置与大小（null = 未记录，首次启动居中显示）。</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>上次全屏所在显示器（"x,y,w,h"；多显示器时下次全屏回到同一块屏）。</summary>
    public string FullscreenMonitor { get; set; } = "";

    /// <summary>
    /// 空闲（没有设备连接）时每隔多少分钟静默重建一次接收广播，0 = 不自动刷新。
    /// 「手机搜不到、重启程序就好了」本质上就是把广播重建一次 —— 这里让它自动做。
    /// </summary>
    public int IdleBroadcastRefreshMinutes { get; set; } = 10;

    /// <summary>锁屏 / 切换用户 / 断开远程会话时：自动锁定加密库并遮挡画面。</summary>
    public bool LockOnSessionLock { get; set; } = true;

    /// <summary>更新源地址（留空则不检查更新）。</summary>
    public string UpdateUrl { get; set; } = "";

    /// <summary>是否已经看过首次使用引导动画。</summary>
    public bool HasSeenTutorial { get; set; }

    // ---------------- 启动入场动画 ----------------

    /// <summary>启动时放一段入场动画（可以在设置里关掉；急着用时点一下或按任意键也能跳过）。</summary>
    public bool ShowIntro { get; set; } = true;

    /// <summary>
    /// 用哪个启动视频：内置片段的 key（如 <c>fleet</c>），或 <c>file:完整路径</c> 表示用户导入的文件。
    /// 留空＝默认内置。导入的文件**直接播放、不转码**。
    /// </summary>
    public string IntroSource { get; set; } = "";

    /// <summary>从第几秒开始播（0＝从头）。只裁时长，不动画质。</summary>
    public double IntroStart { get; set; }

    /// <summary>播到第几秒结束（0＝到片尾）。</summary>
    public double IntroEnd { get; set; }

    /// <summary>
    /// 额外延长多少秒：正片放完后**停在最后一帧**再展示这么久（0＝不延长）。
    /// 用户要的是"可以设定让它变长多少秒；超过默认时长之后，点鼠标就能跳过"。
    /// </summary>
    public double IntroExtend { get; set; }

    // ---------------- 启动动画：画面（都可编辑） ----------------

    /// <summary>入场画面宽度（像素；0＝跟随主窗口宽度）。</summary>
    public int IntroWidth { get; set; }

    /// <summary>入场画面高度（像素；0＝跟随主窗口高度）。</summary>
    public int IntroHeight { get; set; }

    /// <summary>填充方式：fill＝铺满并裁掉多余（默认）/ fit＝完整显示（可能有黑边）/ stretch＝拉伸填满。</summary>
    public string IntroStretch { get; set; } = "fill";

    /// <summary>裁切/对齐位置：水平 0＝左 0.5＝中 1＝右。</summary>
    public double IntroAlignH { get; set; } = 0.5;

    /// <summary>裁切/对齐位置：垂直 0＝上 0.5＝中 1＝下。</summary>
    public double IntroAlignV { get; set; } = 0.5;

    /// <summary>入场动画音量（0～1，默认 0.85）。</summary>
    public double IntroVolume { get; set; } = 0.85;

    /// <summary>进场淡入时长（毫秒，默认 760）。</summary>
    public int IntroFadeInMs { get; set; } = 760;

    /// <summary>出场时长（毫秒，默认 880）。</summary>
    public int IntroFadeOutMs { get; set; } = 880;

    /// <summary>出场是否打一下白闪（转场感）。</summary>
    public bool IntroFlash { get; set; } = true;

    /// <summary>预览时是否像"重进一次"那样把界面藏起来（默认是）。</summary>
    public bool IntroPreviewReenter { get; set; } = true;

    // ---------------- 安卓 adb 鼠标控制（⑧） ----------------

    /// <summary>用鼠标控制手机（点击→点按、滚轮/拖动→滑动）。默认关闭。</summary>
    public bool AdbMouseControl { get; set; }

    /// <summary>手机无线调试的主机地址（上次填过的，下次自动带出来）。</summary>
    public string AdbHost { get; set; } = "";

    /// <summary>手机无线调试的**连接**端口（「IP 地址和端口」里的那个）。</summary>
    public string AdbConnectPort { get; set; } = "";

    /// <summary>手机无线调试的**配对**端口（「使用配对码配对设备」里的那个）。</summary>
    public string AdbPairPort { get; set; } = "";

    /// <summary>启动/投屏开始时若已经记过地址，就自动尝试连一次（连不上不影响投屏）。</summary>
    public bool AdbAutoConnect { get; set; } = true;

    /// <summary>
    /// 本机是否与手机**成功配对过**。
    /// 用途：没配对过时连接必然失败（手机拒绝未配对的调试连接），界面据此直接引导去配对，
    /// 而不是让用户对着"连接失败"反复重试。
    /// </summary>
    public bool AdbPairedOnce { get; set; }

    /// <summary>是否在画面右上角显示通讯状态浮层。</summary>
    public bool ShowStats { get; set; } = true;

    /// <summary>
    /// 只在卡顿时显示通讯状态浮层（默认开）：画面正常时完全不出现，不挡画面；
    /// 一旦出现滞后/停滞就浮出来，恢复正常后再多留几秒。
    /// </summary>
    public bool StatsOnlyWhenLagging { get; set; } = true;

    /// <summary>手机镜像是否“铺满画面”（true=裁切填满，false=完整显示并留黑边）。</summary>
    public bool MirrorFill { get; set; }

    /// <summary>延迟优先：向手机申请更低分辨率/码率，镜像更跟手。</summary>
    public bool LowLatency { get; set; } = true;

    /// <summary>镜像静音：省掉音频同步缓冲，可进一步降低延迟。</summary>
    public bool MuteAudio { get; set; }



    /// <summary>稳定不变的设备 UUID（DLNA UDN）。</summary>
    public string DeviceUuid { get; set; } = "";

    // ---------------- 持久化 ----------------

    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>
    /// 配置目录覆盖（--settings-root）：供自检/界面冒烟用独立配置，避免动到用户真实设置。
    /// </summary>
    public static string? RootOverride { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 必须允许 NaN/Infinity：小窗位置这类字段的"没设过"就是 double.NaN，
        // 而 System.Text.Json 默认遇到 NaN 会**抛异常** —— Save() 又把异常吞了，
        // 结果就是"所有设置都存不下来"（实测：settings.json 的修改时间一直停在旧日期）。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>最近一次保存失败的原因（空＝没失败）。给界面用，免得又静默失败。</summary>
    [JsonIgnore] public string LastSaveError { get; private set; } = "";

    public static CastSettings Load()
    {
        string dir = RootOverride
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        CastSettings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<CastSettings>(File.ReadAllText(path), JsonOpts) ?? new CastSettings()
                : new CastSettings();
        }
        catch { s = new CastSettings(); }

        if (string.IsNullOrWhiteSpace(s.DeviceUuid)) s.DeviceUuid = Guid.NewGuid().ToString("D");
        if (string.IsNullOrWhiteSpace(s.DeviceName)) s.DeviceName = "PC";
        if (s.HttpPort is < 1024 or > 65535) s.HttpPort = 2869;
        s.FilePath = path;
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FilePath)) return;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            LastSaveError = "";
        }
        catch (Exception ex)
        {
            // 以前这里是空的 catch —— 结果"设置存不下来"这种事完全没人知道。
            // 现在记下来，界面会显示，日志也会看到。
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            SaveFailed?.Invoke(LastSaveError);
        }
    }

    /// <summary>保存失败时通知一声（宿主会写进日志，设置窗口会显示出来）。</summary>
    public static event Action<string>? SaveFailed;
}