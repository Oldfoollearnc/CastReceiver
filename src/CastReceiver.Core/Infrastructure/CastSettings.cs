using System.Text.Json;
using System.Text.Json.Serialization;

namespace CastReceiver.Core.Infrastructure;

/// <summary>应用配置：设备名、端口、播放器与 Miracast 选项。持久化到 %LocalAppData%\CastReceiver\settings.json。</summary>
public sealed class CastSettings
{
    /// <summary>
    /// 手机端投屏列表里显示的名字。
    /// 默认＝**当前 Windows 账户名**（按用户 2026-10-11 的要求改；以前固定叫 "PC"）：
    /// 一个网段里可能有好几台电脑，手机列表里显示账户名才认得出该选哪一台。
    /// </summary>
    public string DeviceName { get; set; } = DefaultDeviceName;

    /// <summary>
    /// 默认设备名：当前 Windows 账户名（取不到就退回程序名，绝不显示空名字 ——
    /// 空名字会让手机端列表里出现一行没有名字的条目）。
    /// </summary>
    public static string DefaultDeviceName => NormalizeAccountName(Environment.UserName);

    /// <summary>账户名的兜底规则（空/空白 → 程序名）。抽成纯函数是为了能自检。</summary>
    public static string NormalizeAccountName(string? accountName)
        => string.IsNullOrWhiteSpace(accountName) ? "投屏Window" : accountName.Trim();

    /// <summary>
    /// 这个名字是不是"用户从没改过"（空，或者还是旧版本的默认值 "PC"）。
    /// 老版本存过 "PC" 的用户升级后要自动换成账户名；**用户自己填过的名字一律不动**。
    /// </summary>
    public static bool IsDefaultDeviceName(string? name)
        => string.IsNullOrWhiteSpace(name) || name.Trim().Equals("PC", StringComparison.OrdinalIgnoreCase);

    /// <summary>把配置里存下来的设备名解析成"这次真正要用的名字"。</summary>
    public static string ResolveDeviceName(string? stored)
        => IsDefaultDeviceName(stored) ? DefaultDeviceName : stored!.Trim();

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

    /// <summary>
    /// 默认高度 = 画面 218（384 宽下接近 16:9，一开就不留黑边）+ 控制条常驻占位 34。
    /// 早先这里是 218 —— 那是"控制条还没常驻"时的画面高度；现在控制条恒占 34px，
    /// 默认值必须跟着变成 252，否则新建的小窗会比设计矮 34px。
    /// </summary>
    public double MiniHeight { get; set; } = 252;

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

    /// <summary>
    /// 自动适配（默认开）：按"不裁切的前提下铺到最大"计算，窗口/全屏尺寸变化时自动跟随。
    ///
    /// 自动模式下界面只留一枚「自动」小标，适应/缩放那组按钮整体收起；
    /// 点它（或设置里选任一手动方式、拖缩放）即进入手动，按钮重新出现。
    /// 全屏时同样生效 —— 以前全屏只会在"用户从没选过"时偷偷切成铺满（裁掉画面边缘），
    /// 与"无裁切"正好相反。
    /// </summary>
    public bool MirrorFitAuto { get; set; } = true;

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

    /// <summary>
    /// AirPlay 自动降档记忆：上一段投屏持续卡顿时置位，下一次连接请求 720p30；
    /// 连续两段健康会话后自动清除。**内部状态，不在界面上暴露**（用户看到的是自动效果）。
    /// </summary>
    public bool AirPlaySmoothFallback { get; set; }

    /// <summary>强调色：Mint / Amber / Violet / Rose / Blue / System。</summary>
    public string Accent { get; set; } = "Mint";

    /// <summary>界面密度：Comfortable / Compact。</summary>
    public string Density { get; set; } = "Comfortable";

    /// <summary>
    /// 右侧信息栏是否收起（像视频客户端那样只留一条窄把手，画面铺满整个宽度）。
    /// 记进设置是为了"收起后下次打开还是收起的" —— 极简是这位用户的长期偏好。
    /// </summary>
    public bool SideCollapsed { get; set; }

    /// <summary>
    /// 界面动效（窗口入场、悬停、待机页 3D 转台）。默认开。
    /// 刻意不受 Windows 全局「动画效果」影响：本机系统开关是关的，而用户要的是看得见的动效。
    /// 需要静态界面时在 设置 → 通用 里关掉即可。
    /// </summary>
    public bool UiMotion { get; set; } = true;

    /// <summary>
    /// 悬浮控制条（画面下方那条截屏/录制/小窗/音量）在多少秒无操作后自动消失。
    /// 0 = 常驻不消失。默认 3 秒：条压在画面上，一直挂着会挡内容；
    /// 鼠标移到画面上或条上会立刻重新出现。
    /// </summary>
    public int BarAutoHideSeconds { get; set; } = 3;

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
    /// 用哪个启动动画：<c>brand</c>（程序自绘的品牌动画，默认）/ 内置视频片段的 key
    /// （如 <c>builtin</c>），或 <c>file:完整路径</c> 表示用户导入的文件。
    /// </summary>
    public string IntroSource { get; set; } = IntroClips.Brand;

    /// <summary>
    /// 迁移标记：老版本默认值是内置视频片段（一段与产品无关的片子），观感差。
    /// 升级时把"没选过 / 还是默认值"的用户切到品牌动画；用户后来手动选了视频就不再动。
    /// </summary>
    public bool IntroBrandMigrated { get; set; }

    /// <summary>从第几秒开始播（0＝从头）。只裁时长，不动画质。</summary>
    /// <summary>内置开场的起播点（秒）。1.55 对应源片段 47.55s —— 裁剪时开头多留了 1.55s。</summary>
    public double IntroStart { get; set; } = 1.55;

    /// <summary>播到第几秒结束（0＝到片尾）。</summary>
    /// <summary>内置开场的结束点（秒）。34.70 对应源片段 80.70s（源总长 80.71s，所以正好到片尾）。</summary>
    public double IntroEnd { get; set; } = 34.70;

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

    // ---------------- 裁掉画面自带的黑边 ----------------
    //
    // 很多动画/电影压制把 2.4:1 的画面塞进 16:9 的框里，上下是**录在像素里**的黑边：
    // 这种情况"铺满"也甩不掉（铺满只管把整帧铺到窗口上）。下面的裁切比例由设置里的
    // 「裁掉自带黑边」开关 + 一次离线检测得到，播放时按它放大，把黑边移出可视区。

    /// <summary>是否裁掉画面自带的黑边（默认关）。</summary>
    public bool IntroCropBars { get; set; }

    /// <summary>上边黑边占整帧高度的比例（0～0.45）。</summary>
    public double IntroCropTop { get; set; }

    /// <summary>下边黑边占比。</summary>
    public double IntroCropBottom { get; set; }

    /// <summary>左边黑边占整帧宽度的比例。</summary>
    public double IntroCropLeft { get; set; }

    /// <summary>右边黑边占比。</summary>
    public double IntroCropRight { get; set; }

    /// <summary>真正生效的裁切比例（开关关掉时全是 0）。</summary>
    public (double Top, double Bottom, double Left, double Right) IntroCropEffective =>
        IntroCropBars
            ? (Math.Clamp(IntroCropTop, 0, 0.45), Math.Clamp(IntroCropBottom, 0, 0.45),
               Math.Clamp(IntroCropLeft, 0, 0.45), Math.Clamp(IntroCropRight, 0, 0.45))
            : (0, 0, 0, 0);

    // ---------------- 电脑控制手机（安卓 · adb 无线） ----------------

    // ---------------- 电脑控制手机（安卓 · adb 无线） ----------------

    /// <summary>
    /// 「电脑控制手机」**总开关**（设置 → 安全）。**默认关闭**。
    ///
    /// 按用户 2026-10-16 的要求，这个功能默认下线：关着的时候界面整块不出现、
    /// 程序完全不碰 adb（不起进程、不做发现、不轮询）。用户想用自己在设置里打开，
    /// 不需要重新编译；产品层面是否保留该功能另见 <see cref="Features.AndroidRemoteControl"/>。
    /// </summary>
    public bool AdbControlEnabled { get; set; }

    /// <summary>
    /// 用鼠标/键盘控制手机（点击＝点按、滚轮＝滑动、系统键、文字）。默认关闭，但**投屏时会自动打开**
    /// （见 <see cref="AdbAutoConnect"/>）：用户要的是"手机投上来就能用，电脑端不用点任何东西"。
    /// 仅在 <see cref="AdbControlEnabled"/> 打开后才有意义。
    /// </summary>
    public bool AdbMouseControl { get; set; }

    /// <summary>手机无线调试的主机地址（上次填过的，下次自动带出来）。</summary>
    public string AdbHost { get; set; } = "";

    /// <summary>手机无线调试的**连接**端口（「IP 地址和端口」里的那个）。</summary>
    public string AdbConnectPort { get; set; } = "";

    /// <summary>手机无线调试的**配对**端口（「使用配对码配对设备」里的那个）。</summary>
    public string AdbPairPort { get; set; } = "";

    /// <summary>
    /// **投屏时自动连接手机控制**（设置 → 安全里可关）。默认开。
    /// 打开时：安卓手机投上来的那一刻，自动发现并连上它、并自动打开「电脑控制手机」，
    /// 手机端只需要投屏、电脑端不需要任何操作。连不上不影响投屏本身。
    /// </summary>
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

    // 启动阶段 Load() 会被调用好几次（入场动画取窗口矩形 → 闪屏几何 → 宿主），
    // 每次都"读盘 + 反序列化 + 回写"是白付的三份 I/O。文件没被外部改过时直接复用同一份实例。
    private static CastSettings? _cached;
    private static string _cachedPath = "";
    private static DateTime _cachedWriteUtc;

    public static CastSettings Load()
    {
        string dir = RootOverride
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");

        // 命中缓存：同一次启动里的后续调用（以及 Save 之后的读）都不再碰磁盘
        if (_cached is not null && string.Equals(_cachedPath, path, StringComparison.Ordinal))
        {
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) == _cachedWriteUtc) return _cached;
            }
            catch { }
        }

        CastSettings s;
        bool dirty;
        try
        {
            dirty = !File.Exists(path);
            s = dirty
                ? new CastSettings()
                : JsonSerializer.Deserialize<CastSettings>(File.ReadAllText(path), JsonOpts) ?? new CastSettings();
        }
        catch { s = new CastSettings(); dirty = true; }

        if (string.IsNullOrWhiteSpace(s.DeviceUuid)) { s.DeviceUuid = Guid.NewGuid().ToString("D"); dirty = true; }
        // 设备名：没填过、或还是旧版本的 "PC"，都换成当前 Windows 账户名；用户改过的名字保留
        string deviceName = ResolveDeviceName(s.DeviceName);
        if (!string.Equals(deviceName, s.DeviceName, StringComparison.Ordinal)) { s.DeviceName = deviceName; dirty = true; }
        if (s.HttpPort is < 1024 or > 65535) { s.HttpPort = 2869; dirty = true; }

        // 开屏动画：老默认是"内置视频片段"（那段与产品无关的片子）。升级时把默认值换掉，
        // 用户明确选过的视频/自定义文件保持不动。
        if (!s.IntroBrandMigrated)
        {
            if (string.IsNullOrWhiteSpace(s.IntroSource)
                || string.Equals(s.IntroSource, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                s.IntroSource = IntroClips.Brand;
            }
            s.IntroBrandMigrated = true;
            dirty = true;
        }

        // 清理一次开发期的试验强调色（极光蓝紫）：产品方向已回到原来的薄荷绿极简风。
        // 之后用户自己在设置里选的强调色不会再被改动。
        if (s.Accent.Equals("Aurora", StringComparison.OrdinalIgnoreCase))
        {
            s.Accent = "Mint";
            dirty = true;
        }

        s.FilePath = path;
        if (dirty) s.Save();
        _cached = s;
        _cachedPath = path;
        try { _cachedWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { _cachedWriteUtc = DateTime.MinValue; }
        return s;
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FilePath)) return;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            LastSaveError = "";
            // 自己写下去的当然是最新的：把缓存时间戳对齐，避免下一次 Load() 又白读一遍磁盘
            if (ReferenceEquals(_cached, this))
            {
                _cachedPath = FilePath;
                try { _cachedWriteUtc = File.GetLastWriteTimeUtc(FilePath); } catch { }
            }
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