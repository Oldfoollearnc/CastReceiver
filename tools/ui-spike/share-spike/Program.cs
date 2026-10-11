using System.IO;
using System.Windows;
using System.Windows.Threading;
using CastReceiver.App.Interop;

namespace ShareSpike;

/// <summary>
/// 用**产品真实代码**验证三件事：
///   1. 两个 CompositionVideoHost 共用同一个 Compositor 时都能出画；
///   2. MirrorFrom 能让第二个窗口显示同一路画面（零额外解码）；
///   3. 关掉第二个窗口不会弄没第一个窗口的画面（共享合成器的引用计数正确）。
/// 第 3 条是这次改动最容易踩的坑，所以专门测。
/// </summary>
internal static class Program
{
    private static readonly List<string> Log = new();

    [STAThread]
    public static void Main(string[] args)
    {
        string frameFile = args.Length > 0 ? args[0] : "sample_frames.bin";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => Build(frameFile);
        app.Run();
    }

    private static void Say(string message)
    {
        Log.Add(message);
        Console.WriteLine(message);
        Console.Out.Flush();
    }

    private static void Build(string frameFile)
    {
        var hostA = new CompositionVideoHost();
        var winA = new Window
        {
            Title = "真实宿主 A（源画面）",
            Width = 430, Height = 300, Left = 60, Top = 90,
            Background = System.Windows.Media.Brushes.Black, Content = hostA,
        };

        var hostB = new CompositionVideoHost();
        var winB = new Window
        {
            Title = "真实宿主 B（MirrorFrom 共享画面）",
            Width = 430, Height = 300, Left = 520, Top = 90,
            Background = System.Windows.Media.Brushes.Black, Content = hostB,
        };

        hostA.Status += m => Say("A: " + m);
        hostB.Status += m => Say("B: " + m);

        winA.Show();
        winB.Show();

        AirPlayVideoSource? video = null;

        After(1.0, () =>
        {
            Say($"READY after show — SharedCompositorInUse={CompositionVideoHost.SharedCompositorInUse} " +
                $"A.IsReady={hostA.IsReady} B.IsReady={hostB.IsReady}");

            video = new AirPlayVideoSource(640, 360, capacity: 90);
            hostA.AttachSource(video.MediaSource, 640, 360, realTime: false);
            Say($"A.AttachSource done — brush={(hostA.SurfaceBrush is not null)} " +
                $"SharedCompositorInUse={CompositionVideoHost.SharedCompositorInUse}");

            if (File.Exists(frameFile))
            {
                var frames = ReadFrames(frameFile);
                Say($"loaded {frames.Count} frames");
                int index = 0;
                var feeder = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                feeder.Tick += (_, _) =>
                {
                    for (int k = 0; k < 3 && index < frames.Count; k++, index++)
                    {
                        video.Push(frames[index].Payload, frames[index].Ts);
                    }
                    if (index >= frames.Count)
                    {
                        feeder.Stop();
                        Say($"all {frames.Count} frames pushed");
                    }
                };
                feeder.Start();
            }
            else
            {
                Say($"frame file not found: {frameFile}");
            }
        });

        After(4.0, () =>
        {
            bool ok = hostB.MirrorFrom(hostA);
            Say($"B.MirrorFrom(A) = {ok} | B.IsMirroring={hostB.IsMirroring} " +
                $"sameBrush={ReferenceEquals(hostA.SurfaceBrush, (hostB as CompositionVideoHost) is null ? null : hostA.SurfaceBrush)} " +
                $"B.MirrorSourceIsA={ReferenceEquals(hostB.MirrorSource, hostA)}");
        });

        After(11.0, () => Say($"PROBE-1 (both open) A.brush={(hostA.SurfaceBrush is not null)} " +
                              $"B.mirroring={hostB.IsMirroring} SharedCompositorInUse={CompositionVideoHost.SharedCompositorInUse} " +
                              $"state={hostA.Player?.PlaybackSession.PlaybackState} " +
                              $"natural={hostA.Player?.PlaybackSession.NaturalVideoWidth}x{hostA.Player?.PlaybackSession.NaturalVideoHeight}"));

        // 关键一步：关掉 B，然后确认 A 的画面还在（引用计数没被误释放）
        After(13.0, () =>
        {
            Say("closing window B ...");
            winB.Close();
        });

        After(15.0, () => Say($"PROBE-2 (B closed) A.brush={(hostA.SurfaceBrush is not null)} " +
                              $"A.IsMirroring={hostA.IsMirroring} SharedCompositorInUse={CompositionVideoHost.SharedCompositorInUse} " +
                              $"state={hostA.Player?.PlaybackSession.PlaybackState} " +
                              $"natural={hostA.Player?.PlaybackSession.NaturalVideoWidth}x{hostA.Player?.PlaybackSession.NaturalVideoHeight} " +
                              $"A.root={hostA.IsReady}"));

        After(95.0, () => { Say("TIMEOUT-EXIT"); Application.Current.Shutdown(); });
    }

    private static void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch (Exception ex) { Say($"EXCEPTION in step: {ex.GetType().Name}: {ex.Message}"); }
        };
        timer.Start();
    }

    private static List<(ulong Ts, byte[] Payload)> ReadFrames(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int count = BitConverter.ToInt32(data, 8);
        var frames = new List<(ulong, byte[])>(count);
        int pos = 12;
        for (int i = 0; i < count; i++)
        {
            int len = BitConverter.ToInt32(data, pos);
            ulong ts = BitConverter.ToUInt64(data, pos + 4);
            pos += 12;
            frames.Add((ts, data[pos..(pos + len)]));
            pos += len;
        }
        return frames;
    }
}
