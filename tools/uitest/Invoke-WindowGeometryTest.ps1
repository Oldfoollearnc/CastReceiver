# 窗口几何回归测试：最大化不遮挡任务栏 + 窗口位置记忆
#
# 用法：
#   powershell.exe -ExecutionPolicy Bypass -File Invoke-WindowGeometryTest.ps1 -Exe "..\..\发布版\投屏Window\投屏Window.exe"
#
# 验证内容：
#   1) 点「最大化」后，窗口矩形 == 显示器工作区（任务栏可见），而不是整块屏幕
#   2) 点「还原」后窗口回到比工作区小的尺寸
#   3) 把窗口挪到指定位置并关闭 → 重新启动后位置被还原
#   4) 使用独立配置目录（--settings-root），不会动到你的真实设置
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12,
    [string]$WorkRoot = "$env:TEMP\CastReceiverGeometryTest"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace WG -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
[StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint flags; }
'@

$ErrorActionPreference = "Stop"
$script:failures = 0
function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures++ }

function Get-MainWindowHandle([int]$targetPid, [string]$title, [int]$tries = 30) {
    for ($i = 0; $i -lt $tries; $i++) {
        $script:found = [IntPtr]::Zero
        $cb = [WG.Api+EnumProc] {
            param($h, $l)
            [uint32]$o = 0
            [void][WG.Api]::GetWindowThreadProcessId($h, [ref]$o)
            if ($o -eq $targetPid -and [WG.Api]::IsWindowVisible($h)) {
                $sb = New-Object System.Text.StringBuilder 256
                [void][WG.Api]::GetWindowTextW($h, $sb, 256)
                if ($sb.ToString() -eq $title) { $script:found = $h; return $false }
            }
            return $true
        }
        [void][WG.Api]::EnumWindows($cb, [IntPtr]::Zero)
        if ($script:found -ne [IntPtr]::Zero) { return $script:found }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}

function Get-Rect($h) {
    $r = New-Object WG.Api+RECT
    [void][WG.Api]::GetWindowRect($h, [ref]$r)
    return [pscustomobject]@{ X = $r.L; Y = $r.T; W = $r.R - $r.L; H = $r.B - $r.T }
}

function Get-WorkArea($h) {
    $m = [WG.Api]::MonitorFromWindow($h, 2)
    $mi = New-Object WG.Api+MONITORINFO
    $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
    [void][WG.Api]::GetMonitorInfo($m, [ref]$mi)
    return [pscustomobject]@{
        Work    = [pscustomobject]@{ X = $mi.rcWork.L; Y = $mi.rcWork.T; W = $mi.rcWork.R - $mi.rcWork.L; H = $mi.rcWork.B - $mi.rcWork.T }
        Monitor = [pscustomobject]@{ X = $mi.rcMonitor.L; Y = $mi.rcMonitor.T; W = $mi.rcMonitor.R - $mi.rcMonitor.L; H = $mi.rcMonitor.B - $mi.rcMonitor.T }
    }
}

function Invoke-ByName($win, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { Write-Bad "未找到控件：$name"; return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Ok "已点击：$name"; return $true }
    catch { Write-Bad "点击失败：$name（$($_.Exception.Message)）"; return $false }
}

if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
$settingsRoot = Join-Path $WorkRoot "cfg"
$vaultRoot = Join-Path $WorkRoot "vault"
New-Item -ItemType Directory -Path $settingsRoot -Force | Out-Null

# ---------------------------------------------------------------- 第一次启动

Write-Step "启动（独立配置，不动你的真实设置）"
$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro --vault-root `"$vaultRoot`" --settings-root `"$settingsRoot`" --instance-suffix geo" -PassThru
Start-Sleep -Seconds $StartupWaitSeconds
$h = Get-MainWindowHandle $p.Id "投屏Window"
if ($h -eq [IntPtr]::Zero) { Write-Bad "未找到主窗口"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }
$area = Get-WorkArea $h
Write-Host ("  屏幕区域 {0}x{1} @ ({2},{3})；工作区 {4}x{5} @ ({6},{7})（任务栏占 {8}px）" -f `
    $area.Monitor.W, $area.Monitor.H, $area.Monitor.X, $area.Monitor.Y, `
    $area.Work.W, $area.Work.H, $area.Work.X, $area.Work.Y, ($area.Monitor.H - $area.Work.H))

$win = [System.Windows.Automation.AutomationElement]::FromHandle($h)

# ---------------------------------------------------------------- 最大化

Write-Step "点「最大化」→ 不应遮住任务栏"
Invoke-ByName $win "最大化" | Out-Null
Start-Sleep -Seconds 2
$r = Get-Rect $h
Write-Host ("  最大化后窗口：{0}x{1} @ ({2},{3})" -f $r.W, $r.H, $r.X, $r.Y)
$coversTaskbar = ($r.H -ge $area.Monitor.H) -and (($area.Monitor.H - $area.Work.H) -gt 0)
if ($coversTaskbar) { Write-Bad "窗口高度 $($r.H) >= 屏幕高度 $($area.Monitor.H)：任务栏被遮住了" }
else { Write-Ok "窗口高度 $($r.H) < 屏幕高度 $($area.Monitor.H)：任务栏可见" }
if ([math]::Abs($r.H - $area.Work.H) -le 2 -and [math]::Abs($r.W - $area.Work.W) -le 2) {
    Write-Ok "最大化尺寸与工作区一致（$($area.Work.W)x$($area.Work.H)）"
} else {
    Write-Bad "最大化尺寸与工作区不一致：窗口 $($r.W)x$($r.H)，工作区 $($area.Work.W)x$($area.Work.H)"
}

# ---------------------------------------------------------------- 还原

Write-Step "点「还原」"
Invoke-ByName $win "还原" | Out-Null
Start-Sleep -Seconds 2
$r2 = Get-Rect $h
Write-Host ("  还原后窗口：{0}x{1} @ ({2},{3})" -f $r2.W, $r2.H, $r2.X, $r2.Y)
if ($r2.W -lt $area.Work.W -and $r2.H -lt $area.Work.H) { Write-Ok "已恢复为窗口尺寸（小于工作区）" }
else { Write-Bad "还原后尺寸没有变小" }

# ---------------------------------------------------------------- 位置记忆

Write-Step "关闭程序 → 窗口位置应被写进配置"
$before = Get-Rect $h
Write-Host ("  关闭前窗口：{0}x{1} @ ({2},{3})" -f $before.W, $before.H, $before.X, $before.Y)
# 直接给主窗口句柄发 WM_CLOSE：Process.CloseMainWindow 有时会挑到控制条/浮层之类的其它顶层窗口
[void][WG.Api]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Seconds 5
if ($p.HasExited) { Write-Ok "程序已正常退出" }
else { Write-Host "  进程未退出（设置里若是「最小化到托盘」属正常，配置同样会写入）"; $p.Kill() }
Start-Sleep -Seconds 2

$settingsFile = Join-Path $settingsRoot "settings.json"
if (-not (Test-Path $settingsFile)) { Write-Bad "没有生成配置文件：$settingsFile" }
else {
    $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $saved = "left=$($json.WindowLeft) top=$($json.WindowTop) w=$($json.WindowWidth) h=$($json.WindowHeight)"
    Write-Host "  配置里记录：$saved"
    if ($null -eq $json.WindowLeft -or $null -eq $json.WindowTop) { Write-Bad "配置里没有窗口位置" }
    elseif ([math]::Abs($json.WindowWidth - $before.W) -le 3 -and [math]::Abs($json.WindowHeight - $before.H) -le 3) {
        Write-Ok "关闭时的窗口尺寸已写入配置"
    }
    else { Write-Bad "写入的尺寸与关闭前不一致（配置 $($json.WindowWidth)x$($json.WindowHeight)，实际 $($before.W)x$($before.H)）" }
}

Write-Step "写入一个可辨认的位置，重新启动应还原到它"
# 直接改配置来验证"启动时还原"这半边（比模拟鼠标拖动更确定）
$json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
$json.WindowLeft = 320
$json.WindowTop = 240
$json.WindowWidth = 900
$json.WindowHeight = 620
$json | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsFile -Encoding UTF8

$p2 = Start-Process -FilePath $Exe -ArgumentList "--no-intro --vault-root `"$vaultRoot`" --settings-root `"$settingsRoot`" --instance-suffix geo" -PassThru
Start-Sleep -Seconds $StartupWaitSeconds
$h2 = Get-MainWindowHandle $p2.Id "投屏Window"
if ($h2 -eq [IntPtr]::Zero) { Write-Bad "第二次启动没找到主窗口"; Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue }
else {
    $after = Get-Rect $h2
    Write-Host ("  重启后窗口：{0}x{1} @ ({2},{3})（期望 900x620 @ (320,240)）" -f $after.W, $after.H, $after.X, $after.Y)
    if ([math]::Abs($after.X - 320) -le 3 -and [math]::Abs($after.Y - 240) -le 3 -and
        [math]::Abs($after.W - 900) -le 3 -and [math]::Abs($after.H - 620) -le 3) {
        Write-Ok "启动时按配置还原了窗口位置与大小"
    } else {
        Write-Bad "启动时没有还原窗口位置与大小"
    }
    $p2.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 3
    if (-not $p2.HasExited) { $p2.Kill() }
}

Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*投屏Window*" -and $_.Path -like "*$($Exe -replace '\\','\\')*" } | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "`n========================================"
Write-Host $(if ($script:failures -eq 0) { "窗口几何测试：全部通过 ✓" } else { "窗口几何测试：$script:failures 项失败" }) -ForegroundColor $(if ($script:failures -eq 0) { "Green" } else { "Red" })
exit $script:failures
