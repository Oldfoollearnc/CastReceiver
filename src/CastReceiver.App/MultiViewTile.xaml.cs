using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CastReceiver.App;

/// <summary>
/// 多设备并排的一格（③）＋「点一下放大这一路」（④）。
///
/// 一格同时是两种角色，靠 <see cref="SetFocused"/> 切换，**不会换父节点**：
/// <list type="bullet">
/// <item>格子态：顶部标题栏 + 小画面，排在主画面右侧一列；</item>
/// <item>主画面态：标题栏收起、边框去掉、占满整个画面区。</item>
/// </list>
/// 为什么不换父节点：HwndHost 一旦被挪到别的父节点上，子窗口会被销毁重建，
/// 画面必须重新挂一遍 —— 小窗那次"一片黑"就是这么来的（见 docs/升级-实现进度.md 三·1）。
/// 这里只改 Grid 的行列与可见性，宿主窗口自始至终不重建。
/// </summary>
public partial class MultiViewTile : UserControl
{
    /// <summary>标题栏高度（主画面态收起，格子态展开）。</summary>
    public const double HeaderHeight = 26;

    /// <summary>格子之间的间距。</summary>
    public const double Gap = 8;

    /// <summary>用户要求把这一路放大（点击格子）。参数是会话 id。</summary>
    public event Action<string>? FocusRequested;

    private bool _focused;

    public MultiViewTile(string sessionId)
    {
        SessionId = sessionId;
        InitializeComponent();
    }

    /// <summary>这一格是哪一路会话。</summary>
    public string SessionId { get; }

    /// <summary>这一格的画面宿主（由主窗口负责 AttachSource）。</summary>
    public Interop.CompositionVideoHost Host => TileHost;

    /// <summary>当前是不是"被放大成主画面"的那一格。</summary>
    public bool IsMain => _focused;

    /// <summary>
    /// 切换角色。格子态需要给出画面区的像素高度（按这一路画面的宽高比算好），
    /// 主画面态直接拉伸填满，不用设尺寸。
    /// </summary>
    public void SetFocused(bool focused, double videoHeight = 0)
    {
        _focused = focused;
        if (focused)
        {
            Chrome.BorderThickness = new Thickness(0);
            Chrome.CornerRadius = new CornerRadius(0);
            Chrome.Cursor = Cursors.Arrow;
            Chrome.Margin = new Thickness(0);
            Chrome.HorizontalAlignment = HorizontalAlignment.Stretch;
            Chrome.VerticalAlignment = VerticalAlignment.Stretch;
            Chrome.Width = double.NaN;
            Chrome.Height = double.NaN;
            HeaderRow.Height = new GridLength(0);
            Header.Visibility = Visibility.Collapsed;
            Frame.BorderThickness = new Thickness(0);
        }
        else
        {
            Chrome.BorderThickness = new Thickness(1);
            Chrome.CornerRadius = new CornerRadius(10);
            Chrome.Cursor = Cursors.Hand;
            Chrome.Margin = new Thickness(0, 0, 0, Gap);
            Chrome.HorizontalAlignment = HorizontalAlignment.Stretch;
            Chrome.VerticalAlignment = VerticalAlignment.Top;
            Chrome.Width = double.NaN;
            Chrome.Height = HeaderHeight + Math.Max(24, videoHeight);
            HeaderRow.Height = new GridLength(HeaderHeight);
            Header.Visibility = Visibility.Visible;
            Frame.BorderThickness = new Thickness(0, 1, 0, 0);
        }
    }

    /// <summary>标题栏内容：名字 + 「第几路/共几路」+ 状态点（有画面推进＝薄荷绿，停住＝灰）。</summary>
    public void SetState(string label, int index, int total, bool live)
    {
        if (TxtLabel.Text != label) TxtLabel.Text = label;
        string indexText = total > 1 ? $"{index}/{total}" : "";
        if (TxtIndex.Text != indexText) TxtIndex.Text = indexText;
        Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, live ? "B.Ok" : "B.TextFaint");
        ToolTip = live
            ? $"{label}（第 {index} 路，共 {total} 路）· 点一下放大这一路"
            : $"{label}（第 {index} 路，共 {total} 路）· 画面似乎停住了";
    }

    private void Chrome_Click(object sender, MouseButtonEventArgs e)
    {
        // 已经是被放大的那一格：不做事（否则会把点击吃掉，影响画面上的其它交互）
        if (_focused)
        {
            return;
        }

        e.Handled = true;
        FocusRequested?.Invoke(SessionId);
    }
}
