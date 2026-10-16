namespace CastReceiver.Core.Android;

/// <summary>
/// 电脑这一侧能发给手机的一组按键（「电脑控制手机」用的系统键）。
///
/// 为什么只收这一小撮：每一条都得是**任何一台安卓都认**的 —— 这里全部是 AOSP 里的经典 keycode
/// （<c>KEYCODE_BACK</c> 从 Android 1.0 到现在都是 4），并且都用**数字**发（`input keyevent 4`），
/// 不用 `KEYCODE_APP_SWITCH` 这类名字：名字是后加的，老机器的 input 认不出来，
/// 而数字是稳定 ABI。名字只用于界面与日志。
/// </summary>
public enum AndroidKey
{
    /// <summary>返回。</summary>
    Back,
    /// <summary>回到桌面。</summary>
    Home,
    /// <summary>最近任务（多任务）。</summary>
    Recents,
    /// <summary>下拉通知栏。</summary>
    Notifications,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    /// <summary>电源（按一下＝亮/灭屏）。</summary>
    Power,
    /// <summary>只亮屏，不灭屏。</summary>
    WakeUp,
    /// <summary>只灭屏，不亮屏。</summary>
    Sleep,
    Enter,
    /// <summary>退格（删前一个字符）。</summary>
    Delete,
    /// <summary>向前删（Delete 键）。</summary>
    ForwardDelete,
    Tab,
    Space,
    Escape,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
    /// <summary>方向键中间那颗（确定）。</summary>
    DpadCenter,
    PageUp,
    PageDown,
    /// <summary>行首。</summary>
    MoveHome,
    /// <summary>行尾。</summary>
    MoveEnd,
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
    /// <summary>菜单键（老安卓的 MENU）。</summary>
    Menu,
    /// <summary>相机键。</summary>
    Camera,
    /// <summary>粘贴（配合手机剪贴板用）。</summary>
    Paste,
    Copy,
    Cut,
}

/// <summary>
/// 按键表与命令拼装（纯函数，便于自检：键位写错不会崩，只会"按错键"，最难发现）。
/// </summary>
public static class AndroidKeys
{
    private static readonly Dictionary<AndroidKey, (int Code, string Label, string Wire)> Table = new()
    {
        [AndroidKey.Back] = (4, "返回", "KEYCODE_BACK"),
        [AndroidKey.Home] = (3, "主页", "KEYCODE_HOME"),
        [AndroidKey.Recents] = (187, "最近任务", "KEYCODE_APP_SWITCH"),
        [AndroidKey.Notifications] = (83, "通知栏", "KEYCODE_NOTIFICATION"),
        [AndroidKey.VolumeUp] = (24, "音量 +", "KEYCODE_VOLUME_UP"),
        [AndroidKey.VolumeDown] = (25, "音量 −", "KEYCODE_VOLUME_DOWN"),
        [AndroidKey.VolumeMute] = (164, "静音", "KEYCODE_VOLUME_MUTE"),
        [AndroidKey.Power] = (26, "电源", "KEYCODE_POWER"),
        [AndroidKey.WakeUp] = (224, "亮屏", "KEYCODE_WAKEUP"),
        [AndroidKey.Sleep] = (223, "息屏", "KEYCODE_SLEEP"),
        [AndroidKey.Enter] = (66, "回车", "KEYCODE_ENTER"),
        [AndroidKey.Delete] = (67, "退格", "KEYCODE_DEL"),
        [AndroidKey.ForwardDelete] = (112, "向前删", "KEYCODE_FORWARD_DEL"),
        [AndroidKey.Tab] = (61, "Tab", "KEYCODE_TAB"),
        [AndroidKey.Space] = (62, "空格", "KEYCODE_SPACE"),
        [AndroidKey.Escape] = (111, "Esc", "KEYCODE_ESCAPE"),
        [AndroidKey.DpadUp] = (19, "上", "KEYCODE_DPAD_UP"),
        [AndroidKey.DpadDown] = (20, "下", "KEYCODE_DPAD_DOWN"),
        [AndroidKey.DpadLeft] = (21, "左", "KEYCODE_DPAD_LEFT"),
        [AndroidKey.DpadRight] = (22, "右", "KEYCODE_DPAD_RIGHT"),
        [AndroidKey.DpadCenter] = (23, "确定", "KEYCODE_DPAD_CENTER"),
        [AndroidKey.PageUp] = (92, "上翻页", "KEYCODE_PAGE_UP"),
        [AndroidKey.PageDown] = (93, "下翻页", "KEYCODE_PAGE_DOWN"),
        [AndroidKey.MoveHome] = (122, "行首", "KEYCODE_MOVE_HOME"),
        [AndroidKey.MoveEnd] = (123, "行尾", "KEYCODE_MOVE_END"),
        [AndroidKey.MediaPlayPause] = (85, "播放/暂停", "KEYCODE_MEDIA_PLAY_PAUSE"),
        [AndroidKey.MediaNext] = (87, "下一曲", "KEYCODE_MEDIA_NEXT"),
        [AndroidKey.MediaPrevious] = (88, "上一曲", "KEYCODE_MEDIA_PREVIOUS"),
        [AndroidKey.Menu] = (82, "菜单", "KEYCODE_MENU"),
        [AndroidKey.Camera] = (27, "相机", "KEYCODE_CAMERA"),
        [AndroidKey.Paste] = (279, "粘贴", "KEYCODE_PASTE"),
        [AndroidKey.Copy] = (278, "复制", "KEYCODE_COPY"),
        [AndroidKey.Cut] = (277, "剪切", "KEYCODE_CUT"),
    };

    /// <summary>表里所有的键（自检遍历用，避免"加了键忘了加自检"）。</summary>
    public static IReadOnlyCollection<AndroidKey> All => Table.Keys;

    /// <summary>安卓 keycode（数字）。</summary>
    public static int Code(AndroidKey key) => Table.TryGetValue(key, out var entry) ? entry.Code : 0;

    /// <summary>界面文字（中文）。</summary>
    public static string Label(AndroidKey key) => Table.TryGetValue(key, out var entry) ? entry.Label : key.ToString();

    /// <summary>规范名（AOSP 的 KEYCODE_xxx；日志与诊断里用，比数字好读）。</summary>
    public static string Wire(AndroidKey key) => Table.TryGetValue(key, out var entry) ? entry.Wire : key.ToString();

    /// <summary>连按多次才有意义的键（音量、方向、切歌）。</summary>
    public static bool IsRepeatable(AndroidKey key) => key is AndroidKey.VolumeUp or AndroidKey.VolumeDown
        or AndroidKey.DpadUp or AndroidKey.DpadDown or AndroidKey.DpadLeft or AndroidKey.DpadRight
        or AndroidKey.MediaNext or AndroidKey.MediaPrevious;

    /// <summary>
    /// 拼一条按键命令。多次按键写成**一条命令**（`input keyevent 24 24 24`）：
    /// 每次调用 adb 都要起一个进程（无线上几十到上百毫秒），音量按 5 下不该起 5 个进程。
    /// 长按与多次互斥（`--longpress` 只作用于紧跟的那一个 keycode），同时给出时以长按为准。
    /// </summary>
    public static string BuildKeyEventArguments(AndroidKey key, int repeat = 1, bool longPress = false)
    {
        int code = Code(key);
        if (longPress)
        {
            return $"shell input keyevent --longpress {code}";
        }

        int count = Math.Clamp(repeat, 1, 20);
        return count == 1
            ? $"shell input keyevent {code}"
            : $"shell input keyevent {string.Join(' ', Enumerable.Repeat(code, count))}";
    }

    /// <summary>
    /// 物理键盘按键名（WPF <c>Key</c> 枚举的 ToString()）→ 安卓键。
    /// 刻意只收"发过去确实有用"的那些；<c>Escape</c> 映射成**返回**（安卓上 Esc 没什么用，
    /// 而"按 Esc 返回"是所有人对遥控的直觉）。其余键返回 null ＝ 不往手机发。
    /// </summary>
    public static AndroidKey? FromVirtualKeyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "return" or "enter" => AndroidKey.Enter,
            "back" => AndroidKey.Delete,
            "delete" => AndroidKey.ForwardDelete,
            "tab" => AndroidKey.Tab,
            "space" => AndroidKey.Space,
            "escape" => AndroidKey.Back,
            "up" => AndroidKey.DpadUp,
            "down" => AndroidKey.DpadDown,
            "left" => AndroidKey.DpadLeft,
            "right" => AndroidKey.DpadRight,
            "home" => AndroidKey.MoveHome,
            "end" => AndroidKey.MoveEnd,
            "pageup" => AndroidKey.PageUp,
            "pagedown" => AndroidKey.PageDown,
            "browserback" => AndroidKey.Back,
            "mediaplaypause" or "mediaplay" or "mediapause" => AndroidKey.MediaPlayPause,
            "medianexttrack" => AndroidKey.MediaNext,
            "mediaprevioustrack" => AndroidKey.MediaPrevious,
            "volumeup" => AndroidKey.VolumeUp,
            "volumedown" => AndroidKey.VolumeDown,
            "volumemute" => AndroidKey.VolumeMute,
            _ => null,
        };
    }
}
