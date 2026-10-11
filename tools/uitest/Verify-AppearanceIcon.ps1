# 顶栏「切换外观」按钮可见性回归验证。
#
# 背景：深色主题下图标是「月亮」（填充路径）。旧路径的内弧半径（6.4）小于两端点弦长的一半（8），
#       按 SVG 规则半径会被等比放大到 8、与外弧完全重合，图形面积归零 —— 深色下按钮完全看不见。
#
# 注意：主窗口默认开着反截屏（SetWindowDisplayAffinity / WDA_EXCLUDEFROMCAPTURE），
#       任何外部抓屏（本脚本、截图工具、录屏软件）都只能拍到窗口「背后」的内容，
#       PrintWindow 也不行。所以这里用 --settings-root 另起一个配置独立、且关掉反截屏的实例来截图，
#       全过程不碰用户自己的 %LocalAppData%\CastReceiver\settings.json。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\uitest\Verify-AppearanceIcon.ps1 -Exe "发布版\投屏Window\投屏Window.exe"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 14,
    [string]$OutDir = "docs",
    [string]$WindowTitle = "投屏Window",
    # 默认自动准备临时配置目录；也可以手工指定一个（里面要有 AntiCapture=false 的 settings.json）
    [string]$SettingsRoot = ""
)
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Text;using System.Collections.Generic;
public class ApCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowDisplayAffinity(IntPtr h, out uint affinity);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

  public static string Describe(IntPtr h) {
    var r = new RECT(); GetWindowRect(h, out r);
    var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
    var c = new StringBuilder(256); GetClassNameW(h, c, 256);
    uint aff; GetWindowDisplayAffinity(h, out aff);
    return string.Format("hwnd=0x{0:X} vis={1} min={2} affinity=0x{3:X} cls={4} rect=({5},{6},{7},{8}) title={9}",
      h.ToInt64(), IsWindowVisible(h), IsIconic(h), aff, c, r.Left, r.Top, r.Right, r.Bottom, t);
  }
  public static List<string> ListAll(uint target) {
    var list = new List<string>();
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) list.Add(Describe(h));
      return true; }, IntPtr.Zero);
    return list;
  }
  // 只认「可见、未最小化、标题匹配、宽度像主窗口」的那个，跳过启动闪屏与提示浮层
  public static IntPtr FindByTitle(uint target, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target && IsWindowVisible(h) && !IsIconic(h)) {
        var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
        var r = new RECT(); GetWindowRect(h, out r);
        if (t.ToString() == title && (r.Right - r.Left) > 400) { found = h; return false; }
      }
      return true; }, IntPtr.Zero);
    return found;
  }
}
"@

# 后台进程抢不到前台焦点：SetForegroundWindow 会被系统拒绝，抓到的是别的窗口。
# 置顶（TOPMOST）才能稳定抓到应用自己的画面。
$HWND_TOPMOST = [IntPtr](-1); $HWND_NOTOPMOST = [IntPtr](-2)
$SWP_NOSIZE = 0x0001; $SWP_NOMOVE = 0x0002; $SWP_SHOWWINDOW = 0x0040
function Set-Topmost($hwnd, $on) {
    $after = if ($on) { $HWND_TOPMOST } else { $HWND_NOTOPMOST }
    [ApCap]::SetWindowPos($hwnd, $after, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW)) | Out-Null
    if ($on) { [ApCap]::BringWindowToTop($hwnd) | Out-Null; [ApCap]::SetForegroundWindow($hwnd) | Out-Null }
    Start-Sleep -Milliseconds 900
}

function Find-ByName($pid_, $name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc, $nc)))
}

function Capture($hwnd, $out) {
    $r = New-Object ApCap+RECT
    [ApCap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    # 主窗口可能有一部分在屏幕外（实测 rect 从 x=-110 开始），BitBlt 不接受负坐标，先裁到屏幕内
    $left = [Math]::Max(0, $r.Left); $top = [Math]::Max(0, $r.Top)
    $w = $r.Right - $left; $h = $r.Bottom - $top
    if ($w -le 0 -or $h -le 0) { Write-Host "  窗口尺寸异常，跳过 $out" -ForegroundColor Red; return }
    # 画面走 D3D 合成（Windows.UI.Composition），PrintWindow 抓出来是全白，
    # 因此和 Invoke-ThemeCheck.ps1 一样直接抓屏（要求窗口在最前、未被遮挡）。
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    # 顶栏右侧（含外观按钮 + 三个窗口按钮）放大 3 倍；窗口右上角 = 位图右上角
    $cw = [Math]::Min(420, $w); $ch = [Math]::Min(46, $h)
    $crop = New-Object System.Drawing.Rectangle ($w - $cw), 0, $cw, $ch
    $part = $bmp.Clone($crop, $bmp.PixelFormat)
    $zoom = New-Object System.Drawing.Bitmap ($cw * 3), ($ch * 3)
    $zg = [System.Drawing.Graphics]::FromImage($zoom)
    $zg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $zg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $zg.DrawImage($part, 0, 0, $cw * 3, $ch * 3)
    $zg.Dispose()
    $zoomOut = [System.IO.Path]::ChangeExtension($out, $null).TrimEnd('.') + "-header.png"
    $zoom.Save($zoomOut, [System.Drawing.Imaging.ImageFormat]::Png)
    $part.Dispose(); $zoom.Dispose(); $bmp.Dispose()
    Write-Host ("  已保存 {0} ({1}x{2}) + {3}" -f $out, $w, $h, $zoomOut)
}

# ---------------------------------------------------------------- 准备独立配置（关掉反截屏）
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tempSettingsRoot = $false
if (-not $SettingsRoot) {
    $SettingsRoot = Join-Path $projectRoot "tests\tmp-capture-settings"
    New-Item -ItemType Directory -Path $SettingsRoot -Force | Out-Null
    $userSettings = Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json"
    $json = if (Test-Path $userSettings) {
        [System.IO.File]::ReadAllText($userSettings) -replace '"AntiCapture"\s*:\s*true', '"AntiCapture": false'
    } else {
        "{`n  `"AntiCapture`": false,`n  `"Theme`": `"Dark`"`n}"
    }
    [System.IO.File]::WriteAllText((Join-Path $SettingsRoot "settings.json"), $json, [System.Text.UTF8Encoding]::new($false))
    $tempSettingsRoot = $true
    Write-Host "临时配置目录（已关闭反截屏）：$SettingsRoot"
}
$InstanceSuffix = "dsh-verify"      # 避免与正在运行的实例抢单例

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$spArgs = @('--no-intro', '--settings-root "{0}"' -f $SettingsRoot, '--instance-suffix "{0}"' -f $InstanceSuffix)
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList ($spArgs -join ' ') -PassThru
Write-Host "已启动，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds
if ($p.HasExited) { Write-Host "进程已退出" -ForegroundColor Red; exit 1 }

$main = [ApCap]::FindByTitle([uint32]$p.Id, $WindowTitle)
if ($main -eq [IntPtr]::Zero) {
    Write-Host "该进程的顶层窗口：" -ForegroundColor Yellow
    [ApCap]::ListAll([uint32]$p.Id) | ForEach-Object { Write-Host ("  " + $_) }
    $p.Refresh(); $main = $p.MainWindowHandle
    Write-Host "按标题没找到主窗口，退回 MainWindowHandle" -ForegroundColor Yellow
}
if ($main -eq [IntPtr]::Zero) { Write-Host "未取到窗口句柄" -ForegroundColor Red; exit 1 }
Write-Host ("目标窗口：" + [ApCap]::Describe($main))
[ApCap]::ShowWindow($main, 9) | Out-Null
Set-Topmost $main $true

$r = New-Object ApCap+RECT
[ApCap]::GetWindowRect($main, [ref]$r) | Out-Null
$pt = New-Object ApCap+POINT
$pt.X = $r.Right - 24; $pt.Y = $r.Top + 24
$top = [ApCap]::WindowFromPoint($pt)
Write-Host ("右上角最上层窗口就是目标窗口：" + ($top -eq $main))

Write-Host "① 深色主题（默认）"
Capture $main (Join-Path $OutDir "verify-appearance-dark.png")

$btn = Find-ByName $p.Id "切换外观"
Write-Host ("② 点击「切换外观」：" + $(if ($btn) { "找到按钮" } else { "未找到按钮" }))
if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() | Out-Null }
Start-Sleep -Seconds 2
Set-Topmost $main $true
Write-Host "③ 浅色主题"
Capture $main (Join-Path $OutDir "verify-appearance-light.png")

Set-Topmost $main $false

$p.CloseMainWindow() | Out-Null
for ($i = 0; $i -lt 20 -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Write-Host "强杀" -ForegroundColor Yellow }
else { Write-Host "已优雅退出" -ForegroundColor Green }

if ($tempSettingsRoot) { Remove-Item $SettingsRoot -Recurse -Force -ErrorAction SilentlyContinue }
