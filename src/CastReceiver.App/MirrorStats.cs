using Windows.Media.Playback;

namespace CastReceiver.App;

/// <summary>
/// 镜像通讯状态统计：判断"画面是否正常推进、有没有滞后"。
/// 采集自播放管道本身：播放状态、播放位置推进速度（实时比 = 画面推进 / 真实时间）、累计滞后、停滞次数。
/// 说明：Miracast 是直播流，个别驱动不回读播放位置；那种情况下会自动退化为"仅按播放状态判断"，不会误报。
/// </summary>
public sealed class MirrorStats
{
    private readonly Queue<double> _ratios = new();
    private TimeSpan _lastPosition = TimeSpan.MinValue;
    private DateTime _lastSample = DateTime.UtcNow;
    private bool _stallOpen;
    private bool _positionAdvances;

    /// <summary>实时比：画面推进速度 ÷ 真实时间。1.00 = 完全跟得上。</summary>
    public double RealTimeRatio { get; private set; } = 1.0;
    public bool HasRatio { get; private set; }

    /// <summary>累计滞后（秒）：播放过程中"真实时间 - 画面推进"的累计，反映管道是否在落后。</summary>
    public double LagSeconds { get; private set; }

    public int StallCount { get; private set; }
    public double StallSeconds { get; private set; }
    public double PlayedSeconds { get; private set; }
    public DateTime LastStallUtc { get; private set; } = DateTime.MinValue;
    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.None;

    public void Reset()
    {
        _ratios.Clear();
        _lastPosition = TimeSpan.MinValue;
        _lastSample = DateTime.UtcNow;
        _stallOpen = false;
        _positionAdvances = false;
        HasRatio = false;
        RealTimeRatio = 1.0;
        LagSeconds = 0;
        StallCount = 0;
        StallSeconds = 0;
        PlayedSeconds = 0;
        LastStallUtc = DateTime.MinValue;
        State = MediaPlaybackState.None;
    }

    public void Sample(MediaPlaybackState state, TimeSpan position)
    {
        var now = DateTime.UtcNow;
        double wall = (now - _lastSample).TotalSeconds;
        State = state;
        if (wall < 0.4) return;

        if (state == MediaPlaybackState.Playing) PlayedSeconds += wall;

        bool havePrevious = _lastPosition != TimeSpan.MinValue;
        double advanced = havePrevious ? (position - _lastPosition).TotalSeconds : 0;

        if (havePrevious && advanced > 0.05)
        {
            _positionAdvances = true;
            double ratio = Math.Clamp(advanced / wall, 0, 1.5);
            _ratios.Enqueue(ratio);
            while (_ratios.Count > 5) _ratios.Dequeue();
            RealTimeRatio = _ratios.Average();
            HasRatio = true;

            // 推进落后于真实时间的部分累加为“滞后”；追帧时缓慢回退，最低 0
            double behind = wall - advanced;
            if (state == MediaPlaybackState.Playing)
                LagSeconds = Math.Max(0, LagSeconds + (behind > 0.05 ? behind : -0.05));
            _stallOpen = false;
        }
        else if (havePrevious && state == MediaPlaybackState.Playing && wall >= 1.0 && _positionAdvances)
        {
            StallSeconds += wall;
            LagSeconds += wall;
            LastStallUtc = now;
            if (!_stallOpen) { StallCount++; _stallOpen = true; }
        }

        _lastPosition = position;
        _lastSample = now;
    }

    /// <summary>健康度：ok / warn / bad。</summary>
    public string Verdict
    {
        get
        {
            if (State == MediaPlaybackState.Buffering || State == MediaPlaybackState.Opening) return "warn";
            if (State != MediaPlaybackState.Playing) return "bad";
            if (LastStallUtc != DateTime.MinValue && (DateTime.UtcNow - LastStallUtc).TotalSeconds < 8) return "warn";
            if (HasRatio && RealTimeRatio < 0.9) return "warn";
            if (LagSeconds > 2.5) return "warn";
            return "ok";
        }
    }

    private string StateText => State switch
    {
        MediaPlaybackState.Playing => "推送中",
        MediaPlaybackState.Buffering => "缓冲中",
        MediaPlaybackState.Opening => "建立中",
        MediaPlaybackState.Paused => "已暂停",
        _ => "无画面",
    };

    /// <summary>第一行：状态 + 已推送时长 + 滞后 + 实时比。</summary>
    public string Summary
    {
        get
        {
            string text = $"{StateText} {TimeSpan.FromSeconds(PlayedSeconds):mm\\:ss}";
            text += HasRatio ? $" · 滞后 {LagSeconds:F1}s · 实时 {RealTimeRatio:F2}" : " · 实时比不可用";
            return text;
        }
    }

    /// <summary>第二行：流参数 + 音频 + 停滞。</summary>
    public string Detail(string streamInfo, int volume, bool muted)
    {
        string text = "";
        if (!string.IsNullOrWhiteSpace(streamInfo) && streamInfo != "未知") text += streamInfo + " · ";
        text += muted ? "已静音" : $"音量 {volume}";
        text += StallCount > 0 ? $" · 停滞 {StallCount} 次/{StallSeconds:F0}s" : " · 无停滞";
        return text;
    }
}