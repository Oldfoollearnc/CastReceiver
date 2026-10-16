namespace CastReceiver.Core.AirPlay.Mirror;

/// <summary>对一帧的处置结果。</summary>
public enum FrameDisposition
{
    /// <summary>收下（队列还有余量）。</summary>
    Accept,

    /// <summary>清空积压后收下：这一帧本身就是关键帧，可以立刻起解。</summary>
    AcceptAfterResync,

    /// <summary>丢弃：正在等关键帧，这一帧不是关键帧。</summary>
    DropWaitingKey,

    /// <summary>丢弃：等关键帧等太久（超时兜底），随后由调用方收下下一帧。</summary>
    DropTimeoutProbe,

    /// <summary>丢弃：积压超容量且这一帧不是关键帧 —— 转去等下一个关键帧。</summary>
    DropOverflow,
}

/// <summary>
/// 直播镜像的丢帧策略（纯逻辑，可自检）。
///
/// 为什么需要它：解码跟不上时，队列会积压。早先的做法是"丢最旧的一帧"——
/// 但 H.264 的 P 帧引用前面的帧，丢掉中间某一帧之后，解码器拿到的就是
/// "引用了不存在帧"的数据，参考链一断就是**花屏/卡顿，一直到下一个关键帧才好**。
/// 现象正是用户报的"苹果投屏卡顿"。
///
/// 现在的策略：积压到上限时**不再零散丢帧**，而是清空积压、等下一个 IDR 关键帧
/// 再重新起解（IDR 不引用任何帧，起解一定干净）。为了不让画面长时间定格，
/// 等待超过 <see cref="MaxWaitForKeyFrame"/> 就放弃等待、收下当前帧（宁可短暂瑕疵，
/// 也不要一直冻着），并如实计数。
///
/// 时间戳由调用方按自己的节奏继续推：丢帧只是**跳过了这段时间的内容**，
/// 不会把延迟越积越多（直播要的就是"跟得上"，不是"一帧不落"）。
/// </summary>
public sealed class MirrorFrameGate
{
    private readonly int _capacity;
    private DateTime? _waitingSinceUtc;

    /// <param name="capacity">允许积压的帧数上限（超过就进入重同步）。</param>
    /// <param name="maxWaitForKeyFrameSeconds">等关键帧的兜底上限（秒）。</param>
    public MirrorFrameGate(int capacity = 8, double maxWaitForKeyFrameSeconds = 2.0)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maxWaitForKeyFrameSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWaitForKeyFrameSeconds));
        }

        _capacity = capacity;
        MaxWaitForKeyFrame = TimeSpan.FromSeconds(maxWaitForKeyFrameSeconds);
    }

    /// <summary>积压上限。</summary>
    public int Capacity => _capacity;

    /// <summary>等关键帧的兜底上限。</summary>
    public TimeSpan MaxWaitForKeyFrame { get; }

    /// <summary>当前是否处于"等关键帧"状态（界面状态文字用）。</summary>
    public bool WaitingForKeyFrame => _waitingSinceUtc is not null;

    /// <summary>已经等了多久（没在等则为 <see cref="TimeSpan.Zero"/>）。</summary>
    public TimeSpan Waited(DateTime nowUtc)
        => _waitingSinceUtc is null ? TimeSpan.Zero : nowUtc - _waitingSinceUtc.Value;

    /// <summary>重新开始（例如整路会话重来）。</summary>
    public void Reset() => _waitingSinceUtc = null;

    /// <summary>
    /// 决定这一帧怎么处置。
    /// </summary>
    /// <param name="annexB">已经转成 Annex-B 的一帧（含 4 字节起始码）。</param>
    /// <param name="queuedCount">调用方当前积压的帧数。</param>
    /// <param name="nowUtc">当前时间（传入是为了让自检可以确定性验证超时）。</param>
    /// <param name="clearBacklog">返回 true 时调用方必须**先清空自己的积压**。</param>
    public FrameDisposition Offer(ReadOnlySpan<byte> annexB, int queuedCount, DateTime nowUtc, out bool clearBacklog)
    {
        bool keyFrame = MirrorStreamReceiver.IsKeyFrame(annexB);

        // ① 正在等关键帧
        if (_waitingSinceUtc is not null)
        {
            if (keyFrame)
            {
                _waitingSinceUtc = null;
                clearBacklog = true;
                return FrameDisposition.AcceptAfterResync;
            }

            if (nowUtc - _waitingSinceUtc.Value >= MaxWaitForKeyFrame)
            {
                // ② 等太久了：放弃等待。这一帧仍丢掉，让调用方收下**下一帧**，
                //    这样"决定"与"收下"分开，调用方不必在超时这一帧上做特殊处理。
                _waitingSinceUtc = null;
                clearBacklog = true;
                return FrameDisposition.DropTimeoutProbe;
            }

            clearBacklog = false;
            return FrameDisposition.DropWaitingKey;
        }

        // ③ 积压超上限
        if (queuedCount >= _capacity)
        {
            if (keyFrame)
            {
                // 关键帧自己就能起解：丢掉旧积压、直接收下它
                clearBacklog = true;
                return FrameDisposition.AcceptAfterResync;
            }

            _waitingSinceUtc = nowUtc;
            clearBacklog = true;
            return FrameDisposition.DropOverflow;
        }

        clearBacklog = false;
        return FrameDisposition.Accept;
    }
}
