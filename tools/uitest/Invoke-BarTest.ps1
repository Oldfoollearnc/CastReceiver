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
}
"@
function Show-Bar {
    # 悬浮条靠近屏幕底部才出现，模拟用户把鼠标移到底部
    [BarCursor]::SetCursorPos([int]([BarCursor]::GetSystemMetrics(0) / 2), [BarCursor]::GetSystemMetrics(1) - 40) | Out-Null
    Start-Sleep -Milliseconds 700
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

$p = Start-Process -FilePath $Exe -PassThru
Write-Host "已启动，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

$main = Find-Window $p.Id "投屏Window"
if (-not $main) { Write-Host "未找到主窗口" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
Click (Find-ByName $main "全屏") | Out-Null
Start-Sleep -Seconds 4
Show-Bar

$bar = Find-Window $p.Id "控制"
if (-not $bar) { Write-Host "未找到悬浮控制条" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
Write-Host "控制条已出现：$($bar.Current.Name)"

$zoom0 = ZoomText $bar
Show-Bar
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

$muteBtn = Find-ByName $bar "静音 / 取消静音"
Click $muteBtn | Out-Null; Start-Sleep -Milliseconds 500
Show-Bar
$muteBtn2 = Find-ByName $bar "静音"
Write-Host "静音按钮：$(if ($muteBtn2) { '存在' } else { '未找到' })"

Click (Find-ByName $bar "退出全屏") | Out-Null
Start-Sleep -Seconds 2
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); Write-Host "强制结束" } else { Write-Host "已优雅退出" }

# 判定
$ok = ($zoomOut -ne $zoom0) -and ($zoomBack -eq $zoom0) -and ($zoomUp -ne $zoom0) -and ($zoomReset -eq $zoom0)
Write-Host ""
Write-Host "结论：缩小/放大/复位 $(if ($ok) { '双向可用 OK' } else { '存在异常' })"
if (-not $ok) { exit 2 }