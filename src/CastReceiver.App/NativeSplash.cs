using System.Runtime.InteropServices;
using System.Text;

namespace CastReceiver.App;

/// <summary>
/// 原生 Win32 启动动画窗口。
/// 为什么不用 WPF 窗口：实测「解析 XAML 约 700ms + 显示第一个 WPF 窗口约 950ms」是固定开销，
/// 与窗口复杂度无关；而直接用 Win32 + GDI 创建窗口只需几十毫秒，
/// 这样用户双击图标后几乎立刻就能看到"正在启动"的动画。
/// </summary>
internal static class NativeSplash
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int SwShow = 5;
    private const int WmPaint = 0x000F;
    private const int WmTimer = 0x0113;
    private const int WmDestroy = 0x0002;
    private const int WmLButtonDown = 0x0201;
    private const uint DtCenter = 0x00000001, DtVCenter = 0x00000004, DtSingleLine = 0x00000020;

    // 窗口尺寸不再写死：它现在是一整块全屏黑幕（尺寸在 Show 里按虚拟屏幕算）
    private const int Width = 0, Height = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr HWnd; public uint Message; public IntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style; public IntPtr WndProc; public int ClsExtra; public int WndExtra;
        public IntPtr Instance; public IntPtr Icon; public IntPtr Cursor; public IntPtr Background;
        public string? MenuName; public string? ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int ew, int eh);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool AngleArc(IntPtr hdc, int x, int y, int radius, float startAngle, float sweepAngle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc; public int Erase; public Rect Paint; public int Restore; public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    private static IntPtr _hwnd;
    private static int _angle;
    private static int _bgColor = 0x000E0C0B;    // #0B0C0E
    private static int _fgColor = 0x00EFEDEC;    // #ECEDEF
    private static int _dimColor = 0x00AEA6A0;   // #A0A6AE
    private static int _lineColor = 0x00302A26;  // #262A30
    private static int _accentColor = 0x0097DC3D; // #3DDC97（薄荷绿，强调色默认值）
    private static string _step = "正在启动…";

    // ---- 品牌开屏动画（IntroSource = "brand"）----
    private static bool _brand;
    private static long _brandStartMs;
    private static Thread? _brandThread;
    private static volatile bool _brandStop;
    private const int WmAppStop = 0x8000 + 1;
    private static int _brandDurationMs = 3200;
    private static IntPtr _fTitle, _fSub, _fHint;

    /// <summary>
    /// 品牌开场是不是还在放（App 靠它决定"别在首帧就关掉闪屏"）。
    /// 用 _brand 而不是 _hwnd：窗口是在独立线程上创建的，主窗口首帧渲染时
    /// 它可能还没建好 —— 那一瞬间也必须算"还在放"。
    /// </summary>
    public static bool BrandRunning => _brand;

    // 黑幕的几何：给的就是主窗口的位置与大小（不再全屏）
    private static int _x = int.MinValue, _y = int.MinValue, _w, _h;
    private static WndProcDelegate? _procRef;   // 保住委托，避免被 GC

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static bool IsVisible => _hwnd != IntPtr.Zero;

    /// <summary>按主题配色显示启动画面（颜色为 #RRGGBB 字符串）。</summary>
    public static void Show(string step, string bg, string fg, string dim, string line,
        int x = int.MinValue, int y = int.MinValue, int w = 0, int h = 0, string accent = "#3DDC97")
    {
        _x = x; _y = y; _w = w; _h = h;
        _bgColor = ParseColor(bg, 0x0B0C0E);
        _fgColor = ParseColor(fg, 0xECEDEF);
        _dimColor = ParseColor(dim, 0xA0A6AE);
        _lineColor = ParseColor(line, 0x262A30);
        _accentColor = ParseColor(accent, 0x3DDC97);
        Show(step);
    }

    private static int ParseColor(string hex, int fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return fallback;
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            // COLORREF 是 0x00BBGGRR
            return (b << 16) | (g << 8) | r;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 品牌开场动画：不播任何视频，程序自己画（手机轮廓 + 名字 + 细线 + 标语，约 3.2 秒）。
    /// 默认就用它 —— 不依赖素材、不用解码 4K，观感统一；单击可跳过。
    /// </summary>
    public static void ShowBrandIntro(string bg, string fg, string dim, string line, string accent,
        int x = int.MinValue, int y = int.MinValue, int w = 0, int h = 0) 
    {
        _x = x; _y = y; _w = w; _h = h;
        _bgColor = ParseColor(bg, 0x0B0C0E);
        _fgColor = ParseColor(fg, 0xECEDEF);
        _dimColor = ParseColor(dim, 0xA0A6AE);
        _lineColor = ParseColor(line, 0x262A30);
        _accentColor = ParseColor(accent, 0x3DDC97);
        _brand = true;
        _brandStop = false;
        _brandStartMs = Environment.TickCount64;

        // 必须放在**独立线程**上：WPF 启动期间主线程被 XAML 解析/首帧渲染占住，
        // 挂在主线程上的窗口收不到 WM_TIMER（实测：3.2 秒里只画了 4 帧），
        // 动画看起来就是"卡住的一张图"。这里自己建窗、自己跑消息循环。
        var thread = new Thread(BrandThreadProc)
        {
            IsBackground = true,
            Name = "brand-intro",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _brandThread = thread;
        thread.Start();
    }

    /// <summary>品牌开场的独立消息循环（与 WPF 完全解耦）。</summary>
    private static void BrandThreadProc()
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            _procRef = WndProc;
            var wc = new WndClass
            {
                Style = 0,
                WndProc = Marshal.GetFunctionPointerForDelegate(_procRef),
                Instance = GetModuleHandle(null),
                ClassName = "CastReceiverBrandSplash",
            };
            RegisterClassW(ref wc);

            int x = _x, y = _y, w = _w, h = _h;
            if (w <= 0 || h <= 0)
            {
                x = GetSystemMetrics(76); y = GetSystemMetrics(77);
                w = GetSystemMetrics(78); h = GetSystemMetrics(79);
                if (w <= 0 || h <= 0) { x = 0; y = 0; w = GetSystemMetrics(0); h = GetSystemMetrics(1); }
            }

            hwnd = CreateWindowEx(WsExTopmost | WsExToolWindow, wc.ClassName!, "投屏Window",
                WsPopup, x, y, w, h, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { _brand = false; return; }
            _hwnd = hwnd;
            ShowWindow(hwnd, SwShow);
            UpdateWindow(hwnd);
            SetTimer(hwnd, IntPtr.Zero, 16, IntPtr.Zero);
            Bootstrap.Log("品牌开场动画已显示");

            while (!_brandStop)
            {
                if (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) <= 0) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("品牌开场线程异常：" + ex.Message);
        }
        finally
        {
            _brand = false;
            if (hwnd != IntPtr.Zero)
            {
                KillTimer(hwnd, IntPtr.Zero);
                DestroyWindow(hwnd);
            }
            if (_hwnd == hwnd) _hwnd = IntPtr.Zero;
            foreach (IntPtr font in new[] { _fTitle, _fSub, _fHint })
            {
                if (font != IntPtr.Zero) DeleteObject(font);
            }
            _fTitle = _fSub = _fHint = IntPtr.Zero;
            Bootstrap.Log("品牌开场动画已关闭");
        }
    }

    public static void Show(string step = "正在启动…")
    {
        _step = step;
        if (!_brand) _brandStartMs = Environment.TickCount64;
        try
        {
            _procRef = WndProc;
            var wc = new WndClass
            {
                Style = 0,
                WndProc = Marshal.GetFunctionPointerForDelegate(_procRef),
                Instance = GetModuleHandle(null),
                ClassName = "CastReceiverNativeSplash",
            };
            RegisterClassW(ref wc);

            // 一块纯黑幕，用来挡住 WPF 启动那 1.5 秒。
            // 几何＝**主窗口的位置与大小**（与主窗口严丝合缝地交接，不再是一整屏黑）。
            int screenX = _x, screenY = _y, screenW = _w, screenH = _h;
            if (screenW <= 0 || screenH <= 0)
            {
                screenX = GetSystemMetrics(76); screenY = GetSystemMetrics(77);
                screenW = GetSystemMetrics(78); screenH = GetSystemMetrics(79);
                if (screenW <= 0 || screenH <= 0)
                {
                    screenX = 0; screenY = 0;
                    screenW = GetSystemMetrics(0); screenH = GetSystemMetrics(1);
                }
            }

            _hwnd = CreateWindowEx(WsExTopmost | WsExToolWindow, wc.ClassName!, "投屏Window",
                WsPopup, screenX, screenY, screenW, screenH, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            ShowWindow(_hwnd, SwShow);
            UpdateWindow(_hwnd);
            // 品牌动画按 60fps 刷；普通闪屏 25fps 足够
            SetTimer(_hwnd, IntPtr.Zero, (uint)(_brand ? 16 : 40), IntPtr.Zero);
            Bootstrap.Log(_brand ? "品牌开场动画已显示" : "原生闪屏已显示");
        }
        catch (Exception ex) { Bootstrap.Log("原生闪屏失败：" + ex.Message); }
    }

    /// <summary>当前这一步的说明文字（入场动画的信息条直接显示它）。</summary>
    public static string CurrentStep => _step;

    public static void SetStep(string step)
    {
        _step = step;
        try { if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false); } catch { }
    }

    public static void Close()
    {
        try
        {
            if (_brand)
            {
                // 品牌窗口属于它自己的线程：只能发消息让它自己收场
                _brandStop = true;
                if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmAppStop, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            if (_hwnd == IntPtr.Zero) return;
            KillTimer(_hwnd, IntPtr.Zero);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            Bootstrap.Log("原生闪屏已关闭");
        }
        catch { }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTimer:
                if (_brand && Environment.TickCount64 - _brandStartMs >= _brandDurationMs)
                {
                    _brandStop = true;      // 演完就退场：跳出本线程的消息循环
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }
                _angle = (_angle + 24) % 360;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WmPaint:
                try { Paint(hwnd); }
                catch (Exception ex) { Bootstrap.Log("原生闪屏绘制异常：" + ex.Message); try { var hdc2 = BeginPaint(hwnd, out var ps2); EndPaint(hwnd, ref ps2); } catch { } }
                return IntPtr.Zero;
            case WmLButtonDown:
                // 品牌动画：单击跳过（急着用的人不该被开场拦着）
                if (_brand) { _brandStop = true; PostQuitMessage(0); return IntPtr.Zero; }
                return IntPtr.Zero;
            case WmAppStop:
                _brandStop = true;
                PostQuitMessage(0);
                return IntPtr.Zero;
            case WmDestroy:
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void Paint(IntPtr hwnd)
    {
        var ps = BeginPaint(hwnd, out var paint);
        IntPtr hdc = paint.Hdc;
        try
        {
            GetClientRect(hwnd, out var rect);

            IntPtr bg = CreateSolidBrush(_bgColor);
            FillRect(hdc, ref rect, bg);
            DeleteObject(bg);

            SetBkMode(hdc, 1 /*TRANSPARENT*/);
            if (_brand) PaintBrand(hdc, rect);
            else PaintStatic(hdc, rect);
        }
        catch (Exception ex) { Bootstrap.Log("启动黑幕绘制异常：" + ex.Message); }
        finally { EndPaint(hwnd, ref paint); }
    }

    /// <summary>没有开屏动画时的静态闪屏：名字 + 细线 + 一步进度文字。</summary>
    private static void PaintStatic(IntPtr hdc, Rect rect)
    {
        int cx = (rect.Left + rect.Right) / 2;
        int cy = (rect.Top + rect.Bottom) / 2;

        IntPtr titleFont = CreateFontW(-30, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE_QUALITY*/, 0, "Microsoft YaHei UI");
        IntPtr oldFont = titleFont != IntPtr.Zero ? SelectObject(hdc, titleFont) : IntPtr.Zero;
        SetTextColor(hdc, _fgColor);
        var titleRect = new Rect { Left = rect.Left, Top = cy - 54, Right = rect.Right, Bottom = cy - 8 };
        DrawTextW(hdc, "投屏Window", -1, ref titleRect, DtCenter | DtVCenter | DtSingleLine);

        // 标题下一条很短的薄荷绿细线：呼吸式宽度，暗示"在动"，但不是进度条也不是转圈
        int lineWidth = 26 + (int)(Math.Abs(Math.Sin(_angle * Math.PI / 180.0)) * 22);
        IntPtr pen = CreatePen(0, 2, _accentColor);
        IntPtr oldPen = pen != IntPtr.Zero ? SelectObject(hdc, pen) : IntPtr.Zero;
        MoveToEx(hdc, cx - lineWidth / 2, cy + 6, IntPtr.Zero);
        LineTo(hdc, cx + lineWidth / 2, cy + 6);
        if (oldPen != IntPtr.Zero) SelectObject(hdc, oldPen);
        if (pen != IntPtr.Zero) DeleteObject(pen);

        IntPtr stepFont = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        if (stepFont != IntPtr.Zero) SelectObject(hdc, stepFont);
        SetTextColor(hdc, _dimColor);
        var stepRect = new Rect { Left = rect.Left, Top = cy + 22, Right = rect.Right, Bottom = cy + 48 };
        DrawTextW(hdc, _step, -1, ref stepRect, DtCenter | DtVCenter | DtSingleLine);

        if (stepFont != IntPtr.Zero) DeleteObject(stepFont);
        if (oldFont != IntPtr.Zero) SelectObject(hdc, oldFont);
        if (titleFont != IntPtr.Zero) DeleteObject(titleFont);
    }

    /// <summary>
    /// 品牌开屏动画：一台立着的手机 + 品牌名 + 一条会走完的细线，约 3.2 秒演完自己退场。
    ///
    /// 全部用 GDI 画（不加载任何素材、不参与 WPF 首帧开销），但用的是"产品渲染图"的思路：
    /// 金属中框的纵向渐变、屏幕里的斜向反光、屏幕顶端正中的前摄开孔、机身右侧的按键，
    /// 加上背后一圈很淡的强调色光晕 —— 不再是之前那个 46×78 的线框小图标。
    /// </summary>
    private static void PaintBrand(IntPtr hdc, Rect rect)
    {
        double t = (Environment.TickCount64 - _brandStartMs) / 1000.0;
        double total = _brandDurationMs / 1000.0;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int cx = rect.Left + w / 2, cy = rect.Top + h / 2;

        double aPhone = Ease(t, 0.05, 0.72);
        double aTitle = Ease(t, 0.34, 1.02);
        double aSub = Ease(t, 0.72, 1.34);
        double aOut = 1 - Ease(t, total - 0.55, total - 0.05);
        if (aOut <= 0.001) return;

        EnsureBrandFonts();

        // 背景：极轻的纵向渐变，让整块画面有"空间"而不是一块死黑
        FillVerticalGradient(hdc, rect, Blend(_bgColor, 0xFFFFFF, 0.045), _bgColor);

        // 文字先画：此时 DC 还没有被任何裁剪区域动过，最稳。
        // （曾经放在手机之后，裁剪恢复一旦不彻底，标题下面的细线/标语就整段不画。）
        // ---- 尺寸：先给文字留出固定高度，再把"手机 + 文字"整体居中 ----
        // 窗口高度不一定是 800（上次的窗口可能更矮）：按 h 扣掉文字块，绝不把标题挤出窗外。
        // 文字锚在**窗口底部**：窗口高度不一定是 800（上次的窗口可能更矮），
        // 按"底部往上 200px"排，永远不会被挤出可视区。
        int ph = Math.Clamp(h - 300, 160, 430);
        int pw = (int)(ph * 0.496);
        int top = 36 + (int)((1 - aPhone) * 16);
        int left = cx - pw / 2;
        int radius = Math.Max(16, pw * 9 / 100);

        // ---- 背后的强调色光晕：14 层圆角矩形，越靠内越亮 ----
        int halo = (int)(pw * 0.30);
        for (int i = 14; i >= 1; i--)
        {
            int d = halo * i / 14;
            double k = 0.004 + 0.0026 * (14 - i);
            IntPtr glowBrush = CreateSolidBrush(Blend(_bgColor, _accentColor, k * aPhone));
            IntPtr glowRgn = CreateRoundRectRgn(left - d, top - d, left + pw + d, top + ph + d, radius * 2, radius * 2);
            int glowSave = SaveDC(hdc);
            SelectClipRgn(hdc, glowRgn);
            var glowRect = new Rect { Left = left - d, Top = top - d, Right = left + pw + d, Bottom = top + ph + d };
            FillRect(hdc, ref glowRect, glowBrush);
            RestoreDC(hdc, glowSave);
            DeleteObject(glowRgn);
            DeleteObject(glowBrush);
        }

        // ---- 机身：圆角矩形内做纵向金属渐变 ----
        IntPtr bodyRgn = CreateRoundRectRgn(left, top, left + pw, top + ph, radius * 2, radius * 2);
        int bodySave = SaveDC(hdc);
        SelectClipRgn(hdc, bodyRgn);
        var bodyRect = new Rect { Left = left, Top = top, Right = left + pw, Bottom = top + ph };
        FillVerticalGradient(hdc, bodyRect,
            Blend(_bgColor, Rgb(0x6A7079), 0.85 * aPhone),
            Blend(_bgColor, Rgb(0x0C0D11), 1.0 * aPhone));
        RestoreDC(hdc, bodySave);
        DeleteObject(bodyRgn);

        // 外框：一圈很细的亮边（顶部亮、底部暗，才像金属中框）
        DrawRoundRect(hdc, left, top, left + pw, top + ph, radius * 2, radius * 2,
            Blend(_bgColor, Rgb(0xC8CDD6), 0.42 * aPhone), 1);
        DrawRoundRect(hdc, left + 2, top + 2, left + pw - 2, top + ph - 2, (radius - 2) * 2, (radius - 2) * 2,
            Blend(_bgColor, 0xFFFFFF, 0.10 * aPhone), 1);

        // ---- 屏幕：深色玻璃 + 一道缓慢扫过的反光 ----
        int inset = Math.Max(9, pw * 60 / 1000);
        int sr = Math.Max(10, radius - 5);
        int sLeft = left + inset, sTop = top + inset, sRight = left + pw - inset, sBottom = top + ph - inset;
        IntPtr screenRgn = CreateRoundRectRgn(sLeft, sTop, sRight, sBottom, sr * 2, sr * 2);
        int screenSave = SaveDC(hdc);
        SelectClipRgn(hdc, screenRgn);
        var screenRect = new Rect { Left = sLeft, Top = sTop, Right = sRight, Bottom = sBottom };
        FillVerticalGradient(hdc, screenRect,
            Blend(_bgColor, Rgb(0x161922), aPhone),
            Blend(_bgColor, Rgb(0x050609), aPhone));

        // 斜向反光带：三片平行四边形，扫过一个来回，很淡（≈5% 白）
        if (t > 0.55)
        {
            double cycle = 2.4;
            double phase = ((t - 0.55) % cycle) / cycle;      // 0…1
            double travel = (sRight - sLeft + 220) * phase - 110;
            int bandW = Math.Max(26, pw / 6);
            for (int k = 0; k <= 0; k++)
            {
                int bx = sLeft + (int)travel + k * bandW / 2;
                int tilt = Math.Max(18, pw / 7);
                var pts = new[]
                {
                    new Point { X = bx, Y = sTop },
                    new Point { X = bx + bandW, Y = sTop },
                    new Point { X = bx + bandW + tilt, Y = sBottom },
                    new Point { X = bx + tilt, Y = sBottom },
                };
                IntPtr bandBrush = CreateSolidBrush(Blend(Rgb(0x0A0B10), 0xFFFFFF, 0.035 * aPhone));
                IntPtr oldBand = SelectObject(hdc, bandBrush);
                Polygon(hdc, pts, 4);
                SelectObject(hdc, oldBand);
                DeleteObject(bandBrush);
            }
        }
        RestoreDC(hdc, screenSave);
        DeleteObject(screenRgn);
        DrawRoundRect(hdc, sLeft, sTop, sRight, sBottom, sr * 2, sr * 2,
            Blend(_bgColor, 0x000000, 0.55 * aPhone), 1);

        // ---- 前摄开孔：屏幕顶端正中（与 3D 模型一致） ----
        int hole = Math.Max(3, pw / 40);
        int hy = sTop + hole + Math.Max(4, inset / 2);
        IntPtr holeBrush = CreateSolidBrush(Blend(Rgb(0x05060A), 0xFFFFFF, 0.0));
        IntPtr oldHole = SelectObject(hdc, holeBrush);
        Ellipse(hdc, cx - hole, hy - hole, cx + hole, hy + hole);
        SelectObject(hdc, oldHole);
        DeleteObject(holeBrush);
        DrawEllipse(hdc, cx - hole, hy - hole, cx + hole, hy + hole,
            Blend(Rgb(0x05060A), 0xFFFFFF, 0.16 * aPhone), 1);

        // ---- 机身右侧的两颗按键 ----
        int btnW = Math.Max(2, pw / 80);
        IntPtr btnBrush = CreateSolidBrush(Blend(_bgColor, Rgb(0x6E747E), 0.55 * aPhone));
        IntPtr oldBtn = SelectObject(hdc, btnBrush);
        RoundRect(hdc, left + pw - 1, top + (int)(ph * 0.40), left + pw + btnW, top + (int)(ph * 0.52), btnW, btnW);
        RoundRect(hdc, left + pw - 1, top + (int)(ph * 0.55), left + pw + btnW, top + (int)(ph * 0.63), btnW, btnW);
        SelectObject(hdc, oldBtn);
        DeleteObject(btnBrush);

        // ---- 品牌名 / 标语 / 进度细线 ----
        int titleTop = h - 196;
        var titleRect = new Rect
        {
            Left = rect.Left,
            Top = titleTop + (int)((1 - aTitle) * 12),
            Right = rect.Right,
            Bottom = titleTop + 46 + (int)((1 - aTitle) * 12),
        };
        DrawLabel(hdc, "投屏Window", _fTitle, Blend(_bgColor, _fgColor, aTitle * aOut), titleRect);

        int lineY = h - 138;
        double lineK = Ease(t, 0.42, 1.18);
        int lineWidth = (int)(lineK * Math.Max(90, pw * 0.62));
        if (lineWidth > 1)
        {
            IntPtr penL = CreatePen(0, 2, Blend(_bgColor, _accentColor, aTitle * aOut));
            IntPtr oldPenL = SelectObject(hdc, penL);
            MoveToEx(hdc, cx - lineWidth / 2, lineY, IntPtr.Zero);
            LineTo(hdc, cx + lineWidth / 2, lineY);
            SelectObject(hdc, oldPenL);
            DeleteObject(penL);
        }

        var subRect = new Rect { Left = rect.Left, Top = h - 126, Right = rect.Right, Bottom = h - 98 };
        DrawLabel(hdc, "手机画面，投到大屏", _fSub, Blend(_bgColor, _dimColor, aSub * aOut), subRect);

        // 进度：一条 150px 的细轨，2.6 秒走完（不是转圈，但能看出"在准备"）
        int progW = Math.Min(150, (int)(pw * 0.9));
        int progY = h - 84;
        int progLeft = cx - progW / 2;
        DrawLine(hdc, progLeft, progY, progLeft + progW, progY, Blend(_bgColor, _lineColor, 1.0 * aOut), 2);
        double prog = Ease(t, 0.35, 2.55);
        if (prog > 0.01)
        {
            DrawLine(hdc, progLeft, progY, progLeft + (int)(progW * prog), progY,
                Blend(_bgColor, _accentColor, 0.9 * aOut), 2);
        }

        double aHint = (t > 0.9 ? Math.Min(1.0, (t - 0.9) / 0.4) : 0) * aOut;
        if (aHint > 0.02)
        {
            var hintRect = new Rect { Left = rect.Left, Top = h - 46, Right = rect.Right, Bottom = h - 20 };
            DrawLabel(hdc, "单击跳过", _fHint, Blend(_bgColor, _dimColor, 0.55 * aHint), hintRect);
        }
    }

    private static void DrawRoundRect(IntPtr hdc, int l, int t, int r, int b, int ew, int eh, int color, int width)
    {
        IntPtr pen = CreatePen(0, width, color);
        IntPtr oldPen = SelectObject(hdc, pen);
        IntPtr oldBrush = SelectObject(hdc, GetStockObject(5 /*NULL_BRUSH*/));
        RoundRect(hdc, l, t, r, b, ew, eh);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    private static void DrawEllipse(IntPtr hdc, int l, int t, int r, int b, int color, int width)
    {
        IntPtr pen = CreatePen(0, width, color);
        IntPtr oldPen = SelectObject(hdc, pen);
        IntPtr oldBrush = SelectObject(hdc, GetStockObject(5 /*NULL_BRUSH*/));
        Ellipse(hdc, l, t, r, b);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    private static void DrawLine(IntPtr hdc, int x1, int y1, int x2, int y2, int color, int width)
    {
        IntPtr pen = CreatePen(0, width, color);
        IntPtr oldPen = SelectObject(hdc, pen);
        MoveToEx(hdc, x1, y1, IntPtr.Zero);
        LineTo(hdc, x2, y2);
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    /// <summary>在矩形里画纵向渐变（GDI 的 GradientFill，COLORREF 顺序：上 → 下）。</summary>
    private static void FillVerticalGradient(IntPtr hdc, Rect rect, int topColor, int bottomColor)
    {
        var vertices = new TRIVERTEX[2];
        vertices[0].X = rect.Left;
        vertices[0].Y = rect.Top;
        SetVertexColor(ref vertices[0], topColor);
        vertices[1].X = rect.Right;
        vertices[1].Y = rect.Bottom;
        SetVertexColor(ref vertices[1], bottomColor);
        uint[] mesh = { 0, 1 };
        GradientFill(hdc, vertices, 2, mesh, 1, 1 /*GRADIENT_FILL_RECT_V*/);
    }

    /// <summary>0xRRGGBB → COLORREF（0x00BBGGRR）。写颜色时按人眼顺序写，转换交给它。</summary>
    private static int Rgb(int rgb)
        => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void SetVertexColor(ref TRIVERTEX vertex, int color)
    {
        vertex.Red = (ushort)(((color & 0xFF) * 257));
        vertex.Green = (ushort)((((color >> 8) & 0xFF) * 257));
        vertex.Blue = (ushort)((((color >> 16) & 0xFF) * 257));
        vertex.Alpha = 0xFF00;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRIVERTEX
    {
        public int X;
        public int Y;
        public ushort Red;
        public ushort Green;
        public ushort Blue;
        public ushort Alpha;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);
    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")]
    private static extern bool Polygon(IntPtr hdc, Point[] points, int count);
    [DllImport("msimg32.dll")]
    private static extern bool GradientFill(IntPtr hdc, TRIVERTEX[] vertices, uint vertexCount,
        uint[] mesh, uint meshCount, uint mode);

    private static void EnsureBrandFonts()
    {
        if (_fTitle != IntPtr.Zero) return;
        _fTitle = CreateFontW(-38, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        _fSub = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        _fHint = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
    }

    private static int DrawLabel(IntPtr hdc, string text, IntPtr font, int color, Rect rect)
    {
        IntPtr old = font != IntPtr.Zero ? SelectObject(hdc, font) : IntPtr.Zero;
        SetTextColor(hdc, color);
        int height = DrawTextW(hdc, text, -1, ref rect, DtCenter | DtVCenter | DtSingleLine);
        if (old != IntPtr.Zero) SelectObject(hdc, old);
        return height;
    }

    /// <summary>0…1 的平滑过渡（smoothstep）。</summary>
    private static double Ease(double t, double from, double to)
    {
        if (t <= from) return 0;
        if (t >= to) return 1;
        double x = (t - from) / (to - from);
        return x * x * (3 - 2 * x);
    }

    /// <summary>按 alpha 在两个 COLORREF 之间插值（背景往往就是黑，等价于淡入淡出）。</summary>
    private static int Blend(int background, int foreground, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        int br = background & 0xFF, bg = (background >> 8) & 0xFF, bb = (background >> 16) & 0xFF;
        int fr = foreground & 0xFF, fg = (foreground >> 8) & 0xFF, fb = (foreground >> 16) & 0xFF;
        int r = (int)Math.Round(br + (fr - br) * alpha);
        int g = (int)Math.Round(bg + (fg - bg) * alpha);
        int b = (int)Math.Round(bb + (fb - bb) * alpha);
        return (b << 16) | (g << 8) | r;
    }
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}