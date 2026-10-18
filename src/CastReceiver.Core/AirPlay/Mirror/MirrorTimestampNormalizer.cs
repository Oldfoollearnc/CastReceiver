namespace CastReceiver.Core.AirPlay.Mirror;

/// <summary>
/// 把 iPhone 的 Q32.32 演示时间戳规整成"从 0 开始、单调递增、间隔合理"的媒体时间轴。
///
/// 为什么需要它：手机上来的时间戳偶尔会**倒退**（重连、切后台）或**大跳**（暂停后恢复）。
/// 直接喂给 MediaStreamSource 会出两种事故：
/// <list type="bullet">
/// <item>倒退 / 重复：解码器时间轴乱掉，画面卡住或直接丢帧；</item>
/// <item>向前大跳：播放器认为"下一帧在 10 秒后"，画面就那么冻着。</item>
/// </list>
///
/// 这里只做三件事：
/// <list type="number">
/// <item>第一帧为 0 点；</item>
/// <item>时间戳不许倒退（重复/倒退 → 上一帧 + 1ms，并标记不连续）；</item>
/// <item>单帧间隔超过 <see cref="MaxGap"/> → 重新锚定到"上一帧 + 一个标称帧长"。</item>
/// </list>
/// 正常范围内完全不动原始节奏 —— 手机给的时间戳本来就是平滑的。
///
/// **为什么"向前跳"不再标 Discontinuous**：画面静止时手机本来就会隔几百毫秒到几秒才发一帧，
/// 这属于正常停顿。早先每遇到超过 500ms 的间隔就把它标成不连续，等于让解码器
/// 每隔几秒重置一次参考链 —— 用户看到的正是"偶发卡顿 + 短暂花屏"。
/// 现在只有**时间戳倒退**才标不连续；向前的时间跳变按标称帧长压缩即可，
/// 媒体时间轴本身是连续的，解码器不需要重建。
/// </summary>
public sealed class MirrorTimestampNormalizer
{
    /// <summary>
    /// 允许的最大单帧间隔（超过就按"跳变"把时间轴压缩，避免播放器以为要等很久）。
    /// 放得比"静止画面的正常停顿"宽：静态页面停 1 秒很常见，那不是故障。
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1.2);

    /// <summary>时间戳倒退/重复时强制前进的最小步长。</summary>
    public static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(1);

    private readonly TimeSpan _nominalStep;

    private bool _hasBase;
    private TimeSpan _base;
    private TimeSpan _last;
    private TimeSpan _lastDuration;

    /// <param name="fps">标称帧率（决定跳变后补的帧长；30fps → 33.3ms）。</param>
    public MirrorTimestampNormalizer(int fps = 30)
    {
        _nominalStep = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 240));
        _lastDuration = _nominalStep;
    }

    /// <summary>标称帧长。</summary>
    public TimeSpan NominalStep => _nominalStep;

    /// <summary>上一次映射得到的展示时间。</summary>
    public TimeSpan Last => _last;

    /// <summary>上一次映射算出的帧时长（已夹紧到合理区间）。</summary>
    public TimeSpan LastDuration => _lastDuration;

    /// <summary>
    /// 最近一次映射是否发生了"跳变后重新锚定"（调用方应把这一帧标成 Discontinuous）。
    /// </summary>
    public bool LastWasDiscontinuity { get; private set; }

    /// <summary>清空基准（会话重来 / 画面被遮挡后恢复）。</summary>
    public void Reset()
    {
        _hasBase = false;
        _base = TimeSpan.Zero;
        _last = TimeSpan.Zero;
        _lastDuration = _nominalStep;
        LastWasDiscontinuity = false;
    }

    /// <summary>
    /// 把 Q32.32 的原始时间戳映射到展示时间轴。
    /// </summary>
    public TimeSpan Map(ulong remoteTimestamp)
    {
        // Q32.32 秒：高 32 位整数秒，低 32 位小数
        var absolute = TimeSpan.FromSeconds(remoteTimestamp / 4294967296.0);

        LastWasDiscontinuity = false;

        if (!_hasBase)
        {
            _hasBase = true;
            _base = absolute;
            _last = TimeSpan.Zero;
            _lastDuration = _nominalStep;
            return TimeSpan.Zero;
        }

        TimeSpan relative = absolute - _base;

        // ① 倒退 / 重复：强制前进 1ms（绝不能把非单调的 PTS 交给解码器）
        if (relative <= _last)
        {
            _lastDuration = MinStep;
            _last += MinStep;
            LastWasDiscontinuity = true;
            return _last;
        }

        TimeSpan delta = relative - _last;

        // ② 向前跳（静止画面停顿 / 手机暂停后恢复）：把基准往前挪，使这一帧落在
        //    "上一帧 + 标称帧长"的位置。媒体时间轴依旧连续，**不**标不连续 ——
        //    标了会让解码器白白重置一次参考链（表现为短暂花屏）。
        if (delta > MaxGap)
        {
            _base += delta - _nominalStep;
            relative = _last + _nominalStep;
            _lastDuration = _nominalStep;
            _last = relative;
            return _last;
        }

        // ③ 正常：保留手机给的真实间隔（夹到 1ms … 500ms）
        _lastDuration = delta < MinStep ? MinStep : delta;
        _last = relative;
        return _last;
    }
}
