using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using CastReceiver.Core.AirPlay.Mirror;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace CastReceiver.App.Interop;

/// <summary>
/// 把 AirPlay 镜像帧（Annex-B 形式的 H.264 访问单元）喂给 Media Foundation 解码，
/// 得到一个可以交给 <see cref="CompositionVideoHost.AttachSource"/> 的 <see cref="MediaSource"/>。
///
/// 为什么用 <see cref="MediaStreamSource"/>：它是"推流"式的 MediaSource，
/// 由系统在需要解码时回调 <c>SampleRequested</c> 向我们要数据，正好匹配
/// "手机持续推、我们边收边喂"的镜像场景；而且它和现有渲染链路完全兼容——
/// 截屏/录制/反截屏/画质档位都建立在 MediaPlayer 之上，换掉 MediaSource 即可全部复用。
///
/// 2026-10 苹果投屏卡顿的三处关键修正：
/// <list type="number">
/// <item><b><c>BufferTime = 0</c></b>：MediaStreamSource 的默认缓冲是 **3 秒**，它会提前
///       向我们要 3 秒的样本再开始播。直播镜像要的是"现在"，3 秒预取既抬高延迟又让
///       队列一直处于"要么空、要么满"的状态，是卡顿/冻屏最可疑的来源。设为 0 后按需取帧。</item>
/// <item><b>样本缓冲池化 + 关键帧/不连续标记</b>：1080p 关键帧不再每帧分配托管大数组；
///       关键帧显式标记，重同步后的第一帧标 Discontinuous，解码器才知道该从哪重新起解。</item>
/// <item><b>时间戳防跳变</b>：手机时间戳倒退/大跳时重新锚定（见
///       <see cref="MirrorTimestampNormalizer"/>），不再把"10 秒后"的时间戳直接交给播放器。</item>
/// </list>
///
/// 采样时间戳按"首个帧为零点"重排：手机发的是它自己的 Q32.32 秒时钟
/// （近似开机纳秒数，不是 Unix 纪元），直接当 TimeSpan 用会得到离谱的值。
/// </summary>
internal sealed class AirPlayVideoSource : IDisposable
{
    /// <summary>解码器取走样本后归还托管缓冲（池化租约）。</summary>
    private sealed class SampleLease
    {
        private byte[]? _buffer;

        public SampleLease(int length)
        {
            int size = Math.Max(1, length);
            _buffer = ArrayPool<byte>.Shared.Rent(size);
            // AsBuffer 把托管数组包成 IBuffer（CsWinRT 的零拷贝封装）。
            // 数组在样本 Processed 之前不能被回收，所以这里用"租约 + Processed"管理生命周期。
            Buffer = _buffer.AsBuffer(0, size);
        }

        public IBuffer Buffer { get; }

        public Span<byte> Span
        {
            get
            {
                byte[]? buffer = _buffer;
                return buffer is null ? Span<byte>.Empty : buffer.AsSpan(0, (int)Buffer.Length);
            }
        }

        /// <summary>幂等归还（丢帧与 Processed 都可能走到这里）。</summary>
        public void Return()
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private readonly record struct QueuedSample(MediaStreamSample Sample, SampleLease Lease, TimeSpan Timestamp, bool KeyFrame);

    private readonly object _gate = new();
    private readonly Queue<QueuedSample> _queue = new();

    /// <summary>
    /// 丢帧策略（在 Core 里，纯逻辑、有自检）：积压超限时不再零散丢最旧的帧，
    /// 而是清空积压、等下一个 IDR 再起解 —— 否则参考链一断就是花屏/卡顿。
    /// </summary>
    private readonly MirrorFrameGate _frameGate;

    /// <summary>手机时间戳 → 单调展示时间轴。</summary>
    private readonly MirrorTimestampNormalizer _clock;

    private MediaStreamSource? _streamSource;
    private MediaSource? _mediaSource;
    private MediaStreamSourceSampleRequest? _pendingRequest;
    private MediaStreamSourceSampleRequestDeferral? _pendingDeferral;

    private bool _started;

    /// <summary>
    /// 解码器还没有任何参考帧：先把非关键帧丢掉，等第一个 IDR 再起解。
    /// 不做这一步的话，会话开始那一下会先解出 1–2 秒的花屏（P 帧引用了不存在的帧）。
    /// </summary>
    private bool _needFirstKeyFrame = true;

    /// <summary>第一帧到达的时间（等 IDR 的兜底用；一帧都没来时为 null）。</summary>
    private DateTime? _firstFrameUtc;

    /// <summary>等第一个 IDR 的上限：超过就收下当前帧，宁可短暂瑕疵也不要一直黑着。</summary>
    private static readonly TimeSpan FirstKeyFrameTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>推送但尚未被解码器取走的帧的上限；超出时清空积压并等关键帧（直播以"跟上"为先）。</summary>
    public AirPlayVideoSource(int width = 1920, int height = 1080, int capacity = 8, int fps = 30)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _frameGate = new MirrorFrameGate(capacity);
        _clock = new MirrorTimestampNormalizer(Math.Clamp(fps, 1, 240));

        VideoWidth = Math.Max(1, width);
        VideoHeight = Math.Max(1, height);
        VideoFPS = Math.Clamp(fps, 1, 240);

        var properties = VideoEncodingProperties.CreateH264();
        properties.Width = (uint)VideoWidth;
        properties.Height = (uint)VideoHeight;
        try
        {
            // FrameRate 在 CsWinRT 投影里是只读属性，但它返回的 MediaRatio 可以就地改。
            // 必须与向手机声明的 maxFPS 一致：声明 30 却按 60 喂，管线会等帧、看起来就是"一顿一顿"。
            properties.FrameRate.Numerator = (uint)VideoFPS;
            properties.FrameRate.Denominator = 1;
        }
        catch
        {
            // 某些编码属性不允许设帧率，忽略即可
        }

        _streamSource = new MediaStreamSource(new VideoStreamDescriptor(properties))
        {
            CanSeek = false,       // 直播流不可 seek
            // 关键：默认 3 秒！直播镜像必须按需取帧，否则延迟与卡顿都会被放大。
            BufferTime = TimeSpan.Zero,
        };
        _streamSource.SampleRequested += OnSampleRequested;
        _streamSource.Starting += OnStarting;
        _streamSource.Closed += OnClosed;

        // 注意：MediaStreamSource 并不是 MediaSource 的子类，必须走工厂方法
        _mediaSource = MediaSource.CreateFromMediaStreamSource(_streamSource);
    }

    /// <summary>交给 <see cref="CompositionVideoHost.AttachSource"/> 的媒体源。</summary>
    public MediaSource MediaSource => _mediaSource
        ?? throw new ObjectDisposedException(nameof(AirPlayVideoSource));

    /// <summary>本次会话向手机请求的分辨率与帧率（渲染表面初始尺寸也用它）。</summary>
    public int VideoWidth { get; }
    public int VideoHeight { get; }
    public int VideoFPS { get; }

    /// <summary>已推送的帧数。</summary>
    public long PushedCount { get; private set; }

    /// <summary>因为解码跟不上而丢弃的帧数（含"重同步时清空的积压"）。</summary>
    public long DroppedCount { get; private set; }

    /// <summary>重同步次数：每次"积压超限 → 清空积压 → 等关键帧重新起解"算一次。</summary>
    public long ResyncCount { get; private set; }

    /// <summary>当前是否在等关键帧（等的时候画面会停一下，界面据此如实说明）。</summary>
    public bool WaitingForKeyFrame
    {
        get
        {
            lock (_gate)
            {
                return (_needFirstKeyFrame && _firstFrameUtc is not null) || _frameGate.WaitingForKeyFrame;
            }
        }
    }

    /// <summary>解码器已经取走的帧数。</summary>
    public long DeliveredCount { get; private set; }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>状态变化（供界面显示）。</summary>
    public event Action<string>? Status;

    /// <summary>推入一帧（Annex-B 借用视图 + 手机给的原始时间戳）。</summary>
    public void Push(MirrorFrame frame)
    {
        if (frame.Length <= 0 || frame.Buffer is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_streamSource is null)
            {
                return;
            }

            // 起播必须从 IDR 开始：解码器拿到一个引用不存在参考帧的 P 帧，只会解出花屏。
            if (_needFirstKeyFrame)
            {
                _firstFrameUtc ??= DateTime.UtcNow;
                if (!frame.IsKeyFrame && DateTime.UtcNow - _firstFrameUtc.Value < FirstKeyFrameTimeout)
                {
                    DroppedCount++;
                    return;
                }

                _needFirstKeyFrame = false;
                _frameGate.Reset();   // 明确从"不在等关键帧"的状态开始
            }

            // 先问策略：这一帧收不收、要不要先清空积压。必须用**入队前**的积压数判断。
            FrameDisposition disposition = _frameGate.Offer(frame.IsKeyFrame, _queue.Count, DateTime.UtcNow, out bool clearBacklog);
            if (clearBacklog && _queue.Count > 0)
            {
                DroppedCount += _queue.Count;
                DrainQueueLocked();
            }

            if (disposition is FrameDisposition.DropOverflow
                             or FrameDisposition.DropWaitingKey
                             or FrameDisposition.DropTimeoutProbe)
            {
                DroppedCount++;
                // 关键：这里**不要**去兑现挂起的请求。我们正在等关键帧，
                // 把一个参考链残缺的帧交给解码器只会换来花屏/卡顿。
                return;
            }

            if (disposition == FrameDisposition.AcceptAfterResync)
            {
                ResyncCount++;
            }

            TimeSpan timestamp = _clock.Map(frame.Timestamp);
            TimeSpan duration = _clock.LastDuration;
            bool discontinuity = _clock.LastWasDiscontinuity || disposition == FrameDisposition.AcceptAfterResync;

            // 把帧复制进"租约"缓冲；IBuffer 零拷贝包住它，样本 Processed 后数组才回到池里。
            var lease = new SampleLease(frame.Length);
            try
            {
                frame.Span.CopyTo(lease.Span);

                MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(lease.Buffer, timestamp);
                sample.Duration = duration;
                sample.KeyFrame = frame.IsKeyFrame;
                sample.Discontinuous = discontinuity;
                sample.Processed += (_, _) => lease.Return();

                _queue.Enqueue(new QueuedSample(sample, lease, timestamp, frame.IsKeyFrame));
                PushedCount++;

                TryFulfillPendingRequest();
            }
            catch (Exception ex)
            {
                lease.Return();
                Status?.Invoke($"交付镜像帧失败：{ex.Message}");
            }
        }
    }

    /// <summary>解码器来要数据；队列空时挂起一个 deferral，等下一帧到达再交付。</summary>
    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                args.Request.Sample = DequeueLocked();
                DeliveredCount++;
                return;
            }

            // 队列空：不能直接返回空（会被当成流结束），要延迟到有数据再给
            if (_pendingRequest is not null)
            {
                // 上一次的请求还没兑现（正常不该发生），先把它兑现掉避免泄漏
                try
                {
                    _pendingDeferral?.Complete();
                }
                catch
                {
                    // 忽略
                }
            }

            _pendingRequest = args.Request;
            try
            {
                _pendingDeferral = args.Request.GetDeferral();
            }
            catch
            {
                // 某些情况下不允许取 deferral，那就只能等下一次请求
                _pendingRequest = null;
                _pendingDeferral = null;
            }
        }
    }

    private void TryFulfillPendingRequest()
    {
        if (_pendingRequest is null || _queue.Count == 0)
        {
            return;
        }

        try
        {
            _pendingRequest.Sample = DequeueLocked();
            DeliveredCount++;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"交付镜像帧失败：{ex.Message}");
        }
        finally
        {
            try
            {
                _pendingDeferral?.Complete();
            }
            catch
            {
                // 忽略
            }
            _pendingRequest = null;
            _pendingDeferral = null;
        }
    }

    /// <summary>取出一帧交给解码器（租约由样本的 Processed 归还，不在这里动）。</summary>
    private MediaStreamSample DequeueLocked() => _queue.Dequeue().Sample;

    /// <summary>清空积压并归还它们的缓冲租约（只处理"还没交给解码器"的样本）。</summary>
    private void DrainQueueLocked()
    {
        while (_queue.Count > 0)
        {
            _queue.Dequeue().Lease.Return();
        }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        // 直播流：立刻开始，不需要 seek
        try
        {
            args.Request.SetActualStartPosition(TimeSpan.Zero);
        }
        catch
        {
            // 忽略
        }

        if (!_started)
        {
            _started = true;
            Status?.Invoke("AirPlay 解码器已就绪，等待画面");
        }
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
    {
        lock (_gate)
        {
            DrainQueueLocked();
            try
            {
                _pendingDeferral?.Complete();
            }
            catch
            {
                // 忽略
            }
            _pendingRequest = null;
            _pendingDeferral = null;
        }
    }

    /// <summary>清空积压（例如画面被遮挡/切走时避免瞬间追帧）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            DrainQueueLocked();
            _clock.Reset();
            _frameGate.Reset();
            // 积压清空后解码器等于重新起解：下一个 IDR 之前的内容都别再喂进去
            _needFirstKeyFrame = true;
            _firstFrameUtc = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_streamSource is null)
            {
                return;
            }

            try
            {
                _streamSource.SampleRequested -= OnSampleRequested;
                _streamSource.Starting -= OnStarting;
                _streamSource.Closed -= OnClosed;
            }
            catch
            {
                // 忽略
            }

            DrainQueueLocked();
            try
            {
                _pendingDeferral?.Complete();
            }
            catch
            {
                // 忽略
            }
            _pendingRequest = null;
            _pendingDeferral = null;

            MediaStreamSource? streamSource = _streamSource;
            _streamSource = null;
            _mediaSource = null;

            // MediaStreamSource 在本投影里既不实现 IDisposable 也没有 Close（编译器可静态判定），
            // 所以这里只解除引用；MediaSource/MediaPlayer 释放时会连带释放它。
            _ = streamSource;
        }
    }
}
