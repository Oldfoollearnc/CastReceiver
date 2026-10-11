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
    private static string _step = "正在启动…";

    // 黑幕的几何：给的就是主窗口的位置与大小（不再全屏）
    private static int _x = int.MinValue, _y = int.MinValue, _w, _h;
    private static WndProcDelegate? _procRef;   // 保住委托，避免被 GC

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static bool IsVisible => _hwnd != IntPtr.Zero;

    /// <summary>按主题配色显示启动动画（颜色为 #RRGGBB 字符串）。</summary>
    public static void Show(string step, string bg, string fg, string dim, string line,
        int x = int.MinValue, int y = int.MinValue, int w = 0, int h = 0)
    {
        _x = x; _y = y; _w = w; _h = h;
        _bgColor = ParseColor(bg, 0x0B0C0E);
        _fgColor = ParseColor(fg, 0xECEDEF);
        _dimColor = ParseColor(dim, 0xA0A6AE);
        _lineColor = ParseColor(line, 0x262A30);
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

    public static void Show(string step = "正在启动…")
    {
        _step = step;
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
            SetTimer(_hwnd, IntPtr.Zero, 40, IntPtr.Zero);
            Bootstrap.Log("原生闪屏已显示");
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
                _angle = (_angle + 24) % 360;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case WmPaint:
                try { Paint(hwnd); }
                catch (Exception ex) { Bootstrap.Log("原生闪屏绘制异常：" + ex.Message); try { var hdc2 = BeginPaint(hwnd, out var ps2); EndPaint(hwnd, ref ps2); } catch { } }
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

            // 只铺满纯黑：这不再是"一个转圈小窗"，而是全屏的一块黑幕，
            // 紧接着就由主窗口的入场遮罩从黑里把画面淡出来 —— 视觉上是连贯的一次淡入。
            IntPtr bg = CreateSolidBrush(_bgColor);
            FillRect(hdc, ref rect, bg);
            DeleteObject(bg);
        }
        catch (Exception ex) { Bootstrap.Log("启动黑幕绘制异常：" + ex.Message); }
        finally { EndPaint(hwnd, ref paint); }
    }
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}