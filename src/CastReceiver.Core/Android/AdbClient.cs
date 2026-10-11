using System.Diagnostics;
using System.Text;

namespace CastReceiver.Core.Android;

/// <summary>一次 adb 调用的结果。<see cref="Ok"/> 只代表"命令成功退出"，不代表业务成功。</summary>
public sealed record AdbResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, string CommandLine)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>stdout + stderr 合并后的可读文本（adb 把不少错误写在 stdout 上）。</summary>
    public string Text
    {
        get
        {
            string text = (StdOut + "\n" + StdErr).Trim();
            return text.Length == 0 ? $"（无输出，退出码 {ExitCode}）" : text;
        }
    }

    /// <summary>取最后一行非空输出（adb 的成功提示通常就一行）。</summary>
    public string LastLine
    {
        get
        {
            string[] lines = Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[^1].Trim();
        }
    }
}

/// <summary>
/// adb 可执行文件的定位与调用。
///
/// 顺序：**随程序带的这一份**（Assets\adb\adb.exe）→ PATH → ANDROID_HOME / ANDROID_SDK_ROOT
/// → 常见安装位置。自带优先是为了"用户什么都没装也能用"，而 PATH 兜底是为了
/// 让开发机上已经有的、版本更新的 adb 也能被用上（当自带的被删掉时）。
///
/// 调用一律走进程、带超时：adb 会因为"设备离线"这类情况卡很久，
/// 界面线程不能被它拖住（所有调用都在后台线程上）。
/// </summary>
public sealed class AdbClient
{
    private readonly string _exe;

    public AdbClient(string executablePath)
    {
        _exe = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
    }

    /// <summary>本次实际使用的 adb 路径（界面上要如实显示，便于排查）。</summary>
    public string ExecutablePath => _exe;

    /// <summary>这份 adb 是不是"随程序带的"。</summary>
    public bool IsBundled => _exe.StartsWith(BundledDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>随程序发布的 adb 目录（发布版里是 Assets\adb）。</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "adb");

    /// <summary>随程序发布的 adb 可执行文件路径（不保证存在）。</summary>
    public static string BundledExecutable => Path.Combine(BundledDirectory, "adb.exe");

    /// <summary>找到一份可用的 adb；找不到返回 null（界面据此显示「本机没有 adb」）。</summary>
    public static string? Locate()
    {
        var candidates = new List<string>();

        try
        {
            if (File.Exists(BundledExecutable)) candidates.Add(BundledExecutable);
        }
        catch
        {
            // 忽略
        }

        // PATH 上的 adb
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try { candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); } catch { }
                }
            }
        }
        catch
        {
            // 忽略
        }

        // Android SDK 常见位置
        foreach (string root in new[]
                 {
                     Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "",
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
                     @"C:\Android\android-sdk",
                     @"C:\platform-tools",
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try { candidates.Add(Path.Combine(root, "platform-tools", "adb.exe")); } catch { }
            try { candidates.Add(Path.Combine(root, "adb.exe")); } catch { }
        }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        return null;
    }

    /// <summary>创建客户端；找不到 adb 时返回 null。</summary>
    public static AdbClient? Create() => Locate() is { } exe ? new AdbClient(exe) : null;

    // ------------------------------------------------------------------ 调用

    /// <summary>跑一条 adb 命令（同步阻塞，必须从后台线程调用）。</summary>
    public AdbResult Run(string arguments, int timeoutMs = 20000)
    {
        var info = new ProcessStartInfo(_exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        string commandLine = $"adb {arguments}";
        try
        {
            using var process = new Process { StartInfo = info };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new AdbResult(-1, "", "无法启动 adb 进程", false, commandLine);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool exited = process.WaitForExit(Math.Max(1000, timeoutMs));
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new AdbResult(-1, stdout.ToString(), stderr.ToString(), true, commandLine);
            }

            // 收尾等待异步输出读干（WaitForExit(int) 不保证输出已读完）
            try { process.WaitForExit(); } catch { }
            return new AdbResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false, commandLine);
        }
        catch (Exception ex)
        {
            return new AdbResult(-1, "", $"{ex.GetType().Name}: {ex.Message}", false, commandLine);
        }
    }

    /// <summary>指定设备的命令（<c>-s serial</c>）。</summary>
    public AdbResult RunOn(string serial, string arguments, int timeoutMs = 20000)
        => Run($"-s {Quote(serial)} {arguments}", timeoutMs);

    /// <summary>参数里可能有空格/特殊字符时加引号。</summary>
    public static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
