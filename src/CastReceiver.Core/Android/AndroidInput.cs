using System.Globalization;

namespace CastReceiver.Core.Android;

/// <summary>
/// 往手机注入触摸（点击 / 滑动）。
///
/// 边界（按需求定的）：**只做鼠标能表达的两件事** —— 点击→点按、滚轮/拖动→滑动。
/// 不做键盘输入，所以不需要手机装输入法，也不申请任何额外权限。
///
/// 走的是 <c>adb shell input</c>（`input tap` / `input swipe`），
/// 这是 Android 自带、任何一台开了调试的手机都有的能力。
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

    /// <summary>已注入的触摸事件数（界面/自检用来确认真的发出去了）。</summary>
    public int InjectedCount { get; private set; }

    /// <summary>最近一次注入的结果（失败原因要能查到）。</summary>
    public AdbResult? LastResult { get; private set; }

    /// <summary>点按。</summary>
    public AdbResult Tap(int x, int y, int timeoutMs = 8000)
        => Run($"shell input tap {x} {y}", timeoutMs);

    /// <summary>从一点滑到另一点（拖动、滚轮都用它）。</summary>
    public AdbResult Swipe(int x1, int y1, int x2, int y2, int durationMs = 220, int timeoutMs = 12000)
    {
        int duration = Math.Clamp(durationMs, 30, 5000);
        return Run($"shell input swipe {x1} {y1} {x2} {y2} {duration}", timeoutMs);
    }

    /// <summary>在一点上按住再滑（长按拖动），先按下、滑、再抬手。</summary>
    public AdbResult Drag(int fromX, int fromY, int toX, int toY, int durationMs = 320)
        => Swipe(fromX, fromY, toX, toY, durationMs);

    private AdbResult Run(string arguments, int timeoutMs)
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
            _log?.Invoke($"{arguments} → 成功（第 {InjectedCount} 次）");
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
