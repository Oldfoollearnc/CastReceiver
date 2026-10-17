using System.Windows;
using System.Windows.Media.Animation;

namespace CastReceiver.App;

/// <summary>
/// 把 <see cref="ColumnDefinition.Width"/> 从 A 动到 B —— WPF 没有内置的 GridLength 动画。
///
/// 为什么值得破一次"只动 Opacity/Translate/Scale"的规矩（见 <see cref="Motion"/> 的说明）：
/// 侧栏折叠本身就是**布局**变化，用位移假装只会露出底下的空白；而这是一次性的交互
/// （240ms、单列、静态卡片），不是每帧都在跑的东西，掉帧风险可以接受。
/// </summary>
internal sealed class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public GridLength From { get; set; }
    public GridLength To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double progress = animationClock.CurrentProgress ?? 0;
        if (EasingFunction is not null) progress = EasingFunction.Ease(progress);

        // 只处理像素宽度：Auto / Star 参与插值没有意义，当成 0（侧栏一直是像素宽度）
        double from = From.IsAbsolute ? From.Value : 0;
        double to = To.IsAbsolute ? To.Value : 0;
        return new GridLength(Math.Max(0, from + (to - from) * progress), GridUnitType.Pixel);
    }
}