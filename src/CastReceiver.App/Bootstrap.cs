using System.Diagnostics;
using System.IO;

namespace CastReceiver.App;

/// <summary>启动引导日志：AppLog 还没建好之前，用它记录启动各阶段耗时。</summary>
internal static class Bootstrap
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastReceiver", "startup.log");

    static Bootstrap()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, $"启动跟踪 {DateTime.Now:HH:mm:ss.fff}\n");
        }
        catch { }
    }

    public static void Log(string step)
    {
        try { File.AppendAllText(Path, $"{Watch.ElapsedMilliseconds,6} ms  {step}\n"); } catch { }
    }
}
