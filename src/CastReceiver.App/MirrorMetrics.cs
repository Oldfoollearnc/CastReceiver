namespace CastReceiver.App;

/// <summary>
/// 码率 / 延迟的滑动历史，供状态浮层的曲线使用。
///
/// 采两个序列：
/// <list type="bullet">
/// <item><b>码率</b>：AirPlay 侧用**实测吞吐**（收到的字节数换算），Miracast 侧用平台协商值 ——
/// 两者的含义不同，界面上要写清单位，不要混为一谈；</item>
/// <item><b>延迟</b>：直接取 <see cref="MirrorStats.LagSeconds"/>（累计滞后）与实时比。</item>
/// </list>
/// </summary>
public sealed class MirrorMetrics
{
    /// <summary>保留最近 60 个采样点（配合 1 秒采样 = 最近 1 分钟）。</summary>
    public const int Capacity = 60;

    private readonly List<double> _kbps = new();
    private readonly List<double> _lagMs = new();

    public IReadOnlyList<double> Kbps => _kbps;
    public IReadOnlyList<double> LagMs => _lagMs;

    public int Count => _kbps.Count;
    public double CurrentKbps => _kbps.Count > 0 ? _kbps[^1] : 0;
    public double CurrentLagMs => _lagMs.Count > 0 ? _lagMs[^1] : 0;
    public double PeakKbps => _kbps.Count > 0 ? _kbps.Max() : 0;

    public void Add(double kbps, double lagMs)
    {
        _kbps.Add(double.IsFinite(kbps) && kbps > 0 ? kbps : 0);
        _lagMs.Add(double.IsFinite(lagMs) && lagMs > 0 ? lagMs : 0);

        while (_kbps.Count > Capacity) _kbps.RemoveAt(0);
        while (_lagMs.Count > Capacity) _lagMs.RemoveAt(0);
    }

    public void Reset()
    {
        _kbps.Clear();
        _lagMs.Clear();
    }

    /// <summary>把字节增量换算成 kbps（按实际经过的秒数，不用名义间隔）。</summary>
    public static double ToKbps(long byteDelta, double elapsedSeconds)
        => elapsedSeconds <= 0.05 ? 0 : byteDelta * 8.0 / 1000.0 / elapsedSeconds;
}
