using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace CastReceiver.App;

/// <summary>
/// 轻量提示条（右下角淡入淡出）：用于"已保存到…"这类需要看见结果的反馈。
/// 独立顶层窗口 + 归属主窗口，因此不会跑到别的程序上面去。
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private Action? _action;

    public ToastWindow(Window owner, string title, string body, string? actionText = null, Action? action = null, bool ok = true)
    {
        InitializeComponent();
        Owner = owner;
        TxtTitle.Text = title;
        TxtBody.Text = body;
        Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "B.Ok" : "B.Warn");
        if (actionText is not null && action is not null)
        {
            BtnAction.Content = actionText;
            BtnAction.Visibility = Visibility.Visible;
            _action = action;
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) =>
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>滑入（从下方 16px 滑上来 + 淡入）。</summary>
    public void SlideIn()
    {
        try
        {
            double target = Top;
            Top = target + 16;
            BeginAnimation(TopProperty, new System.Windows.Media.Animation.DoubleAnimation(
                target + 16, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });
        }
        catch { }
    }

    /// <summary>平滑移动到新的堆叠位置（保持队列整齐）。</summary>
    public void MoveTo(double top)
    {
        try
        {
            if (Math.Abs(Top - top) < 1) return;
            BeginAnimation(TopProperty, new System.Windows.Media.Animation.DoubleAnimation(Top, top, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });
        }
        catch { }
    }

    private void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        try { _action?.Invoke(); } catch { }
        Dismiss();
    }

    public void Dismiss()
    {
        _timer.Stop();
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }
}