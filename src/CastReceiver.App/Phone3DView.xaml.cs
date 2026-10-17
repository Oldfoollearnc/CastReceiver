using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace CastReceiver.App;

/// <summary>
/// 待机页的 3D 手机（纯 WPF Viewport3D，无外部模型文件）。
///
/// 设计约束（按"简约、不要数字大屏科技感"定的）：
///   · 石墨灰机身 + 很弱的右侧轮廓光，屏幕上没有任何彩色；
///   · 圆角机身/圆角屏幕是脚本生成的网格，不是拿方块硬拼的；
///   · 只在待机时以 30fps 缓慢自转，鼠标移动只加 ±12° 的视差；
///   · 切到"连接中 / 投屏中"就停表，不占 UI 线程。
/// </summary>
public partial class Phone3DView : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _running;
    private double _time;
    private double _pointerX;
    private double _pointerY;
    private double _angleY = -24;
    private double _angleX = 9;

    public Phone3DView()
    {
        InitializeComponent();
        BuildModel();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) => { if (IsVisible) Start(); };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Stop(); };
    }

    /// <summary>开始自转（待机页显示时调用）。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Start();
    }

    /// <summary>停止自转（连接中/投屏中调用，省 UI 线程）。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
    }

    /// <summary>鼠标在舞台里移动时给一点视差；<paramref name="nx"/> / <paramref name="ny"/> 取 -1…1。</summary>
    public void SetPointer(double nx, double ny)
    {
        _pointerX = Math.Clamp(nx, -1, 1);
        _pointerY = Math.Clamp(ny, -1, 1);
    }

    public void ResetPointer() => _pointerX = _pointerY = 0;

    /// <summary>入场：从 0.92 倍回到 1 倍（只动 ScaleTransform3D，不重排）。</summary>
    public void PlayEntrance()
    {
        if (!Motion.Enabled)
        {
            ModelScale.ScaleX = ModelScale.ScaleY = ModelScale.ScaleZ = 1;
            return;
        }

        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        ModelScale.ScaleX = ModelScale.ScaleY = ModelScale.ScaleZ = 1;
        ModelScale.BeginAnimation(ScaleTransform3D.ScaleXProperty, animation);
        ModelScale.BeginAnimation(ScaleTransform3D.ScaleYProperty, animation);
        ModelScale.BeginAnimation(ScaleTransform3D.ScaleZProperty, animation);
    }

    private void OnTick()
    {
        _time += 0.033;

        // 很慢的自转 + 很轻的视差；0.085 的插值让鼠标动完是"跟过去"而不是"跳过去"
        double idleY = Math.Sin(_time * 0.40) * 6.0;
        double targetY = -24 + idleY + _pointerX * 12;
        double targetX = 9 - _pointerY * 7;
        _angleY += (targetY - _angleY) * 0.085;
        _angleX += (targetX - _angleX) * 0.085;

        if (SpinY.Rotation is AxisAngleRotation3D spin) spin.Angle = _angleY;
        if (TiltX.Rotation is AxisAngleRotation3D tilt) tilt.Angle = _angleX;

        // 轻微上下浮动，避免"立正站着"的僵硬感
        ModelFloat.OffsetY = Math.Sin(_time * 0.75) * 0.010;
    }

    // ---------------------------------------------------------------- 建模

    private void BuildModel()
    {
        const double w = 0.70;
        const double h = 1.46;
        const double d = 0.070;
        const double r = 0.115;

        Material body = Material(
            diffuse: Color.FromRgb(0x2A, 0x2D, 0x33),
            specular: Color.FromRgb(0x5A, 0x62, 0x6F),
            power: 40);

        Material back = Material(
            diffuse: Color.FromRgb(0x16, 0x18, 0x1C),
            specular: Color.FromRgb(0x2E, 0x33, 0x3B),
            power: 22);

        Material screen = Material(
            diffuse: Color.FromRgb(0x05, 0x06, 0x08),
            emissive: Color.FromRgb(0x0A, 0x0B, 0x0E),
            specular: Color.FromRgb(0x3A, 0x41, 0x4C),
            power: 120);

        Material cameraBar = Material(
            diffuse: Color.FromRgb(0x0D, 0x0F, 0x12),
            specular: Color.FromRgb(0x28, 0x2C, 0x33),
            power: 50);

        Material buttons = Material(
            diffuse: Color.FromRgb(0x34, 0x38, 0x3F),
            specular: Color.FromRgb(0x55, 0x5C, 0x67),
            power: 36);

        var group = new Model3DGroup
        {
            Children =
            {
                // 背板：比机身稍小、往后错 2mm，侧面看起来有"中框 + 背板"的层次
                new GeometryModel3D(RoundedSlab(w - 0.012, h - 0.012, 0.006, r - 0.006, 10, -d / 2 - 0.003), back),
                new GeometryModel3D(RoundedSlab(w, h, d, r, 12), body),
                new GeometryModel3D(RoundedSlab(w - 0.075, h - 0.10, 0.004, 0.070, 10, d / 2 + 0.0015), screen),
                new GeometryModel3D(RoundedSlab(0.145, 0.032, 0.003, 0.016, 8, d / 2 + 0.0025, y: 0.655), cameraBar),
                new GeometryModel3D(Box(0.011, 0.20, 0.028, w / 2 + 0.004, 0.32, 0), buttons),
                new GeometryModel3D(Box(0.011, 0.12, 0.028, w / 2 + 0.004, 0.08, 0), buttons),
                new GeometryModel3D(Box(0.011, 0.09, 0.028, -w / 2 - 0.004, 0.20, 0), buttons),
            },
        };

        ModelRoot.Content = group;
    }

    private static Material Material(Color diffuse, Color specular, double power, Color? emissive = null)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(diffuse)));
        if (emissive is { } e)
        {
            group.Children.Add(new EmissiveMaterial(new SolidColorBrush(e)));
        }
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(specular), power));
        return group;
    }

    /// <summary>
    /// 圆角板：把"圆角矩形"沿 Z 挤出成实体。
    /// 前后两面用中心扇形三角化，侧面按外法线逐段生成 —— WPF 才会正确打光，圆角也才是圆角。
    /// </summary>
    private static MeshGeometry3D RoundedSlab(double w, double h, double d, double r, int segmentsPerCorner,
        double zOffset = 0, double y = 0)
    {
        double x0 = -w / 2, x1 = w / 2;
        double y0 = y - h / 2, y1 = y + h / 2;
        double radius = Math.Max(0.001, Math.Min(r, Math.Min(w, h) / 2 - 0.001));
        int seg = Math.Max(2, segmentsPerCorner);

        var perimeter = new List<(double X, double Y, double NX, double NY)>(seg * 4 + 4);
        // 四个圆角，按逆时针（右→上→左→下）生成；直边由相邻圆角的端点自然连接
        AddCorner(perimeter, x1 - radius, y0 + radius, -90, 0, radius, seg);
        AddCorner(perimeter, x1 - radius, y1 - radius, 0, 90, radius, seg);
        AddCorner(perimeter, x0 + radius, y1 - radius, 90, 180, radius, seg);
        AddCorner(perimeter, x0 + radius, y0 + radius, 180, 270, radius, seg);

        var mesh = new MeshGeometry3D();
        double zf = zOffset + d / 2;
        double zb = zOffset - d / 2;

        // 正面（+Z）：中心扇形
        int centerFront = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, y, zf));
        mesh.Normals.Add(new Vector3D(0, 0, 1));
        foreach (var p in perimeter)
        {
            mesh.Positions.Add(new Point3D(p.X, p.Y, zf));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
        }
        for (int i = 0; i < perimeter.Count; i++)
        {
            int a = centerFront + 1 + i;
            int b = centerFront + 1 + (i + 1) % perimeter.Count;
            mesh.TriangleIndices.Add(centerFront);
            mesh.TriangleIndices.Add(a);
            mesh.TriangleIndices.Add(b);
        }

        // 背面（-Z）：反绕序
        int centerBack = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, y, zb));
        mesh.Normals.Add(new Vector3D(0, 0, -1));
        foreach (var p in perimeter)
        {
            mesh.Positions.Add(new Point3D(p.X, p.Y, zb));
            mesh.Normals.Add(new Vector3D(0, 0, -1));
        }
        for (int i = 0; i < perimeter.Count; i++)
        {
            int a = centerBack + 1 + i;
            int b = centerBack + 1 + (i + 1) % perimeter.Count;
            mesh.TriangleIndices.Add(centerBack);
            mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a);
        }

        // 侧面：每段一个四边形（正面点 → 背面点 → 背面下一点 → 正线下一点）
        for (int i = 0; i < perimeter.Count; i++)
        {
            var p = perimeter[i];
            var q = perimeter[(i + 1) % perimeter.Count];
            var n = new Vector3D(p.NX, p.NY, 0);
            int baseIndex = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(p.X, p.Y, zf));
            mesh.Positions.Add(new Point3D(p.X, p.Y, zb));
            mesh.Positions.Add(new Point3D(q.X, q.Y, zb));
            mesh.Positions.Add(new Point3D(q.X, q.Y, zf));
            for (int k = 0; k < 4; k++) mesh.Normals.Add(n);
            mesh.TriangleIndices.Add(baseIndex);
            mesh.TriangleIndices.Add(baseIndex + 1);
            mesh.TriangleIndices.Add(baseIndex + 2);
            mesh.TriangleIndices.Add(baseIndex);
            mesh.TriangleIndices.Add(baseIndex + 2);
            mesh.TriangleIndices.Add(baseIndex + 3);
        }

        return mesh;
    }

    /// <summary>生成一个圆角的圆弧点（角度、法线都按外法线算）。</summary>
    private static void AddCorner(List<(double X, double Y, double NX, double NY)> points,
        double cx, double cy, double startDeg, double endDeg, double radius, int segments)
    {
        for (int i = 0; i <= segments; i++)
        {
            double t = (double)i / segments;
            double deg = startDeg + (endDeg - startDeg) * t;
            double rad = deg * Math.PI / 180.0;
            double nx = Math.Cos(rad);
            double ny = Math.Sin(rad);
            points.Add((cx + nx * radius, cy + ny * radius, nx, ny));
        }
    }

    /// <summary>轴对齐的盒子（6 个面 × 4 顶点，带法线）。</summary>
    private static MeshGeometry3D Box(double w, double h, double d, double cx, double cy, double cz)
    {
        double x0 = cx - w / 2, x1 = cx + w / 2;
        double y0 = cy - h / 2, y1 = cy + h / 2;
        double z0 = cz - d / 2, z1 = cz + d / 2;
        var mesh = new MeshGeometry3D();

        AddFace(mesh, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(0, 0, 1));
        AddFace(mesh, new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(0, 0, -1));
        AddFace(mesh, new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(-1, 0, 0));
        AddFace(mesh, new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(1, 0, 0));
        AddFace(mesh, new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), new(0, 1, 0));
        AddFace(mesh, new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), new(0, -1, 0));

        return mesh;
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal)
    {
        int i = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);
        for (int k = 0; k < 4; k++) mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(i);
        mesh.TriangleIndices.Add(i + 1);
        mesh.TriangleIndices.Add(i + 2);
        mesh.TriangleIndices.Add(i);
        mesh.TriangleIndices.Add(i + 2);
        mesh.TriangleIndices.Add(i + 3);
    }
}
