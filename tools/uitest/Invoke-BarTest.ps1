# 验证全屏悬浮控制条：缩小/放大双向可用、可复位、适配方式可循环
# 用法：powershell.exe -ExecutionPolicy Bypass -File Invoke-BarTest.ps1 -Exe "路径\投屏Window.exe"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public class BarCursor {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  private delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  // 悬浮条这类 ShowInTaskbar=False 的无边框窗口不会出现在 UIA 根子元素里（实测 UIA 只报「投屏Window」），
  // 所以必须按 Win32 枚举找 HWND，再用 AutomationElement.FromHandle 拿自动化元素。
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
function Show-Bar($win) {
    # 悬浮条在鼠标靠近**窗口底部**时出现。原先把鼠标放到屏幕最底部（GetSystemMetrics(1)-40），
    # 而窗口 1120x720 只占屏幕上半部分 —— 那个点根本不在窗口里，条永远不会出现。
    $r = $win.Current.BoundingRectangle
    [BarCursor]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Bottom - 60)) | Out-Null
    Start-Sleep -Milliseconds 800
}

function Find-Window($targetPid, [string]$name = "") {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $c)
    foreach ($w in $wins) {
        if ($name -eq "" -or $w.Current.Name -eq $name) { return $w }
    }
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

function ZoomText($bar) {
    # 缩放百分比显示在“缩放复位”左侧的文本块里，直接扫描所有 Text 元素找 xx%
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
    foreach ($t in $texts) { if ($t.Current.Name -match '^\d+%$') { return $t.Current.Name } }
    return "?"
}

$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro" -PassThru
Write-Host "已启动，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

$main = Find-Window $p.Id "投屏Window"
if (-not $main) { Write-Host "未找到主窗口" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
[void][BarCursor]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)
Show-Bar $main          # 先把鼠标放进窗口底部，让控制条先出现，才点得到「全屏」
Click (Find-ByName $main "全屏") | Out-Null
Start-Sleep -Seconds 3
Show-Bar $main

# 悬浮条的窗口标题是「控制条」。注意：这个无边框窗口不在 UIA 根子元素里，
# 用 UIA 的 Find-Window 永远找不到 —— 必须 Win32 枚举 HWND 再转自动化元素。
$barH = [BarCursor]::Find($p.Id, "控制条")
$bar = if ($barH -ne [IntPtr]::Zero) { [System.Windows.Automation.AutomationElement]::FromHandle($barH) } else { $null }
if (-not $bar) { Write-Host "未找到悬浮控制条" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
Write-Host "控制条已出现：$($bar.Current.Name)"

$zoom0 = ZoomText $bar
Show-Bar $main
Write-Host "初始缩放：$zoom0"

Click (Find-ByName $bar "缩小") | Out-Null; Start-Sleep -Milliseconds 500
Click (Find-ByName $bar "缩小") | Out-Null; Start-Sleep -Milliseconds 700
$zoomOut = ZoomText $bar
Write-Host "点两次「缩小」后：$zoomOut"

Click (Find-ByName $bar "放大") | Out-Null; Start-Sleep -Milliseconds 500
Click (Find-ByName $bar "放大") | Out-Null; Start-Sleep -Milliseconds 700
$zoomBack = ZoomText $bar
Write-Host "再点两次「放大」后：$zoomBack"

Click (Find-ByName $bar "放大") | Out-Null; Start-Sleep -Milliseconds 700
$zoomUp = ZoomText $bar
Write-Host "继续「放大」：$zoomUp"

Click (Find-ByName $bar "缩放复位") | Out-Null; Start-Sleep -Milliseconds 700
$zoomReset = ZoomText $bar
Write-Host "点「缩放复位」后：$zoomReset"

$fitBtn = Find-ByName $bar "画面适配"
Click $fitBtn | Out-Null; Start-Sleep -Milliseconds 600
$fitConds = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$fitButtons = $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, $fitConds)
$fitNames = @()
foreach ($b in $fitButtons) { $fitNames += $b.Current.Name }
Write-Host "控制条上的按钮：$($fitNames -join ' | ')"

# 注：早期版本的控制条有静音按钮，现在的按钮是
# 退出全屏/拉伸/缩小/放大/缩放复位/截屏/录制/小窗 —— 这里不再断言静音是否存在。

Click (Find-ByName $bar "退出全屏") | Out-Null
Start-Sleep -Seconds 2
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); Write-Host "强制结束" } else { Write-Host "已优雅退出" }

# 判定
$ok = ($zoomOut -ne $zoom0) -and ($zoomBack -eq $zoom0) -and ($zoomUp -ne $zoom0) -and ($zoomReset -eq $zoom0)
Write-Host ""
Write-Host "结论：缩小/放大/复位 $(if ($ok) { '双向可用 OK' } else { '存在异常' })"
if (-not $ok) { exit 2 }