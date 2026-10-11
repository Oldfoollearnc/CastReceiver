using System.Globalization;
using System.Text.RegularExpressions;

namespace CastReceiver.Core.Android;

/// <summary>adb 列出的一台设备。</summary>
public sealed record AdbDevice(string Serial, string State)
{
    /// <summary>product: / model: / device: / transport_id: 这些附加属性（adb devices -l 才有）。</summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } = new Dictionary<string, string>();

    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public string? Model => Extras.TryGetValue("model", out string? m) ? m : null;

    /// <summary>序列号带端口（192.168.1.5:5555）说明是**无线**连接的。</summary>
    public bool IsWireless => Serial.Contains(':');

    public string Host => IsWireless ? Serial[..Serial.LastIndexOf(':')] : "";

    public int Port => IsWireless && int.TryParse(Serial[(Serial.LastIndexOf(':') + 1)..], out int port) ? port : 0;

    public string DisplayName
    {
        get
        {
            string? model = Model;
            return string.IsNullOrWhiteSpace(model) ? Serial : $"{model}（{Serial}）";
        }
    }
}

/// <summary>
/// 手机的屏幕信息。
///
/// 三个尺寸各有各的用处，别混：
/// <list type="bullet">
/// <item><b>Reported</b>（<c>wm size</c>，Override 优先）：系统报的**分辨率**（自然方向，或用户改过的分辨率）；</item>
/// <item><b>Logical</b>（<c>dumpsys display</c> 的 real WxH）：**当前方向**下真正生效的显示尺寸
///       —— 横屏时它与 Reported 是互换的，这条才是"点按坐标"该用的尺寸；</item>
/// <item><b>Rotation</b>：0/1/2/3（0°/90°/180°/270°）。</item>
/// </list>
///
/// 为什么不能只靠 Reported + 旋转去推 Logical：**实测**（Android 14 模拟器执行
/// <c>wm size 2340x1080</c>）时，Reported 变成 2340×1080 而旋转仍是 0，
/// 若按"自然方向一定是竖屏"去 Min/Max 推算，会算成 1080×2340 —— 整个坐标全错。
/// </summary>
public sealed record AndroidScreen(int ReportedWidth, int ReportedHeight, int LogicalWidth, int LogicalHeight, int Rotation)
{
    /// <summary>旋转是不是左右横屏（宽高需要互换）。</summary>
    public bool IsSideways => Rotation is 1 or 3;

    /// <summary>当前方向下的屏幕宽（像素）—— 点按坐标用的就是这一组。</summary>
    public int CurrentWidth => LogicalWidth > 0
        ? LogicalWidth
        : (IsSideways ? Math.Max(ReportedWidth, ReportedHeight) : Math.Min(ReportedWidth, ReportedHeight));

    /// <summary>当前方向下的屏幕高（像素）。</summary>
    public int CurrentHeight => LogicalHeight > 0
        ? LogicalHeight
        : (IsSideways ? Math.Min(ReportedWidth, ReportedHeight) : Math.Max(ReportedWidth, ReportedHeight));

    /// <summary>系统报的分辨率（自然方向）—— 只有"自然方向坐标系"才用它。</summary>
    public int NaturalWidth => ReportedWidth;

    public int NaturalHeight => ReportedHeight;

    /// <summary>Logical 是从 dumpsys 里读到的，还是按旋转推算出来的（界面/自检要能区分证据强弱）。</summary>
    public bool HasMeasuredLogicalSize => LogicalWidth > 0 && LogicalHeight > 0;

    public bool IsValid => ReportedWidth > 0 && ReportedHeight > 0;
}

/// <summary>
/// adb 输出的解析（全是纯函数，便于用真实输出样本做自检）。
/// 解析原则与设备识别一致：**认不出就说认不出**（返回 null / -1），不猜一个可能错的默认值。
/// </summary>
public static partial class AdbParsers
{
    [GeneratedRegex(@"^\s*SurfaceOrientation\s*:\s*(\d)", RegexOptions.Multiline)] private static partial Regex SurfaceOrientationRegex();
    [GeneratedRegex(@"mCurrentOrientation\s*=\s*(\d)")] private static partial Regex CurrentOrientationRegex();
    [GeneratedRegex(@"[mM]Rotation\s*=\s*(\d+)")] private static partial Regex MRotationRegex();
    [GeneratedRegex(@"\brotation\s*=\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex RotationWordRegex();
    [GeneratedRegex(@"orientation\s*=\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex OrientationRegex();
    [GeneratedRegex(@"\breal\s+(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex RealSizeRegex();
    [GeneratedRegex(@"Physical size:\s*(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex PhysicalSizeRegex();
    [GeneratedRegex(@"Override size:\s*(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex OverrideSizeRegex();
    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)((?:\s+\w+:\S+)*)\s*$")] private static partial Regex DeviceLineRegex();

    /// <summary>
    /// 解析 <c>adb devices -l</c>。
    /// 形如：<c>192.168.1.5:5555  device product:x model:MI 13 device:y transport_id:2</c>；
    /// 也有 <c>unauthorized</c> / <c>offline</c> 这类状态（照样列出来，界面要如实显示）。
    /// </summary>
    public static IReadOnlyList<AdbDevice> ParseDevices(string? output)
    {
        var list = new List<AdbDevice>();
        if (string.IsNullOrWhiteSpace(output)) return list;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith('*')) continue;                 // "* daemon started successfully"
            if (line.StartsWith("adb server", StringComparison.OrdinalIgnoreCase)) continue;

            Match match = DeviceLineRegex().Match(line);
            if (!match.Success) continue;

            var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in match.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon > 0)
                {
                    extras[token[..colon]] = token[(colon + 1)..];
                }
            }

            list.Add(new AdbDevice(match.Groups[1].Value, match.Groups[2].Value) { Extras = extras });
        }

        return list;
    }

    /// <summary>
    /// 解析 <c>adb shell wm size</c>。**Override size 优先**：用户改过分辨率时，
    /// 真正生效的是 Override，而 Physical 只是硬件分辨率（拿错会让坐标整体偏移）。
    /// 返回的还只是"系统报的分辨率"；当前方向下的逻辑尺寸要靠 <see cref="ParseLogicalSize"/>。
    /// </summary>
    public static (int Width, int Height)? ParseReportedSize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        Match over = OverrideSizeRegex().Match(output);
        if (over.Success)
        {
            return (int.Parse(over.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(over.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        Match physical = PhysicalSizeRegex().Match(output);
        if (physical.Success)
        {
            return (int.Parse(physical.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(physical.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>
    /// 从 <c>dumpsys display</c> 里读**当前方向**的显示尺寸（<c>real W x H</c>）。
    /// 优先 <c>mOverrideDisplayInfo</c>（用户改过分辨率/模拟器改了尺寸时它才是生效值），
    /// 其次 <c>mBaseDisplayInfo</c>。读不到返回 null（调用方再按旋转推算）。
    /// </summary>
    public static (int Width, int Height)? ParseLogicalSize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        (int Width, int Height)? over = MatchRealSize(output, "mOverrideDisplayInfo=");
        if (over is not null) return over;

        return MatchRealSize(output, "mBaseDisplayInfo=");
    }

    private static (int Width, int Height)? MatchRealSize(string output, string marker)
    {
        int at = output.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        // 只在标记之后的一小段里找，避免抓到别的 DisplayInfo
        string tail = output[at..Math.Min(output.Length, at + 1200)];
        Match match = RealSizeRegex().Match(tail);
        if (!match.Success) return null;

        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 找屏幕旋转。不同 Android 版本写法不一样，按"最明确 → 最含糊"的顺序试；
    /// 一种都不匹配就返回 -1（由调用方决定怎么退化，不在这里偷偷当 0）。
    /// 实测（Android 14）：<c>dumpsys display</c> 的 <c>mCurrentOrientation=N</c> 最好用；
    /// 老版本是 <c>dumpsys input</c> 的 <c>SurfaceOrientation: N</c>。
    /// </summary>
    public static int ParseRotation(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return -1;

        foreach (Regex regex in new[] { CurrentOrientationRegex(), SurfaceOrientationRegex(), MRotationRegex(), RotationWordRegex(), OrientationRegex() })
        {
            Match match = regex.Match(output);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value) && value is >= 0 and <= 3)
            {
                return value;
            }
        }

        return -1;
    }

    /// <summary>解析 <c>getprop ro.build.version.sdk</c>（拿不到返回 -1）。</summary>
    public static int ParseSdk(string? output)
        => int.TryParse((output ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sdk) ? sdk : -1;

    /// <summary>解析 <c>getprop ro.build.version.release</c>。</summary>
    public static string? ParseAndroidRelease(string? output)
    {
        string text = (output ?? "").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary><c>adb connect</c> 的输出是否表示成功（"connected to ..." / "already connected to ..."）。</summary>
    public static bool ConnectSucceeded(string? output)
    {
        string text = output ?? "";
        return text.Contains("connected to", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// adb 的输出是否表示"这台 adb/这台手机不支持无线调试配对"。
    /// 典型：老 adb 不认识 pair 子命令（unknown command）；或手机端没有配对服务（failed to pair / protocol fault）。
    /// </summary>
    public static bool LooksLikePairingUnsupported(string? output)
    {
        string text = (output ?? "").ToLowerInvariant();
        return text.Contains("unknown command")            // adb 太老
            || text.Contains("failed to pair")
            || text.Contains("protocol fault")
            || text.Contains("cannot connect to pairing")
            || text.Contains("no pairing service");
    }
}
