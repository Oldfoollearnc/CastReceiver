using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CastReceiver.App.Services;

/// <summary>
/// 窗口防捕获（Windows 的 SetWindowDisplayAffinity / WDA_EXCLUDEFROMCAPTURE）：
/// 开启后，其它程序（会议共享、远程协助、录屏软件、系统截图）截取本程序窗口时只会得到黑屏。
///
/// 代价：这条保护对本程序自己同样生效 —— 所以「自己截屏/录制」必须走别的取帧路径，
/// 由 <see cref="AntiCaptureProbe"/> 在本机实测决定用哪一条（详见 docs/安全与隐私评估.md）。
/// </summary>
public static class WindowProtection
{
    public const uint WdaNone = 0x00000000;
    public const uint WdaExcludeFromCapture = 0x00000011;
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>给窗口加/去防捕获保护。只有顶层窗口支持；失败返回 false（不抛异常）。</summary>
    public static bool SetProtection(Window window, bool enable)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            return SetWindowDisplayAffinity(handle, enable ? WdaExcludeFromCapture : WdaNone);
        }
        catch { return false; }
    }

    public static bool SetProtection(IntPtr handle, bool enable)
    {
        try
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
            return SetWindowDisplayAffinity(handle, enable ? WdaExcludeFromCapture : WdaNone);
        }
        catch { return false; }
    }

    public static uint GetProtection(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return WdaNone;
            return GetWindowDisplayAffinity(handle, out uint affinity) ? affinity : WdaNone;
        }
        catch { return WdaNone; }
    }

    /// <summary>把保护套用到本程序的所有顶层窗口（主窗、控制条、状态浮层、提示条、库窗）。</summary>
    public static int ApplyToAll(bool enable)
    {
        int count = 0;
        try
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (window is null) continue;
                if (SetProtection(window, enable)) count++;
            }
        }
        catch { }
        return count;
    }

    /// <summary>窗口客户区的真实像素尺寸（DIP 与像素在缩放显示器上并不相同）。</summary>
    public static (int Width, int Height) GetClientSize(IntPtr handle)
    {
        try
        {
            if (handle != IntPtr.Zero && GetClientRect(handle, out var rect))
                return (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// 用 PrintWindow(PW_RENDERFULLCONTENT) 直接向窗口要一帧 —— 不经过屏幕，因此有可能绕开防捕获限制。
    /// 这是「既保护窗口、又能自己录制」的首选路径（实测结论见 AntiCaptureProbe）。
    /// </summary>
    public static Bitmap? GrabWindow(IntPtr handle)
    {
        try
        {
            var (width, height) = GetClientSize(handle);
            if (width <= 0 || height <= 0) return null;
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            IntPtr hdc = graphics.GetHdc();
            bool ok;
            try { ok = PrintWindow(handle, hdc, PwRenderFullContent); }
            finally { graphics.ReleaseHdc(hdc); }
            return ok ? bitmap : null;
        }
        catch { return null; }
    }

    /// <summary>判断一帧是不是「全黑/空」——用于识别被防捕获挡掉的取帧结果。</summary>
    public static bool LooksBlank(Bitmap? bitmap, int sampleStep = 7)
    {
        if (bitmap is null) return true;
        try
        {
            int samples = 0, bright = 0;
            for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 24))
            {
                for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 24))
                {
                    var pixel = bitmap.GetPixel(x, y);
                    samples++;
                    if (pixel.R + pixel.G + pixel.B > 36) bright++;
                }
            }
            if (samples == 0) return true;
            return bright * 100 / samples < 2;      // 亮像素少于 2% 视为黑屏
        }
        catch { return true; }
    }
}
