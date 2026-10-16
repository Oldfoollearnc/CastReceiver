using System.Globalization;

namespace CastReceiver.Core.Android;

/// <summary>
/// 往手机注入输入（触摸 / 按键 / 文字），以及从手机取回截屏。
///
/// 边界（按需求定的）：**只用安卓自带、任何一台开了调试的手机都有的能力** ——
/// <c>adb shell input</c>（tap / swipe / keyevent / text）、<c>adb exec-out screencap</c>。
/// 不要求手机 root，也不要求装任何应用（中文输入是唯一的例外：需要手机装 ADBKeyboard，见 AndroidText）。
/// </summary>
public sealed class AndroidInput
{
    private readonly AdbClient _adb;
    private readonly Func<string?> _serial;
    private readonly Action<string>? _log;

    public AndroidInput(AdbClient adb, Func<string?> serial, Action<string>? log = null)
    {
        _adb = adb;
        _serial = serial;
        _log = log;
    }

    /// <summary>已成功注入的事件总数（触摸 + 按键 + 文字，界面显示用）。</summary>
    public int InjectedCount { get; private set; }

    /// <summary>点按次数。</summary>
    public int TapCount { get; private set; }

    /// <summary>滑动次数。</summary>
    public int SwipeCount { get; private set; }

    /// <summary>按键次数（按了几颗键，不是发了几条命令）。</summary>
    public int KeyCount { get; private set; }

    /// <summary>文字发送次数（发了几段文字）。</summary>
    public int TextCount { get; private set; }

    /// <summary>最近一次注入的结果（失败原因要能查到）。</summary>
    public AdbResult? LastResult { get; private set; }

    /// <summary>最近一次注入的可读描述（界面上如实回显，让人知道"刚才那下发出去了什么"）。</summary>
    public string LastInjection { get; private set; } = "";

    // ------------------------------------------------------------------ 触摸

    /// <summary>点按。</summary>
    public AdbResult Tap(int x, int y, int timeoutMs = 8000)
    {
        AdbResult result = Run($"shell input tap {x} {y}", timeoutMs);
        if (result.Ok) TapCount++;
        return result;
    }

    /// <summary>从一点滑到另一点（拖动、滚轮都用它）。</summary>
    public AdbResult Swipe(int x1, int y1, int x2, int y2, int durationMs = 220, int timeoutMs = 12000)
    {
        int duration = Math.Clamp(durationMs, 30, 5000);
        AdbResult result = Run($"shell input swipe {x1} {y1} {x2} {y2} {duration}", timeoutMs);
        if (result.Ok) SwipeCount++;
        return result;
    }

    /// <summary>在一点上按住再滑（长按拖动），先按下、滑、再抬手。</summary>
    public AdbResult Drag(int fromX, int fromY, int toX, int toY, int durationMs = 320)
        => Swipe(fromX, fromY, toX, toY, durationMs);

    // ------------------------------------------------------------------ 按键

    /// <summary>
    /// 按一颗系统键（返回 / 主页 / 音量 / 电源…）。<paramref name="repeat"/> 次按键会拼成**一条** adb 命令。
    /// </summary>
    public AdbResult Key(AndroidKey key, int repeat = 1, bool longPress = false, int timeoutMs = 8000)
    {
        string arguments = AndroidKeys.BuildKeyEventArguments(key, repeat, longPress);
        AdbResult result = Run(arguments, timeoutMs, quiet: true);
        if (result.Ok)
        {
            KeyCount += longPress ? 1 : Math.Clamp(repeat, 1, 20);
            LastInjection = longPress
                ? $"长按 {AndroidKeys.Label(key)}（{AndroidKeys.Wire(key)}）"
                : Math.Clamp(repeat, 1, 20) > 1
                    ? $"{AndroidKeys.Label(key)} × {Math.Clamp(repeat, 1, 20)}"
                    : AndroidKeys.Label(key);
            _log?.Invoke($"按键 → {AndroidKeys.Label(key)}（{arguments}）");
        }

        return result;
    }

    // ------------------------------------------------------------------ 文字

    /// <summary>
    /// 按计划发送文字（计划由 <see cref="AndroidText.BuildPlan"/> 生成，可能是一条也可能是几条命令）。
    /// 中途失败就停下并如实回报 —— 不继续把剩下的片段发出去（半截文字比发不出去更难解释）。
    /// </summary>
    public AdbResult SendText(AndroidTextPlan plan, int timeoutMs = 12000)
    {
        if (!plan.Ok)
        {
            var refused = new AdbResult(-1, "", plan.Note, false, "（未发送）");
            LastResult = refused;
            _log?.Invoke("文字未发送：" + plan.Note);
            return refused;
        }

        AdbResult last = new(0, "", "", false, "（未执行）");
        foreach (string command in plan.Commands)
        {
            last = Run(command, timeoutMs, quiet: true);
            if (!last.Ok) break;
        }

        if (last.Ok)
        {
            TextCount++;
            LastInjection = $"{plan.ModeLabel}：{plan.Commands.Count} 条命令";
            _log?.Invoke($"文字已发送（{plan.ModeLabel}，{plan.Commands.Count} 条命令）");
        }

        LastResult = last;
        return last;
    }

    // ------------------------------------------------------------------ 截屏

    /// <summary>手机截屏（PNG 原始字节，不落明文文件由调用方决定）。</summary>
    public AndroidCapture Screenshot(int timeoutMs = 25000)
    {
        string arguments = "exec-out screencap -p";
        string? serial = _serial();
        if (string.IsNullOrWhiteSpace(serial))
        {
            _log?.Invoke("截屏失败：没有已连接的安卓设备");
            return new AndroidCapture(false, null, 0, 0, "没有已连接的安卓设备", $"adb {arguments}");
        }

        AdbBinaryResult result = _adb.RunOnBinary(serial, arguments, timeoutMs);
        if (!result.Ok)
        {
            _log?.Invoke($"截屏失败：{result.ErrorText}");
            return new AndroidCapture(false, null, 0, 0, result.ErrorText, result.CommandLine);
        }

        (int Width, int Height)? size = PngProbe.ReadSize(result.StdOut);
        if (size is null)
        {
            // 老机型/个别 ROM 的 screencap 不给 PNG（原始 RGBA）：不猜格式，如实报错
            string why = "手机传回来的不是 PNG：" + PngProbe.Describe(result.StdOut);
            _log?.Invoke("截屏失败：" + why);
            return new AndroidCapture(false, null, 0, 0, why, result.CommandLine);
        }

        _log?.Invoke($"截屏成功：{size.Value.Width}×{size.Value.Height}，{result.StdOut.Length / 1024} KB");
        return new AndroidCapture(true, result.StdOut, size.Value.Width, size.Value.Height, null, result.CommandLine);
    }

    // ------------------------------------------------------------------ 内部

    private AdbResult Run(string arguments, int timeoutMs, bool quiet = false)
    {
        string? serial = _serial();
        if (string.IsNullOrWhiteSpace(serial))
        {
            var missing = new AdbResult(-1, "", "没有已连接的安卓设备", false, $"adb {arguments}");
            LastResult = missing;
            return missing;
        }

        // 用 -s 明确指定设备：同时插着模拟器/真机时不能靠"唯一设备"的默认行为
        AdbResult result = _adb.RunOn(serial, arguments, timeoutMs);
        LastResult = result;
        if (result.Ok)
        {
            InjectedCount++;
            if (!quiet) _log?.Invoke($"{arguments} → 成功（第 {InjectedCount} 次）");
        }
        else
        {
            _log?.Invoke($"{arguments} → 失败：{result.Text}");
        }

        return result;
    }

    /// <summary>坐标格式化（日志用）。</summary>
    public static string Format(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"({x},{y})");
}
