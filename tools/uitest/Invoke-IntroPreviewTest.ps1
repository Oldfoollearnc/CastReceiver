# 入场动画「预览」回归测试：预览结束后不能挂着一块关不掉的黑屏
#
# 用法：
#   powershell.exe -ExecutionPolicy Bypass -File Invoke-IntroPreviewTest.ps1 -Exe "..\..\src\CastReceiver.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\投屏Window.exe"
#
# 验证内容（连点两次预览，正是用户实测会挂黑屏的场景）：
#   1) 点「预览入场动画」后，原生入场窗（窗口类 CastReceiverIntroWindow）确实出现
#   2) 素材放完后它**自己消失**（不是被 30 秒看门狗拖走、更不是永远挂着）
#   3) 设置窗重新可见，提示文字变成「预览结束（界面已放回）」
#   4) 第二次连点同样能收场
#
# 独立配置目录（--settings-root / --vault-root / --instance-suffix），不动用户的真实设置。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$WorkRoot = "",
    [int]$PreviewEndSeconds = 2.5,
    [int]$PreviewTimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"
if (-not $WorkRoot) {
    # 默认放在仓库内的忽略目录：某些受限环境（比如自动化沙箱）只允许进程写工作区
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    $WorkRoot = Join-Path $repoRoot "_temp\intro-preview"
}
$Exe = (Resolve-Path $Exe).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:failures = 0
function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures++ }
function Write-Info($t) { Write-Host "  · $t" }

# ---------------------------------------------------------------- Win32 窗口枚举
# 用一段 C# 做枚举：PowerShell 里用 scriptblock 当 EnumWindows 回调极慢（一次要几百毫秒），
# 而入场窗只活几秒，慢枚举会「看不见」它（实测踩过：以为窗口没出现，其实一直在）。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class IntroWindowProbe
{
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    /// <summary>列出该进程的顶层窗口类名；可见的加 "V:" 前缀。</summary>
    public static List<string> Classes(int pid)
    {
        var list = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == (uint)pid)
            {
                var sb = new StringBuilder(256);
                GetClassNameW(h, sb, 256);
                list.Add((IsWindowVisible(h) ? "V:" : "h:") + sb.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>入场画面窗口（NativeIntroWindow 注册的窗口类）是否还在显示。</summary>
    public static bool IntroVisible(int pid)
    {
        foreach (string c in Classes(pid))
        {
            if (c.StartsWith("V:") && c.Contains("CastReceiverIntroWindow")) return true;
        }
        return false;
    }
}
'@

# 入场画面窗口：类名由 NativeIntroWindow 注册，且仍在显示 —— 它就是"关不掉的黑屏"
function Get-IntroVisible([int]$targetPid) { return [IntroWindowProbe]::IntroVisible($targetPid) }

function Get-WindowClasses([int]$targetPid) { return ([IntroWindowProbe]::Classes($targetPid)) -join ', ' }

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# ---------------------------------------------------------------- UIA 辅助
function New-ElementCondition([string]$automationId, [string]$name, $controlType) {
    $conds = New-Object System.Collections.ArrayList
    if ($automationId) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)))
    }
    if ($name) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    if ($controlType) {
        [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)))
    }
    if ($conds.Count -eq 1) { return $conds[0] }
    return New-Object System.Windows.Automation.AndCondition($conds.ToArray())
}

# 在整个进程的所有顶层窗口里找控件（启动瞬间还可能是闪屏窗，所以必须轮询）
function Wait-Element([int]$targetPid, [string]$automationId, [string]$name, $controlType, [int]$TimeoutSeconds = 15) {
    $cond = New-ElementCondition $automationId $name $controlType
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $winCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)
        foreach ($w in $wins) {
            $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($el) { return $el }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Invoke-Element($el, [string]$what) {
    if (-not $el) { Write-Bad "找不到控件：$what"; return $false }
    try {
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$null)) {
            $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        } else {
            $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
        Write-Info "已点击：$what"
        return $true
    } catch {
        Write-Bad "点击失败：$what（$($_.Exception.Message)）"
        return $false
    }
}

# ---------------------------------------------------------------- 准备独立配置
$startupLog = Join-Path $env:LOCALAPPDATA "CastReceiver\startup.log"
$appLog = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
$startupStamp = if (Test-Path $startupLog) { (Get-Item $startupLog).LastWriteTime } else { [datetime]::MinValue }
$appStamp = if (Test-Path $appLog) { (Get-Item $appLog).LastWriteTime } else { [datetime]::MinValue }

$clip = Join-Path (Split-Path $Exe) "Assets\intro\intro-ninja.mp4"
if (-not (Test-Path $clip)) { Write-Host "找不到内置素材：$clip" -ForegroundColor Red; exit 1 }

if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
$settingsRoot = Join-Path $WorkRoot "cfg"
$vaultRoot = Join-Path $WorkRoot "vault"
New-Item -ItemType Directory -Force -Path $settingsRoot, $vaultRoot | Out-Null

$template = Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json"
$json = if (Test-Path $template) { Get-Content $template -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
$json.ShowIntro = $false                 # 启动不放动画：这次只验"预览"这条路径
$json.IntroSource = "file:$clip"
$json.IntroStart = 0
$json.IntroEnd = $PreviewEndSeconds
$json.IntroExtend = 0
$json.IntroFadeInMs = 200
$json.IntroFadeOutMs = 300
$json.IntroPreviewReenter = $true        # 用户实测的配置：藏起界面再播
$json.AutoStartDlna = $false
$json.AutoStartMiracast = $false
$json.AutoStartAirPlay = $false
$json.ShowStats = $false
$json.HasSeenTutorial = $true
$json.MinimizeToTray = $false
$json.RunAtStartup = $false
$json.PreventSleep = $false
$json.WindowLeft = 80; $json.WindowTop = 80; $json.WindowWidth = 1280; $json.WindowHeight = 720
$settingsPath = Join-Path $settingsRoot "settings.json"
[System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

Write-Step "启动应用（独立配置：$settingsRoot）"
$testStart = Get-Date          # 只看这之后写进 castreceiver.log 的行（那份日志里还留着历史会话）
# 注意：Start-Process -ArgumentList 不会替参数加引号，而仓库路径里带空格（"投屏Window 桌面版"），
# 必须自己拼一条带引号的命令行，否则参数会被空格切断（实测 settings-root 被切成了别的目录）。
$argLine = "--vault-root `"$vaultRoot`" --settings-root `"$settingsRoot`" --instance-suffix `"-uia-intro`""
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList $argLine
Write-Info "PID = $($p.Id)"

# ---------------------------------------------------------------- 打开设置 → 画面
$openSettings = Wait-Element $p.Id "" "打开设置" $null 40
if (-not $openSettings) {
    Write-Bad "主界面没起来（找不到「打开设置」）"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Ok "主界面已就绪"
Start-Sleep -Seconds 2

if (-not (Invoke-Element $openSettings "打开设置")) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

$navVisual = Wait-Element $p.Id "" "开场" ([System.Windows.Automation.ControlType]::RadioButton) 20
if (-not (Invoke-Element $navVisual "设置 → 开场")) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Start-Sleep -Seconds 1

# ---------------------------------------------------------------- 连点两次预览
for ($round = 1; $round -le 2; $round++) {
    Write-Step "第 $round 次预览"

    $btn = Wait-Element $p.Id "IntroPreviewButton" "" $null 10
    if (-not (Invoke-Element $btn "预览入场动画")) { break }

    # 高频轮询（20ms）：入场窗只活几秒，慢枚举会看不见它 —— 同时把"存活时长"量出来
    $clickedAt = Get-Date
    $seenIntro = $false
    $firstSeen = $null
    $lastSeen = $null
    $polls = 0
    $deadline = (Get-Date).AddSeconds($PreviewTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $polls++
        if (Get-IntroVisible $p.Id) {
            $seenIntro = $true
            if (-not $firstSeen) { $firstSeen = Get-Date }
            $lastSeen = Get-Date
        } elseif ($seenIntro) {
            break
        }
        Start-Sleep -Milliseconds 20
    }

    if ($seenIntro) {
        $life = [math]::Round(($lastSeen - $firstSeen).TotalSeconds, 1)
        $delay = [math]::Round(($firstSeen - $clickedAt).TotalSeconds, 1)
        Write-Ok "入场窗已出现（点击后 $delay 秒，存活 $life 秒）"
    } else {
        Write-Bad "入场窗没有出现，预览没跑起来"
        Write-Info "轮询 $polls 次；当前顶层窗：$(Get-WindowClasses $p.Id)"
    }

    if ($seenIntro -and $lastSeen -and -not (Get-IntroVisible $p.Id)) {
        Write-Ok "入场窗已自行消失（没有残留黑屏）"
    } elseif ($seenIntro) {
        Write-Bad "入场窗 $PreviewTimeoutSeconds 秒后仍挂在屏幕上（黑屏关不掉的原始问题）"
        Write-Info "当前顶层窗：$(Get-WindowClasses $p.Id)"
    }

    # 界面必须放回来：设置窗可见 + 提示文字
    $back = Wait-Until {
        $b = Wait-Element $p.Id "IntroPreviewButton" "" $null 1
        return ($b -and -not $b.Current.IsOffscreen)
    } 15
    if ($back) { Write-Ok "设置窗已重新可见" } else { Write-Bad "设置窗没有被放回来（界面还藏着）" }

    $hint = Wait-Element $p.Id "IntroHintText" "" $null 5
    $hintText = if ($hint) { $hint.Current.Name } else { "" }
    if ($hintText -like "*预览结束*") { Write-Ok "提示文字：$hintText" }
    else { Write-Bad "提示文字不对：'$hintText'（期望「预览结束（界面已放回）」）" }
}

# ---------------------------------------------------------------- 日志佐证
Write-Step "日志佐证"
Start-Sleep -Seconds 1
$startupChanged = (Test-Path $startupLog) -and (Get-Item $startupLog).LastWriteTime -gt $startupStamp
$appChanged = (Test-Path $appLog) -and (Get-Item $appLog).LastWriteTime -gt $appStamp

if (-not $startupChanged) {
    Write-Info "本次运行没写 startup.log（受限环境里应用写不了 %LOCALAPPDATA%），跳过日志断言"
} else {
    $logText = Get-Content $startupLog -Raw -Encoding UTF8
    $closed = ([regex]::Matches($logText, "原生入场动画已收场")).Count
    $deadline = ([regex]::Matches($logText, "超过硬性期限")).Count
    if ($closed -ge 2) { Write-Ok "startup.log 里有 $closed 次正常收场" }
    else { Write-Bad "startup.log 里只有 $closed 次收场（期望 ≥2）`n$logText" }
    if ($deadline -eq 0) { Write-Ok "没有出现「超过硬性期限」（说明时长判据是对的）" }
    else { Write-Bad "出现 $deadline 次「超过硬性期限」：片子被提前掐断了" }
}

if (-not $appChanged) {
    Write-Info "本次运行没写 castreceiver.log，跳过「残留入场窗」断言"
} else {
    # castreceiver.log 是追加的，里面还留着历史会话（用户上一次挂黑屏的那次就留了十几条），
    # 所以只统计**本次运行开始之后**写入的行。
    $leftover = 0
    foreach ($line in (Get-Content $appLog -Encoding UTF8 | Select-Object -Last 800)) {
        if ($line -notmatch "发现残留的入场窗") { continue }
        if ($line -match '^(\d{2}):(\d{2}):(\d{2})\.(\d{3})') {
            $stamp = New-Object datetime($testStart.Year, $testStart.Month, $testStart.Day,
                [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4])
            if ($stamp -ge $testStart.AddSeconds(-2)) { $leftover++ }
        }
    }

    if ($leftover -eq 0) { Write-Ok "本次运行没有出现「发现残留的入场窗」" }
    else { Write-Bad "本次运行出现 $leftover 次「发现残留的入场窗」（界面巡检在反复抢救黑窗）" }
}

# ---------------------------------------------------------------- 收尾
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "入场动画预览回归测试：全部通过" -ForegroundColor Green
} else {
    Write-Host "入场动画预览回归测试：$($script:failures) 项失败" -ForegroundColor Red
}
exit $script:failures
