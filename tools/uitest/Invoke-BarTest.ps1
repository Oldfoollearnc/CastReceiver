# 验证悬浮控制条（极简化版）与它周边的这一轮交互。
#
# 断言：
#   ① 窗口模式：条上就是 电脑音量 / 截屏 / 录制 / 小窗 四个按钮，没有"更多/展开"这一层
#   ② 舞台顶栏已经有的按钮（全屏 / 适应 / 缩小 / 放大 / 缩放复位）不再重复出现在控制条上
#   ③ 小窗按钮是开关：点一下开、再点一下关；喇叭点开音量弹层、再点收起
#   ③c 右键条：常用功能菜单（截屏 / 录制 / 静音 / 全屏 / 设置…）
#   ③d 侧栏把手：收起后侧栏内容从自动化树里消失，再点又回来
#   ④ 全屏时多一个「退出全屏」；点了真能退出并恢复窗口尺寸
#   ⑤ 双击画面进全屏、再双击退出；舞台顶栏的缩放仍双向可用
#
# 用法：
#   powershell.exe -ExecutionPolicy Bypass -File Invoke-BarTest.ps1 -Exe "路径\投屏Window.exe"
#   加 -ShotDir 目录可把「窗口模式 / 音量弹层 / 右键菜单 / 全屏」截图存下来（人眼复核用）
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 14,
    [string]$ShotDir = ""
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public class BarWin {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
  public static void Click() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  public static void DoubleClick() { Click(); System.Threading.Thread.Sleep(90); Click(); }
  public static void RightClick() { mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); }
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

$script:fail = 0
function Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:fail++ }
function Check($cond, $t) { if ($cond) { Ok $t } else { Bad $t } }

function Find-Window($targetPid, [string]$name = "") {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $c)
    foreach ($w in $wins) { if ($name -eq "" -or $w.Current.Name -eq $name) { return $w } }
    return $null
}

function Find-ByName($win, [string]$name) {
    if (-not $win) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 按名字前缀找按钮：喇叭的自动化名会带状态（「电脑音量（已静音）」），不能用全等匹配
function Find-ByNameLike($win, [string]$pattern) {
    if (-not $win) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) {
        if ($b.Current.Name -like $pattern) { return $b }
    }
    return $null
}

function Find-ById($win, [string]$id) {
    if (-not $win) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Click($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false }
}

function Text-Of($el) { if ($el) { return $el.Current.Name } else { return "?" } }

# 无边框浮层窗口：按标题从 Win32 拿句柄，再转成自动化元素
function Bar([int]$targetPid, [string]$title) {
    $h = [BarWin]::Find($targetPid, $title)
    if ($h -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($h)
}

function Bar-Buttons($bar) {
    if (-not $bar) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $found = $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
    $names = @()
    foreach ($b in $found) { $names += $b.Current.Name }
    return $names
}

function Rect-Of($win) {
    $r = $win.Current.BoundingRectangle
    return "{0},{1} {2}x{3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height
}

# 窗口模式常驻；全屏时鼠标靠近底部才出现 —— 点之前先把鼠标放过去
function Show-Bar($win) {
    $r = $win.Current.BoundingRectangle
    [BarWin]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Bottom - 60)) | Out-Null
    Start-Sleep -Milliseconds 900
}

function Save-Shot($win, [string]$name, [int]$pad = 16) {
    if (-not $ShotDir -or -not $win) { return }
    try {
        Add-Type -AssemblyName System.Drawing
        if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Path $ShotDir -Force | Out-Null }
        $dir = (Resolve-Path -LiteralPath $ShotDir).Path
        $r = $win.Current.BoundingRectangle
        $x = [int][Math]::Max(0, $r.X - $pad); $y = [int][Math]::Max(0, $r.Y - $pad)
        $w = [int]($r.Width + 2 * $pad); $h = [int]($r.Height + 2 * $pad)
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $path = Join-Path $dir "$name.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "  截图：$path"
    } catch { Write-Host "  截图失败：$_" }
}

# ============================================================ 起程序
$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro", "--demo-cast" -PassThru
Write-Host "已启动（--no-intro --demo-cast），等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

$main = Find-Window $p.Id "投屏Window"
if (-not $main) { Bad "未找到主窗口"; Stop-Process -Id $p.Id -Force; exit 1 }
[void][BarWin]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)
Show-Bar $main

# ============================================================ ① 窗口模式：三个按钮全摆出来
Write-Host "`n=== ① 窗口模式：音量 / 截屏 / 录制 / 小窗 直接全摆出来 ==="
$bar = Bar $p.Id "控制条"
if (-not $bar) { Bad "未找到悬浮控制条"; Stop-Process -Id $p.Id -Force; exit 1 }

$windowed = Bar-Buttons $bar
Write-Host "  按钮：$($windowed -join ' | ')"
Check ($windowed -contains "截屏") "控制条上有「截屏」"
Check ($windowed -contains "录制") "控制条上有「录制」"
Check ($windowed -contains "小窗") "控制条上有「小窗」"
Check ([bool]($windowed | Where-Object { $_ -like "电脑音量*" })) "控制条上有「电脑音量」"
Check ($windowed.Count -eq 4) "控制条上就 4 个按钮，没有多余的（实际 $($windowed.Count) 个）"
Check (-not ($windowed -contains "更多操作")) "没有「更多操作」这一层（按钮少，直接全展示）"
Save-Shot $bar "01-窗口模式"

# ============================================================ ② 不重复舞台顶栏的按钮
Write-Host "`n=== ② 不该再重复「舞台顶栏已经有了」的按钮 ==="
foreach ($dup in @("全屏", "退出全屏", "适应", "铺满", "拉伸", "原始", "画面适配", "缩小", "放大", "缩放复位")) {
    Check (-not ($windowed -contains $dup)) "控制条上没有「$dup」"
}

# ============================================================ ③ 小窗按钮是开关
Write-Host "`n=== ③ 「小窗」点一下开、再点一下关 ==="
Click (Find-ByName $bar "小窗") | Out-Null
Start-Sleep -Milliseconds 1000
Check ([bool]([BarWin]::Find($p.Id, "小窗") -ne [IntPtr]::Zero)) "点一下：小窗开了"
Click (Find-ByName $bar "小窗") | Out-Null
Start-Sleep -Milliseconds 1000
Check ([BarWin]::Find($p.Id, "小窗") -eq [IntPtr]::Zero) "再点一下：小窗关了"

# ============================================================ ③b 音量弹层
Write-Host "`n=== ③b 点喇叭：弹出音量滑杆；再点收起 ==="
Click (Find-ByNameLike $bar "电脑音量*") | Out-Null
Start-Sleep -Milliseconds 900
$vol = Bar $p.Id "音量"
Check ([bool]$vol) "音量弹层出现了"
if ($vol) {
    $slider = Find-ByName $vol "电脑音量"
    Check ([bool]$slider) "弹层里有音量滑杆"
    Save-Shot $vol "02-音量弹层"
}
Click (Find-ByNameLike $bar "电脑音量*") | Out-Null
Start-Sleep -Milliseconds 900
Check ([BarWin]::Find($p.Id, "音量") -eq [IntPtr]::Zero -or -not (Bar $p.Id "音量").IsVisible()) "再点一次收起来了"

# ============================================================ ③c 右键菜单
Write-Host "`n=== ③c 右键控制条：常用功能菜单 ==="
$br = $bar.Current.BoundingRectangle
[BarWin]::SetCursorPos([int]($br.Left + $br.Width / 2), [int]($br.Top + $br.Height / 2)) | Out-Null
Start-Sleep -Milliseconds 300
[BarWin]::RightClick()
Start-Sleep -Milliseconds 900
$menuNames = @()
$root = [System.Windows.Automation.AutomationElement]::RootElement
$menuCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $menuCond)) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($m in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) { $menuNames += $m.Current.Name }
}
Write-Host "  菜单项：$($menuNames -join ' | ')"
Check ([bool]($menuNames | Where-Object { $_ -like "截屏*" })) "菜单里有「截屏」"
Check ([bool]($menuNames | Where-Object { $_ -like "录制*" -or $_ -like "开始录制*" -or $_ -like "停止录制*" })) "菜单里有「录制」"
Check ([bool]($menuNames | Where-Object { $_ -like "静音*" -or $_ -like "取消静音*" })) "菜单里有「静音」"
Check ([bool]($menuNames | Where-Object { $_ -like "进入全屏*" -or $_ -like "退出全屏*" })) "菜单里有「全屏」"
Check ([bool]($menuNames | Where-Object { $_ -like "设置*" })) "菜单里有「设置…」"
Save-Shot $main "03-右键菜单"
# 关掉菜单：按 Esc（菜单弹层吃键）
[void][BarWin]::SetCursorPos([int]($br.Left + $br.Width / 2), [int]($br.Top - 80))
Start-Sleep -Milliseconds 400

# ============================================================ ③d 侧栏折叠
Write-Host "`n=== ③d 侧栏把手：收起 / 展开 ==="
$handle = Find-ByName $main "折叠侧栏"
Check ([bool]$handle) "主界面上有「折叠侧栏」把手"
Check ([bool](Find-ByName $main "接收端开关")) "收起前：侧栏的「接收端开关」看得见"
Click $handle | Out-Null
Start-Sleep -Milliseconds 1200
Check ([bool](Find-ByName $main "展开侧栏")) "点一下：把手变成「展开侧栏」"
Check (-not [bool](Find-ByName $main "接收端开关")) "侧栏内容已收起（开关不在自动化树里）"
Click (Find-ByName $main "展开侧栏") | Out-Null
Start-Sleep -Milliseconds 1200
Check ([bool](Find-ByName $main "接收端开关")) "再点一下：侧栏回来了"

# ============================================================ ④ 缩放搬去舞台顶栏后仍然可用
Write-Host "`n=== ④ 舞台顶栏的缩放（原控制条上的那套）仍双向可用 ==="
$zoom0 = Text-Of (Find-ById $main "StageZoomText")
Click (Find-ByName $main "缩小画面") | Out-Null; Start-Sleep -Milliseconds 500
Click (Find-ByName $main "缩小画面") | Out-Null; Start-Sleep -Milliseconds 700
$zoomOut = Text-Of (Find-ById $main "StageZoomText")
Click (Find-ByName $main "放大画面") | Out-Null; Start-Sleep -Milliseconds 500
Click (Find-ByName $main "放大画面") | Out-Null; Start-Sleep -Milliseconds 700
$zoomBack = Text-Of (Find-ById $main "StageZoomText")
Write-Host "  初始 $zoom0 → 两次缩小 $zoomOut → 两次放大 $zoomBack"
Check ($zoomOut -ne $zoom0) "缩小有效（$zoom0 → $zoomOut）"
Check ($zoomBack -eq $zoom0) "放大能回到原值（$zoomOut → $zoomBack）"

# ============================================================ ⑤ 全屏：多一个「退出全屏」
Write-Host "`n=== ⑤ 全屏时多一个「退出全屏」，双击画面也能进出 ==="
$before = Rect-Of $main
Click (Find-ByName $main "全屏") | Out-Null
Start-Sleep -Seconds 4
Show-Bar $main
$bar = Bar $p.Id "控制条"
if (-not $bar) {
    Bad "全屏下没找到控制条"
} else {
    $fsButtons = Bar-Buttons $bar
    Write-Host "  全屏下按钮：$($fsButtons -join ' | ')"
    Check ($fsButtons -contains "退出全屏") "全屏时出现「退出全屏」"
    Check ($fsButtons.Count -eq 5) "全屏时 5 个按钮（实际 $($fsButtons.Count) 个）"
    Save-Shot $bar "04-全屏"
    Click (Find-ByName $bar "退出全屏") | Out-Null
    Start-Sleep -Seconds 3
    $main = Find-Window $p.Id "投屏Window"
    $after = Rect-Of $main
    Write-Host "  退出全屏：$before → $after"
    Check ($after -eq $before) "退出全屏后窗口尺寸恢复"
    $back = Bar-Buttons (Bar $p.Id "控制条")
    Check (-not ($back -contains "退出全屏")) "回到窗口模式后「退出全屏」又收掉了"
}

# 双击画面 = 进全屏；再双击 = 退出（Esc 退出全屏另有按键路径）
$windowedRect = Rect-Of $main
$mr = $main.Current.BoundingRectangle
[BarWin]::SetCursorPos([int]($mr.Left + $mr.Width * 0.4), [int]($mr.Top + $mr.Height * 0.6)) | Out-Null
Start-Sleep -Milliseconds 400
[BarWin]::DoubleClick()
Start-Sleep -Seconds 3
$fsRect = Rect-Of (Find-Window $p.Id "投屏Window")
Write-Host "  双击后窗口：$fsRect"
Check ($fsRect -ne $windowedRect) "双击画面进入了全屏"
Start-Sleep -Milliseconds 400
[BarWin]::DoubleClick()
Start-Sleep -Seconds 3
$backRect = Rect-Of (Find-Window $p.Id "投屏Window")
Write-Host "  再双击后窗口：$backRect"
Check ($backRect -eq $windowedRect) "再双击退出了全屏"

# ============================================================ 收尾
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill(); Write-Host "强制结束" } else { Write-Host "已优雅退出" }

Write-Host ""
if ($script:fail -eq 0) { Write-Host "结论：悬浮控制条极简化 —— 全部通过 OK" -ForegroundColor Green; exit 0 }
Write-Host "结论：$($script:fail) 项不通过" -ForegroundColor Red
exit 2