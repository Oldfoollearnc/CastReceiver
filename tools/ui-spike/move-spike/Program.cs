using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CastReceiver.App.Interop;
using CastReceiver.App.Services;

namespace MoveSpike;

/// <summary>
/// ③ 多设备并排的可行性实测（用产品真实代码）。测四件事：
///   1. 同一路画面在宿主之间「搬动」后还能出画（Detach(disposeSource:false) → 另一个宿主 AttachSource）；
///   2. 搬回来也还能出画（来回搬两次不会把 MediaSource 用坏）；
///   3. 搬动期间解码不中断（帧一直喂着，搬完立刻继续出画）；
///   4. 新建的格子宿主用「先可见、下一轮布局再挂画面」的写法能出画（宫格新增一路时就是这个顺序）。
///
/// 判定不靠 SurfaceBrush 是否为 null，而是用 PrintWindow 取像素看画面**是否真的不是黑的**
/// （小窗那次"一片黑"就是程序化断言全过、实际全黑）。
/// </summary>
internal static class Program
{
    private static readonly List<string> Verdicts = new();

    private static Window _winA = null!, _winB = null!, _winC = null!;
    private static CompositionVideoHost _hostA = null!, _hostB = null!, _hostC = null!;
    private static AirPlayVideoSource _video = null!;

    private static List<(ulong Ts, byte[] Payload)> _frames = new();
    private static int _frameIndex;
    private static ulong _feedClock;
    private static DispatcherTimer _feeder = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        string frameFile = args.Length > 0 ? args[0] : Path.Combine("..", "sample_frames.bin");
        if (!File.Exists(frameFile))
        {
            Console.WriteLine($"RESULT: FAIL 找不到帧文件 {Path.GetFullPath(frameFile)}");
            return;
        }
        _frames = ReadFrames(frameFile);
        Console.WriteLine($"loaded {_frames.Count} real H.264 frames from {Path.GetFullPath(frameFile)}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => Build();
        app.Run();
    }

    private static void Say(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }

    private static void Build()
    {
        _hostA = new CompositionVideoHost();
        _winA = new Window
        {
            Title = "move-spike A（主位，模拟主窗口宿主）",
            Width = 480, Height = 300, Left = 40, Top = 80,
            Background = Brushes.Black, Content = _hostA, Topmost = true,
        };

        _hostB = new CompositionVideoHost();
        _winB = new Window
        {
            Title = "move-spike B（格子位）",
            Width = 480, Height = 300, Left = 560, Top = 80,
            Background = Brushes.Black, Content = _hostB, Topmost = true,
        };

        // C 用来重现「宫格新增一路」的创建顺序：宿主先 Collapsed 放进 Grid，之后才变可见并挂画面
        _hostC = new CompositionVideoHost { Visibility = Visibility.Collapsed };
        var gridC = new Grid { Background = Brushes.Black };
        gridC.Children.Add(_hostC);
        _winC = new Window
        {
            Title = "move-spike C（后创建的格子）",
            Width = 480, Height = 300, Left = 300, Top = 430,
            Background = Brushes.Black, Content = gridC, Topmost = true,
        };

        _hostA.Status += m => Say("  [A] " + m);
        _hostB.Status += m => Say("  [B] " + m);
        _hostC.Status += m => Say("  [C] " + m);

        _winA.Show();
        _winB.Show();
        _winC.Show();

        After(1.0, () =>
        {
            Say($"READY A.IsReady={_hostA.IsReady} B.IsReady={_hostB.IsReady} C.IsReady={_hostC.IsReady} " +
                $"SharedCompositorInUse={CompositionVideoHost.SharedCompositorInUse}");

            _video = new AirPlayVideoSource(640, 360, capacity: 30);
            _hostA.AttachSource(_video.MediaSource, 640, 360, realTime: false);
            StartFeeding();
        });

        // ---- 1. 主位出画 ----
        After(4.0, () => Probe("step1-主位首次出画", _hostA, expect: true, "初始挂载"));

        // ---- 2. 搬到格子 B ----
        After(5.0, () =>
        {
            Say("move #1: A.Detach(disposeSource:false) → B.AttachSource(同一个 MediaSource)");
            _hostA.Detach(disposeSource: false);
            _hostB.AttachSource(_video.MediaSource, 640, 360, realTime: false);
        });
        After(8.0, () => Probe("step2-搬到B后出画", _hostB, expect: true, "第一次搬动"));

        // ---- 3. 再搬回主位 A ----
        After(9.0, () =>
        {
            Say("move #2: B.Detach(disposeSource:false) → A.AttachSource(同一个 MediaSource)");
            _hostB.Detach(disposeSource: false);
            _hostA.AttachSource(_video.MediaSource, 640, 360, realTime: false);
        });
        After(12.0, () => Probe("step3-搬回A后出画", _hostA, expect: true, "第二次搬动（换回主位）"));

        // ---- 4. 后创建的格子宿主：先可见，下一轮布局再挂画面 ----
        After(13.0, () =>
        {
            Say("newtile: 把新建宿主设为可见，等一轮布局后再 AttachSource（宫格新增一路的顺序）");
            _hostC.Visibility = Visibility.Visible;
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                _hostA.Detach(disposeSource: false);
                _hostC.AttachSource(_video.MediaSource, 640, 360, realTime: false);
            }, DispatcherPriority.Loaded);
        });
        After(16.0, () => Probe("step4-新建格子出画", _hostC, expect: true, "后创建宿主 + 先可见再挂"));

        After(17.5, () =>
        {
            _feeder.Stop();
            Say($"frame feeder: pushed={_video.PushedCount} delivered={_video.DeliveredCount} dropped={_video.DroppedCount}");
            int passed = Verdicts.Count(v => v.StartsWith("PASS", StringComparison.Ordinal));
            Say($"RESULT: {(passed == Verdicts.Count ? "PASS" : "FAIL")} {passed}/{Verdicts.Count}");
            foreach (string v in Verdicts) Say("  " + v);
            Application.Current.Shutdown();
        });

        After(45.0, () => { Say("RESULT: FAIL 超时退出"); Application.Current.Shutdown(); });
    }

    /// <summary>持续以 30fps 喂真实帧（时间戳自己按 1/30 秒递增，保证单调）。</summary>
    private static void StartFeeding()
    {
        _feeder = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _feeder.Tick += (_, _) =>
        {
            for (int i = 0; i < 2; i++)
            {
                (ulong _, byte[] payload) = _frames[_frameIndex];
                _frameIndex = (_frameIndex + 1) % _frames.Count;
                _feedClock += 4294967296UL / 30UL;      // Q32.32：每帧 1/30 秒
                try { _video.Push(payload, _feedClock); }
                catch (Exception ex) { Say($"  push EXCEPTION: {ex.GetType().Name}: {ex.Message}"); }
            }
        };
        _feeder.Start();
    }

    private static void Probe(string step, CompositionVideoHost host, bool expect, string note)
    {
        try
        {
            var player = host.Player;
            var session = player?.PlaybackSession;
            bool brush = host.SurfaceBrush is not null;
            var shot = WindowProtection.GrabWindow(host.ChildHandle);
            bool blank = WindowProtection.LooksBlank(shot);
            (int bright, int samples) = Brightness(shot);
            string state = session?.PlaybackState.ToString() ?? "no-player";
            string natural = $"{session?.NaturalVideoWidth}x{session?.NaturalVideoHeight}";
            Say($"[{step}] brush={brush} state={state} natural={natural} printwindow={(shot is null ? "null" : $"{shot.Width}x{shot.Height}")} " +
                $"blank={blank} bright={bright}/{samples} ({note})");

            bool ok = brush && !blank;
            Verdicts.Add((ok == expect ? "PASS " : "FAIL ") + step + (ok ? "（有画面）" : "（没有画面）"));
        }
        catch (Exception ex)
        {
            Verdicts.Add($"FAIL {step} 异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (int Bright, int Samples) Brightness(System.Drawing.Bitmap? bitmap)
    {
        if (bitmap is null) return (0, 0);
        try
        {
            int samples = 0, bright = 0;
            for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 30))
            {
                for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 30))
                {
                    var pixel = bitmap.GetPixel(x, y);
                    samples++;
                    if (pixel.R + pixel.G + pixel.B > 60) bright++;
                }
            }
            return (bright, samples);
        }
        catch { return (0, 0); }
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
