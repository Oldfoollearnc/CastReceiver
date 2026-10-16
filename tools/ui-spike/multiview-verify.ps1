# 多设备并排（③）+ 放大切换（④）验证脚本（开发期用，不参与产品构建）
#
# 用**两个真实的伪 iPhone 客户端**同时投屏，验证：
#   1. 只有 1 路时：宫格完全收起（右列不占位置，画面铺满整块画面区）
#   2. 两路同时投时：出现 1 个格子，且**两路都真的有画面**（不是黑屏）
#   3. 点格子＝把那一路放大（④）：格子里的「第几路」从 2/2 变成 1/2，两路画面都还在
#
# 为什么两路都要查像素：本项目的"一片黑"事故就是断言全过、画面全黑（见 docs/升级-实现进度.md 三·1）。
# 为什么用「第几路」而不是设备名判断切换：两个伪客户端都是 Fake iPhone，设备名一样；
# 序号是唯一能区分"哪一路被放大"的界面证据（与小窗那次 2/2 → 1/2 的验证同一思路）。
param(
    [string]$Exe,
    [string]$SettingsRoot,
    [string]$ShotDir,
    [string]$Python,
    [string]$Client,
    [string]$Vectors,
    [string]$Frames,
    [string]$LocalIp,
    [int]$WaitReady = 12,
    [int]$HoldFirst = 90,
    [int]$HoldSecond = 70
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 没传 -LocalIp 就自己找"真正在用的那块网卡"（有默认网关的那个）：
# 之前手填过 192.168.5.11，过一阵 DHCP 换成 .4，整片断言就全失败了 —— 别再手填。
if ([string]::IsNullOrWhiteSpace($LocalIp)) {
    $LocalIp = (Get-NetIPConfiguration |
        Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        ForEach-Object { $_.IPv4Address.IPAddress } |
        Where-Object { $_ -and $_ -notlike '169.254.*' } |
        Select-Object -First 1)
}
if ([string]::IsNullOrWhiteSpace($LocalIp)) { throw '找不到可用的局域网 IP，请用 -LocalIp 指定' }
Write-Host "本机局域网地址：$LocalIp"

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MV {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<IntPtr> ForProcess(uint want) {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == want && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int[] Bounds(IntPtr h) {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}
'@

function Get-AppWindows([int]$ProcessId) {
    $rows = @()
    foreach ($h in [MV]::ForProcess([uint32]$ProcessId)) {
        $b = [MV]::Bounds($h)
        $rows += [pscustomobject]@{ Handle = $h; Title = [MV]::Title($h); Left = $b[0]; Top = $b[1]; Width = $b[2]; Height = $b[3] }
    }
    return $rows
}

# 采样一块区域的"非暗像素占比"。全黑约 0，有画面明显更高。
function PictureRatio([int]$X, [int]$Y, [int]$W, [int]$H) {
    if ($W -le 4 -or $H -le 4) { return 0.0 }
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
    $bright = 0; $total = 0
    for ($yy = 2; $yy -lt $H - 2; $yy += 4) {
        for ($xx = 2; $xx -lt $W - 2; $xx += 4) {
            $c = $bmp.GetPixel($xx, $yy)
            if (($c.R + $c.G + $c.B) -gt 90) { $bright++ }
            $total++
        }
    }
    $g.Dispose(); $bmp.Dispose()
    if ($total -eq 0) { return 0.0 }
    return [math]::Round($bright / $total, 3)
}

function Shot([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# 读界面上的「并排格子」：靠 AutomationId 找，名字就是「第几路/共几路」。
# 只有**没被放大**的那几路才有标题栏，所以这个数量正好等于宫格里的格子数。
function Get-TileInfo([int]$ProcessId) {
    $rows = @()
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MultiViewTileIndex')))
        $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        foreach ($el in $found) {
            $r = $el.Current.BoundingRectangle
            $rows += [pscustomobject]@{
                Index = $el.Current.Name
                Left = [int]$r.Left; Top = [int]$r.Top; Right = [int]$r.Right; Bottom = [int]$r.Bottom
            }
        }
    } catch { Write-Host ("UIAutomation 读取失败：" + $_.Exception.Message) }
    return $rows
}

$script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $tag = if ($Ok) { "PASS" } else { "FAIL" }
    if (-not $Ok) { $script:fail++ }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

# 按 AutomationId 取一组元素的显示文字（格子标题栏的序号 / 设备名）
function Get-ByAutomationId([int]$ProcessId, [string]$AutomationId) {
    $names = @()
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)))
        foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $names += $el.Current.Name
        }
    } catch { }
    return $names
}

# ---------------------------------------------------------------- 准备
New-Item -ItemType Directory -Force -Path $SettingsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

# 伪客户端是 Python 脚本，而仓库路径里有中文和空格：
# Start-Process 传参不自动加引号、控制台代码页还会改动非 ASCII 字符，
# 所以先把要用的东西复制到一个纯 ASCII、无空格的临时目录再跑（踩过这个坑）。
$work = Join-Path $env:TEMP 'multiview-verify-work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Copy-Item (Join-Path (Split-Path $Client -Parent) '*.py') $work -Force
Copy-Item $Client (Join-Path $work 'airplay_fake_client.py') -Force
Copy-Item $Vectors (Join-Path $work 'fairplay-vectors.json') -Force
Copy-Item $Frames (Join-Path $work 'sample_frames.bin') -Force
$pyClient = Join-Path $work 'airplay_fake_client.py'
$pyVectors = Join-Path $work 'fairplay-vectors.json'
$pyFrames = Join-Path $work 'sample_frames.bin'

# 参数一律显式加引号（路径带空格时必须）
$clientArgLine = '"{0}" "{1}" "{2}" PC aa:bb:cc:dd:ee:ff' -f $pyClient, $LocalIp, $pyVectors

$settings = [ordered]@{
    DeviceName          = 'PC'
    AntiCapture         = $false
    AutoStartAirPlay    = $true
    AutoStartMiracast   = $false
    AutoStartDlna       = $false
    HasSeenTutorial     = $true
    MiniOnMinimize      = $false
    MiniPinned          = $false
    ShowStats           = $false
    AutoFullscreen      = $false
    NotifyOnConnect     = $false
    GlobalHotkeys       = $false
    PrivacyLog          = $true
    IdleBroadcastRefreshMinutes = 0
}
$settings | ConvertTo-Json | Set-Content -Path (Join-Path $SettingsRoot 'settings.json') -Encoding UTF8

# ---------------------------------------------------------------- 启动
$proc = Start-Process -FilePath $Exe -ArgumentList '--no-intro', '--settings-root', $SettingsRoot -PassThru
Start-Sleep -Seconds $WaitReady
Check "程序已启动" (-not $proc.HasExited) ("pid=" + $proc.Id)
if ($proc.HasExited) { exit 1 }

$main = Get-AppWindows $proc.Id | Where-Object { $_.Title -eq '投屏Window' } | Select-Object -First 1
Check "找到主窗口" ($null -ne $main) ("")
if ($null -eq $main) { Stop-Process -Id $proc.Id -Force; exit 1 }

# 固定窗口大小，让画面区足够大（否则格子会把主画面挤得很窄，采样区域不好取）
[void][MV]::SetWindowPos($main.Handle, [MV]::HWND_TOPMOST, 60, 40, 1500, 900, 0x0040)
Start-Sleep -Milliseconds 800
[void][MV]::SetForegroundWindow($main.Handle)
Start-Sleep -Milliseconds 600
$main = Get-AppWindows $proc.Id | Where-Object { $_.Title -eq '投屏Window' } | Select-Object -First 1
$dpi = [MV]::GetDpiForWindow($main.Handle) / 96.0
Write-Host ("主窗口 {0},{1} {2}x{3}；DPI 缩放 {4}" -f $main.Left, $main.Top, $main.Width, $main.Height, $dpi)

# 画面区（按 XAML 的边距推算：顶栏 46、四周 16/4/10、底栏 30、右侧栏 286+14）
$sideW = [int](300 * $dpi)
$stageLeft = $main.Left + [int](17 * $dpi)
$stageTop = $main.Top + [int](50 * $dpi)
$stageRight = $main.Left + $main.Width - [int](17 * $dpi) - $sideW
$stageBottom = $main.Top + $main.Height - [int](40 * $dpi)
$stageW = $stageRight - $stageLeft
$stageH = $stageBottom - $stageTop
Write-Host ("画面区推算：{0},{1} {2}x{3}" -f $stageLeft, $stageTop, $stageW, $stageH)

$tiles0 = Get-TileInfo $proc.Id
Check "未投屏时没有格子" (@($tiles0).Count -eq 0) ("格子数=" + @($tiles0).Count)

# ---------------------------------------------------------------- 第 1 路
$argLine1 = $clientArgLine + (' {0} "{1}"' -f $HoldFirst, $pyFrames)
$client1 = Start-Process -FilePath $Python -ArgumentList $argLine1 -PassThru -RedirectStandardOutput (Join-Path $ShotDir 'client1.log') -RedirectStandardError (Join-Path $ShotDir 'client1.err')
Start-Sleep -Seconds 14
[void][MV]::SetForegroundWindow($main.Handle)
Start-Sleep -Milliseconds 500

$tiles1 = Get-TileInfo $proc.Id
Check "1 路时宫格完全收起（没有格子）" (@($tiles1).Count -eq 0) ("格子数=" + @($tiles1).Count)

# 宫格收起时画面应当铺满整块画面区：紧贴右边缘取样
$edge = PictureRatio ($stageRight - [int](160 * $dpi)) ($stageTop + [int](60 * $dpi)) ([int](140 * $dpi)) ([int](240 * $dpi))
Check "1 路：画面铺到画面区右边缘（宫格没占位）" ($edge -gt 0.05) ("右边缘非暗占比=" + $edge)
$center = PictureRatio ($stageLeft + [int](60 * $dpi)) ($stageTop + [int](40 * $dpi)) ([int](300 * $dpi)) ([int](300 * $dpi))
Check "1 路：主画面有画面（不是黑屏）" ($center -gt 0.05) ("非暗占比=" + $center)
Shot (Join-Path $ShotDir '并排-1路-宫格收起.png') $main.Left $main.Top $main.Width $main.Height

# ---------------------------------------------------------------- 第 2 路
$argLine2 = $clientArgLine + (' {0} "{1}"' -f $HoldSecond, $pyFrames)
$client2 = Start-Process -FilePath $Python -ArgumentList $argLine2 -PassThru -RedirectStandardOutput (Join-Path $ShotDir 'client2.log') -RedirectStandardError (Join-Path $ShotDir 'client2.err')
Start-Sleep -Seconds 14
[void][MV]::SetForegroundWindow($main.Handle)
Start-Sleep -Milliseconds 500

$tiles2 = Get-TileInfo $proc.Id
Check "2 路时出现 1 个格子" (@($tiles2).Count -eq 1) ("格子数=" + @($tiles2).Count + " 序号=" + (@($tiles2) | ForEach-Object { $_.Index }) -join ',')
$index2 = if (@($tiles2).Count -ge 1) { @($tiles2)[0].Index } else { "" }
Check "格子上写的是第 2 路（1/2 位置留给被放大的那一路）" ($index2 -eq '2/2') ("格子序号=" + $index2)

if (@($tiles2).Count -ge 1) {
    $t = @($tiles2)[0]
    $tileRight = $t.Right + [int](9 * $dpi)
    $tileLeft = $tileRight - [int](246 * $dpi)
    $tileTop = $t.Top
    $tilePic = PictureRatio ($tileLeft + [int](6 * $dpi)) ($tileTop + [int](32 * $dpi)) ([int](220 * $dpi)) ([int](110 * $dpi))
    Check "格子里的画面有内容（不是黑屏）" ($tilePic -gt 0.05) ("格子非暗占比=" + $tilePic)
}
$mainPic2 = PictureRatio ($stageLeft + [int](60 * $dpi)) ($stageTop + [int](40 * $dpi)) ([int](260 * $dpi)) ([int](300 * $dpi))
Check "2 路：主画面仍有画面" ($mainPic2 -gt 0.05) ("非暗占比=" + $mainPic2)

# 格子的名字必须是识别出来的型号（伪客户端报 iPhone15,2 → iPhone 14 Pro）。
# 这一条专防"识别结果比会话先到、被丢掉"那个坑：丢了不会崩，只会静默显示兜底名字。
$labels = Get-ByAutomationId $proc.Id 'MultiViewTileLabel'
$labelText = ($labels -join ',')
Check "格子标题显示识别出的型号（不是兜底名）" ($labelText -like '*iPhone*') ("格子标题=" + $labelText)
Shot (Join-Path $ShotDir '并排-2路.png') $main.Left $main.Top $main.Width $main.Height

# ---------------------------------------------------------------- 点格子＝放大这一路（④）
if (@($tiles2).Count -ge 1) {
    $t = @($tiles2)[0]
    $clickX = [int](($t.Left + $t.Right) / 2)
    $clickY = $t.Top + [int](13 * $dpi)          # 点标题栏（WPF 区域，稳）
    [MV]::Click($clickX, $clickY)
    Start-Sleep -Seconds 4
    [void][MV]::SetForegroundWindow($main.Handle)
    Start-Sleep -Milliseconds 500

    $tiles3 = Get-TileInfo $proc.Id
    $index3 = if (@($tiles3).Count -ge 1) { @($tiles3)[0].Index } else { "" }
    Check "点格子后焦点切到第 2 路（格子变成第 1 路）" ($index3 -eq '1/2') ("格子序号=" + $index3 + " 格子数=" + @($tiles3).Count)

    if (@($tiles3).Count -ge 1) {
        $t3 = @($tiles3)[0]
        $tileRight3 = $t3.Right + [int](9 * $dpi)
        $tileLeft3 = $tileRight3 - [int](246 * $dpi)
        $tilePic3 = PictureRatio ($tileLeft3 + [int](6 * $dpi)) ($t3.Top + [int](32 * $dpi)) ([int](220 * $dpi)) ([int](110 * $dpi))
        Check "切换后格子里的画面还在" ($tilePic3 -gt 0.05) ("格子非暗占比=" + $tilePic3)
    }
    $mainPic3 = PictureRatio ($stageLeft + [int](60 * $dpi)) ($stageTop + [int](40 * $dpi)) ([int](260 * $dpi)) ([int](300 * $dpi))
    Check "切换后主画面仍在（换成另一路）" ($mainPic3 -gt 0.05) ("非暗占比=" + $mainPic3)
    Shot (Join-Path $ShotDir '并排-2路-点格子放大另一路.png') $main.Left $main.Top $main.Width $main.Height
}

# ---------------------------------------------------------------- 收尾：一路断开，另一路必须继续
Stop-Process -Id $client2.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 7
$tilesAfter = Get-TileInfo $proc.Id
Check "断掉被放大的那一路后：格子归零（只剩一路，自动接回主画面）" (@($tilesAfter).Count -eq 0) ("格子数=" + @($tilesAfter).Count)
$afterPic = PictureRatio ($stageLeft + [int](60 * $dpi)) ($stageTop + [int](40 * $dpi)) ([int](260 * $dpi)) ([int](300 * $dpi))
Check "另一路仍在投（画面没被一起清掉）" ($afterPic -gt 0.05) ("非暗占比=" + $afterPic)

Stop-Process -Id $client1.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 8
$tilesEnd = Get-TileInfo $proc.Id
Check "两路都断开后宫格归零" (@($tilesEnd).Count -eq 0) ("格子数=" + @($tilesEnd).Count)
$endPic = PictureRatio ($stageLeft + [int](60 * $dpi)) ($stageTop + [int](40 * $dpi)) ([int](260 * $dpi)) ([int](300 * $dpi))
Check "全部断开后画面回到待机（不是残留的旧画面）" ($endPic -lt 0.05) ("非暗占比=" + $endPic)
Shot (Join-Path $ShotDir '并排-全部断开.png') $main.Left $main.Top $main.Width $main.Height

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host ("==== 多设备并排验证：失败 {0} 项 ====" -f $script:fail)
exit $script:fail
