using CastReceiver.Core.Infrastructure;
using LibVLCSharp.Shared;

namespace CastReceiver.Core.Media;

/// <summary>基于 LibVLC 的播放实现：支持 HLS/MP4/FLV/TS 直链与自定义 UA/Referer（规避防盗链）。</summary>
public sealed class VlcPlayerEngine : IMediaPlayerEngine, IPlaybackDiagnostics
{
    private readonly LibVLC _libVlc;
    private readonly AppLog _log;
    private readonly CastSettings _settings;
    private TrackInfo? _current;
    private string? _lastError;
    private TransportState _state = TransportState.NoMediaPresent;
    private int _volume;
    private bool _muted;
    private LibVLCSharp.Shared.Media? _currentMedia;
    private long _positionMs;
    private long _lengthMs;
    private bool _disposed;
    private Task _queueTail = Task.CompletedTask;
    private readonly object _queueGate = new();

    public VlcPlayerEngine(CastSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLCSharp.Shared.LibVLC(
            "--no-video-title-show",
            "--quiet",
            "--network-caching=1200",
            "--live-caching=800",
            "--file-caching=300",
            "--avcodec-hw=any",
            "--no-snapshot-preview",
            "--http-reconnect");
        Raw = new MediaPlayer(_libVlc);
        // 注意：只有在确实需要诊断时才挂接 VLC 日志回调。
        // 实测挂上日志回调后，VLC 会把每条日志回送托管代码，播放时钟会停止推进（进度永远停在 0.2s），
        // 因此默认不启用，仅在 --verbose 诊断模式下开启。
        if (EnableVlcLog)
        {
            _libVlc.Log += (_, e) =>
            {
                try { VlcMessage?.Invoke($"[{e.Level}] {e.Message}"); } catch { }
            };
        }
        _volume = Math.Clamp(settings.Volume, 0, 100);
        Raw.Volume = _volume;
        Raw.EnableHardwareDecoding = true;
        Raw.EndReached += (_, _) =>
        {
            _log.Info("player", "播放结束");
            SetState(TransportState.Stopped);
        };
        Raw.Playing += (_, _) => SetState(TransportState.Playing);
        Raw.Paused += (_, _) => SetState(TransportState.Paused);
        Raw.Stopped += (_, _) => SetState(_current is null ? TransportState.NoMediaPresent : TransportState.Stopped);
        Raw.EncounteredError += (_, _) =>
        {
            _lastError = "播放器遇到错误（URL 不可达 / 编码不支持 / 防盗链被拒）";
            _log.Error("player", _lastError);
            SetState(TransportState.Stopped);
        };
        Raw.Buffering += (_, _) =>
        {
            // 仅在播放/过渡阶段才反映缓冲，避免把用户主动暂停的状态覆盖掉
            if (_state is TransportState.Playing or TransportState.Transitioning)
                SetState(TransportState.Transitioning);
        };
        Raw.TimeChanged += (_, e) =>
        {
            // 以事件回调为准：实测某些情况下（无视频输出窗口/纯音频输出缺失时）
            // MediaPlayer.Time/Length 的直读值会长时间停留在 0，只有事件里带着真实进度。
            if (e.Time > 0) _positionMs = e.Time;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        };
        Raw.LengthChanged += (_, e) =>
        {
            if (e.Length > 0) _lengthMs = e.Length;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _log.Info("player", $"LibVLC 已就绪（版本 {_libVlc.Version}）");
    }

    public MediaPlayer Raw { get; }

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;

    /// <summary>LibVLC 自身的日志（诊断用；需先设置 EnableVlcLog=true）。</summary>
    public event Action<string>? VlcMessage;

    /// <summary>是否启用 VLC 日志回调。默认关闭：开启后播放时钟会异常（进度不推进），仅诊断时使用。</summary>
    public static bool EnableVlcLog { get; set; }

    /// <summary>诊断开关：为 true 时播放器操作在调用线程同步执行（默认走串行后台队列）。</summary>
    public static bool SynchronousControl { get; set; }

    public TransportState State => _state;

    public TimeSpan Duration
    {
        get
        {
            long ms = Raw.Length > 0 ? Raw.Length : _lengthMs;
            return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    public TimeSpan Position
    {
        get
        {
            long ms = Math.Max(Raw.Time, _positionMs);
            // 部分环境下（无视频输出窗口/纯解码）Time 事件不刷新，用 0..1 浮点进度换算兜底
            float p = Raw.Position;
            var duration = Duration;
            if (p > 0 && duration > TimeSpan.Zero)
                ms = Math.Max(ms, (long)(duration.TotalMilliseconds * p));
            return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    /// <summary>诊断用：原始播放器读数（native 与事件两份）。</summary>
    public string RawTelemetry =>
        $"nativeTime={Raw.Time} eventTime={_positionMs} floatPos={Raw.Position:F3} nativeLen={Raw.Length} eventLen={_lengthMs} vlcState={Raw.State}";

    // ---------------- 界面专用（绝不调用本地 VLC，UI 线程可安全高频读取） ----------------

    /// <summary>仅供界面显示：由事件驱动缓存的时长，不做任何本地调用。</summary>
    public TimeSpan DurationCached => _lengthMs > 0 ? TimeSpan.FromMilliseconds(_lengthMs) : TimeSpan.Zero;

    /// <summary>仅供界面显示：由事件驱动缓存的播放位置，不做任何本地调用。</summary>
    public TimeSpan PositionCached => _positionMs > 0 ? TimeSpan.FromMilliseconds(_positionMs) : TimeSpan.Zero;

    // 我们自己记录的音量是权威值：VLC 的直读值在输出未建立时是 -1，
// 且异步应用前会短暂返回旧值（会导致 DLNA GetVolume 回读到上一次的值）
    public int Volume => _volume;

    public bool Muted => Raw.Mute;

    public TrackInfo? Current => _current;

    public string? LastError => _lastError;

    public Task SetUriAsync(string uri, string? metadata, CancellationToken ct = default)
    {
        _lastError = null;
        _current = new TrackInfo(uri, metadata, TimeSpan.Zero);
        SetState(TransportState.Transitioning);

        return EnqueueAsync(() =>
        {
            var media = new LibVLCSharp.Shared.Media(_libVlc, new Uri(uri));
            if (!string.IsNullOrWhiteSpace(_settings.StreamUserAgent))
                media.AddOption($":http-user-agent={_settings.StreamUserAgent}");
            if (!string.IsNullOrWhiteSpace(_settings.StreamReferer))
                media.AddOption($":http-referrer={_settings.StreamReferer}");
            media.AddOption(":http-reconnect");
            media.AddOption(":adaptive-logic=highest");

            _log.Info("player", $"装载媒体：{Truncate(uri)}");
            try { _currentMedia?.Dispose(); } catch { }
            _currentMedia = media;
            _positionMs = 0;
            _lengthMs = 0;
            Raw.Play(media);
        });
    }

    /// <summary>
    /// 所有会与 VLC 窗口/输出交互的操作都放到同一条串行后台队列执行，且严格先进先出。
    /// 重要 1：绝不能在 WPF UI 线程上同步调用 Play/Stop/SetMedia —— VLC 会等待视频输出窗口释放，
    ///          与 UI 线程互相等待导致死锁（实测卡死 10 分钟以上）。
    /// 重要 2：必须保证顺序（用任务链而不是 SemaphoreSlim，后者不保证 FIFO，
    ///          曾出现“刚装载的新媒体被上一次排队的 Stop 立刻杀掉”的问题）。
    /// </summary>
    private Task EnqueueAsync(Action action)
    {
        if (SynchronousControl)
        {
            try { action(); }
            catch (Exception ex) { _log.Warn("player", $"播放器操作失败：{ex.GetType().Name} {ex.Message}"); }
            return Task.CompletedTask;
        }
        lock (_queueGate)
        {
            var next = _queueTail.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { _log.Warn("player", $"播放器操作失败：{ex.GetType().Name} {ex.Message}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _queueTail = next;
            return next;
        }
    }

    public void Play()
    {
        if (_current is null) return;
        EnqueueAsync(() =>
        {
            switch (Raw.State)
            {
                case VLCState.Paused:
                    Raw.SetPause(false);           // 从暂停恢复
                    break;
                case VLCState.Ended:
                case VLCState.Stopped:
                case VLCState.Error:
                    // 已结束/已停止：重新开始（不能先 Stop() 再 Play()，那会把刚装载的媒体重开一次）
                    if (_currentMedia is not null) Raw.Play(_currentMedia);
                    break;
                default:
                    // Opening / Buffering / Playing / NothingSpecial：已经在路上，不打断
                    break;
            }
        });
        SetState(TransportState.Playing);
    }

    public void Pause()
    {
        if (_current is null) return;
        EnqueueAsync(() => Raw.Pause());
        SetState(TransportState.Paused);
    }

    public void Stop()
    {
        EnqueueAsync(() => Raw.Stop());
        SetState(_current is null ? TransportState.NoMediaPresent : TransportState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_current is null) return;
        long ms = (long)Math.Max(0, position.TotalMilliseconds);
        _positionMs = ms;
        EnqueueAsync(() => Raw.Time = ms);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// DLNA 播放画面的“适应 / 铺满”。
    /// 铺满时把裁剪比例设成显示区域的比例（VLC 会裁掉多余部分后铺满），适应时恢复自动。
    /// 注意：必须在视频已经加载/尺寸有效时调用才生效，调用方需要在开始播放、窗口尺寸变化时重新套用。
    /// </summary>
    public void SetFillMode(bool fill, int viewWidth, int viewHeight)
    {
        EnqueueAsync(() =>
        {
            try
            {
                if (!fill)
                {
                    Raw.CropGeometry = null;
                    _log.Info("player", "画面填充：适应（已恢复自动比例）");
                    return;
                }
                if (viewWidth < 50 || viewHeight < 50)
                {
                    _log.Debug("player", $"画面填充：显示区域尺寸无效（{viewWidth}×{viewHeight}），跳过");
                    return;
                }
                string geometry = $"{viewWidth}:{viewHeight}";
                Raw.CropGeometry = geometry;
                _log.Info("player", $"画面填充：铺满（裁剪比例 {geometry}，实际值={Raw.CropGeometry ?? "null"}）");
            }
            catch (Exception ex) { _log.Warn("player", $"设置画面填充失败：{ex.Message}"); }
        });
    }

    /// <summary>诊断用：当前裁剪比例。</summary>
    public string CropDebug => Raw.CropGeometry ?? "-";

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _settings.Volume = _volume;
        EnqueueAsync(() => Raw.Volume = _volume);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        EnqueueAsync(() => Raw.Mute = muted);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(TransportState state)
    {
        if (_state == state) return;
        _state = state;
        if (state == TransportState.Playing && _current is not null && Raw.Length > 0)
            _current = _current with { Duration = TimeSpan.FromMilliseconds(Raw.Length) };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Truncate(string s) => s.Length <= 110 ? s : s[..110] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_current is null && _currentMedia is null)
            {
                // 从未播放过：VLC 没有启动任何输入，直接释放即可（早期版本会在这里无条件调用 Stop，
                // 在退出/重复实例等场景下会踩到原生层的 use-after-free，直接让进程以 0xc0000005 崩溃）
            }
            else
            {
                // 通过串行队列停止，并**确认它真的结束了**才释放原生对象；
                // 若超时未结束则放弃释放（进程即将退出，宁可泄漏也不能 use-after-free）
                var stopTask = EnqueueAsync(() => { try { Raw.Stop(); } catch { } });
                if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    _log.Warn("player", "停止播放超时，跳过原生对象释放以避免崩溃（进程退出时由系统回收）");
                    return;
                }
            }
        }
        catch (Exception ex) { _log.Debug("player", $"停止播放异常：{ex.Message}"); }

        try { Raw.Dispose(); } catch { }
        try { _currentMedia?.Dispose(); } catch { }
        try { _libVlc.Dispose(); } catch { }
    }
}
