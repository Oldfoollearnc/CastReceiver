using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace CastReceiver.App.Services;

/// <summary>开机自启（写 HKCU Run 键，不需要管理员）。</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "投屏Window";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return false;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return false;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else key.DeleteValue(ValueName, false);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>导出诊断包：把日志、配置、自检报告打成一个 zip，方便反馈问题。</summary>
public static class DiagnosticBundle
{
    public static string? Export(Core.Infrastructure.CastSettings settings, Core.Infrastructure.AppLog log, string? extra)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "投屏Window 诊断包");
            Directory.CreateDirectory(dir);
            string zipPath = Path.Combine(dir, $"诊断包_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            void AddText(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
                w.Flush();
            }

            string summary = new StringBuilder()
                .AppendLine($"投屏Window 诊断包  {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine(new string('-', 60))
                .AppendLine($"操作系统　　{Environment.OSVersion}")
                .AppendLine($"64 位进程　{Environment.Is64BitProcess}")
                .AppendLine($"运行时　　　{Environment.Version}")
                .AppendLine($"设备名　　　{settings.DeviceName}")
                .AppendLine($"画质档位　　{settings.QualityPreset}")
                .AppendLine($"主题/强调色　{settings.Theme} / {settings.Accent}")
                .AppendLine($"适配/缩放　{settings.MirrorFit} / {settings.MirrorZoom:P0}")
                .AppendLine()
                .AppendLine(extra ?? "")
                .ToString();
            AddText("概要.txt", summary);

            try
            {
                if (log.FilePath is not null && File.Exists(log.FilePath))
                    zip.CreateEntryFromFile(log.FilePath, "运行日志.txt", CompressionLevel.Optimal);
            }
            catch { }

            try
            {
                string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CastReceiver", "settings.json");
                if (File.Exists(cfg)) zip.CreateEntryFromFile(cfg, "配置.json", CompressionLevel.Optimal);
            }
            catch { }

            return zipPath;
        }
        catch { return null; }
    }
}