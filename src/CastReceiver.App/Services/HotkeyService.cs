using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CastReceiver.App.Services;

/// <summary>全局快捷键（即使程序不在前台也生效）。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    public const int IdFullscreen = 0xA001;
    public const int IdScreenshot = 0xA002;
    public const int IdRecord = 0xA003;
    public const int IdPanic = 0xA004;
    public const int IdLock = 0xA005;
    public const int IdPrivacy = 0xA006;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private readonly IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    public event Action<int>? Pressed;

    public HotkeyService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
    }

    /// <summary>Ctrl+Alt+F 全屏 · S 截屏 · R 录屏 · H 紧急隐藏 · L 锁定加密库 · P 遮挡画面。</summary>
    public bool Register()
    {
        if (_registered || _hwnd == IntPtr.Zero) return _registered;
        try
        {
            bool a = RegisterHotKey(_hwnd, IdFullscreen, ModControl | ModAlt | ModNoRepeat, 0x46);  // F
            bool b = RegisterHotKey(_hwnd, IdScreenshot, ModControl | ModAlt | ModNoRepeat, 0x53);  // S
            bool c = RegisterHotKey(_hwnd, IdRecord, ModControl | ModAlt | ModNoRepeat, 0x52);      // R
            bool d = RegisterHotKey(_hwnd, IdPanic, ModControl | ModAlt | ModNoRepeat, 0x48);       // H
            bool e = RegisterHotKey(_hwnd, IdLock, ModControl | ModAlt | ModNoRepeat, 0x4C);        // L
            bool f = RegisterHotKey(_hwnd, IdPrivacy, ModControl | ModAlt | ModNoRepeat, 0x50);     // P
            _registered = a || b || c || d || e || f;
        }
        catch { }
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero) return;
        try
        {
            UnregisterHotKey(_hwnd, IdFullscreen);
            UnregisterHotKey(_hwnd, IdScreenshot);
            UnregisterHotKey(_hwnd, IdRecord);
            UnregisterHotKey(_hwnd, IdPanic);
            UnregisterHotKey(_hwnd, IdLock);
            UnregisterHotKey(_hwnd, IdPrivacy);
        }
        catch { }
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        try { _source?.RemoveHook(Hook); } catch { }
        _source = null;
    }
}

/// <summary>窗口级效果：Win11 云母材质、全屏时阻止休眠、隐藏鼠标指针。</summary>
public static class WindowEffects
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern bool ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern int LoadCursor(IntPtr instance, int cursor);

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;          // Mica
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    /// <summary>尝试开启 Mica（Win11 22H2+）；失败返回 false，调用方应回退到纯色背景。</summary>
    public static bool TryEnableMica(Window window, bool enable)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            int value = enable ? DwmsbtMainWindow : 1;   // 1 = 无材质
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>全屏观看时阻止屏保/休眠。</summary>
    public static void SetKeepAwake(bool on)
    {
        try
        {
            if (on) SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            else SetThreadExecutionState(EsContinuous);
        }
        catch { }
    }

    private static bool _cursorHidden;

    /// <summary>隐藏/恢复鼠标指针（全屏无操作时用）。</summary>
    public static void SetCursorHidden(bool hide)
    {
        if (_cursorHidden == hide) return;
        _cursorHidden = hide;
        try { ShowCursor(!hide); } catch { }
    }
}