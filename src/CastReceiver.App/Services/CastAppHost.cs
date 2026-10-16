using CastReceiver.Core.Audio;
using CastReceiver.Core.Infrastructure;
using CastReceiver.Core.Miracast;

namespace CastReceiver.App.Services;

/// <summary>
/// 应用运行期总控（纯镜像版）：只管配置、日志与 Miracast 接收端。
/// 说明：早期版本还内置了 DLNA（腾讯视频等 App 投屏）与 VLC 播放内核，
/// 现已按需求移除——本程序只做手机镜像接收，启动更快、包体更小、链路更短。
/// </summary>
public sealed class CastAppHost : IDisposable
{
    private readonly List<string> _recentNotices = new();

    public CastAppHost()
    {
        Settings = CastSettings.Load();
        Log = new AppLog { PrivacyMode = Settings.PrivacyLog };

        // 保存失败必须看得见：以前 Save() 里是空 catch，
        // 结果"设置改了但没存下来"（比如 NaN 序列化抛异常）完全没人知道，用户只会觉得开关自己又打开了。
        CastSettings.SaveFailed += message => Log.Error("settings", "设置保存失败（改动不会生效）：" + message);
        Miracast = new MiracastService(Log)
        {
            LowLatency = Settings.LowLatency,
            MuteAudio = Settings.MuteAudio,
            TransmitKeyboard = Settings.KeyboardBackchannel,
            PrivacyLog = Settings.PrivacyLog,
            AllowConnectionTakeover = Settings.AllowConnectionTakeover,
            PairingMode = Settings.PairingMode,
            ForgetDeviceHistory = Settings.ForgetDeviceHistory,
            IdleBroadcastRefreshMinutes = Settings.IdleBroadcastRefreshMinutes,
        };

        Miracast.Notice += Publish;
        AirPlay = new AirPlayService(Log, Publish);
        Audio = new AudioOutputSwitcher(Log);
    }

    public CastSettings Settings { get; }
    public AppLog Log { get; }
    public MiracastService Miracast { get; }

    /// <summary>AirPlay 接收端（iPhone/iPad 投屏）。与 Miracast 各自独立、互不影响。</summary>
    public AirPlayService AirPlay { get; }

    /// <summary>投屏期间自动切换音频输出（⑥）。</summary>
    public AudioOutputSwitcher Audio { get; }

    public LanInterface? Lan { get; private set; }
    public string NetworkCategory { get; private set; } = "Unknown";
    public bool FirewallRulePresent { get; private set; }
    public string? HotspotConflict { get; private set; }

    public event Action<string>? Notice;
    public event Action? StateChanged;

    public IReadOnlyList<string> RecentNotices => _recentNotices;

    public void Publish(string message)
    {
        _recentNotices.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (_recentNotices.Count > 200) _recentNotices.RemoveAt(_recentNotices.Count - 1);
        // 会话事件走 Event 通道：隐私模式下只进界面，不落盘（投屏起止时间不留在磁盘上）
        Log.Event("app", message);
        Notice?.Invoke(message);
        StateChanged?.Invoke();
    }

    public string PrimaryAddress => Lan?.Address.ToString() ?? "未连接";

    public void RefreshNetworkInfo()
    {
        var lan = NetInfo.GetLanInterface();
        string category = lan is null ? "Unknown" : NetInfo.GetNetworkCategory(lan.Name);
        bool firewall = NetInfo.HasFirewallRuleFor(NetworkFixer.RulePrefix);
        ApplyNetworkSnapshot(lan, category, firewall);
        ApplyHotspotConflict(NetInfo.DetectHotspotConflict());
    }

    /// <summary>把后台探测到的网络状态写入宿主（避免在 UI 线程上跑 netsh/PowerShell）。</summary>
    public void ApplyNetworkSnapshot(LanInterface? lan, string category, bool firewallRulePresent)
    {
        Lan = lan;
        NetworkCategory = category;
        FirewallRulePresent = firewallRulePresent;
    }

    public void ApplyHotspotConflict(string? conflict) => HotspotConflict = conflict;

    public async Task<MiracastStartResult> StartMiracastAsync()
    {
        Miracast.LowLatency = Settings.LowLatency;
        Miracast.MuteAudio = Settings.MuteAudio;
        Miracast.TransmitKeyboard = Settings.KeyboardBackchannel;
        Miracast.QualityPreset = Settings.QualityPreset;
        Miracast.PrivacyLog = Settings.PrivacyLog;
        Miracast.AllowConnectionTakeover = Settings.AllowConnectionTakeover;
        Miracast.PairingMode = Settings.PairingMode;
        Miracast.ForgetDeviceHistory = Settings.ForgetDeviceHistory;
        Miracast.IdleBroadcastRefreshMinutes = Settings.IdleBroadcastRefreshMinutes;
        Log.PrivacyMode = Settings.PrivacyLog;
        // 设备名以**当前设置**为准：接收端内部的空闲刷新/看门狗恢复都读它，
        // 否则改完名字之后这些自动重启会把名字改回旧的（见 MiracastService.DesiredDeviceName）
        Miracast.DesiredDeviceName = Settings.DeviceName;
        var result = await Miracast.StartAsync(Settings.DeviceName, Settings.MiracastConfirmConnection);
        Publish(result.Success ? result.Message : $"{result.Message} —— {result.Detail}");
        StateChanged?.Invoke();
        return result;
    }

    public async Task StopMiracastAsync()
    {
        await Miracast.StopAsync();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 设置里改完设备名后调用：让新名字**当场生效**（不需要重启程序）。
    ///
    /// 两条链路的名字来源不同，都要处理：
    /// <list type="bullet">
    /// <item>Miracast：由接收端重新广播（有设备在投时不打断，见 ApplyDeviceNameAsync）；</item>
    /// <item>AirPlay：名字进 mDNS 实例名，必须重新广播 —— 但没有会话时才敢重播，
    /// 否则会把正在投屏的 iPhone 踢下线。</item>
    /// </list>
    /// 返回一句可直接显示给用户的结果说明。
    /// </summary>
    public async Task<string> ApplyDeviceNameAsync()
    {
        string name = Settings.DeviceName;
        Miracast.DesiredDeviceName = name;
        DeviceNameApplyResult result = await Miracast.ApplyDeviceNameAsync(name).ConfigureAwait(true);
        string message = result.Message;

        if (AirPlay.IsRunning)
        {
            if (AirPlay.SessionCount == 0)
            {
                StopAirPlay();
                bool ok = StartAirPlay();
                message = ok ? $"{message}；AirPlay 已按“{name}”重新广播" : $"{message}；AirPlay 重新广播失败";
            }
            else
            {
                message = $"{message}；AirPlay 有设备正在投屏，断开后生效";
            }
        }

        StateChanged?.Invoke();
        return message;
    }

    /// <summary>启动 AirPlay 接收端（同步：只是绑端口 + 广播，很快）。返回是否已运行。</summary>
    public bool StartAirPlay()
    {
        if (!Settings.AutoStartAirPlay)
        {
            return false;
        }

        bool ok = AirPlay.Start(Settings);
        StateChanged?.Invoke();
        return ok;
    }

    public void StopAirPlay()
    {
        AirPlay.Stop();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try { AirPlay.Dispose(); } catch { }
        try { Miracast.Dispose(); } catch { }
        try { Log.Dispose(); } catch { }
    }
}