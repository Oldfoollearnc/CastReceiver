using System.Runtime.InteropServices;

namespace CastReceiver.App;

/// <summary>
/// 原生启动遮罩（Win32 + GDI）。
///
/// 为什么不用 WPF：解析 XAML + 显示第一个 WPF 窗口约 1 秒，这段时间如果什么都不画，
/// 用户看到的就是"点了图标没反应"。原生窗口几十毫秒就能出来，正好补上这段。
///
/// 画面刻意做到最简：一块深色底 + 一条来回滚动的加载条 + 一句"正在做什么"。
/// 之前试过带手机模型的开场动画：GDI 逐帧画渐变/裁剪/多边形，会闪、也容易掉帧 ——
/// 启动画面不该是性能测试。现在只画圆角矩形和文字，并且**双缓冲**，30fps 稳定不闪。
/// </summary>
internal static class NativeSplash
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int SwShow = 5;
    private const int WmPaint = 0x000F;
    private const int WmTimer = 0x0113;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmDestroy = 0x0002;
    private const int WmAppStop = 0x8000 + 1;

    private const uint DtCenter = 0x00000001, DtVCenter = 0x00000004, DtSingleLine = 0x00000020;
    private const int SrcCopy = 0x00CC0020;
    private const int NullBrush = 5;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc; public int Erase; public Rect Paint; public int Restore; public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int ew, int eh);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static IntPtr _hwnd;
    private static WndProcDelegate? _procRef;

    private static int _bgColor = 0x000E0C0B;    // COLORREF: #0B0C0E
    private static int _fgColor = 0x00EFEDEC;    // #ECEDEF
    private static int _dimColor = 0x00AEA6A0;   // #A0A6AE
    private static int _lineColor = 0x00302A26;  // #262A30
    private static int _accentColor = 0x0097DC3D; // #3DDC97

    private static volatile string _step = "正在启动…";
    private static int _tick;

    // 双缓冲的内存位图：整个遮罩生命周期只建一次（1280×800 每帧重建 4MB 会抖）
    private static IntPtr _memDc, _memBmp;
    private static int _memW, _memH;
    private static IntPtr _fTitle, _fStep;

    // 遮罩窗自己一条线程：WPF 启动时主线程被 XAML 解析占住，挂在主线程上的窗口
    // 收不到 WM_TIMER（实测 3 秒只画了 4 帧），加载条就会一顿一顿的。
    private static Thread? _thread;
    private static volatile bool _stop;
    private static volatile bool _running;
    private static long _startMs;

    // 窗口几何：给的就是主窗口的位置与大小（与主窗口严丝合缝地交接）
    private static int _x = int.MinValue, _y = int.MinValue, _w, _h;

    /// <summary>遮罩是不是还在（App 用来决定首帧渲染后是否收掉它）。</summary>
    public static bool LoadingRunning => _running;

    /// <summary>兼容旧调用点。</summary>
    public static bool BrandRunning => _running;

    public static bool IsVisible => _hwnd != IntPtr.Zero;

    /// <summary>按主题配色显示加载遮罩（颜色为 #RRGGBB 字符串）。</summary>
    public static void Show(string step, string bg, string fg, string dim, string line,
        int x = int.MinValue, int y = int.MinValue, int w = 0, int h = 0, string accent = "#3DDC97")
    {
        _x = x; _y = y; _w = w; _h = h;
        _bgColor = ParseColor(bg, 0x0B0C0E);
        _fgColor = ParseColor(fg, 0xECEDEF);
        _dimColor = ParseColor(dim, 0xA0A6AE);
        _lineColor = ParseColor(line, 0x262A30);
        _accentColor = ParseColor(accent, 0x3DDC97);
        _step = string.IsNullOrWhiteSpace(step) ? "正在启动…" : step;
        StartWindowThread();
    }

    /// <summary>兼容旧调用点：品牌动画已经并入加载遮罩。</summary>
    public static void ShowBrandIntro(string bg, string fg, string dim, string line, string accent,
        int x = int.MinValue, int y = int.MinValue, int w = 0, int h = 0)
        => Show("正在启动…", bg, fg, dim, line, x, y, w, h, accent);

    private static void StartWindowThread()
    {
        if (_running) return;

        _stop = false;
        _running = true;
        _startMs = Environment.TickCount64;

        var thread = new Thread(WindowThreadProc)
        {
            IsBackground = true,
            Name = "startup-mask",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();
    }

    /// <summary>遮罩线程：建窗 → 30fps 刷新 → 收到停止消息后自己收场。</summary>
    private static void WindowThreadProc()
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
                ClassName = "CastReceiverStartupMask",
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
            if (hwnd == IntPtr.Zero) { _running = false; return; }
            _hwnd = hwnd;
            ShowWindow(hwnd, SwShow);
            UpdateWindow(hwnd);
            SetTimer(hwnd, IntPtr.Zero, 33, IntPtr.Zero);   // 30fps 足够，别为一条加载条烧 CPU
            Bootstrap.Log("启动遮罩已显示");

            while (!_stop)
            {
                // 兜底：主窗口若因为异常始终没渲染，遮罩不能永远盖着
                if (Environment.TickCount64 - _startMs > 30_000)
                {
                    Bootstrap.Log("启动遮罩超时退出");
                    break;
                }
                if (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) <= 0) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("启动遮罩线程异常：" + ex.Message);
        }
        finally
        {
            if (hwnd != IntPtr.Zero)
            {
                KillTimer(hwnd, IntPtr.Zero);
                DestroyWindow(hwnd);
            }
            if (_hwnd == hwnd) _hwnd = IntPtr.Zero;
            ReleaseBuffer();
            _running = false;
            Bootstrap.Log("启动遮罩已关闭");
        }
    }

    /// <summary>更新"正在做什么"。可从任意线程调用。</summary>
    public static void SetStep(string step)
    {
        _step = string.IsNullOrWhiteSpace(step) ? "正在启动…" : step;
        try { if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false); } catch { }
    }

    public static string CurrentStep => _step;

    /// <summary>收掉遮罩（主窗口首帧渲染完时调用）。</summary>
    public static void Close()
    {
        try
        {
            if (!_running && _hwnd == IntPtr.Zero) return;
            _stop = true;
            if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmAppStop, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTimer:
                _tick++;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WmEraseBkgnd:
                return (IntPtr)1;           // 自己双缓冲画，别让系统先刷一遍
            case WmPaint:
                try { Paint(hwnd); }
                catch (Exception ex) { Bootstrap.Log("启动遮罩绘制异常：" + ex.Message); }
                return IntPtr.Zero;
            case WmAppStop:
                _stop = true;
                PostQuitMessage(0);
                return IntPtr.Zero;
            case WmDestroy:
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>整块画到内存位图再一次性贴到窗口：不会看到"先清屏再画"的闪烁。</summary>
    private static void Paint(IntPtr hwnd)
    {
        var ps = BeginPaint(hwnd, out var paint);
        IntPtr hdc = paint.Hdc;
        try
        {
            GetClientRect(hwnd, out var rect);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return;

            if (_memDc == IntPtr.Zero || _memW != w || _memH != h)
            {
                ReleaseBuffer();
                _memDc = CreateCompatibleDC(hdc);
                _memBmp = CreateCompatibleBitmap(hdc, w, h);
                if (_memDc == IntPtr.Zero || _memBmp == IntPtr.Zero)
                {
                    ReleaseBuffer();
                    DrawMask(hdc, rect);       // 内存 DC 建不出来就退回直画
                    return;
                }
                SelectObject(_memDc, _memBmp);
                _memW = w;
                _memH = h;
            }

            DrawMask(_memDc, rect);
            BitBlt(hdc, 0, 0, w, h, _memDc, 0, 0, SrcCopy);
        }
        catch (Exception ex) { Bootstrap.Log("启动遮罩绘制异常：" + ex.Message); }
        finally
        {
            EndPaint(hwnd, ref paint);
        }
    }

    private static void DrawMask(IntPtr hdc, Rect rect)
    {
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int cx = rect.Left + w / 2;
        int cy = rect.Top + h / 2;

        // 纯色底：没有渐变、没有大图，启动这几秒不该有任何解码/重绘压力
        IntPtr bg = CreateSolidBrush(_bgColor);
        FillRect(hdc, ref rect, bg);
        DeleteObject(bg);

        SetBkMode(hdc, 1 /*TRANSPARENT*/);

        // 字体只建一次：30fps 下每帧 CreateFont/DeleteFont 也是白白的 GDI 开销
        EnsureFonts();

        // 品牌名（小、暗，不抢加载条的注意力）
        IntPtr oldFont = _fTitle != IntPtr.Zero ? SelectObject(hdc, _fTitle) : IntPtr.Zero;
        SetTextColor(hdc, _dimColor);
        var titleRect = new Rect { Left = rect.Left, Top = cy - 74, Right = rect.Right, Bottom = cy - 46 };
        DrawTextW(hdc, "投屏Window", -1, ref titleRect, DtCenter | DtVCenter | DtSingleLine);

        // 加载条：一条暗轨 + 一段来回滚动的强调色（纯矩形，3–4px 高不需要圆角）
        int trackW = Math.Min(260, Math.Max(150, w / 5));
        int trackH = 4;
        int trackLeft = cx - trackW / 2;
        int trackTop = cy - (h > 480 ? 8 : 2);

        IntPtr trackBrush = CreateSolidBrush(_lineColor);
        var trackRect = new Rect { Left = trackLeft, Top = trackTop, Right = trackLeft + trackW, Bottom = trackTop + trackH };
        FillRect(hdc, ref trackRect, trackBrush);
        DeleteObject(trackBrush);

        int segW = Math.Max(52, trackW / 3);
        // 三角波 + smoothstep：两端速度慢、中间快，像"在加载"而不是机器般匀速
        const double period = 1.6;                   // 一个来回 1.6 秒
        double t = (_tick * 0.033) / period;
        double phase = t - Math.Floor(t);            // 0…1
        double tri = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
        double eased = tri * tri * (3 - 2 * tri);
        int segLeft = trackLeft + (int)((trackW - segW) * eased);

        IntPtr segBrush = CreateSolidBrush(_accentColor);
        var segRect = new Rect { Left = segLeft, Top = trackTop, Right = segLeft + segW, Bottom = trackTop + trackH };
        FillRect(hdc, ref segRect, segBrush);
        DeleteObject(segBrush);

        // 当前步骤
        if (_fStep != IntPtr.Zero) SelectObject(hdc, _fStep);
        SetTextColor(hdc, _dimColor);
        var stepRect = new Rect { Left = rect.Left, Top = trackTop + 30, Right = rect.Right, Bottom = trackTop + 58 };
        DrawTextW(hdc, _step, -1, ref stepRect, DtCenter | DtVCenter | DtSingleLine);

        if (oldFont != IntPtr.Zero) SelectObject(hdc, oldFont);
    }

    private static void EnsureFonts()
    {
        if (_fTitle == IntPtr.Zero)
            _fTitle = CreateFontW(-15, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        if (_fStep == IntPtr.Zero)
            _fStep = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
    }

    private static void ReleaseBuffer()
    {
        if (_memBmp != IntPtr.Zero) { DeleteObject(_memBmp); _memBmp = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        _memW = _memH = 0;
        if (_fTitle != IntPtr.Zero) { DeleteObject(_fTitle); _fTitle = IntPtr.Zero; }
        if (_fStep != IntPtr.Zero) { DeleteObject(_fStep); _fStep = IntPtr.Zero; }
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
            return (b << 16) | (g << 8) | r;
        }
        catch { return fallback; }
    }
}
