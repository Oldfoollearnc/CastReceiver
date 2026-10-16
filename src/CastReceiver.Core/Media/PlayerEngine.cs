using CastReceiver.Core.Infrastructure;

namespace CastReceiver.Core.Media;

public enum TransportState { NoMediaPresent, Stopped, Transitioning, Playing, Paused }

public sealed record TrackInfo(string Uri, string? Metadata, TimeSpan Duration);

/// <summary>
/// 播放内核抽象。**刻意不暴露任何 LibVLC 类型** —— 原先这里有个 `MediaPlayer Raw { get; }`，
/// 正是它把 LibVLCSharp 拖进了 Core、进而拖进发布包。真正需要它的只有测试项目里的
/// 自检工具（把画面挂到 VideoView 上），那里用的是具体类型 VlcPlayerEngine，所以摘掉不影响谁。
/// </summary>
public interface IMediaPlayerEngine : IDisposable
{
    event EventHandler? StateChanged;
    event EventHandler? PositionChanged;

    TransportState State { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    int Volume { get; }
    bool Muted { get; }
    TrackInfo? Current { get; }
    string? LastError { get; }

    Task SetUriAsync(string uri, string? metadata, CancellationToken ct = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(int volume);
    void SetMuted(bool muted);
}


/// <summary>
/// 播放内核的额外诊断信息（音量与原始遥测）。**只有测试项目里的 VlcPlayerEngine 实现它**：
/// 产品路径不需要 —— 设置页的自检传的是 player: null，DLNA 播放已整体移除。
/// 有了这层接口，Core 就不必引用 LibVLCSharp，发布包里也不会带上这个 LGPL 绑定。
/// </summary>
public interface IPlaybackDiagnostics
{
    int Volume { get; }
    string RawTelemetry { get; }
}
