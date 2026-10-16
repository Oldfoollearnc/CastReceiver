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
    // v4 通知下**不再发** WM_LBUTTONUP / WM_LBUTTONDBLCLK：左键单击与双击都走 NIN_SELECT，
    // 键盘选中走 NIN_KEYSELECT。升到 v4 后如果只认旧消息，点托盘图标就会"没反应"。
    private const int NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const int MfString = 0x00000000, MfSeparator = 0x00000800;
    private const int MfChecked = 0x00000008, MfGrayed = 0x00000001;
    private const int TpmRightButton = 0x0002, TpmReturnCmd = 0x0100;
    private const int WmContextMenu = 0x007B;          // v4 通知下右键走这个
    private const int NimSetVersion = 0x00000004;
    private const int NotifyIconVersion4 = 4;

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
            if (_added)
            {
                // 升到 v4：右键以 WM_CONTEXTMENU 通知、支持通知中心与键盘唤起。
                // 注意 uTimeoutOrVersion 在 Vista+ 已被忽略，这里只当版本号用。
                _data.uTimeoutOrVersion = NotifyIconVersion4;
                Shell_NotifyIcon(NimSetVersion, ref _data);
                _data.uFlags = NifMessage | NifIcon | NifTip;
            }
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
                {
                    // 托盘图标要的是 SM_CXSMICON 尺寸（16/20/24）——app.ico 里这些尺寸是单独备好的。
                    // 原来优先用 large（系统大图标，通常 32px），等于让 shell 把 32px 缩到 16px，高 DPI 下发糊。
                    if (small[0] != IntPtr.Zero) return small[0];
                    return large[0];
                }
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
                case WmLButtonUp:      // 旧版（v0）行为：某些系统上 NIM_SETVERSION 会失败，保留兜底
                case WmLButtonDblClk:
                case NinSelect:        // v4：左键单击/双击
                case NinKeySelect:     // v4：键盘选中（键盘导航到托盘区域时）
                    _showWindow();
                    handled = true;
                    break;
                case WmRButtonUp:
                case WmContextMenu:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 托盘菜单要按当前状态显示勾选/禁用，所以由主窗口按需回读这些状态。
    /// 以前菜单项写的是「开始 / 停止录制」「遮挡 / 恢复画面」这种斜杠文案——用户看不出当前处于哪一态。
    /// </summary>
    public Func<bool>? CanCaptureState { get; set; }
    public Func<bool>? MaskedState { get; set; }
    public Func<bool>? RecordingState { get; set; }
    public Func<bool>? FullscreenState { get; set; }

    private static bool Ask(Func<bool>? probe)
    {
        try { return probe?.Invoke() == true; } catch { return false; }
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            bool canCapture = Ask(CanCaptureState);
            bool masked = Ask(MaskedState);
            bool recording = Ask(RecordingState);
            bool full = Ask(FullscreenState);
            int capFlags = canCapture ? MfString : MfString | MfGrayed;

            AppendMenu(menu, MfString, 1, "显示主窗口");
            AppendMenu(menu, MfString | (full ? MfChecked : 0), 2, full ? "退出全屏" : "进入全屏");
            AppendMenu(menu, capFlags, 3, "截取当前画面（加密）");
            AppendMenu(menu, capFlags | (recording ? MfChecked : 0), 4,
                       recording ? "停止录制并保存（加密）" : "开始录制（加密）");
            AppendMenu(menu, MfString, 6, "打开我的库");
            AppendMenu(menu, MfString, 7, "锁定我的库");
            AppendMenu(menu, MfString | (masked ? MfChecked : 0), 8, masked ? "恢复画面" : "遮挡画面");
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