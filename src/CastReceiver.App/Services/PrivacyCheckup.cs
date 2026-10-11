using System.IO;
using Microsoft.Win32;

namespace CastReceiver.App.Services;

public sealed record CheckupItem(string Title, string Detail, bool Ok, bool CanAutoFix);

/// <summary>
/// 隐私体检：把「别人还能从哪里看到你的投屏」逐项查一遍。
/// 只读检测 + 仅对当前用户（HKCU）项提供一键修复，不做需要管理员权限的系统改动。
/// </summary>
public static class PrivacyCheckup
{
    private const string GameBarKey = @"HKEY_CURRENT_USER\System\GameConfigStore";
    private const string GameDvrKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR";

    public static IReadOnlyList<CheckupItem> Run(bool privacyLog, bool antiCapture, bool vaultUnlocked,
                                                 bool forgetDeviceHistory, string printWindowResult)
    {
        var items = new List<CheckupItem>();

        // 1. Xbox Game Bar 后台录制：会在你不知情时录下屏幕
        try
        {
            object? enabled = Registry.GetValue(GameBarKey, "GameDVR_Enabled", null);
            object? capture = Registry.GetValue(GameDvrKey, "AppCaptureEnabled", null);
            bool gameDvrOn = enabled is int e && e != 0;
            bool captureOn = capture is int c && c != 0;
            items.Add(new CheckupItem("Xbox Game Bar 后台录制",
                gameDvrOn || captureOn
                    ? "处于开启状态：系统可能在你不知情时录下屏幕内容（含镜像画面）"
                    : "已关闭",
                !(gameDvrOn || captureOn), true));
        }
        catch
        {
            items.Add(new CheckupItem("Xbox Game Bar 后台录制", "无法读取（注册表不可访问）", true, false));
        }

        // 2. Windows「回忆 / Recall」：会周期性截屏并保存
        try
        {
            bool recallPresent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Recall") is not null
                                 || Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\RecallUserSettings") is not null;
            items.Add(new CheckupItem("Windows 回忆（Recall）",
                recallPresent
                    ? "检测到系统存在「回忆」配置项：它会对屏幕内容定期截图，建议在 设置 → 隐私和安全性 → 回忆和快照 中关闭"
                    : "未检测到（系统未提供该功能或已关闭）",
                !recallPresent, false));
        }
        catch
        {
            items.Add(new CheckupItem("Windows 回忆（Recall）", "无法检测", true, false));
        }

        // 3. 旧版本的明文文件
        try
        {
            var legacy = LegacyMigration.Find();
            long bytes = legacy.Sum(f => f.Bytes);
            items.Add(new CheckupItem("旧版本的明文截图/录像",
                legacy.Count == 0
                    ? "没有发现"
                    : $"发现 {legacy.Count} 个明文文件（{bytes / 1024.0 / 1024:F1} MB）：任何人打开文件夹就能看到，建议立即导入加密库并删除",
                legacy.Count == 0, false));
        }
        catch
        {
            items.Add(new CheckupItem("旧版本的明文截图/录像", "检测失败", true, false));
        }

        // 4. %TEMP% 里的明文帧残留
        try
        {
            int leftovers = Directory.EnumerateDirectories(Path.GetTempPath(), "CastReceiverFrames_*").Count();
            items.Add(new CheckupItem("临时目录里的明文帧",
                leftovers == 0 ? "没有发现" : $"发现 {leftovers} 个旧的明文帧目录（旧版本录制时留下），建议清理",
                leftovers == 0, true));
        }
        catch
        {
            items.Add(new CheckupItem("临时目录里的明文帧", "检测失败", true, false));
        }

        // 5. 云盘同步
        try
        {
            string legacyDir = CaptureService.OutputDirectory;
            bool cloud = CaptureService.IsInCloudFolder(legacyDir);
            items.Add(new CheckupItem("旧目录是否在云盘同步范围内",
                cloud ? $"是（{legacyDir}）—— 旧文件可能已被自动上传到云端" : "否",
                !cloud, false));
        }
        catch
        {
            items.Add(new CheckupItem("旧目录是否在云盘同步范围内", "检测失败", true, false));
        }

        // 6. 日志隐私
        items.Add(new CheckupItem("日志隐私模式",
            privacyLog
                ? "已开启：投屏的连接/断开时间不会写进日志文件"
                : "已关闭：日志里会记录投屏会话事件（含时间），建议保持开启",
            privacyLog, false));

        // 7. 反截屏
        items.Add(new CheckupItem("反截屏保护",
            antiCapture
                ? $"已开启（{printWindowResult}）"
                : "已关闭：其它程序（会议共享、远程协助、录屏软件）可以截取你的镜像画面",
            antiCapture, false));

        // 8. 设备历史
        items.Add(new CheckupItem("无线显示设备历史",
            forgetDeviceHistory
                ? "每次启动都会清空系统记录的已知投屏设备"
                : "系统会保留「上次连接过的设备」记录（含最近连接时间），可在设置里改为启动时清空",
            forgetDeviceHistory, false));

        // 9. 当前库是否已上锁
        items.Add(new CheckupItem("加密库状态",
            vaultUnlocked ? "当前已解锁：关闭库窗口或按 Ctrl+Alt+L 可立即上锁" : "当前已上锁",
            !vaultUnlocked, false));

        return items;
    }

    /// <summary>关闭 Xbox Game Bar 的后台录制（仅改当前用户设置，随时可在系统设置里改回）。</summary>
    public static bool DisableGameBarBackgroundRecording()
    {
        try
        {
            Registry.SetValue(GameBarKey, "GameDVR_Enabled", 0, RegistryValueKind.DWord);
            Registry.SetValue(GameBarKey, "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
            Registry.SetValue(GameDvrKey, "AppCaptureEnabled", 0, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}
