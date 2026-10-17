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
/// </summary>
public partial class VolumePopupWindow : Window
{
    /// <summary>用户拖了滑杆 / 滚了滚轮：新音量（0..1）。</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>用户点了喇叭：请求切到「非当前静音状态」。</summary>
    public event Action<bool>? MuteRequested;

    private bool _syncing;
    private bool _muted;
    private readonly DispatcherTimer _idle;

    public VolumePopupWindow()
    {
        InitializeComponent();

        // 弹层不抢焦点，所以没有"失焦自动关"这条路：鼠标停手 5 秒就自己收起来。
        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _idle.Tick += (_, _) => { _idle.Stop(); Hide(); };
        MouseEnter += (_, _) => Restart();
        MouseMove += (_, _) => Restart();
        MouseLeave += (_, _) => Restart();
        IsVisibleChanged += (_, _) => { if (IsVisible) Restart(); else _idle.Stop(); };
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