using System.Collections.Concurrent;

namespace CastReceiver.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level,-5}] {Category,-9} {Message}";
}

/// <summary>进程内轻量日志：既写文件（滚动）、也广播给界面，同时可选回显到控制台（自检模式）。</summary>
public sealed class AppLog : IDisposable
{
    private readonly BlockingCollection<string> _fileQueue = new(new ConcurrentQueue<string>());
    private readonly string? _filePath;
    private readonly Thread? _writer;
    private readonly object _gate = new();

    public event Action<LogEntry>? EntryWritten;
    public bool EchoToConsole { get; set; }
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// 隐私模式（默认开）：投屏会话事件（谁、什么时候连上/断开）只广播给界面，不写日志文件。
    /// 需求很明确——投屏的开始时间、结束时间不能让本人之外的人知道，所以这些事件默认不落盘。
    /// </summary>
    public bool PrivacyMode { get; set; } = true;

    public AppLog(string? directory = null, string fileName = "castreceiver.log")
    {
        try
        {
            directory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, fileName);
            // 简单滚动：超过 4MB 就重建
            if (File.Exists(_filePath) && new FileInfo(_filePath).Length > 4 * 1024 * 1024)
                File.Move(_filePath, _filePath + ".1", overwrite: true);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "AppLog" };
            _writer.Start();
        }
        catch { _filePath = null; }
    }

    public string? FilePath => _filePath;

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public void Error(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// 会话事件（连接/断开/开始结束投屏）：隐私模式下只进界面，不落盘。
    /// 需要长期留痕时把设置里的「隐私日志」关掉即可恢复写入。
    /// </summary>
    public void Event(string category, string message)
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Info, category, message);
        if (EchoToConsole) Console.WriteLine(entry.ToString());
        try { EntryWritten?.Invoke(entry); } catch { }
        if (PrivacyMode) return;
        lock (_gate) { try { _fileQueue.Add(entry.ToString()); } catch { } }
    }

    /// <summary>日志文件路径（设置页用来提示与清理）。</summary>
    public string? LogFilePath => _filePath;

    /// <summary>删除已写入的日志文件（含滚动副本），返回是否成功。</summary>
    public bool TryClearFile()
    {
        try
        {
            if (_filePath is null) return false;
            if (File.Exists(_filePath)) File.Delete(_filePath);
            string rolled = _filePath + ".1";
            if (File.Exists(rolled)) File.Delete(rolled);
            return true;
        }
        catch { return false; }
    }

    private void Write(LogLevel level, string category, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTime.Now, level, category, message);
        if (EchoToConsole) Console.WriteLine(entry.ToString());
        try { EntryWritten?.Invoke(entry); } catch { }
        lock (_gate) { try { _fileQueue.Add(entry.ToString()); } catch { } }
    }

    private void WriterLoop()
    {
        foreach (var line in _fileQueue.GetConsumingEnumerable())
        {
            try { if (_filePath is not null) File.AppendAllText(_filePath, line + Environment.NewLine); } catch { }
        }
    }

    public void Dispose()
    {
        try { _fileQueue.CompleteAdding(); } catch { }
        try { _writer?.Join(500); } catch { }
    }
}