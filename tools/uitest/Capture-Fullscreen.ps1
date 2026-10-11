# 全屏沉浸截图：进入全屏并把鼠标移动到屏幕底部让悬浮控制条出现，然后截取整个桌面
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$StartupWaitSeconds = 14
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class FsCap {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
"@

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds $StartupWaitSeconds

# 进入全屏：优先点击窗口内的「全屏」按钮，失败则用 F11
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$clicked = $false
if ($win) {
    $b = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "全屏")
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $b)
    if ($btn) {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $clicked = $true
    }
}
if (-not $clicked) {
    [System.Windows.Forms.SendKeys]::SendWait("{F11}")
}
Start-Sleep -Seconds 3

# 鼠标移到屏幕底部中间，触发悬浮控制条显示
$w = [FsCap]::GetSystemMetrics(0); $h = [FsCap]::GetSystemMetrics(1)
[FsCap]::SetCursorPos([int]($w / 2), $h - 40) | Out-Null
Start-Sleep -Milliseconds 900

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "已保存全屏截图：$Out ($w x $h)" -ForegroundColor Green

Start-Sleep -Seconds 1
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue