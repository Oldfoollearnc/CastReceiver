# 启动路径回归：**开着入场动画启动**时，不能弹出"发生异常"对话框，也不能有界面线程异常
#
# 用法：powershell.exe -ExecutionPolicy Bypass -File Invoke-StartupCheck.ps1 -Exe "发布版\投屏Window\投屏Window.exe"
#
# 为什么单独有这个脚本：入场动画那条路径只在"启动时真的放动画"（ShowIntro=true）时才走，
# 其它界面测试为了稳定都关掉了它，于是这里的异常曾经溜过去（实测：启动时弹
# "发生异常：Value cannot be null. (Parameter 'callback')"）。这个脚本专盯这条路径。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$WorkRoot = "",
    [int]$IntroEndSeconds = 2,
    [int]$WaitSeconds = 22
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class StartProbe {
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    public static List<string> VisibleWindows(int pid) {
        var list = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) {
                var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
                var c = new StringBuilder(256); GetClassNameW(h, c, 256);
                list.Add(c.ToString() + " | " + t.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

$script:failures = 0
function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures++ }
function Write-Info($t) { Write-Host "  · $t" }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Exe = (Resolve-Path $Exe).Path
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot "_temp\startup-check" }
if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
$settingsRoot = Join-Path $WorkRoot "cfg"
$vaultRoot = Join-Path $WorkRoot "vault"
New-Item -ItemType Directory -Force -Path $settingsRoot, $vaultRoot | Out-Null

$clip = Join-Path (Split-Path $Exe) "Assets\intro\intro-ninja.mp4"
if (-not (Test-Path $clip)) { Write-Bad "找不到内置素材：$clip"; exit 1 }

$template = Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json"
$json = if (Test-Path $template) { Get-Content $template -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
$json.ShowIntro = $true                   # ← 关键：这条路径就是要放动画
$json.IntroSource = "file:$clip"
$json.IntroStart = 0
$json.IntroEnd = $IntroEndSeconds
$json.IntroExtend = 0
$json.IntroFadeInMs = 200
$json.IntroFadeOutMs = 300
$json.AntiCapture = $false
$json.AutoStartDlna = $false; $json.AutoStartMiracast = $false; $json.AutoStartAirPlay = $false
$json.HasSeenTutorial = $true; $json.MinimizeToTray = $false; $json.PreventSleep = $false
$json.WindowLeft = 60; $json.WindowTop = 40; $json.WindowWidth = 1280; $json.WindowHeight = 800
[System.IO.File]::WriteAllText((Join-Path $settingsRoot "settings.json"), ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

$appLog = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
$logBefore = if (Test-Path $appLog) { (Get-Item $appLog).Length } else { 0 }

Write-Step "开着入场动画启动"
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList "--vault-root `"$vaultRoot`" --settings-root `"$settingsRoot`" --instance-suffix `"-startup`""
Write-Info "PID = $($p.Id)，等 $WaitSeconds 秒（动画放完 + 交接）"
Start-Sleep -Seconds $WaitSeconds

if ($p.HasExited) { Write-Bad "进程已退出（启动就崩了）"; exit 1 }

# ① 不能有"发生异常"对话框：它是 Win32 对话框（class #32770），标题正是「投屏Window」
$windows = [StartProbe]::VisibleWindows($p.Id)
$dialog = @($windows | Where-Object { $_ -like '#32770*投屏Window*' })
if ($dialog.Count -eq 0) { Write-Ok "没有「发生异常」对话框" }
else { Write-Bad ("出现了异常对话框：" + ($dialog -join ' / ')) }
Write-Info ("当前可见窗口：" + ($windows -join ' / '))

# ② 界面必须可用：能找到主界面的入口控件
$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$found = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "打开设置")
    $found = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($found) { break }
}
if ($found) { Write-Ok "主界面已就绪（能找到「打开设置」）" } else { Write-Bad "主界面控件找不到（界面可能没交接回来）" }

# ③ 入场窗必须自己收掉，不能留黑屏
$intro = @($windows | Where-Object { $_ -like '*CastReceiverIntroWindow*' })
if ($intro.Count -eq 0) { Write-Ok "入场窗已收场（没有残留）" } else { Write-Bad ("入场窗还在：" + ($intro -join ' / ')) }

# ④ 日志里不能有界面线程异常
if (Test-Path $appLog) {
    $fs = [System.IO.File]::Open($appLog, 'Open', 'Read', 'ReadWrite')
    $fs.Seek($logBefore, 'Begin') | Out-Null
    $fresh = (New-Object System.IO.StreamReader($fs)).ReadToEnd()
    $fs.Close()
    $errors = @($fresh -split "`n" | Where-Object { $_ -match '界面线程异常|发生异常' })
    if ($errors.Count -eq 0) { Write-Ok "本次启动日志里没有界面异常" }
    else { Write-Bad ("本次启动日志里有 " + $errors.Count + " 条异常：`n" + (($errors | Select-Object -First 3) -join "`n")) }
} else {
    Write-Info "没有 castreceiver.log（受限环境里应用写不了 %LOCALAPPDATA%），跳过日志断言"
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($script:failures -eq 0) { Write-Host "启动路径回归：全部通过" -ForegroundColor Green } else { Write-Host ("启动路径回归：" + $script:failures + " 项失败") -ForegroundColor Red }
exit $script:failures
