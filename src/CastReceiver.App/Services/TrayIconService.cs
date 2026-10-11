using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CastReceiver.App.Services;

/// <summary>
/// 托盘图标：直接用 Shell_NotifyIcon 实现（不引入 WinForms，避免与 WPF 类型冲突）。
/// 提供显示窗口/全屏/截屏/设置/退出菜单，以及连接状态气泡通知。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int NimAdd = 0x00000000, NimModify = 0x00000001, NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001, NifIcon = 0x00000002, NifTip = 0x00000004, NifInfo = 0x00000010;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmLButtonDblClk = 0x0203;
    private const int MfString = 0x00000000, MfSeparator = 0x00000800;
    private const int TpmRightButton = 0x0002, TpmReturnCmd = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, int type, int cx, int cy, int load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const int CallbackMessage = 0x8000 + 17;   // WM_APP + 17

    private readonly Action _showWindow, _toggleFullscreen, _screenshot, _settings, _exit;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _icon = IntPtr.Zero;
    private HwndSource? _source;
    private NOTIFYICONDATA _data;
    private bool _added;

    public TrayIconService(Window window, Action showWindow, Action toggleFullscreen, Action screenshot, Action settings, Action exit)
    {
        _showWindow = showWindow;
        _toggleFullscreen = toggleFullscreen;
        _screenshot = screenshot;
        _settings = settings;
        _exit = exit;

        void Attach()
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(Hook);
        }
        if (window.IsLoaded) Attach();
        else window.SourceInitialized += (_, _) => Attach();
    }

    public void Show(string deviceName)
    {
        if (_added || _hwnd == IntPtr.Zero) return;
        try
        {
            _icon = LoadAppIcon();
            _data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = CallbackMessage,
                hIcon = _icon,
                szTip = Trim($"投屏Window · {deviceName}", 127),
                szInfo = "",
                szInfoTitle = "",
            };
            _added = Shell_NotifyIcon(NimAdd, ref _data);
        }
        catch { }
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (ExtractIconEx(exe, 0, large, small, 1) > 0)
                    return large[0] != IntPtr.Zero ? large[0] : small[0];
            }
        }
        catch { }
        try { return LoadImage(IntPtr.Zero, "#32512", 1 /*IMAGE_ICON*/, 16, 16, 0); } catch { return IntPtr.Zero; }
    }

    public void Balloon(string title, string text, bool warning = false)
    {
        if (!_added) return;
        try
        {
            _data.uFlags = NifInfo | NifIcon | NifTip;
            _data.szInfoTitle = Trim(title, 63);
            _data.szInfo = Trim(text, 255);
            _data.dwInfoFlags = warning ? 0x00000002 : 0x00000001;   // NIIF_WARNING : NIIF_INFO
            _data.uTimeoutOrVersion = 4000;
            Shell_NotifyIcon(NimModify, ref _data);
            _data.uFlags = NifMessage | NifIcon | NifTip;
        }
        catch { }
    }

    public void UpdateTooltip(string text)
    {
        if (!_added) return;
        try
        {
            _data.szTip = Trim(text, 127);
            _data.uFlags = NifTip | NifIcon;
            Shell_NotifyIcon(NimModify, ref _data);
        }
        catch { }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;
        int evt = lParam.ToInt32() & 0xFFFF;
        try
        {
            switch (evt)
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    _showWindow();
                    handled = true;
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, 1, "显示主窗口");
            AppendMenu(menu, MfString, 2, "进入 / 退出全屏");
            AppendMenu(menu, MfString, 3, "截取当前画面（加密）");
            AppendMenu(menu, MfString, 4, "开始 / 停止录制（加密）");
            AppendMenu(menu, MfString, 6, "打开我的库");
            AppendMenu(menu, MfString, 7, "锁定我的库");
            AppendMenu(menu, MfString, 8, "遮挡 / 恢复画面");
            AppendMenu(menu, MfSeparator, 0, "");
            AppendMenu(menu, MfString, 5, "设置");
            AppendMenu(menu, MfSeparator, 0, "");
            AppendMenu(menu, MfString, 9, "退出");
            GetCursorPos(out var p);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
            switch (cmd)
            {
                case 1: _showWindow(); break;
                case 2: _toggleFullscreen(); break;
                case 3: _screenshot(); break;
                case 4: RecordRequested?.Invoke(); break;
                case 5: _settings(); break;
                case 6: OpenFolderRequested?.Invoke(); break;
                case 7: LockRequested?.Invoke(); break;
                case 8: PrivacyMaskRequested?.Invoke(); break;
                case 9: _exit(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>托盘菜单里的"开始/停止录制"。</summary>
    public event Action? RecordRequested;

    /// <summary>托盘菜单里的"打开我的库"。</summary>
    public event Action? OpenFolderRequested;

    /// <summary>托盘菜单里的"锁定我的库"。</summary>
    public event Action? LockRequested;

    /// <summary>托盘菜单里的"遮挡 / 恢复画面"。</summary>
    public event Action? PrivacyMaskRequested;

    public void Dispose()
    {
        try
        {
            if (_added) { Shell_NotifyIcon(NimDelete, ref _data); _added = false; }
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
            _source?.RemoveHook(Hook);
        }
        catch { }
    }
}