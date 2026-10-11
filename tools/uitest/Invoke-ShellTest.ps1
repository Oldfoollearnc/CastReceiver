# 验证本轮交互改动：首次引导动画、窗口模式下常驻控制条、全屏/退出全屏的尺寸恢复
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

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

$p = Start-Process -FilePath $Exe -PassThru
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

# ② 窗口模式下控制条常驻
$bar = Find-Win $p.Id "控制条"
Write-Host "③ 窗口模式下的悬浮控制条：$(if ($bar) { '已常驻 OK' } else { '未出现 ✗' })"
if ($bar) {
    $fsBtn = Find-ByName $bar "全屏"
    Write-Host "   控制条上的按钮：全屏=$(if ($fsBtn) { '有' } else { '无' })、画面适配=$(if (Find-ByName $bar '画面适配') { '有' } else { '无' })、缩小=$(if (Find-ByName $bar '缩小') { '有' } else { '无' })、放大=$(if (Find-ByName $bar '放大') { '有' } else { '无' })"
    Write-Host "   是否还有音量控件：$(if (Find-ByName $bar '静音') { '有（应为无）' } else { '已删除 OK' })"
}

# ③ 全屏 → 退出 → 尺寸恢复
$before = Rect-Of $main
Write-Host "④ 进入全屏前窗口：$before"
if ($bar) { Click (Find-ByName $bar "全屏") | Out-Null }
Start-Sleep -Seconds 4
$bar = Find-Win $p.Id "控制条"
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