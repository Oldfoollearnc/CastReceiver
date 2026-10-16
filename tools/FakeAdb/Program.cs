using System.Globalization;
using System.Text;

namespace FakeAdb;

/// <summary>
/// 「假的 adb」：给「电脑控制手机」做端到端自检用的桩程序（**不参与产品构建**）。
///
/// 它做两件事：
/// 1) **按 adb 的真实输出格式回话**：devices -l / getprop / dumpsys display / wm size / ime list -s /
///    connect / mdns services / exec-out screencap …；于是程序里"发现 → 连接 → 读屏幕 → 注入"整条链路都能跑完；
/// 2) **把每次调用原样写进日志**（环境变量 FAKE_ADB_LOG 指定的文件），并且额外还原出
///    "手机那侧真正收到的命令行" —— 这一条才是关键证据：它证明我们发的 `input text '...'`
///    经过 Windows argv 与设备 sh 两层引号之后，文字**没有被吃掉、也没有被拆错**。
///
/// 日志一行一次调用，字段用 ASCII 的单元分隔符（0x1F）隔开：
///   &lt;时间&gt; US CALL      US argc=N US argv=v0 US v1 US …      （收到的 argv 原样）
///   &lt;时间&gt; US INJECT    US tap      US x US y
///   &lt;时间&gt; US INJECT    US swipe    US x1 US y1 US x2 US y2 US duration
///   &lt;时间&gt; US INJECT    US keyevent US 逗号分隔的 keycode
///   &lt;时间&gt; US INJECT    US text     US 手机最终会打出来的那串文字
///   &lt;时间&gt; US INJECT    US b64      US base64
///   &lt;时间&gt; US INJECT    US b64text  US 解码后的文字
/// </summary>
internal static class Program
{
    /// <summary>桩程序假装已连上的那台设备（与自检脚本注入的发现结果一致）。</summary>
    private const string Serial = "127.0.0.1:37251";

    private static readonly object LogGate = new();

    private static int Main(string[] argv)
    {
        string[] args = argv;
        string serial = Serial;

        // adb 的 -s <serial> 前缀
        if (args.Length >= 2 && args[0] == "-s")
        {
            serial = args[1];
            args = args[2..];
        }

        LogCall(argv);

        string command = args.Length > 0 ? args[0] : "";

        switch (command)
        {
            case "version":
                Write("Android Debug Bridge version 1.0.41");
                Write("Version 37.0.1-fake (自检桩程序)");
                Write($"Installed as {Environment.ProcessPath}");
                return 0;

            case "start-server":
            case "kill-server":
            case "wait-for-device":
            case "root":
            case "remount":
                return 0;

            case "devices":
                // 注意：这里必须报出"已连上的那台"，程序的 RefreshCoreAsync 靠它认设备
                Write("List of devices attached");
                Write($"{Serial}     device product:panther model:Pixel_7_Fake device:panther transport_id:3");
                return 0;

            case "connect":
                Write($"connected to {args.ElementAtOrDefault(1) ?? Serial}");
                return 0;

            case "disconnect":
                Write($"disconnected {args.ElementAtOrDefault(1) ?? Serial}");
                return 0;

            case "pair":
                Write($"Successfully paired to {args.ElementAtOrDefault(1) ?? Serial}");
                return 0;

            case "mdns":
                foreach (string line in MdnsOutput().Split('\n')) Write(line);
                return 0;

            case "shell":
                return RunShell(args.Skip(1).ToArray());

            case "exec-out":
                if (args.Contains("screencap")) return DumpScreen();
                return 0;

            default:
                return 0;
        }
    }

    // ------------------------------------------------------------------ 设备侧 shell

    private static int RunShell(string[] parts)
    {
        // adb 会把 shell 之后的参数**用空格拼起来**交给手机上的 sh —— 这里照做，
        // 于是日志里的 shell 行就是"手机真正会执行的那一行"。
        string line = string.Join(' ', parts);
        LogField("SHELL", line);

        List<string> tokens = ShellSplit(line);
        if (tokens.Count == 0) return 0;

        if (tokens[0] == "getprop")
        {
            Write(tokens.ElementAtOrDefault(1) switch
            {
                "ro.build.version.sdk" => "34",
                "ro.build.version.release" => "14",
                "ro.product.model" => "Pixel_7_Fake",
                _ => "",
            });
            return 0;
        }

        if (tokens[0] == "wm")
        {
            Write("Physical size: 1080x2400");
            return 0;
        }

        if (tokens[0] == "ime")
        {
            foreach (string ime in (Environment.GetEnvironmentVariable("FAKE_ADB_IME")
                                    ?? "com.android.adbkeyboard/.AdbIME\ncom.baidu.input/.ImeService").Split('\n'))
            {
                Write(ime.Trim());
            }
            return 0;
        }

        if (tokens[0] == "dumpsys")
        {
            if (tokens.ElementAtOrDefault(1) == "display")
            {
                // 真实样本的裁剪版：旋转 0，当前方向 1080×2400
                Write("  mViewports=[DisplayViewport{type=INTERNAL, valid=true, displayId=0, orientation=0}]");
                Write("    mCurrentOrientation=0");
                Write("    mBaseDisplayInfo=DisplayInfo{\"Built-in Screen\", real 1080 x 2400, rotation 0}");
                return 0;
            }

            if (tokens.ElementAtOrDefault(1) == "input")
            {
                Write("Input Reader State:");
                Write("  SurfaceOrientation: 0");
                return 0;
            }

            return 0;
        }

        if (tokens[0] == "settings")
        {
            Write("0");
            return 0;
        }

        if (tokens[0] == "input")
        {
            RecordInput(tokens);
            return 0;
        }

        if (tokens[0] == "am" && tokens.ElementAtOrDefault(1) == "broadcast")
        {
            RecordBroadcast(tokens);
            return 0;
        }

        return 0;
    }

    private static void RecordInput(List<string> tokens)
    {
        string kind = tokens.ElementAtOrDefault(1) ?? "";

        switch (kind)
        {
            case "tap":
                LogField("INJECT", "tap",
                    tokens.ElementAtOrDefault(2) ?? "",
                    tokens.ElementAtOrDefault(3) ?? "");
                break;

            case "swipe":
                LogField("INJECT", "swipe",
                    tokens.ElementAtOrDefault(2) ?? "", tokens.ElementAtOrDefault(3) ?? "",
                    tokens.ElementAtOrDefault(4) ?? "", tokens.ElementAtOrDefault(5) ?? "",
                    tokens.ElementAtOrDefault(6) ?? "");
                break;

            case "keyevent":
                // 允许 --longpress 前缀
                string codes = string.Join(',', tokens.Skip(2).Where(t => !t.StartsWith("--", StringComparison.Ordinal)));
                LogField("INJECT", "keyevent", codes);
                break;

            case "text":
                // 手机最终会打出来的那串文字（引号已经被 shell 剥掉）
                LogField("INJECT", "text", string.Join(' ', tokens.Skip(2)));
                break;
        }
    }

    private static void RecordBroadcast(List<string> tokens)
    {
        // 形如：am broadcast -a ADB_INPUT_B64 --es msg <base64>
        int actionAt = tokens.IndexOf("-a");
        string action = actionAt >= 0 ? tokens.ElementAtOrDefault(actionAt + 1) ?? "" : "";
        int msgAt = tokens.IndexOf("msg");
        string payload = msgAt >= 0 ? tokens.ElementAtOrDefault(msgAt + 1) ?? "" : "";

        LogField("INJECT", "broadcast", action, payload);

        if (action.EndsWith("ADB_INPUT_B64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                LogField("INJECT", "b64text", decoded);
            }
            catch (Exception ex)
            {
                LogField("INJECT", "b64text", "(解码失败：" + ex.Message + ")");
            }
        }
    }

    // ------------------------------------------------------------------ 截屏

    private static int DumpScreen()
    {
        byte[]? png = null;
        string? source = Environment.GetEnvironmentVariable("FAKE_ADB_SCREEN");
        try
        {
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
            {
                png = File.ReadAllBytes(source);
            }
        }
        catch
        {
            // 读不到就退回下面的最小 PNG
        }

        png ??= MinimalPng(1080, 2400);
        using Stream stdout = Console.OpenStandardOutput();
        stdout.Write(png, 0, png.Length);
        stdout.Flush();
        LogField("SCREEN", png.Length.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>只有一个 IHDR 的最小 PNG（够程序判定"这是图"并读出尺寸；脚本一般会给一张真图）。</summary>
    private static byte[] MinimalPng(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        WriteBigEndian(bytes, 16, width);
        WriteBigEndian(bytes, 20, height);
        return bytes;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    // ------------------------------------------------------------------ 发现结果

    private static string MdnsOutput()
    {
        string? file = Environment.GetEnvironmentVariable("FAKE_ADB_MDNS");
        try
        {
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) return File.ReadAllText(file);
        }
        catch
        {
            // 忽略
        }

        // 默认：连接端点 + 配对端点都广播（真实手机开着无线调试就是这样）
        return "List of discovered mdns services\n" +
               $"adb-FAKE-CONNECT\t_adb-tls-connect._tcp\t{Serial}\n" +
               "adb-FAKE-PAIR\t_adb-tls-pairing._tcp\t127.0.0.1:41235\n";
    }

    // ------------------------------------------------------------------ 设备侧 sh 的单引号还原

    /// <summary>
    /// 按 POSIX sh 的规则把一行命令行拆成参数（单引号内原样、双引号内允许转义、空白分隔）。
    ///
    /// 这一步是"我们的引号转义到底对不对"的证据来源：程序发的是
    /// <c>shell input text '你好 世界'</c>，拆出来必须正好是一个参数 <c>你好 世界</c>。
    /// </summary>
    internal static List<string> ShellSplit(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool hasToken = false;
        char quote = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (quote == '\0' && (c == ' ' || c == '\t'))
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }

            if (quote == '\0' && (c == '\'' || c == '"'))
            {
                quote = c;
                hasToken = true;
                continue;
            }

            if (quote != '\0' && c == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote == '"' && c == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
                hasToken = true;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }

    // ------------------------------------------------------------------ 输出与日志

    private static void Write(string text)
    {
        try
        {
            Console.Out.Write(text);
            Console.Out.Write('\n');
        }
        catch
        {
            // 被管道关掉就算了
        }
    }

    /// <summary>把收到的 argv 原样记一行（字段用 0x1F 分隔）。</summary>
    private static void LogCall(string[] argv)
    {
        var fields = new List<string>
        {
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            "CALL",
            "argc=" + argv.Length.ToString(CultureInfo.InvariantCulture),
        };
        fields.AddRange(argv);
        AppendLine(fields);
    }

    private static void LogField(params string[] fields)
    {
        // 每条日志都以时间开头，这样自检脚本按固定字段位置取值就够了（B 段曾经因为
        // INJECT 行少了时间字段而"明明发了却查不到"，别再去掉这个前缀）
        var all = new List<string> { DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) };
        all.AddRange(fields);
        AppendLine(all);
    }

    private static void AppendLine(IEnumerable<string> fields)
    {
        string? path = Environment.GetEnvironmentVariable("FAKE_ADB_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;

        string line = string.Join('\u001F', fields.Select(Sanitize));
        try
        {
            lock (LogGate)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不该影响自检本身
        }
    }

    /// <summary>换行/分隔符会破坏日志格式，替换掉（自检的文字样本里不会有这些）。</summary>
    private static string Sanitize(string value)
        => (value ?? "").Replace('\u001F', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
