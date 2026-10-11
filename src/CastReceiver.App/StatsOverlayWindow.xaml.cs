using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CastReceiver.App;

/// <summary>
/// 画面右上角的通讯状态浮层。
/// 独立顶层小窗口 + 点击穿透，因此能浮在视频（HwndHost）之上，也不会抢走鼠标事件。
/// 展示内容：实时比（画面推进速度 / 真实时间）、流参数、停滞次数——用来判断"通讯是否健康"。
/// </summary>
public partial class StatsOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public StatsOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(handle, GwlExStyle);
                SetWindowLong(handle, GwlExStyle, ex | WsExTransparent | WsExNoActivate | WsExToolWindow);
            }
            catch { }
        };
    }

    public void Update(string dot, string summary, string detail)
    {
        try
        {
            Dot.Fill = dot switch
            {
                "ok" => (System.Windows.Media.Brush)FindResource("B.Ok"),
                "warn" => (System.Windows.Media.Brush)FindResource("B.Warn"),
                "bad" => (System.Windows.Media.Brush)FindResource("B.Bad"),
                _ => (System.Windows.Media.Brush)FindResource("B.TextFaint"),
            };
            if (TxtLine.Text != summary) TxtLine.Text = summary;
            if (TxtDetail.Text != detail) TxtDetail.Text = detail;
        }
        catch { }
    }

    /// <summary>
    /// 更新码率 / 滞后两条曲线。
    /// 采样点少于 2 个时不显示图表 —— 一个点画不出趋势，显示一条竖线只会让人困惑。
    /// </summary>
    public void UpdateCharts(IReadOnlyList<double> kbps, IReadOnlyList<double> lagMs,
                             string kbpsText, string lagText)
    {
        try
        {
            bool show = kbps.Count >= 2;
            ChartArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                return;
            }

            if (TxtKbpsValue.Text != kbpsText) TxtKbpsValue.Text = kbpsText;
            if (TxtLagValue.Text != lagText) TxtLagValue.Text = lagText;

            DrawSeries(ChartKbps, LineKbps, AreaKbps, kbps);
            DrawSeries(ChartLag, LineLag, AreaLag, lagMs);
        }
        catch { }
    }

    /// <summary>把一个数值序列画成折线 + 面积。刻度按各自序列的最大值自适应。</summary>
    private static void DrawSeries(Canvas canvas, Polyline line, Path area, IReadOnlyList<double> values)
    {
        double width = canvas.Width, height = canvas.Height;
        if (values.Count < 2 || width <= 1 || height <= 1)
        {
            return;
        }

        double max = 1;
        foreach (double value in values)
        {
            if (value > max) max = value;
        }
        max *= 1.15;                       // 顶部留余量，峰值不贴边

        var points = new PointCollection(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double x = width * i / (values.Count - 1);
            double y = height - 1 - Math.Clamp(values[i] / max, 0, 1) * (height - 3);
            points.Add(new Point(x, y));
        }

        line.Points = points;

        var figure = new PathFigure
        {
            StartPoint = new Point(points[0].X, height),
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new PolyLineSegment(points, false));
        figure.Segments.Add(new LineSegment(new Point(points[^1].X, height), false));
        area.Data = new PathGeometry(new[] { figure });
    }
}