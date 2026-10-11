using System.Windows;
using System.Windows.Input;

namespace CastReceiver.App;

/// <summary>
/// 悬浮控制条（独立顶层窗口）：窗口模式与全屏模式都会显示。
/// 之所以单独开窗：视频用 HwndHost 承载，WPF 元素无法叠在它上面（空域限制），
/// 独立顶层窗口才能浮在画面上，从而做到"画面下方常驻一条控制条 + 全屏时自动淡出"。
/// 不提供音量控制：镜像音频由系统渲染，调本地播放器音量对它无效。
/// </summary>
public partial class OverlayBarWindow : Window
{
    public event Action? ToggleFullscreenRequested;
    public event Action? FitToggled;
    /// <summary>缩放增量（±0.1）；NaN 表示复位到 100%。</summary>
    public event Action<double>? ZoomChanged;
    public event Action? ScreenshotRequested;
    public event Action? RecordRequested;

    /// <summary>用户点了「小窗」：打开/关闭画中画小窗。</summary>
    public event Action? MiniWindowRequested;

    public OverlayBarWindow() => InitializeComponent();

    public void SyncState(bool fullscreen, string fitName, double zoom, bool canCapture = true)
    {
        BtnShot.IsEnabled = canCapture;
        BtnRec.IsEnabled = canCapture;
        BtnShot.ToolTip = canCapture ? "截取当前画面（Ctrl+Alt+S）" : "手机镜像开始后才能截屏";
        BtnRec.ToolTip = canCapture ? "开始/停止录制（Ctrl+Alt+R），保存为 MP4" : "手机镜像开始后才能录制";
        BtnFullscreen.Content = fullscreen ? "退出全屏" : "全屏";
        BtnFit.Content = fitName;
        TxtZoom.Text = $"{zoom * 100:F0}%";
        // 到边界只禁用对应方向，另一个方向必须始终可用（否则"缩了就不能放大"）
        BtnZoomOut.IsEnabled = zoom > 0.5 + 0.001;
        BtnZoomIn.IsEnabled = zoom < 2.0 - 0.001;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreenRequested?.Invoke();
    private void BtnFit_Click(object sender, RoutedEventArgs e) => FitToggled?.Invoke();
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomChanged?.Invoke(-0.1);
    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomChanged?.Invoke(0.1);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => ZoomChanged?.Invoke(double.NaN);
    private void BtnShot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();
    private void BtnRec_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();
    private void BtnMini_Click(object sender, RoutedEventArgs e) => MiniWindowRequested?.Invoke();

    /// <summary>小窗开/关时同步按钮的强调状态，让"当前是否画中画"一眼可见。</summary>
    public void SetMiniActive(bool active)
    {
        BtnMini.Foreground = active
            ? (System.Windows.Media.Brush)FindResource("B.Accent")
            : (System.Windows.Media.Brush)FindResource("B.TextDim");
        BtnMini.ToolTip = active
            ? "关闭小窗（画面回到主窗口）"
            : "小窗显示画面（主窗口最小化时也会自动出现）";
    }

    /// <summary>录制状态：按钮变红点并提示停止。</summary>
    public void SetRecording(bool recording)
    {
        BtnRec.Content = recording ? "\uE71A" : "\uE7C8";
        BtnRec.Foreground = recording
            ? (System.Windows.Media.Brush)FindResource("B.Bad")
            : (System.Windows.Media.Brush)FindResource("B.TextDim");
        BtnRec.ToolTip = recording ? "停止录制并保存" : "开始录制（Ctrl+Alt+R），保存为 MP4";
    }
}