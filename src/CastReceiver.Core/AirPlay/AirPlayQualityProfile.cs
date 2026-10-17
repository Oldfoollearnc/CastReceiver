using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.AirPlay;

/// <summary>
/// AirPlay 镜像的"请求画质"。注意这不是本地转码参数，而是**写进 <c>GET /info</c> 的
/// <c>displays[0].width/height/refreshRate/maxFPS</c>**，由 iPhone 决定按什么分辨率推流
/// （与 UxPlay 的 <c>-s wxh@r</c> 是同一套机制）。
///
/// 自动策略（用户 2026-10-17 选定）：先按"画质档位 + 当前链路 + 上次会话健康度"选一个，
/// 再在会话结束后用实测的丢帧/重同步/积压做迟滞调整 —— 只在**下一次连接**生效，
/// 绝不为了切画质把正在投屏的会话踢掉。
/// </summary>
public readonly record struct AirPlayQualityProfile(int Width, int Height, int MaxFPS, string Label, string Reason)
{
    /// <summary>侧栏里显示的简短规格，例如 <c>1280×720@30</c>。</summary>
    public string Spec => $"{Width}×{Height}@{MaxFPS}";

    /// <summary>带画质档位名字的完整标签，例如 <c>低延迟 1280×720@30</c>。</summary>
    public string DisplayText => $"{Label} {Spec}";

    /// <summary>侧栏统计块里的短标签，例如 <c>1080p30</c>（300px 侧栏放不下完整规格）。</summary>
    public string ShortText => $"{Height}p{MaxFPS}";

    /// <summary>
    /// 选一个本次连接的画质。
    /// </summary>
    /// <param name="preset">设置里的画质档位：LowLatency / Balanced / HighQuality。</param>
    /// <param name="link">当前 Wi-Fi 链路（读不到就传 null）。</param>
    /// <param name="preferSmooth">上次会话出现持续卡顿后置位：本次强制降到 720p。</param>
    public static AirPlayQualityProfile ForSettings(string? preset, NetInfo.WlanLink? link, bool preferSmooth)
    {
        bool is24G = link is { Is24G: true };
        bool bandKnown = link is { BandGhz: > 0 };
        int rx = link?.RxMbps ?? 0;
        // 读不到速率（不少网卡不报）时不当作"弱链路"，否则会把好网也一律降成 720p
        bool weakLink = is24G || (rx > 0 && rx < 150);
        bool strongLink = bandKnown && !weakLink && (rx == 0 || rx >= 433);

        string tier = (preset ?? "LowLatency").Trim().ToLowerInvariant();
        if (preferSmooth)
        {
            return new AirPlayQualityProfile(1280, 720, 30, "流畅", "上次会话出现持续卡顿，自动降档");
        }

        return tier switch
        {
            "highquality" when !weakLink => new AirPlayQualityProfile(
                1920, 1080, 30, "高画质",
                bandKnown ? "5GHz 链路正常，按 30fps 稳定推送" : "未读到链路速率，按 1080p30 保守估计"),

            "highquality" => new AirPlayQualityProfile(1280, 720, 30, "流畅", "2.4GHz / 弱链路，高画质请求自动降到 720p"),

            "balanced" when !weakLink => new AirPlayQualityProfile(
                1920, 1080, 30, "均衡", bandKnown ? "5GHz 链路正常" : "未读到链路速率，按 1080p30 保守估计"),

            "balanced" => new AirPlayQualityProfile(1280, 720, 30, "流畅", "2.4GHz / 弱链路"),

            _ => new AirPlayQualityProfile(1280, 720, 30, "低延迟", "低延迟档位：向手机请求 720p30"),
        };
    }

    /// <summary>
    /// 会话结束后的健康评估：决定下次要不要降档 / 能不能升回去。
    /// </summary>
    public static AirPlayQualityDecision Evaluate(AirPlaySessionHealth health, bool alreadySmooth)
    {
        if (health.IsBad)
        {
            return AirPlayQualityDecision.Downgrade;
        }

        // 连续两段健康会话才允许撤销降档，避免"好一点就升、升了又卡"的来回抖动。
        if (alreadySmooth && health.IsGood)
        {
            return AirPlayQualityDecision.Upgrade;
        }

        return AirPlayQualityDecision.Keep;
    }
}

/// <summary>一次 AirPlay 镜像会话的实测健康度（全部来自本地计数，不向手机要数据）。</summary>
public readonly record struct AirPlaySessionHealth(double Seconds, long Frames, long Dropped, long Resync, double AverageQueue)
{
    /// <summary>持续卡顿：丢帧超过 2%（且至少 4 帧）、重同步 ≥ 2 次，或平均积压 ≥ 6 帧。</summary>
    public bool IsBad =>
        Seconds >= 8
        && Frames > 0
        && (Dropped >= Math.Max(4, (long)(Frames * 0.02)) || Resync >= 2 || AverageQueue >= 6.0);

    /// <summary>足够长、帧率跟得上（≥20fps）、零丢弃零重同步。</summary>
    public bool IsGood =>
        Seconds >= 20
        && Frames >= Seconds * 20
        && Dropped == 0
        && Resync == 0
        && AverageQueue < 2.0;
}

/// <summary>自动画质的调整方向。</summary>
public enum AirPlayQualityDecision
{
    Keep,
    Downgrade,
    Upgrade,
}
