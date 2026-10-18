using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CastReceiver.App;

/// <summary>
/// 电脑音量弹层（滑杆 + 静音），浮在悬浮控制条上方。
///
/// 它只负责"显示与收集操作"，音量真正由主窗口去读写（见 <see cref="MainWindow"/>）——
/// 这样悬浮条上的喇叭图标、右键菜单里的静音、这个滑杆永远是同一个状态。
///
/// 关闭规则（按常规弹层的习惯）：
/// <list type="bullet">
/// <item>点到弹层外面任意位置（画面、控制条、别的程序、桌面）→ 立刻收起；</item>
/// <item>鼠标停手 5 秒 → 自己收起（不打断"只滚轮调音量"的场景）；</item>
/// <item>按 Esc → 收起。</item>
/// </list>
/// 早先它不抢焦点，于是"点外面"根本收不到通知，用户必须先关掉它才能继续操作 —— 反直觉。
/// 现在弹层会正常激活（可点击区域本来就只有这一小块），失活即收起。
/// </summary>
public partial class VolumePopupWindow : Window
{
    /// <summary>用户拖了滑杆 / 滚了滚轮：新音量（0..1）。</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>用户点了喇叭：请求切到「非当前静音状态」。</summary>
    public event Action<bool>? MuteRequested;

    /// <summary>因为"点到外面 / 失活"而收起（宿主据此忽略同一次点击里的重新打开）。</summary>
    public event Action? AutoHidden;

    private bool _syncing;
    private bool _muted;
    private readonly DispatcherTimer _idle;

    /// <summary>最近一次"点外面收起"的时间（UTC）：同一次点击不该把它又打开。</summary>
    public DateTime LastAutoHiddenUtc { get; private set; } = DateTime.MinValue;

    public VolumePopupWindow()
    {
        InitializeComponent();

        // 鼠标停手 5 秒就自己收起来（滚轮调音量时不必一直把指针停在弹层上）。
        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _idle.Tick += (_, _) => { _idle.Stop(); HideSilently(auto: false); };
        MouseEnter += (_, _) => Restart();
        MouseMove += (_, _) => Restart();
        MouseLeave += (_, _) => Restart();
        IsVisibleChanged += (_, _) => { if (IsVisible) Restart(); else _idle.Stop(); };

        // 失活 = 用户点了别处（画面、控制条、其它程序）：按常规逻辑立刻收起。
        Deactivated += (_, _) => HideSilently(auto: true);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideSilently(auto: true); e.Handled = true; }
        };
    }

    /// <summary>收起弹层；<paramref name="auto"/> = 由"点外面"触发，需要通知宿主。</summary>
    private void HideSilently(bool auto)
    {
        if (!IsVisible) return;
        _idle.Stop();
        Hide();
        if (!auto) return;
        LastAutoHiddenUtc = DateTime.UtcNow;
        AutoHidden?.Invoke();
    }

    private void Restart()
    {
        _idle.Stop();
        if (IsVisible) _idle.Start();
    }

    /// <summary>把界面同步到真实音量（不触发事件，避免回声）。</summary>
    public void SyncState(float? level, bool? muted)
    {
        _syncing = true;
        try
        {
            _muted = muted == true;
            if (level is { } value)
            {
                SldVolume.Value = Math.Round(value * 100);
                SldVolume.IsEnabled = true;
            }
            else
            {
                SldVolume.IsEnabled = false;   // 读不到音量：滑杆禁用，别让用户拖了个寂寞
            }

            TxtLevel.Text = level is { } v ? $"{(int)Math.Round(v * 100)}%" : "—";
            if (_muted) TxtLevel.Text = "静音";
            ApplyMuteLook();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyMuteLook()
    {
        BtnMute.Content = _muted ? "\uE74F" : "\uE767";
        BtnMute.Foreground = _muted
            ? (Brush)FindResource("B.MediaWarn")
            : (Brush)FindResource("B.MediaTextDim");
        AutomationProperties.SetName(BtnMute, _muted ? "取消静音" : "静音");
    }

    private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        TxtLevel.Text = $"{(int)Math.Round(e.NewValue)}%";
        _muted = false;
        ApplyMuteLook();
        VolumeChanged?.Invoke(e.NewValue / 100.0);
        Restart();
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        MuteRequested?.Invoke(!_muted);
        Restart();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!SldVolume.IsEnabled) return;
        SldVolume.Value = Math.Clamp(SldVolume.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        e.Handled = true;
        Restart();
    }

    /// <summary>弹层自己可以被拖到别处（父窗口下次摆位置时会再拉回来）。</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}