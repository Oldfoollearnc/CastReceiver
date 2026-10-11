# 关机 + 关机确认脚本
#   默认模式：记录标记 → 注册开机自检 → 发起关机（可用 shutdown /a 取消）
#   -Verify   ：开机后自动运行，核对系统事件日志，确认这次是否真的干净关机
#
# 判定依据（System 日志）：
#   1074 = 由程序/用户发起关机      → 关机确实被发起
#   6006 = 事件日志服务停止（干净关机）→ 系统正常走完关机流程
#   6008 = 上次关机是意外            → 说明不是干净关机
#   41   = 内核电源异常              → 掉电/崩溃
param(
    [int]$DelaySeconds = 120,
    [switch]$Verify
)

$ErrorActionPreference = 'Continue'
$base   = Join-Path $env:LOCALAPPDATA 'CastReceiver'
$marker = Join-Path $base 'shutdown-marker.json'
$result = Join-Path $base 'shutdown-result.txt'
$script = $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path $base | Out-Null

function Write-Result([string]$text) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -Path $result -Value "[$stamp] $text" -Encoding UTF8
    Write-Host $text
}

if ($Verify) {
    # ---------- 开机后核对 ----------
    Write-Result '==== 关机确认（本次开机自动运行）===='
    if (-not (Test-Path $marker)) {
        Write-Result '未找到关机标记：本次开机不是由本脚本发起的关机。'
    }
    else {
        $m = Get-Content $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        $since = [datetime]$m.RequestedAt
        Write-Result "关机请求时间：$($since.ToString('yyyy-MM-dd HH:mm:ss'))"
        Write-Result "关机前系统已运行：$($m.UptimeBefore)"

        $events = Get-WinEvent -FilterHashtable @{ LogName='System'; ID=1074,6006,6008,41; StartTime=$since } -ErrorAction SilentlyContinue
        if (-not $events) {
            Write-Result '未在关机时间点后找到相关事件：无法确认（可能日志被清理）。'
        }
        else {
            foreach ($e in ($events | Sort-Object TimeCreated)) {
                $name = switch ($e.Id) {
                    1074 { '关机已发起（1074）' }
                    6006 { '事件日志服务停止 = 干净关机（6006）' }
                    6008 { '意外关机（6008）' }
                    41   { '内核电源异常（41）' }
                    default { "事件 $($e.Id)" }
                }
                Write-Result ("  {0}  {1}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $name)
            }
            $clean  = [bool]($events | Where-Object { $_.Id -eq 6006 })
            $issued = [bool]($events | Where-Object { $_.Id -eq 1074 })
            $dirty  = [bool]($events | Where-Object { $_.Id -in 6008, 41 })
            Write-Result ''
            if ($issued -and $clean -and -not $dirty) {
                Write-Result '结论：✅ 已确认电脑完成了一次干净关机（发起 + 正常收尾，无异常关机事件）。'
            }
            elseif ($issued -and $dirty) {
                Write-Result '结论：⚠ 关机被发起，但存在异常关机事件（非正常收尾），请检查电源/驱动。'
            }
            elseif ($issued) {
                Write-Result '结论：⚠ 关机已发起，但没有记录到正常的关机收尾事件（可能是强制断电或日志未落盘）。'
            }
            else {
                Write-Result '结论：❌ 未找到关机发起记录，无法确认。'
            }
        }
    }
    Write-Result '==== 确认结束 ===='
    Remove-Item $marker -Force -ErrorAction SilentlyContinue
    # 结果也写一份到桌面，方便直接看到
    $desk = [Environment]::GetFolderPath('Desktop')
    Copy-Item $result (Join-Path $desk '关机确认结果.txt') -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 6
    exit 0
}

# ---------- 默认：发起关机 ----------
$os = Get-CimInstance Win32_OperatingSystem
$uptime = (Get-Date) - $os.LastBootUpTime
$markerObj = [ordered]@{
    RequestedAt   = (Get-Date).ToString('o')
    ShutdownAt    = (Get-Date).AddSeconds($DelaySeconds).ToString('o')
    DelaySeconds  = $DelaySeconds
    UptimeBefore  = ('{0:N1} 小时' -f $uptime.TotalHours)
    ComputerName  = $env:COMPUTERNAME
    User          = $env:USERNAME
}
$markerObj | ConvertTo-Json | Set-Content $marker -Encoding UTF8

# 注册开机自检（RunOnce：下次登录时运行一次 -Verify）
$runOnce = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
New-Item -Path $runOnce -Force | Out-Null
Set-ItemProperty -Path $runOnce -Name 'CastShutdownVerify' `
    -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File `"$script`" -Verify"

Write-Host "已记录关机标记：$marker"
Write-Host "已注册开机自检（下次开机自动核对关机结果）"
Write-Host "将在 $DelaySeconds 秒后关机；如需取消，请运行： shutdown /a"
shutdown.exe /s /t $DelaySeconds /c "投屏Window：按用户要求关机（shutdown /a 可取消）" /d p:0:0
Write-Host "关机命令已发出（$DelaySeconds 秒后执行）。"