# 音频输出自动切换（⑥）端到端验证（开发期用，不参与产品构建）
#
# 验的是真实程序的行为：投屏开始时把默认音频设备切到目标设备，断开时还原。
# 每一步都用独立的 WinRT 读回校验（不信任"调用返回成功"）。
#
# 这个脚本会**真的改变系统默认音频设备**，所以结尾放在 finally 里强制还原 ——
# 中途失败也不能把用户的音频留在别的设备上。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$SelfTest,
    [Parameter(Mandatory = $true)][string]$SettingsRoot,
    [Parameter(Mandatory = $true)][string]$Py,
    [Parameter(Mandatory = $true)][string]$Vectors,
    [Parameter(Mandatory = $true)][string]$TargetId,
    [Parameter(Mandatory = $true)][string]$OriginalId,
    [string]$Server = 'PC',
    [string]$Mac = '7A:1B:2C:3D:4E:5F',
    [int]$HoldSeconds = 40
)

$ErrorActionPreference = 'Stop'
$script:fail = 0
$app = $null
$client = $null

function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:fail++ }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

function Get-DefaultAudio {
    $line = (& $SelfTest --audio-endpoints 2>&1 | Select-String '当前默认 = ').Line
    return ($line -split '当前默认 = ')[1].Trim()
}

function ShortId([string]$id) {
    $m = [regex]::Match($id, '\{[0-9a-f\-]{36}\}')
    return $m.Value
}

try {
    # 从已知状态出发
    [void](& $SelfTest --audio-set $OriginalId)
    $start = Get-DefaultAudio
    Check '起始默认设备就是原设备' ($start -eq $OriginalId) (ShortId $start)

    # 隔离配置：打开自动切换，目标设成另一个设备
    if (Test-Path $SettingsRoot) { Remove-Item $SettingsRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $SettingsRoot | Out-Null
    @{ AudioSwitchEnabled = $true; AudioSwitchTargetId = $TargetId; AudioSwitchRestore = $true } |
        ConvertTo-Json | Set-Content -Path (Join-Path $SettingsRoot 'settings.json') -Encoding UTF8

    $ip = (Find-NetRoute -RemoteIPAddress 8.8.8.8 | Select-Object -First 1).IPAddress
    $app = Start-Process -FilePath $Exe -ArgumentList '--no-intro', '--settings-root', $SettingsRoot -PassThru
    Start-Sleep -Seconds 15
    Check '程序已启动' (-not $app.HasExited) ("pid=" + $app.Id)

    $idle = Get-DefaultAudio
    Check '未投屏时不改音频设备' ($idle -eq $OriginalId) (ShortId $idle)

    # 建立投屏会话并保持住（路径含空格，必须手工加引号）
    $clientScript = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $Vectors -Parent) '..\..\..\tools\airplay-port\airplay_fake_client.py'))
    $argLine = @('-u', ('"' + $clientScript + '"'), $ip, ('"' + $Vectors + '"'), $Server, $Mac, $HoldSeconds) -join ' '
    $client = Start-Process -FilePath $Py -ArgumentList $argLine -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $SettingsRoot 'c.out') -RedirectStandardError (Join-Path $SettingsRoot 'c.err')
    Start-Sleep -Seconds 12

    $during = Get-DefaultAudio
    Check '投屏中切换到目标设备' ($during -eq $TargetId) (ShortId $during)

    # 断开 → 应还原
    Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue
    $client = $null
    Start-Sleep -Seconds 7

    $after = Get-DefaultAudio
    Check '断开后还原原设备' ($after -eq $OriginalId) (ShortId $after)
}
finally {
    if ($client) { Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    # 无论如何都要把用户的音频设备放回去
    $now = Get-DefaultAudio
    if ($now -ne $OriginalId) {
        Write-Host "[WARN] 音频设备没被还原（$((ShortId $now))），强制还原"
        [void](& $SelfTest --audio-set $OriginalId)
        $fixed = Get-DefaultAudio
        Check '强制还原成功' ($fixed -eq $OriginalId) (ShortId $fixed)
    }
    else {
        Write-Host "[INFO] 音频设备已是原设备：$((ShortId $now))"
    }
}

Write-Host ''
Write-Host ("==== 音频自动切换验证：失败 {0} 项 ====" -f $script:fail)
exit $script:fail
