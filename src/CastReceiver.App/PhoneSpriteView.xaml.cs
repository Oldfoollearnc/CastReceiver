using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CastReceiver.App;

/// <summary>
/// 待机页的"产品渲染图"：由 Blender 工程（D:\Blender\projects\phone-anim\vivo_x500pro.blend）
/// 渲染的转台，合成一张 10×6 雪碧图；这里只取前 31 帧（-25° → +25° 单调变化），
/// 用鼠标左右位置直接控制旋转角度，上下位置控制轻微视差 —— 比单纯平移更像"真的在转"。
///
/// 连接成功后 MainWindow 会调 <see cref="PlayConnectTransition"/>：回到正面、轻微放大、上浮，
/// 随后整块引导淡出；素材缺失时自动回落到 <see cref="Phone3DView"/>。
/// </summary>
public partial class PhoneSpriteView : UserControl
{
    private const int Columns = 10;
    private const int Rows = 6;
    /// <summary>雪碧图里可用的帧数：60 帧刚好是 -25° → +25° → -25° 的一个完整来回。</summary>
    private const int UsableFrames = 60;
    /// <summary>正面（0°）那一帧。60 帧的中间是 +25°，不是正面 —— 别用 UsableFrames / 2。</summary>
    private const int CenterFrame = 15;

    /// <summary>自动转台速度：60 帧 / 9fps ≈ 6.7 秒一个来回。</summary>
    private const double RotationFramesPerSecond = 9.0;

    private readonly DispatcherTimer _timer;
    private List<BitmapSource>? _frames;
    private Phone3DView? _fallback;
    private bool _initialized;
    private bool _loading;
    private bool _running;
    private bool _connecting;
    private bool _pointerActive;

    private double _frameIndex = CenterFrame;
    private int _lastFrame = -1;
    private double _floatTime;
    private double _pointerY;
    private DateTime _lastPointerUtc = DateTime.MinValue;

    public PhoneSpriteView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) => EnsureInitialized();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Stop(); else Start(); };
    }

    private void EnsureInitialized()
    {
        if (_initialized || _loading) return;

        // 雪碧图是 3200×2400 的 PNG：解码后约 30MB 位图。原先在 Loaded 里同步解码，
        // 正好压在"窗口刚出现"那一下，会明显发木（启动慢的体感来源之一）。
        // 现在改成后台 STA 线程解码 + Freeze，界面先出文字与骨架，素材到了再补图。
        _loading = true;
        _ = LoadSpriteOffThreadAsync();
    }

    /// <summary>后台解码雪碧图：完成后再切回界面线程挂上（位图已 Freeze，可跨线程使用）。</summary>
    private async Task LoadSpriteOffThreadAsync()
    {
        List<BitmapSource>? frames = null;
        try
        {
            var tcs = new TaskCompletionSource<List<BitmapSource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new System.Threading.Thread(() =>
            {
                try { tcs.TrySetResult(LoadFrames()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            })
            {
                IsBackground = true,
                // WPF 图像解码要在 STA 线程上；用一个短命线程最省事，也不占线程池
                Name = "phone-sprite-decode",
            };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            frames = await tcs.Task.ConfigureAwait(true);
        }
        catch
        {
            frames = null;   // 素材损坏：走 3D 兜底
        }

        _loading = false;
        _initialized = true;
        if (frames is not null && frames.Count > CenterFrame)
        {
            _frames = frames;
            Sprite.Source = frames[CenterFrame];
            Start();
            return;
        }

        _fallback = new Phone3DView();
        Sprite.Visibility = Visibility.Collapsed;
        Root.Children.Add(_fallback);
        Start();
    }

    /// <summary>在工作线程上把雪碧图切成 31 帧并冻结（冻结后才能交给界面线程）。</summary>
    private static List<BitmapSource>? LoadFrames()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "ui", "phone-turntable.png");
        if (!File.Exists(path)) return null;

        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        sheet.UriSource = new Uri(path, UriKind.Absolute);
        sheet.EndInit();
        sheet.Freeze();

        int cellWidth = sheet.PixelWidth / Columns;
        int cellHeight = sheet.PixelHeight / Rows;
        if (cellWidth <= 0 || cellHeight <= 0) throw new InvalidOperationException("雪碧图尺寸异常");

        var frames = new List<BitmapSource>(UsableFrames);
        for (int i = 0; i < UsableFrames; i++)
        {
            var crop = new CroppedBitmap(sheet, new Int32Rect(
                (i % Columns) * cellWidth,
                (i / Columns) * cellHeight,
                cellWidth,
                cellHeight));
            crop.Freeze();
            frames.Add(crop);
        }
        return frames;
    }

    /// <summary>开始交互（待机页可见时调用）。</summary>
    public void Start()
    {
        EnsureInitialized();
        if (_fallback is not null)
        {
            _fallback.Start();
            return;
        }
        if (_frames is null)
        {
            return;
        }

        if (!Motion.Enabled)
        {
            Sprite.Source = _frames[CenterFrame];
            return;
        }

        if (_running) return;
        _running = true;
        _connecting = false;
        _timer.Start();
    }

    /// <summary>停止（连接中 / 投屏中 / 遮挡时调用）。</summary>
    public void Stop()
    {
        if (_fallback is not null)
        {
            _fallback.Stop();
            return;
        }
        if (!_running)
        {
            return;
        }

        _running = false;
        _connecting = false;
        _pointerActive = false;
        _timer.Stop();

        if (_frames is not null)
        {
            _frameIndex = CenterFrame;
            _lastFrame = -1;
            Sprite.Source = _frames[CenterFrame];
        }
        Parallax.X = Parallax.Y = 0;
        PointerScale.ScaleX = PointerScale.ScaleY = 1;
        Float.Y = 0;
        Root.Opacity = 1;
        EntranceScale.ScaleX = EntranceScale.ScaleY = 1;
    }

    /// <summary>回到待机初始姿态并开始交互（断开 / 重连后显示引导时调用）。</summary>
    public void ResetForIdle()
    {
        if (_fallback is not null)
        {
            _fallback.ResetPointer();
            _fallback.Start();
            return;
        }

        _connecting = false;
        _pointerActive = false;
        _pointerY = 0;
        _frameIndex = CenterFrame;
        _lastFrame = -1;
        if (_frames is not null) Sprite.Source = _frames[CenterFrame];
        Parallax.X = Parallax.Y = 0;
        PointerScale.ScaleX = PointerScale.ScaleY = 1;
        Float.Y = 0;
        Root.Opacity = 1;
        EntranceScale.ScaleX = EntranceScale.ScaleY = 1;
        Start();
    }

    /// <summary>
    /// 连接成功时的收尾：把手机转回正面、轻微放大/上浮；随后由 MainWindow 把整块引导淡出。
    /// </summary>
    public void PlayConnectTransition()
    {
        if (_fallback is not null)
        {
            _fallback.Stop();
            return;
        }
        if (_frames is null)
        {
            return;
        }

        _connecting = true;
        _pointerActive = false;
        _pointerY = 0;

        if (!Motion.Enabled)
        {
            _frameIndex = CenterFrame;
            Sprite.Source = _frames[CenterFrame];
            return;
        }

        if (!_running)
        {
            _running = true;
            _timer.Start();
        }
    }

    /// <summary>
    /// 鼠标在舞台上移动时给一点上下视差（<paramref name="nx"/> / <paramref name="ny"/> 取 -1…1）。
    /// **横向不再驱动旋转**：转台自己会一直转，鼠标左右划过去只会让手机乱跳，
    /// 手感反而不像"在看一台机器"。
    /// </summary>
    public void SetPointer(double nx, double ny)
    {
        _pointerY = Math.Clamp(ny, -1, 1);
        _pointerActive = true;
        _lastPointerUtc = DateTime.UtcNow;
    }

    public void ResetPointer()
    {
        _pointerActive = false;
    }

    /// <summary>入场：0.94 倍 + 淡入；然后开始交互。</summary>
    public void PlayEntrance()
    {
        if (_fallback is not null)
        {
            _fallback.PlayEntrance();
            return;
        }
        if (!Motion.Enabled)
        {
            EntranceScale.ScaleX = EntranceScale.ScaleY = 1;
            Start();
            return;
        }

        EntranceScale.ScaleX = EntranceScale.ScaleY = 1;
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        Motion.FadeIn(Root, 520, 0, 0);
        Start();
    }

    private void OnTick()
    {
        if (_frames is null) return;

        bool pointerActive = !_connecting
                             && _pointerActive
                             && (DateTime.UtcNow - _lastPointerUtc).TotalSeconds < 2.0;

        double targetY = 0;
        double targetScale = 1;

        if (_connecting)
        {
            // 连接收尾：回正面、放大、上浮
            _frameIndex += (CenterFrame - _frameIndex) * 0.14;
            targetScale = 1.10;
            targetY = -10;
        }
        else
        {
            // 自身转台：一直缓慢转，展示机身；鼠标只影响上下视差
            _frameIndex += RotationFramesPerSecond * 0.033;
            if (_frameIndex >= UsableFrames) _frameIndex -= UsableFrames;

            if (pointerActive)
            {
                targetY = _pointerY * 7.0;
                targetScale = 1 + Math.Abs(_pointerY) * 0.006;
            }
        }

        int frame = ((int)Math.Round(_frameIndex) % UsableFrames + UsableFrames) % UsableFrames;
        if (frame != _lastFrame)
        {
            _lastFrame = frame;
            Sprite.Source = _frames[frame];
        }

        // 横向视差归零（不再跟随鼠标左右）
        Parallax.X += (0 - Parallax.X) * 0.18;
        Parallax.Y += (targetY - Parallax.Y) * 0.18;
        PointerScale.ScaleX += (targetScale - PointerScale.ScaleX) * 0.16;
        PointerScale.ScaleY = PointerScale.ScaleX;

        _floatTime += 0.033;
        Float.Y = Math.Sin(_floatTime * 1.2) * 2.0;
    }
}
