using CastReceiver.Core.AirPlay.Mirror;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Security.Cryptography;

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
/// 采样时间戳按"首个帧为零点"重排：手机发的是它自己的 Q32.32 秒时钟
/// （近似开机纳秒数，不是 Unix 纪元），直接当 TimeSpan 用会得到离谱的值。
/// </summary>
internal sealed class AirPlayVideoSource : IDisposable
{
    /// <summary>默认帧时长（真机时间戳算不出间隔时使用）。</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(1.0 / 60.0);

    private readonly object _gate = new();
    private readonly Queue<Sample> _queue = new();

    /// <summary>
    /// 丢帧策略（在 Core 里，纯逻辑、有自检）：积压超限时不再零散丢最旧的帧，
    /// 而是清空积压、等下一个 IDR 再起解 —— 否则参考链一断就是花屏/卡顿。
    /// </summary>
    private readonly MirrorFrameGate _frameGate;

    private MediaStreamSource? _streamSource;
    private MediaSource? _mediaSource;
    private MediaStreamSourceSampleRequest? _pendingRequest;
    private MediaStreamSourceSampleRequestDeferral? _pendingDeferral;

    private TimeSpan _baseTimestamp;
    private bool _hasBase;
    private TimeSpan _lastTimestamp;
    private bool _started;

    /// <summary>推送但尚未被解码器取走的帧的上限；超出时丢弃最旧的帧（直播以"跟上"为先）。</summary>
    public AirPlayVideoSource(int width = 1920, int height = 1080, int capacity = 8)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _frameGate = new MirrorFrameGate(capacity);

        var properties = VideoEncodingProperties.CreateH264();
        properties.Width = (uint)Math.Max(1, width);
        properties.Height = (uint)Math.Max(1, height);
        try
        {
            // FrameRate 在 CsWinRT 投影里是只读属性，但它返回的 MediaRatio 可以就地改
            properties.FrameRate.Numerator = 60;
            properties.FrameRate.Denominator = 1;
        }
        catch
        {
            // 某些编码属性不允许设帧率，忽略即可
        }

        _streamSource = new MediaStreamSource(new VideoStreamDescriptor(properties))
        {
            CanSeek = false,       // 直播流不可 seek
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

    /// <summary>已推送的帧数。</summary>
    public long PushedCount { get; private set; }

    /// <summary>因为解码跟不上而丢弃的帧数（含"重同步时清空的积压"）。</summary>
    public long DroppedCount { get; private set; }

    /// <summary>重同步次数：每次"积压超限 → 等关键帧重新起解"算一次。</summary>
    public long ResyncCount { get; private set; }

    /// <summary>当前是否在等关键帧（等的时候画面会停一下，界面据此如实说明）。</summary>
    public bool WaitingForKeyFrame
    {
        get
        {
            lock (_gate)
            {
                return _frameGate.WaitingForKeyFrame;
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

    /// <summary>推入一帧（Annex-B 字节 + 手机给的原始时间戳）。</summary>
    public void Push(byte[] annexB, ulong remoteTimestamp)
    {
        if (annexB is null || annexB.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_streamSource is null)
            {
                return;
            }

            // 先问策略：这一帧收不收、要不要先清空积压。必须用**入队前**的积压数判断。
            FrameDisposition disposition = _frameGate.Offer(annexB, _queue.Count, DateTime.UtcNow, out bool clearBacklog);
            if (clearBacklog && _queue.Count > 0)
            {
                DroppedCount += _queue.Count;
                _queue.Clear();
            }

            if (disposition is FrameDisposition.DropOverflow
                             or FrameDisposition.DropWaitingKey
                             or FrameDisposition.DropTimeoutProbe)
            {
                DroppedCount++;
                // 关键：这里**不要**去兑现挂起的请求。我们正在等关键帧，
                // 把一个参考链残缺的帧交给解码器只会换来花屏/卡顿（就是用户报的那个现象）。
                return;
            }

            if (disposition == FrameDisposition.AcceptAfterResync)
            {
                ResyncCount++;
            }

            TimeSpan timestamp = Rebase(remoteTimestamp);
            TimeSpan duration = DefaultDuration;
            if (_hasBase && timestamp > _lastTimestamp)
            {
                TimeSpan delta = timestamp - _lastTimestamp;
                // 真机时间戳偶尔会跳变；只接受合理的间隔，避免解码器被喂进荒谬的时长
                if (delta >= TimeSpan.FromMilliseconds(1) && delta <= TimeSpan.FromMilliseconds(500))
                {
                    duration = delta;
                }
            }

            _queue.Enqueue(new Sample(annexB, timestamp, duration));
            _lastTimestamp = timestamp;
            PushedCount++;

            TryFulfillPendingRequest();
        }
    }

    /// <summary>把手机时钟重排成"第一帧 = 0"。</summary>
    private TimeSpan Rebase(ulong remoteTimestamp)
    {
        // AirPlay 的时间戳是 Q32.32 秒：高 32 位是秒，低 32 位是小数
        double seconds = remoteTimestamp / 4294967296.0;
        var absolute = TimeSpan.FromSeconds(seconds);
        if (!_hasBase)
        {
            _baseTimestamp = absolute;
            _hasBase = true;
            _lastTimestamp = TimeSpan.Zero;
            return TimeSpan.Zero;
        }
        TimeSpan relative = absolute - _baseTimestamp;
        return relative < TimeSpan.Zero ? TimeSpan.Zero : relative;
    }

    /// <summary>解码器来要数据；队列空时挂起一个 deferral，等下一帧到达再交付。</summary>
    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                args.Request.Sample = Dequeue();
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
            _pendingRequest.Sample = Dequeue();
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

    private MediaStreamSample Dequeue()
    {
        Sample sample = _queue.Dequeue();
        var buffer = CryptographicBuffer.CreateFromByteArray(sample.Data);
        var mediaSample = MediaStreamSample.CreateFromBuffer(buffer, sample.Timestamp);
        mediaSample.Duration = sample.Duration;
        return mediaSample;
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
            _queue.Clear();
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
            _queue.Clear();
            _hasBase = false;
            _frameGate.Reset();
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

            _queue.Clear();
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

    private readonly record struct Sample(byte[] Data, TimeSpan Timestamp, TimeSpan Duration);
}
