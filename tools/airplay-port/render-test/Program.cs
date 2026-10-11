using CastReceiver.App.Interop;
using Windows.Media.Playback;

// 把真实的 H.264 Annex-B 帧推给 AirPlayVideoSource，确认 Media Foundation 真的解码出画面。
//   render-test <frames.bin> [paceMs]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: render-test <frames.bin> [paceMs]");
    return 2;
}

byte[] data = File.ReadAllBytes(args[0]);
int paceMs = args.Length > 1 ? int.Parse(args[1]) : 8;

if (data.Length < 12 || data[0] != (byte)'A' || data[1] != (byte)'P' || data[2] != (byte)'H')
{
    Console.Error.WriteLine("不是 APH264 帧文件");
    return 2;
}

int frameCount = BitConverter.ToInt32(data, 8);
var frames = new List<(ulong Ts, byte[] Payload)>(frameCount);
int pos = 12;
for (int i = 0; i < frameCount; i++)
{
    int len = BitConverter.ToInt32(data, pos);
    ulong ts = BitConverter.ToUInt64(data, pos + 4);
    pos += 12;
    frames.Add((ts, data[pos..(pos + len)]));
    pos += len;
}
Console.WriteLine($"loaded {frames.Count} frames, first={frames[0].Payload.Length} bytes, ts0={frames[0].Ts}");

using var source = new AirPlayVideoSource(width: 640, height: 360, capacity: 64);
source.Status += m => Console.WriteLine($"[source] {m}");

int opened = 0, decoded = 0, failed = 0;
long firstFrameBytes = 0;

var player = new MediaPlayer
{
    IsVideoFrameServerEnabled = true,   // 帧服务器模式：不渲染，只出帧，可无窗口验证
    RealTimePlayback = false,
    AutoPlay = false,
};

player.MediaOpened += (_, _) =>
{
    opened++;
    Console.WriteLine($"MediaOpened: natural={player.PlaybackSession.NaturalVideoWidth}x{player.PlaybackSession.NaturalVideoHeight} " +
                      $"duration={player.PlaybackSession.NaturalDuration}");
};
player.VideoFrameAvailable += (_, _) =>
{
    decoded++;
    if (decoded == 1)
    {
        firstFrameBytes = player.PlaybackSession.Position.Ticks;
        Console.WriteLine("VideoFrameAvailable: 第一帧已解码");
    }
};
player.MediaFailed += (_, e) =>
{
    failed++;
    Console.WriteLine($"MediaFailed: {e.ErrorMessage} (0x{e.ExtendedErrorCode?.HResult:X8})");
};

player.Source = source.MediaSource;
player.Play();

foreach ((ulong ts, byte[] payload) in frames)
{
    source.Push(payload, ts);
    if (paceMs > 0)
    {
        await Task.Delay(paceMs);
    }
}

// 给解码器一点时间把队列排空
await Task.Delay(4000);

Console.WriteLine($"RESULT opened={opened} decodedFrames={decoded} failed={failed} " +
                  $"pushed={source.PushedCount} delivered={source.DeliveredCount} dropped={source.DroppedCount} queued={source.QueuedCount}");

try
{
    player.Pause();
    player.Source = null;
}
catch
{
    // 忽略
}

player.Dispose();
return (opened > 0 && decoded > 0 && failed == 0) ? 0 : 1;
