using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CastReceiver.App;

/// <summary>
/// 悬浮控制条（独立顶层窗口）：窗口模式与全屏模式都会显示。
/// 之所以单独开窗：视频用 HwndHost 承载，WPF 元素无法叠在它上面（空域限制），
/// 独立顶层窗口才能浮在画面上，从而做到"画面下方常驻一条极简控制条 + 全屏时自动淡出"。
///
/// 极简：只放"别处没有"的动作 —— 截屏 / 录制 / 小窗。
/// 全屏 / 适配 / 缩放都已经长在舞台顶栏上，这里再放一份就是重复，全部去掉；
/// 唯一的例外是"退出全屏"：全屏时舞台顶栏是收起的，那时它不重复，是唯一看得见的出口。
/// 按钮一共就三四个，直接全摆出来，不再收进"更多"里多一次点击。
/// 不提供音量控制：镜像音频由系统渲染，调本地播放器音量对它无效。
/// </summary>
public partial class OverlayBarWindow : Window
{
    public event Action? ScreenshotRequested;
    public event Action? RecordRequested;
    public event Action? ExitFullscreenRequested;

    /// <summary>用户点了「小窗」：打开/关闭画中画小窗。</summary>
    public event Action? MiniWindowRequested;

    /// <summary>"退出全屏"那一格出现 / 消失改了窗口尺寸：宿主（主窗口）需要重新摆一次位置。</summary>
    public event Action? LayoutChanged;

    private bool _fullscreen;
    private bool _recording;

    public OverlayBarWindow() => InitializeComponent();

    public void SyncState(bool fullscreen, bool canCapture)
    {
        // 全屏那一格会增删，宽度就变了 —— 得让宿主重新居中摆一次
        bool widthChanged = _fullscreen != fullscreen;
        _fullscreen = fullscreen;

        BtnExitFullscreen.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FsDivider.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;

        BtnShot.IsEnabled = canCapture;
        BtnRec.IsEnabled = canCapture;
        BtnShot.ToolTip = canCapture ? "截取当前画面（Ctrl+Alt+S）" : "手机镜像开始后才能截屏";
        // 录制中不要被这句通用提示盖掉"停止录制"
        BtnRec.ToolTip = _recording
            ? "停止录制并保存"
            : canCapture ? "开始 / 停止录制（Ctrl+Alt+R）" : "手机镜像开始后才能录制";

        if (widthChanged)
        {
            UpdateLayout();
            LayoutChanged?.Invoke();
        }
    }

    /// <summary>列表之间的空白（卡片内边距）可拖动整条。</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreenRequested?.Invoke();
    private void BtnShot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();
    private void BtnRec_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();
    private void BtnMini_Click(object sender, RoutedEventArgs e) => MiniWindowRequested?.Invoke();

    /// <summary>小窗开/关时同步按钮的强调状态，让"当前是否画中画"一眼可见。</summary>
    public void SetMiniActive(bool active)
    {
        BtnMini.Foreground = Res(active ? "B.Accent" : "B.MediaTextDim");
        BtnMini.ToolTip = active
            ? "关闭小窗（画面回到主窗口）"
            : "小窗显示画面（主窗口最小化时也会自动出现）";
    }

    /// <summary>录制状态：按钮变红点并提示停止。</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        BtnRec.Content = recording ? "\uE71A" : "\uE7C8";
        BtnRec.Foreground = Res(recording ? "B.MediaBad" : "B.MediaTextDim");
        BtnRec.ToolTip = recording ? "停止录制并保存" : "开始录制（Ctrl+Alt+R），保存为 MP4";
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}