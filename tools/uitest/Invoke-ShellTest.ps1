# 验证本轮交互改动：首次引导动画、窗口模式下常驻控制条、全屏/退出全屏的尺寸恢复
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public class ShellWin {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  private delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  // 控制条是 ShowInTaskbar=False 的无边框窗口，UIA 根子元素里看不到它：必须走 Win32 枚举
  public static IntPtr Find(int pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint o; GetWindowThreadProcessId(h, out o);
      if (o == (uint)pid && IsWindowVisible(h)) {
        var sb = new System.Text.StringBuilder(256); GetWindowTextW(h, sb, 256);
        if (sb.ToString() == title) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

function Find-Win32($targetPid, [string]$name) {
    $h = [ShellWin]::Find($targetPid, $name)
    if ($h -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($h)
}

function All-Windows($targetPid) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $c)
}
function Find-Win($targetPid, [string]$name) {
    foreach ($w in All-Windows $targetPid) { if ($w.Current.Name -eq $name) { return $w } }
    return $null
}
function Find-ByName($win, [string]$name) {
    if (-not $win) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Click($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false }
}
function Rect-Of($win) {
    $r = $win.Current.BoundingRectangle
    return "{0},{1} {2}x{3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height
}

$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro" -PassThru
Write-Host "已启动，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

# ① 首次引导动画
$tut = Find-Win $p.Id "使用引导"
Write-Host "① 首次引导窗口：$(if ($tut) { '已弹出 OK' } else { '未出现 ✗' })"
if ($tut) {
    Start-Sleep -Seconds 2
    $title = Find-ByName $tut "下一步"
    Write-Host "   引导按钮：$(if ($title) { '下一步' } else { '未找到' })"
    Click (Find-ByName $tut "跳过") | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "   点「跳过」后引导已关闭：$(-not [bool](Find-Win $p.Id '使用引导'))"
}

$main = Find-Win $p.Id "投屏Window"
Write-Host "② 主窗口：$(if ($main) { '存在 OK' } else { '未找到 ✗' })"

# ② 窗口模式下控制条常驻（极简化后：直接摆出 截屏 / 录制 / 小窗，没有"更多"这一层）
$bar = Find-Win32 $p.Id "控制条"
Write-Host "③ 窗口模式下的悬浮控制条：$(if ($bar) { '已常驻 OK' } else { '未出现 ✗' })"
if ($bar) {
    $btns = @()
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) { $btns += $b.Current.Name }
    Write-Host "   控制条上的按钮：$(if ($btns.Count) { $btns -join ' | ' } else { '（无）' })"
    Write-Host "   是否还有音量控件：$(if (Find-ByName $bar '静音') { '有（应为无）' } else { '已删除 OK' })"
    Write-Host "   是否还重复舞台顶栏的按钮：$(if (Find-ByName $bar '放大') { '有（应为无）✗' } else { '已去掉 OK' })"
}

# ③ 全屏 → 退出 → 尺寸恢复（进入用舞台顶栏的「全屏」，退出用控制条里的「退出全屏」）
$before = Rect-Of $main
Write-Host "④ 进入全屏前窗口：$before"
if ($main) { Click (Find-ByName $main "全屏") | Out-Null }
Start-Sleep -Seconds 4
# 全屏时控制条只在鼠标靠近屏幕底部时出现，先把它叫出来
[ShellWin]::SetCursorPos([int]([ShellWin]::GetSystemMetrics(0) / 2), [ShellWin]::GetSystemMetrics(1) - 40) | Out-Null
Start-Sleep -Milliseconds 900
$bar = Find-Win32 $p.Id "控制条"
$fs = Rect-Of $main
Write-Host "   全屏中窗口：$fs"
if ($bar) { Click (Find-ByName $bar "退出全屏") | Out-Null }
Start-Sleep -Seconds 3
$main = Find-Win $p.Id "投屏Window"
$after = Rect-Of $main
Write-Host "   退出全屏后：$after"
Write-Host "   尺寸已恢复：$(if ($after -eq $before) { 'OK' } else { '不一致 ✗' })"

$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); Write-Host "强制结束" } else { Write-Host "已优雅退出" }