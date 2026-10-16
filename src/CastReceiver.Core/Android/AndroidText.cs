using System.Text;

namespace CastReceiver.Core.Android;

/// <summary>电脑往手机送文字的三条路，按"能不能用"的顺序选。</summary>
public enum AndroidTextMode
{
    /// <summary><c>adb shell input text</c>：任何一台安卓都有，但**只认 ASCII**（中文它直接报错）。</summary>
    InputText,

    /// <summary>手机上装了 ADBKeyboard（一个开源输入法）：走广播，**中文 / emoji 都行**。</summary>
    AdbKeyboard,

    /// <summary>这段文字这条路送不了（原因写在 <see cref="AndroidTextPlan.Note"/> 里，界面必须如实说）。</summary>
    Unsupported,
}

/// <summary>一次文字发送的执行计划（命令是拼好的，直接交给 adb 即可）。</summary>
public sealed record AndroidTextPlan(AndroidTextMode Mode, IReadOnlyList<string> Commands, string Note)
{
    public bool Ok => Mode != AndroidTextMode.Unsupported && Commands.Count > 0;

    /// <summary>给界面/日志看的一句话（不含手机型号，纯说这条路是什么）。</summary>
    public string ModeLabel => Mode switch
    {
        AndroidTextMode.InputText => "input text（英文/数字/符号）",
        AndroidTextMode.AdbKeyboard => "ADBKeyboard 广播（支持中文）",
        _ => "无法发送",
    };
}

/// <summary>
/// 往手机里注入文字。
///
/// 这里有两层"引号"，必须分开处理，混在一起就会出现"文字被截断 / 被 shell 当命令执行"：
/// <list type="number">
/// <item><b>Windows 这一层</b>：参数串最终交给 <c>CreateProcess</c>，由 Windows 自己拆 argv。
///       所以给设备的引号必须再按 Windows 规则转义一次（见 <see cref="WindowsArgumentEscape"/>）；</item>
/// <item><b>设备这一层</b>：adb 把参数用空格拼起来交给手机的 <c>sh</c>，
///       所以要按 POSIX 单引号规则包起来（见 <see cref="DeviceShellQuote"/>）。</item>
/// </list>
/// 两层各转义一次才能保证"用户看到什么，手机里就出现什么"；
/// 顺带把 <c>;</c><c>$()</c><c>`</c> 这类字符关在单引号里，不会被手机当命令执行。
/// </summary>
public static class AndroidText
{
    /// <summary>ADBKeyboard 的输入法组件名（装上后用它接收电脑发来的文字）。</summary>
    public const string AdbKeyboardIme = "com.android.adbkeyboard/.AdbIME";

    /// <summary>一条 <c>input text</c> 最多带多少字符（太长会撞命令行长度上限，分段发更稳）。</summary>
    public const int MaxInputTextChunk = 200;

    // ------------------------------------------------------------------ 能力判定（纯函数）

    /// <summary>是不是"纯 ASCII 可见字符"（<c>input text</c> 只认这些）。</summary>
    public static bool IsAsciiPrintable(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~') return false;
        }
        return true;
    }

    /// <summary>
    /// <c>adb shell ime list -s</c> 的输出里有没有 ADBKeyboard。
    /// 认不出就是没装 —— 这里宁可保守（装了就一定能看到那条组件），也不能乐观（乐观会让人按了没反应）。
    /// </summary>
    public static bool LooksLikeAdbKeyboard(string? imeListOutput)
        => !string.IsNullOrWhiteSpace(imeListOutput)
           && imeListOutput.Contains("adbkeyboard", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 转义

    /// <summary>设备侧（手机 sh）的单引号转义：<c>'</c> → <c>'\''</c>，其余字符原样。</summary>
    public static string DeviceShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "'\\''") + "'";

    /// <summary>
    /// Windows argv 转义（把一段文本编码成**一个**参数）。
    /// 规则来自 CommandLineToArgvW：反斜杠只有在引号前（或参数结尾）才成对加倍，引号本身用 <c>\"</c>。
    /// </summary>
    public static string WindowsArgumentEscape(string value)
    {
        string text = value ?? "";
        if (text.Length == 0) return "\"\"";

        bool needQuote = text.Any(c => c is ' ' or '\t' or '"');
        if (!needQuote) return text;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in text)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        // 结尾的反斜杠会跟收尾引号粘在一起，必须加倍，否则参数会"多出一个引号"
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 把一段文字切成若干段，**每一段都不含 <c>%s</c>**。
    ///
    /// 为什么：安卓的 <c>input text</c> 里有一段历史行为 —— 把 <c>%s</c> 直接换成一个空格
    /// （早年用它来送空格，因为没有别的办法）。我们要送的是用户真的想打的 <c>%s</c>，
    /// 所以遇到 <c>%s</c> 就在中间断开：<c>"a%sb"</c> → <c>["a%", "sb"]</c>，两段分开送，谁都碰不到 <c>%s</c>。
    /// </summary>
    public static IReadOnlyList<string> SplitAroundPercentS(string text)
    {
        var segments = new List<string>();
        if (string.IsNullOrEmpty(text)) return segments;

        int start = 0;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '%' || text[i + 1] != 's') continue;
            segments.Add(text[start..(i + 1)]);      // 到 % 为止（含 %）
            start = i + 2;                            // 跳过 %s，下一段从 s 开始
            i++;
        }

        if (start < text.Length) segments.Add(text[start..]);
        return segments;
    }

    /// <summary>把一段文字再按长度上限切成几段（都还满足"不含 %s"）。</summary>
    public static IReadOnlyList<string> Chunk(IReadOnlyList<string> segments, int maxLength = MaxInputTextChunk)
    {
        var result = new List<string>();
        int limit = Math.Max(1, maxLength);
        foreach (string segment in segments)
        {
            if (segment.Length <= limit)
            {
                result.Add(segment);
                continue;
            }

            for (int at = 0; at < segment.Length; at += limit)
            {
                result.Add(segment.Substring(at, Math.Min(limit, segment.Length - at)));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ 计划

    /// <summary>
    /// 给定文字与"手机上有没有 ADBKeyboard"，算出要跑的命令。
    /// 送不了就返回 <see cref="AndroidTextMode.Unsupported"/> 并写清原因 —— 绝不假装成功。
    /// </summary>
    public static AndroidTextPlan BuildPlan(string text, bool adbKeyboardInstalled)
    {
        string value = text ?? "";
        if (value.Length == 0)
        {
            return new AndroidTextPlan(AndroidTextMode.Unsupported, Array.Empty<string>(), "没有要发送的文字");
        }

        if (IsAsciiPrintable(value))
        {
            var commands = new List<string>();
            foreach (string chunk in Chunk(SplitAroundPercentS(value)))
            {
                commands.Add("shell input text " + WindowsArgumentEscape(DeviceShellQuote(chunk)));
            }

            string note = value.Contains("%s", StringComparison.Ordinal)
                ? $"英文/数字：分 {commands.Count} 段发送（文字里有 %s，安卓的 input text 会把它当空格，只能拆开发）"
                : $"英文/数字：{commands.Count} 条 input text 命令";
            return new AndroidTextPlan(AndroidTextMode.InputText, commands, note);
        }

        if (adbKeyboardInstalled)
        {
            // 中文走广播：**用 base64 传**，于是不管文字里有什么符号，
            // 都不需要再操心引号/转义（base64 只有 A-Za-z0-9+/=）。
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return new AndroidTextPlan(AndroidTextMode.AdbKeyboard,
                new[] { $"shell am broadcast -a ADB_INPUT_B64 --es msg {encoded}" },
                "中文/emoji：走 ADBKeyboard（base64 广播）");
        }

        string sample = FirstNonAscii(value, out int count);
        return new AndroidTextPlan(AndroidTextMode.Unsupported, Array.Empty<string>(),
            $"这段文字里有 {count} 个非 ASCII 字符（例如「{sample}」）：安卓自带的 input text 只送得了英文数字。" +
            "手机上装一个 ADBKeyboard 输入法后重试，或直接用手机上的输入法打这几个字");
    }

    /// <summary>找出第一个非 ASCII 字符（错误提示里举例用，不显示全文，避免在界面上复述用户输入）。</summary>
    public static string FirstNonAscii(string text, out int count)
    {
        count = 0;
        char first = '\0';
        foreach (char c in text ?? "")
        {
            if (c is >= ' ' and <= '~') continue;
            count++;
            if (first == '\0') first = c;
        }

        return first == '\0' ? "" : first.ToString();
    }

    // ------------------------------------------------------------------ 物理键盘 → 字符

    /// <summary>
    /// 物理键盘按键名（WPF <c>Key</c> 的 ToString()）→ 要送进手机的**字符**（键盘直通用）。
    ///
    /// 为什么不直接接 WPF 的 TextInput 事件：那条路要"当前有能收文字的焦点元素"才会冒泡到窗口，
    /// 而直通时焦点多半停在按钮/窗口上。这里自己查表，**不依赖焦点**,也就能把
    /// "敲了什么字符"钉在自检里（错了不会崩，只会往手机里打错字）。
    /// 只收 ASCII 可见字符：中文得走输入法，安卓的 input text 也送不了。
    /// </summary>
    public static char? CharFromVirtualKeyName(string? name, bool shift)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = name.Trim();

        // 字母
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
        {
            char lower = char.ToLowerInvariant(key[0]);
            return shift ? char.ToUpperInvariant(lower) : lower;
        }

        // 主键盘数字（Shift 出符号）
        if (key.Length == 2 && key[0] == 'D' && key[1] is >= '0' and <= '9')
        {
            int digit = key[1] - '0';
            const string shifted = ")!@#$%^&*(";
            return shift ? shifted[digit] : (char)('0' + digit);
        }

        // 小键盘数字（不分 Shift）
        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && key.Length == 7 && key[6] is >= '0' and <= '9')
        {
            return key[6];
        }

        return key switch
        {
            "OemMinus" => shift ? '_' : '-',
            "OemPlus" => shift ? '+' : '=',
            "OemOpenBrackets" => shift ? '{' : '[',
            "OemCloseBrackets" => shift ? '}' : ']',
            "OemSemicolon" => shift ? ':' : ';',
            "OemQuotes" => shift ? '"' : '\'',
            "OemComma" => shift ? '<' : ',',
            "OemPeriod" => shift ? '>' : '.',
            "OemQuestion" => shift ? '?' : '/',
            "OemTilde" => shift ? '~' : '`',
            "OemPipe" or "OemBackslash" => shift ? '|' : '\\',
            _ => null,
        };
    }
}
