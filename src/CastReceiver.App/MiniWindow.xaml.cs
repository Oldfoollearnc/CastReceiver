using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CastReceiver.App.Interop;

namespace CastReceiver.App;

/// <summary>
/// 小窗（独立顶层窗口）：承载与主窗口**同一路**画面，用于两种场景
///   A) 主窗口最小化后继续看画面（自动出现）；
///   B) 主窗口正常时也能画中画并存（手动打开）。
///
/// 画面通过 <see cref="CompositionVideoHost.MirrorFrom"/> 共享主窗口的画刷，
/// **不额外解码**（见 CompositionInterop 里的共享合成器说明）。
///
/// 空域限制：HwndHost 无法被 WPF 元素覆盖，所以控制条放在画面**下方**的独立一行，
/// 而不是叠在画面上——既避开空域问题，也不必为控制条再开一个顶层窗口。
/// </summary>
public partial class MiniWindow : Window
{
    private const double StripHeight = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>DWM_WINDOW_CORNER_PREFERENCE（Windows 11 才支持）。</summary>
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;

    /// <summary>用户要求回到主窗口（双击画面 / 点按钮 / 关小窗都会触发）。</summary>
    public event Action? ReturnToMainRequested;

    public event Action? ScreenshotRequested;

    /// <summary>用户主动关闭小窗（区别于程序因主窗口恢复而收起）。</summary>
    public event Action? Dismissed;

    /// <summary>用户点击「切换设备」：在多个投屏会话之间轮换呈现。</summary>
    public event Action? SwitchDeviceRequested;

    /// <summary>置顶开关变化。</summary>
    public event Action<bool>? PinChanged;

    private bool _pinned = true;
    private double _stripTarget;

    /// <summary>控制条收起时的窗口高度（控制条出现时窗口在这个基础上长高）。</summary>
    private double _collapsedHeight;

    /// <summary>控制条正在做动画：此时的高度变化是自己造成的，不能拿去更新基准高度。</summary>
    private bool _stripAnimating;

    public MiniWindow()
    {
        InitializeComponent();
        _collapsedHeight = Height;      // 构造时就是收起状态（XAML 里的高度）

        MiniHost.DoubleClicked += () => ReturnToMainRequested?.Invoke();
        MiniHost.MouseLeftButtonDown += OnVideoMouseDown;
        MiniHost.MouseRightButtonUp += OnVideoRightClick;

        // 画面上的鼠标消息不会变成控件的 WPF 事件（画面是 HwndHost 的子窗口，对命中测试是透过的，
        // 消息落到窗口上 —— 实测结论见 Interop/CompositionInterop.cs 里那段说明）。
        // 所以"拖画面移动小窗"必须挂在**窗口级**事件上，否则那条注释说的拖动其实一直没生效。
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsOverVideo(e.GetPosition(MiniHost)))
            {
                OnVideoMouseDown(this, e);
            }
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (IsOverVideo(e.GetPosition(MiniHost)))
            {
                OnVideoRightClick(this, e);
            }
        };

        MouseEnter += (_, _) => AnimateStrip(true);
        MouseLeave += (_, _) => AnimateStrip(false);

        // 用户手动改窗口大小时更新基准高度；控制条动画期间的变化要忽略
        SizeChanged += (_, _) =>
        {
            if (!_stripAnimating && _stripTarget <= 0.5)
            {
                _collapsedHeight = Height;
            }
        };

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        Closing += (_, _) => RememberPlacement();
    }

    /// <summary>小窗里的画面宿主；由主窗口调用 MirrorFrom 接入画面。</summary>
    public CompositionVideoHost Host => MiniHost;

    /// <summary>是否置顶显示。</summary>
    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            Topmost = value;
            BtnPin.Foreground = value
                ? (Brush)FindResource("B.Accent")
                : (Brush)FindResource("B.TextDim");
            BtnPin.ToolTip = value ? "已置顶（点击取消）" : "未置顶（点击置顶）";
            PinChanged?.Invoke(value);
        }
    }

    /// <summary>控制条中间的状态文字（设备名 / 协议 / 分辨率等）。</summary>
    public void SetStateText(string text) => TxtState.Text = text;

    /// <summary>
    /// 多设备时显示「切换设备」按钮，并把当前是第几路写进提示。
    /// 只有一路时按钮收起 —— 没得切换却摆个按钮，会让人以为点了没反应。
    /// </summary>
    public void SetSessionSwitching(bool available, int index, int total)
    {
        BtnSwitch.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (available)
        {
            BtnSwitch.ToolTip = $"切换到下一台设备（当前第 {index} / {total} 路）";
        }
    }

    public void SetCaptureEnabled(bool enabled)
    {
        BtnShot.IsEnabled = enabled;
        BtnShot.ToolTip = enabled ? "截取当前画面" : "镜像开始后才能截屏";
    }

    // ---------------------------------------------------------------- 入场 / 退场

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _collapsedHeight = Height;      // 起始状态：控制条收起

        // 入场：淡入 + 轻微上移，避免"啪"一下出现
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        double target = Top;
        Top = target + 10;
        BeginAnimation(TopProperty, new DoubleAnimation(target + 10, target, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        Pinned = _pinned;
    }

    /// <summary>带缓动淡出，结束后再真正关闭（由主窗口调用）。</summary>
    public void FadeOutAndClose(Action? after = null)
    {
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { }
            after?.Invoke();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // ---------------------------------------------------------------- 控制条

    private void AnimateStrip(bool show)
    {
        double target = show ? StripHeight : 0;
        if (Math.Abs(_stripTarget - target) < 0.5)
        {
            return;
        }
        _stripTarget = target;
        _stripAnimating = true;

        // 目标高度一律基于"收起状态的高度"来算，**绝不读当前的 Height** ——
        // 动画进行中 Height 返回的是插值，鼠标快速进出几次就会越滚越大（曾滚到 286）。
        double windowTo = show
            ? _collapsedHeight + StripHeight
            : Math.Max(MinHeight, _collapsedHeight);

        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        TimeSpan duration = TimeSpan.FromMilliseconds(150);

        Animate(this, HeightProperty, Height, windowTo, duration, ease, () => _stripAnimating = false);
        Animate(Strip, HeightProperty, Strip.ActualHeight, target, duration, ease, null);
    }

    /// <summary>
    /// 先把基准值设为目标值、再用 FillBehavior.Stop 做动画：
    /// 动画结束后属性自然停在目标值，而且不会留下"持住"的动画去挡住用户手动调整窗口大小。
    /// </summary>
    private static void Animate(UIElement target, DependencyProperty property, double from, double to,
                                TimeSpan duration, IEasingFunction ease, Action? completed)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }
        target.BeginAnimation(property, animation);
    }

    // ---------------------------------------------------------------- 交互

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ReturnToMainRequested?.Invoke();
            return;
        }

        try { DragMove(); } catch { }
    }

    /// <summary>某个点是不是落在画面宿主上（宿主还没布局时一律算"不在"）。</summary>
    private bool IsOverVideo(Point point)
        => point.X >= 0 && point.Y >= 0 && point.X <= MiniHost.ActualWidth && point.Y <= MiniHost.ActualHeight
           && MiniHost.ActualWidth > 1 && MiniHost.ActualHeight > 1;

    private void OnVideoRightClick(object sender, MouseButtonEventArgs e)
    {
        // 右键 = 收起控制条（想快速看完整画面时用）
        AnimateStrip(false);
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e) => Pinned = !Pinned;
    private void BtnShot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();
    private void BtnSwitch_Click(object sender, RoutedEventArgs e) => SwitchDeviceRequested?.Invoke();
    private void BtnHome_Click(object sender, RoutedEventArgs e) => ReturnToMainRequested?.Invoke();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    // ---------------------------------------------------------------- 位置记忆

    /// <summary>把窗口挪回屏幕内（换了显示器或分辨率变小之后可能跑到屏幕外）。</summary>
    public void EnsureOnScreen()
    {
        Rect work = SystemParameters.WorkArea;
        if (Width > work.Width) Width = Math.Max(MinWidth, work.Width - 40);
        if (Height > work.Height) Height = Math.Max(MinHeight, work.Height - 40);
        if (Left + Width < work.Left + 40) Left = work.Left + 24;
        if (Left > work.Right - 40) Left = work.Right - Width - 24;
        if (Top + Height < work.Top + 40) Top = work.Top + 24;
        if (Top > work.Bottom - 40) Top = work.Bottom - Height - 24;
    }

    /// <summary>窗口位置/大小的持久化回调（由主窗口写入设置）。</summary>
    public event Action<double, double, double, double>? PlacementChanged;

    private void RememberPlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        // 存"收起状态"的高度：直接存 Height 会把展开的控制条一起存进去，
        // 下次打开就更高，每关一次长高 34px（曾这样一路爬到 286）。
        try { PlacementChanged?.Invoke(Left, Top, Width, Math.Max(MinHeight, _collapsedHeight)); } catch { }
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }
            int preference = DwmcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 旧系统没有这个属性：退回直角，不影响功能
        }
    }
}
