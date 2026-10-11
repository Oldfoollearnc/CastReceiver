using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CastReceiver.App;

/// <summary>
/// 首次启动的使用引导：4 步动画，自动播放、可跳过；也能从设置里重新打开。
/// 目标是把"怎么用"讲清楚，而不是堆文字——每步只有一句。
/// </summary>
public partial class TutorialWindow : Window
{
    private const double StepSeconds = 4.0;

    private readonly (string Title, string Body, string Icon)[] _steps;
    private readonly DispatcherTimer _timer;
    private int _index;
    private bool _finished;

    public TutorialWindow(string deviceName)
    {
        InitializeComponent();

        _steps = new[]
        {
            // WiFi：三段弧 + 底部圆点（线性图标，一眼能认出）
            ("手机与本机连同一个 Wi-Fi",
             "不需要数据线，也不用改路由器设置。",
             "M2.5,8.5 A13,13 0 0 1 21.5,8.5 M6,12.2 A8,8 0 0 1 18,12.2 M9.4,15.8 A3.4,3.4 0 0 1 14.6,15.8 M12,19.6 m-1.1,0 a1.1,1.1 0 1 0 2.2,0 a1.1,1.1 0 1 0 -2.2,0"),
            // 手机
            ("手机下拉快捷面板，点「无线投屏」",
             "不同品牌叫法不同：无线投屏 / 投射屏幕 / 多屏互动 / 无线显示。",
             "M8,2.5 H16 A1.8,1.8 0 0 1 17.8,4.3 V19.7 A1.8,1.8 0 0 1 16,21.5 H8 A1.8,1.8 0 0 1 6.2,19.7 V4.3 A1.8,1.8 0 0 1 8,2.5 Z M10,5 H14 M10.8,19 H13.2"),
            // 设备列表
            ("在设备列表里选本机名称",
             "当前名称可在设置里修改。",
             "M3.5,3.5 H20.5 A1,1 0 0 1 21.5,4.5 V16 A1,1 0 0 1 20.5,17 H3.5 A1,1 0 0 1 2.5,16 V4.5 A1,1 0 0 1 3.5,3.5 Z M8,20.5 H16 M12,17 V20.5"),
            // 全屏角标
            ("画面底部的悬浮条随时可用",
             "全屏、画面适配（适应/铺满/拉伸/原始）与缩放都在这里；全屏时把鼠标移到屏幕底部就会出现。",
             "M3,8 V3 H8 M16,3 H21 V8 M21,16 V21 H16 M8,21 H3 V16"),
        };
        BuildDots();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StepSeconds) };
        _timer.Tick += (_, _) => Advance();
        ShowStep(0);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0), Tag = i };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "B.Line");
            Dots.Children.Add(dot);
        }
    }

    private void ShowStep(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Length - 1);
        var (title, body, icon) = _steps[_index];

        TxtStepTitle.Text = title;
        TxtStepBody.Text = body;
        StepIcon.Data = Geometry.Parse(icon);
        BtnNext.Content = _index == _steps.Length - 1 ? "开始使用" : "下一步";

        for (int i = 0; i < Dots.Children.Count; i++)
            if (Dots.Children[i] is Ellipse e)
                e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i == _index ? "B.Text" : "B.Line");

        // 内容淡入 + 轻微上移，进度条按停留时间走满
        StepText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        StepShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)));
        var widthAnimation = new DoubleAnimation(0, 497, TimeSpan.FromSeconds(StepSeconds));
        ProgressBarFill.BeginAnimation(WidthProperty, widthAnimation);
    }

    private void Advance()
    {
        if (_index >= _steps.Length - 1) { Finish(); return; }
        ShowStep(_index + 1);
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e) => Advance();
    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}