using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CastReceiver.App;

/// <summary>
/// 全局动效工具箱（极光玻璃那一套）：统一时长、缓动与"动画结束后复位"的写法。
///
/// 三条硬规则（改之前先读）：
/// <list type="number">
/// <item><b>尊重系统设置</b>：Windows 里关掉「显示动画」时 <see cref="Enabled"/> 为 false，
///       所有方法退化成"直接到位"，绝不硬播。</item>
/// <item><b>动画不能把属性锁死</b>：一律 <see cref="FillBehavior.Stop"/>，并把基础值先设成终点；
///       否则 BeginAnimation 结束后属性仍被动画持有，之后再赋 Visibility/Opacity 会"没反应"。</item>
/// <item><b>只动 Opacity / TranslateTransform / ScaleTransform</b>：这三样走渲染线程，
///       不触发布局；改 Width/Height/Margin 的动画在低端机上会掉帧。</item>
/// </list>
/// </summary>
internal static class Motion
{
    private static bool _userAllowed = true;

    /// <summary>
    /// 是否允许播放界面动效。由设置里的「界面动效」控制（默认开），**不再看系统全局动画开关**：
    /// 这台机器的 Windows「动画效果」是关的，而用户明确要求升级动效，所以给应用内独立开关。
    /// 想彻底静态显示的用户可以在 设置 → 通用 → 界面动效 里关掉。
    /// </summary>
    public static bool Enabled => _userAllowed;

    /// <summary>设置页调用：开/关全部界面动效。</summary>
    public static void SetEnabled(bool enabled) => _userAllowed = enabled;

    public static readonly IEasingFunction EaseOut = new ExponentialEase { Exponent = 5.2, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutSoft = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
    public static readonly IEasingFunction EaseIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };

    public static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(Math.Max(1, ms));

    /// <summary>淡入（从 0 到 1）。元素的基础 Opacity 会被设成 1，动画结束不会卡在半透明。</summary>
    public static void FadeIn(UIElement element, int ms = 280, int delay = 0, double from = 0)
    {
        if (element is null) return;
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        element.Opacity = 1;
        var animation = new DoubleAnimation(from, 1, Ms(ms))
        {
            BeginTime = Ms(delay),
            EasingFunction = EaseOutSoft,
            FillBehavior = FillBehavior.Stop,
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>淡出（结束后可选回调；回调前会把 Opacity 复位成 1，调用方自己决定 Visibility）。</summary>
    public static void FadeOut(UIElement element, int ms = 180, Action? completed = null, double from = 1)
    {
        if (element is null) return;
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(from, 0, Ms(ms))
        {
            EasingFunction = EaseIn,
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            completed?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// 入场：透明度 + 位移 + 轻微缩放（可选）。<paramref name="translate"/> / <paramref name="scale"/>
    /// 由 XAML 提供，方法只负责从偏移量回到 0 / 1。
    /// </summary>
    public static void Reveal(
        UIElement element,
        TranslateTransform? translate = null,
        double dx = 0,
        double dy = 18,
        int ms = 560,
        int delay = 0,
        double fromOpacity = 0,
        ScaleTransform? scale = null,
        double fromScale = 1.0)
    {
        if (element is null) return;
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            Reset(translate);
            if (scale is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
            }
            return;
        }

        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromOpacity, 1, Ms(ms))
        {
            BeginTime = Ms(delay),
            EasingFunction = EaseOutSoft,
            FillBehavior = FillBehavior.Stop,
        });

        if (translate is not null)
        {
            translate.X = 0;
            translate.Y = 0;
            var easing = EaseOut;
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, Ms(ms))
            {
                BeginTime = Ms(delay),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Ms(ms))
            {
                BeginTime = Ms(delay),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        }

        if (scale is not null)
        {
            scale.ScaleX = scale.ScaleY = 1;
            var easing = EaseOut;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, Ms(ms))
            {
                BeginTime = Ms(delay),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, Ms(ms))
            {
                BeginTime = Ms(delay),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    /// <summary>
    /// 把某一列从 <paramref name="from"/> 动到 <paramref name="to"/>（侧栏折叠 / 展开用）。
    /// 结束后把基础值设成终点并摘掉动画 —— 与其它方法同一条规矩：属性不能被动画持有。
    /// </summary>
    public static void AnimateWidth(ColumnDefinition column, double from, double to, int ms = 240, Action? completed = null)
    {
        if (column is null) return;

        if (!Enabled)
        {
            column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            column.Width = new GridLength(to);
            completed?.Invoke();
            return;
        }

        column.Width = new GridLength(to);   // 基础值先落到终点，动画只是"过程"
        var animation = new GridLengthAnimation
        {
            From = new GridLength(from),
            To = new GridLength(to),
            EasingFunction = EaseInOut,
            Duration = Ms(ms),
        };
        animation.Completed += (_, _) =>
        {
            column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            column.Width = new GridLength(to);
            completed?.Invoke();
        };
        column.BeginAnimation(ColumnDefinition.WidthProperty, animation);
    }

    /// <summary>把位移复位到 0（不做动画）。</summary>
    public static void Reset(TranslateTransform? translate)
    {
        if (translate is null) return;
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = 0;
        translate.Y = 0;
    }

    /// <summary>从当前值弹到某个缩放（用于点击/强调的微交互）。结束后回到 1。</summary>
    public static void Pulse(ScaleTransform? scale, double peak = 1.06, int ms = 220)
    {
        if (scale is null || !Enabled) return;
        scale.ScaleX = scale.ScaleY = 1;
        var animation = new DoubleAnimation(1, peak, Ms(ms / 2))
        {
            AutoReverse = true,
            EasingFunction = EaseOutSoft,
            FillBehavior = FillBehavior.Stop,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>绕中心匀速旋转（加载指示器）。<paramref name="run"/> = false 时停止并归零。</summary>
    public static void Spin(RotateTransform? rotate, bool run, int periodMs = 1100)
    {
        if (rotate is null) return;
        if (!run || !Enabled)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
            return;
        }

        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Ms(periodMs))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = null,
        });
    }

    /// <summary>呼吸（透明度来回，用于"等待中"的光晕）。</summary>
    public static void Breathe(UIElement element, double low = 0.35, double high = 0.9, int periodMs = 2600)
    {
        if (element is null) return;
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = high;
            return;
        }

        element.Opacity = high;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(low, high, Ms(periodMs))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = EaseInOut,
        });
    }

    /// <summary>停掉某个元素上的透明度动画（随后可以自由赋值）。</summary>
    public static void Stop(UIElement? element)
    {
        try { element?.BeginAnimation(UIElement.OpacityProperty, null); } catch { }
    }

    /// <summary>根据主题强制刷新资源引用（切主题后用，颜色立即跟着变）。</summary>
    public static void Invalidate(FrameworkElement? element)
    {
        try { element?.InvalidateVisual(); } catch { }
    }
}
