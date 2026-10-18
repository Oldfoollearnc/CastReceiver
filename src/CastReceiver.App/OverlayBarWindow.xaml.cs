using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// <summary>右键菜单里的「进入全屏」（条上只在全屏时出现"退出"，所以这里单独一个）。</summary>
    public event Action? EnterFullscreenRequested;

    /// <summary>用户点了「小窗」：打开/关闭画中画小窗。</summary>
    public event Action? MiniWindowRequested;

    /// <summary>用户点了喇叭：主窗口负责开关音量弹层。</summary>
    public event Action? VolumeRequested;

    /// <summary>在喇叭上滚滚轮：主窗口按这个增量调本机音量（±0.02）。</summary>
    public event Action<double>? VolumeStepRequested;

    /// <summary>右键菜单里的「设置…」。</summary>
    public event Action? SettingsRequested;

    /// <summary>右键菜单里的「静音 / 取消静音」：请求切到「非当前状态」。</summary>
    public event Action<bool>? MuteRequested;

    /// <summary>画面适配（全屏时的菜单）：Auto / Fit / Fill / Stretch / Native。</summary>
    public event Action<string>? FitModeRequested;

    /// <summary>缩放：+0.1 / -0.1。</summary>
    public event Action<double>? ZoomStepRequested;

    /// <summary>缩放复位到 100%。</summary>
    public event Action? ZoomResetRequested;

    /// <summary>"退出全屏"那一格出现 / 消失改了窗口尺寸：宿主（主窗口）需要重新摆一次位置。</summary>
    public event Action? LayoutChanged;

    /// <summary>鼠标停在条上 / 在条上按下：宿主据此把"自动消失"的计时重新开始。</summary>
    public event Action? Activity;

    private bool _fullscreen;
    private bool _recording;
    private bool _muted;
    private bool _miniActive;

    public OverlayBarWindow()
    {
        InitializeComponent();
        // 条一旦被鼠标碰过，就说明用户在用：把宿主的自动收起计时重置，
        // 否则鼠标停在按钮上不动，条也会在 3 秒后自己消失。
        MouseEnter += (_, _) => Activity?.Invoke();
        PreviewMouseMove += (_, _) => Activity?.Invoke();
        PreviewMouseDown += (_, _) => Activity?.Invoke();
    }

    public void SyncState(bool fullscreen, bool canCapture)
    {
        // 全屏那一格会增删，宽度就变了 —— 得让宿主重新居中摆一次
        bool widthChanged = _fullscreen != fullscreen;
        _fullscreen = fullscreen;

        BtnExitFullscreen.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FsDivider.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        BtnFit.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FitDivider.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;

        BtnShot.IsEnabled = canCapture;
        BtnRec.IsEnabled = canCapture;
        BtnShot.ToolTip = canCapture ? "截取当前画面（Ctrl+Alt+S）" : "手机镜像开始后才能截屏";
        // 录制中不要被这句通用提示盖掉"停止录制"
        BtnRec.ToolTip = _recording
            ? "停止录制并保存"
            : canCapture ? "开始 / 停止录制（Ctrl+Alt+R）" : "手机镜像开始后才能录制";

        // 小窗同样是"有画面才有意义"的动作：没有镜像时点开只会得到一块空窗。
        // 之前这个按钮漏了检查，看起来可点、点了没反应（用户反馈的就是这一条）。
        BtnMini.IsEnabled = canCapture;
        BtnMini.ToolTip = !canCapture
            ? "手机镜像开始后才能开小窗"
            : _miniActive
                ? "关闭小窗（画面回到主窗口）"
                : "小窗显示画面（画中画；最小化时是否自动弹出见 设置 → 画面）";

        if (widthChanged)
        {
            UpdateLayout();
            LayoutChanged?.Invoke();
        }

        // 右键菜单里的两条也跟着状态走（菜单是常用功能的"全集"，名字必须和条上一致）
        MiFullscreen.Header = fullscreen ? "退出全屏" : "进入全屏";
        MiShot.IsEnabled = canCapture;
        MiRec.IsEnabled = canCapture;
        MiMini.IsEnabled = canCapture;
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
    private void BtnVolume_Click(object sender, RoutedEventArgs e) => VolumeRequested?.Invoke();

    private void BtnVolume_Wheel(object sender, MouseWheelEventArgs e)
    {
        VolumeStepRequested?.Invoke(e.Delta > 0 ? 0.02 : -0.02);
        e.Handled = true;
    }

    // ---- 右键菜单：和条上是同一批动作，另外补上"设置" ----

    private void MiShot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();
    private void MiRec_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();
    private void MiMini_Click(object sender, RoutedEventArgs e) => MiniWindowRequested?.Invoke();
    private void MiMute_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(!_muted);
    private void MiFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreenRequested?.Invoke();
        else EnterFullscreenRequested?.Invoke();
    }
    private void MiSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // ---- 画面适配（全屏）----

    /// <summary>把当前适配状态同步到按钮提示与菜单勾选（勾选用 Tag 上的对勾字形表示）。</summary>
    public void SetFitState(string mode, double zoom)
    {
        string zoomText = $"{zoom * 100:F0}%";
        BtnFit.ToolTip = mode == "Auto"
            ? "画面适配：自动（不裁切） · 点开可改"
            : $"画面适配：{FitLabel(mode)} · {zoomText}";

        SetCheck(MiFitAuto, mode == "Auto");
        SetCheck(MiFitFit, mode == "Fit");
        SetCheck(MiFitFill, mode == "Fill");
        SetCheck(MiFitStretch, mode == "Stretch");
        SetCheck(MiFitNative, mode == "Native");
        MiZoomReset.Header = $"缩放复位（当前 {zoomText}）";
    }

    private static string FitLabel(string mode) => mode switch
    {
        "Fill" => "铺满",
        "Stretch" => "拉伸",
        "Native" => "原始",
        _ => "适应",
    };

    private static void SetCheck(MenuItem item, bool on)
    {
        item.Tag = on ? "\uE73E" : "";      // 模板把 Tag 当图标字形
        item.InputGestureText = on ? "当前" : "";
    }

    private void BtnFit_Click(object sender, RoutedEventArgs e)
    {
        ContextMenu? menu = BtnFit.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = BtnFit;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void MiFitMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        string mode = item.Name switch
        {
            "MiFitAuto" => "Auto",
            "MiFitFill" => "Fill",
            "MiFitStretch" => "Stretch",
            "MiFitNative" => "Native",
            _ => "Fit",
        };
        FitModeRequested?.Invoke(mode);
    }

    private void MiZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStepRequested?.Invoke(0.1);
    private void MiZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStepRequested?.Invoke(-0.1);
    private void MiZoomReset_Click(object sender, RoutedEventArgs e) => ZoomResetRequested?.Invoke();

    /// <summary>
    /// 电脑音量的图标与提示：静音时喇叭划掉并变黄，正常时提示当前百分比。
    /// 调的是**本机**音量 —— 镜像的声音是这台电脑在放。
    /// </summary>
    public void SetVolumeState(float? level, bool? muted)
    {
        bool isMuted = muted == true;
        _muted = isMuted;
        BtnVolume.Content = isMuted ? "\uE74F" : "\uE767";
        BtnVolume.Foreground = Res(isMuted ? "B.MediaWarn" : "B.MediaTextDim");
        BtnVolume.ToolTip = isMuted
            ? "电脑音量：已静音（点开滑杆或滚轮调节）"
            : level is { } value
                ? $"电脑音量 {(int)Math.Round(value * 100)}%（点开滑杆，滚轮直接调）"
                : "电脑音量（当前读不到）";
        AutomationProperties.SetName(BtnVolume, isMuted ? "电脑音量（已静音）" : "电脑音量");

        MiMute.Header = isMuted ? "取消静音" : "静音（电脑）";
        MiMute.Tag = isMuted ? "\uE74F" : "\uE767";
    }

    /// <summary>小窗开/关时同步按钮的强调状态，让"当前是否画中画"一眼可见。</summary>
    public void SetMiniActive(bool active)
    {
        _miniActive = active;
        BtnMini.Foreground = Res(active ? "B.Accent" : "B.MediaTextDim");
        BtnMini.ToolTip = active
            ? "关闭小窗（画面回到主窗口）"
            : "小窗显示画面（主窗口最小化时也会自动出现）";
        MiMini.Header = active ? "关闭小窗" : "小窗显示";
    }

    /// <summary>录制状态：按钮变红点并提示停止。</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        BtnRec.Content = recording ? "\uE71A" : "\uE7C8";
        BtnRec.Foreground = Res(recording ? "B.MediaBad" : "B.MediaTextDim");
        BtnRec.ToolTip = recording ? "停止录制并保存" : "开始录制（Ctrl+Alt+R），保存为 MP4";
        MiRec.Header = recording ? "停止录制并保存" : "开始录制";
        MiRec.Tag = recording ? "\uE71A" : "\uE7C8";
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}