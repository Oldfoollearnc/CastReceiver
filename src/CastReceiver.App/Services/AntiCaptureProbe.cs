using System.Drawing;
using System.Text;
using System.Windows;

namespace CastReceiver.App.Services;

public sealed record AntiCaptureProbeResult(
    bool ProtectionApplied,
    bool PrintWindowWorks,
    bool ScreenGrabWorks,
    string VisualCaptureSupport,
    string Report)
{
    /// <summary>能否在「保护一直开着」的前提下自己录制/截图。</summary>
    public bool CanCaptureWhileProtected => PrintWindowWorks;
}

/// <summary>
/// 反截屏取帧路径实测：决定「窗口防捕获开着时，本程序自己怎么拿到画面」。
///
/// 为什么要实测：SetWindowDisplayAffinity 对所有捕获者一视同仁，
/// 连本程序用 GDI 从屏幕 BitBlt 也会拿到黑图。因此按优先级试这几条：
///   P2  PrintWindow(PW_RENDERFULLCONTENT) 直接向窗口要帧（不经过屏幕）—— 首选
///   P3  临时解除保护 + 屏幕抓取 —— 保底（截图可用；录制会周期性暴露，故作为降级）
///   P4  Windows.Graphics.Capture 的 CreateFromVisual（视觉级捕获，可能不受窗口保护影响）—— 仅验证可用性
/// </summary>
public static class AntiCaptureProbe
{
    public static AntiCaptureProbeResult Run(Window mainWindow, IntPtr mirrorChildHandle,
                                             (int X, int Y, int W, int H) stageScreenRect,
                                             Func<string>? visualCaptureProbe = null)
    {
        var report = new StringBuilder();
        bool applied = WindowProtection.SetProtection(mainWindow, true);
        report.AppendLine($"P1 窗口防捕获：{(applied ? "已开启（WDA_EXCLUDEFROMCAPTURE）" : "开启失败（系统不支持或不是顶层窗口）")}");

        // P2：PrintWindow 直接取窗口内容
        bool printWindowWorks = false;
        try
        {
            using var shot = WindowProtection.GrabWindow(mirrorChildHandle);
            if (shot is null) report.AppendLine("P2 PrintWindow 取帧：失败（没有拿到位图）");
            else if (WindowProtection.LooksBlank(shot))
                report.AppendLine($"P2 PrintWindow 取帧：返回黑屏 {shot.Width}×{shot.Height}（受防捕获影响）");
            else
            {
                printWindowWorks = true;
                report.AppendLine($"P2 PrintWindow 取帧：可用 ✓（{shot.Width}×{shot.Height}）→ 录制可保持全程保护");
            }
        }
        catch (Exception ex) { report.AppendLine("P2 PrintWindow 取帧：异常 " + ex.Message); }

        // P3：临时解除保护 + 屏幕抓取（保底）
        bool screenGrabWorks = false;
        try
        {
            WindowProtection.SetProtection(mainWindow, false);
            using var shot = CaptureService.Grab(stageScreenRect.X, stageScreenRect.Y, stageScreenRect.W, stageScreenRect.H);
            screenGrabWorks = shot is not null && !WindowProtection.LooksBlank(shot);
            report.AppendLine(screenGrabWorks
                ? $"P3 临时解除保护 + 屏幕抓取：可用 ✓（{shot!.Width}×{shot.Height}）→ 截图/录制期间保护会短暂失效"
                : "P3 临时解除保护 + 屏幕抓取：不可用");
        }
        catch (Exception ex) { report.AppendLine("P3 屏幕抓取：异常 " + ex.Message); }
        finally
        {
            if (applied) WindowProtection.SetProtection(mainWindow, true);
        }

        // P4：视觉级捕获（未来路径，只验证 API 是否接受我们的合成视觉）
        string visualSupport = "未检测";
        try
        {
            visualSupport = visualCaptureProbe?.Invoke() ?? "未提供";
            report.AppendLine("P4 GraphicsCapture 视觉级捕获：" + visualSupport);
        }
        catch (Exception ex)
        {
            visualSupport = "异常 " + ex.Message;
            report.AppendLine("P4 GraphicsCapture 视觉级捕获：异常 " + ex.Message);
        }

        report.AppendLine();
        report.AppendLine(printWindowWorks
            ? "结论：可以在保持防捕获开启的前提下自己截图/录制（走 PrintWindow 取帧）。"
            : screenGrabWorks
                ? "结论：本机 PrintWindow 取不到画面，将采用降级方案 —— 截图时瞬时解除保护；录制期间保护按设置暂停（可在设置里改为禁止录制）。"
                : "结论：两条路径都不可用。请把「反截屏保护」关掉，否则无法截图/录制。");

        return new AntiCaptureProbeResult(applied, printWindowWorks, screenGrabWorks, visualSupport, report.ToString());
    }
}
