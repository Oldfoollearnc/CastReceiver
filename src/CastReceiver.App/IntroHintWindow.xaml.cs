using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace CastReceiver.App;

/// <summary>
/// 入场动画下方的"单击鼠标跳过"提示条。
///
/// 用户要的效果：当程序**全部加载完成**之后，在画面下方那两条黑边里浮出一行小字提醒可以跳过。
/// 之所以单独开一个顶层小窗，是因为入场画面是原生合成窗（HwndHost 那套空域规则），
/// 在它里面叠 WPF 文字盖不住；而单独的小窗只要**点击穿透**就不会抢走入场窗的"点一下跳过"。
/// </summary>
public partial class IntroHintWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;   // 点击穿透
    private const int WsExNoActivate = 0x08000000;    // 不抢焦点
    private const int WsExToolWindow = 0x00000080;    // 不出现在 Alt-Tab

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public IntroHintWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(ex | WsExTransparent | WsExNoActivate | WsExToolWindow));
            }
            catch { }
        };
    }

    /// <summary>文案（默认"单击鼠标跳过"）。</summary>
    public string Hint
    {
        get => TxtHint.Text;
        set => TxtHint.Text = value;
    }

    /// <summary>
    /// 摆到指定矩形（**物理像素**，入场窗的外框）的底部黑边里：水平居中、贴着下边缘往上 26 DIP。
    /// </summary>
    public void ShowFor(int x, int y, int width, int height, double dpiScale)
    {
        try
        {
            if (dpiScale <= 0) dpiScale = 1;
            double dipX = x / dpiScale, dipY = y / dpiScale;
            double dipW = width / dpiScale, dipH = height / dpiScale;

            Show();                       // 先显示才知道自己的实际宽高（SizeToContent）
            UpdateLayout();

            double w = ActualWidth > 0 ? ActualWidth : 160;
            double h = ActualHeight > 0 ? ActualHeight : 36;
            Left = dipX + (dipW - w) / 2;
            Top = dipY + dipH - h - 26;
            if (Top < dipY + 8) Top = dipY + dipH - h - 8;      // 窗口太矮时至少贴着下边

            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        catch { }
    }

    /// <summary>淡出并关掉（入场动画收场时叫它）。</summary>
    public void FadeOutAndClose()
    {
        try
        {
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) =>
            {
                try { BeginAnimation(OpacityProperty, null); Close(); } catch { }
            };
            BeginAnimation(OpacityProperty, fade);
        }
        catch { try { Close(); } catch { } }
    }
}
