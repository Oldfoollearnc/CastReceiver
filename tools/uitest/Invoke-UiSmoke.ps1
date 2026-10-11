# 界面压力测试（纯镜像新版 UI）：真实点击 + 响应性与 CPU 采样 + 停顿/异常统计 + 优雅退出
# 用法：powershell.exe -ExecutionPolicy Bypass -File Invoke-UiSmoke.ps1 -Exe "路径\投屏Window.exe"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 14
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

function Get-AppWindow($targetPid) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    for ($i = 0; $i -lt 40; $i++) {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Invoke-ByName($win, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { Write-Host "  未找到：$name"; return $false }
    try {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Host "  已点击：$name"
        return $true
    } catch { Write-Host "  点击失败：$name（$($_.Exception.Message)）"; return $false }
}

function Set-Switch($win, [string]$name, [bool]$on) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { Write-Host "  未找到开关：$name"; return }
    $t = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $isOn = $t.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($isOn -ne $on) { $t.Toggle(); Write-Host "  开关 $name → $(if($on){'开'}else{'关'})" }
    else { Write-Host "  开关 $name 已是目标状态" }
}

$logPath = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
$script:logLinesAtStart = if (Test-Path $logPath) { (Get-Content $logPath -Encoding UTF8 | Measure-Object -Line).Lines } else { 0 }
$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro" -PassThru
Write-Host "已启动，等待界面就绪 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

$win = Get-AppWindow $p.Id
if (-not $win) { Write-Host "未找到窗口" -ForegroundColor Red; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host "启动后：窗口='$($win.Current.Name)' 响应=$($p.Responding)"

Write-Host "--- 连续交互 ---"
Invoke-ByName $win "设置" | Out-Null
Start-Sleep -Seconds 7
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 2

Set-Switch $win "接收端开关" $false
Start-Sleep -Seconds 6
Set-Switch $win "接收端开关" $true
Start-Sleep -Seconds 9

Invoke-ByName $win "延迟优先" | Out-Null
Start-Sleep -Seconds 3
Invoke-ByName $win "画质优先" | Out-Null
Start-Sleep -Seconds 3

Invoke-ByName $win "全屏" | Out-Null
Start-Sleep -Seconds 5
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 3

Invoke-ByName $win "重启接收端" | Out-Null
Start-Sleep -Seconds 16

Write-Host "--- 响应性与 CPU 采样（30 秒） ---"
$bad = 0; $n = 0
$cpu0 = $p.TotalProcessorTime.TotalMilliseconds
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 5
    $p.Refresh(); $n++
    if (-not $p.Responding) { $bad++; Write-Host "  第 $n 次采样：未响应" -ForegroundColor Yellow }
}
$sw.Stop()
$p.Refresh()
$cpuUsed = $p.TotalProcessorTime.TotalMilliseconds - $cpu0
$cores = [Environment]::ProcessorCount
$single = [math]::Round($cpuUsed / $sw.Elapsed.TotalMilliseconds * 100, 1)
$total = [math]::Round($single / $cores, 2)
Write-Host "采样 $n 次，未响应 $bad 次；空闲 CPU：单核 $single%（整机 $total%，$cores 核）"

$log = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
if (Test-Path $log) {
    # 只统计本次运行产生的日志（日志文件跨启动追加）
    $fresh = Get-Content $log -Encoding UTF8 | Select-Object -Skip $script:logLinesAtStart
    $stall = ($fresh | Select-String -Pattern "界面线程停顿" | Measure-Object).Count
    $err = ($fresh | Select-String -Pattern "界面线程异常" | Measure-Object).Count
    Write-Host "本次运行日志：界面线程停顿 $stall 条；界面异常 $err 条"
}

$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); Write-Host "  强制结束进程" -ForegroundColor Yellow } else { Write-Host "  已优雅退出" }
Write-Host "结论：$(if ($bad -eq 0) { '界面全程保持响应 OK' } else { '存在未响应采样' })"