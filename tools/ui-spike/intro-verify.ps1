# 启动入场动画验证（主窗口遮罩版）
#
# 用户要求（这一版按它验）：
#   1) 启动**不要再有小窗**（原来那个转圈小窗必须消失）
#   2) 首次启动默认就是大屏/全屏，入场动画是满屏的
#   3) 动画是在**主窗口上加的一层遮罩**（不是另开窗口）：窗口尺寸全程不变，只有里面的像素在动
#   4) 出场要淡出、露出界面；--no-intro 时没有动画
#
# 用法：
#   pwsh -File tools\ui-spike\intro-verify.ps1 -Exe <产品exe> -ShotDir docs\ui-review

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ShotDir = "docs\ui-review"
)

$ErrorActionPreference = 'Stop'
$script:fail = 0
$script:pass = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { $script:pass++; Write-Host ("[PASS] {0} :: {1}" -f $name, $detail) }
    else { $script:fail++; Write-Host ("[FAIL] {0} :: {1}" -f $name, $detail) }
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class IV {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    /// <summary>让窗口自己渲染到 DC（PW_RENDERFULLCONTENT=2）：
    /// 硬件叠加层上的视频用屏幕抓取是黑的，用这个能拿到。</summary>
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>列出该进程所有可见窗口（标题|宽x高|位置）。</summary>
    public static string[] List(uint pid) {
        var list = new List<string>();
        EnumWindows((win, p) => {
            uint owner; GetWindowThreadProcessId(win, out owner);
            if (owner != pid || !IsWindowVisible(win)) return true;
            var sb = new StringBuilder(256); GetWindowTextW(win, sb, 256);
            RECT r; GetWindowRect(win, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 40 || h < 40) return true;
            list.Add(sb.ToString() + "|" + w + "x" + h + "|" + r.Left + "," + r.Top);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    /// <summary>面积最大的那个窗口（主窗口）。</summary>
    public static IntPtr Biggest(uint pid, out int x, out int y, out int w, out int h) {
        IntPtr best = IntPtr.Zero; int bw = 0, bh = 0, bx = 0, by = 0;
        EnumWindows((win, p) => {
            uint owner; GetWindowThreadProcessId(win, out owner);
            if (owner != pid || !IsWindowVisible(win)) return true;
            RECT r; GetWindowRect(win, out r);
            int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
            if ((long)cw * ch > (long)bw * bh) { best = win; bw = cw; bh = ch; bx = r.Left; by = r.Top; }
            return true;
        }, IntPtr.Zero);
        x = bx; y = by; w = bw; h = bh; return best;
    }
}
'@

function Get-Pixels([int]$x, [int]$y, [int]$w, [int]$h, [string]$savePath) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    if ($savePath) { $bmp.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    return @{ Bytes = $bytes; Stride = $data.Stride; Width = $w; Height = $h }
}

# 让窗口自己渲染一遍再取样（视频在硬件叠加层上时，屏幕抓取会是黑的）
function Get-WindowPixels([IntPtr]$hwnd, [int]$w, [int]$h, [string]$savePath) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][IV]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if ($savePath) { $bmp.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    return @{ Bytes = $bytes; Stride = $data.Stride; Width = $w; Height = $h }
}
function Measure-Picture($img, [int]$step = 6) {
    $bytes = $img.Bytes; $stride = $img.Stride
    $bright = 0; $total = 0; $sum = 0.0
    for ($yy = 0; $yy -lt $img.Height; $yy += $step) {
        $row = $yy * $stride
        for ($xx = 0; $xx -lt $img.Width; $xx += $step) {
            $i = $row + $xx * 4
            $lum = ($bytes[$i + 2] * 30 + $bytes[$i + 1] * 59 + $bytes[$i] * 11) / 100
            $sum += $lum; $total++
            if ($lum -gt 70) { $bright++ }
        }
    }
    if ($total -eq 0) { return @{ BrightRatio = 0.0; Mean = 0.0 } }
    return @{ BrightRatio = $bright / $total; Mean = $sum / $total }
}

function Compare-Picture($a, $b, [int]$step = 6) {
    $changed = 0; $total = 0
    for ($yy = 0; $yy -lt $a.Height; $yy += $step) {
        $ra = $yy * $a.Stride; $rb = $yy * $b.Stride
        for ($xx = 0; $xx -lt $a.Width; $xx += $step) {
            $ia = $ra + $xx * 4; $ib = $rb + $xx * 4
            $d = [Math]::Abs([int]$a.Bytes[$ia] - [int]$b.Bytes[$ib]) +
                 [Math]::Abs([int]$a.Bytes[$ia + 1] - [int]$b.Bytes[$ib + 1]) +
                 [Math]::Abs([int]$a.Bytes[$ia + 2] - [int]$b.Bytes[$ib + 2])
            $total++
            if ($d -gt 24) { $changed++ }
        }
    }
    if ($total -eq 0) { return @{ DiffRatio = 0.0 } }
    return @{ DiffRatio = $changed / $total }
}

New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
$work = Join-Path $env:TEMP 'intro-verify'
New-Item -ItemType Directory -Force -Path $work | Out-Null

function New-Settings([string]$dir, [bool]$firstRun) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    @{
        DeviceName = 'PC'; AntiCapture = $false; AutoStartAirPlay = $false; AutoStartMiracast = $false
        HasSeenTutorial = (-not $firstRun); MiniOnMinimize = $false; ShowStats = $false; AutoFullscreen = $false
        NotifyOnConnect = $false; GlobalHotkeys = $false; ShowIntro = $true
    } | ConvertTo-Json | Set-Content (Join-Path $dir 'settings.json') -Encoding UTF8
}

Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$screenW = $screen.Width; $screenH = $screen.Height
Write-Host "屏幕分辨率：${screenW}x${screenH}"

Write-Host ""
Write-Host "==== A. 首次启动：满屏入场动画（主窗口遮罩）===="
$dirA = Join-Path $env:TEMP 'intro-verify-a'
Remove-Item $dirA -Recurse -Force -ErrorAction SilentlyContinue
New-Settings $dirA $true

$procA = Start-Process -FilePath $Exe -ArgumentList '--settings-root', $dirA -PassThru

# 启动瞬间：不该有任何"小窗"（原来那个 360x180 的转圈窗）
Start-Sleep -Milliseconds 700
$windows = [IV]::List([uint32]$procA.Id)
$small = @($windows | Where-Object { [int](($_ -split '\|')[1].Split('x')[0]) -lt 800 })
Check "启动时没有小窗（只有整屏那一块）" ($small.Count -eq 0) `
    ("当时可见窗口：" + $(if ($windows.Count) { $windows -join ' / ' } else { '（还没有窗口）' }))

Start-Sleep -Milliseconds 2600      # 约第 3.3 秒：画面在动、标题已淡入

$wA = 0; $hA = 0; $xA = 0; $yA = 0
$mainA = [IV]::Biggest([uint32]$procA.Id, [ref]$xA, [ref]$yA, [ref]$wA, [ref]$hA)
$fullscreen = ($wA -ge ($screenW * 0.9)) -and ($hA -ge ($screenH * 0.85))
Check "首次启动就是大屏/全屏" $fullscreen ("主窗口 ${wA}x${hA}（屏幕 ${screenW}x${screenH}）")

$windowsA = [IV]::List([uint32]$procA.Id)
Check "动画期间也只有这一个窗口（遮罩在主窗口里，不是另开的）" ($windowsA.Count -eq 1) `
    ("可见窗口数=" + $windowsA.Count + "：" + ($windowsA -join ' / '))

# 连采三帧：MediaPlayer 出第一帧的耗时不稳定，所以"有没有画面"取最亮的一帧、"在不在动"取相邻差异最大值
$samples = @()
$paths = @((Join-Path $work 'a1.png'), (Join-Path $work 'a2.png'), (Join-Path $work 'a3.png'))
for ($k = 0; $k -lt 3; $k++) {
    if ($k -gt 0) { Start-Sleep -Milliseconds 1200 }
    $sw = 0; $sh = 0; $sx = 0; $sy = 0
    $h3 = [IV]::Biggest([uint32]$procA.Id, [ref]$sx, [ref]$sy, [ref]$sw, [ref]$sh)
    if ($h3 -eq [IntPtr]::Zero) { break }
    $img = Get-WindowPixels $h3 $sw $sh $paths[$k]        # 用窗口自渲染取样，硬件视频层也能拿到
    $samples += @{ Img = $img; Stats = (Measure-Picture $img); Path = $paths[$k] }
}

if ($samples.Count -eq 0) {
    Check "满屏画面不是黑屏（有亮部：行星/舰船）" $false '（没抓到画面）'
    Check "画面确实在动（相邻两帧像素有变化）" $false '（没抓到画面）'
} else {
    $bestIdx = 0
    for ($k = 1; $k -lt $samples.Count; $k++) {
        if ($samples[$k].Stats.BrightRatio -gt $samples[$bestIdx].Stats.BrightRatio) { $bestIdx = $k }
    }
    $best = $samples[$bestIdx]
    Copy-Item $best.Path (Join-Path $ShotDir '启动动画-01-满屏舰队列阵.png') -Force

    Check "满屏画面不是黑屏（有亮部：行星/舰船）" ($best.Stats.BrightRatio -gt 0.02) `
        ("三帧里最亮的一帧：亮部占比={0:P1} 平均亮度={1:F0}" -f $best.Stats.BrightRatio, $best.Stats.Mean)

    $maxDiff = 0.0
    for ($k = 1; $k -lt $samples.Count; $k++) {
        $d = (Compare-Picture $samples[$k - 1].Img $samples[$k].Img).DiffRatio
        if ($d -gt $maxDiff) { $maxDiff = $d }
    }
    Check "画面确实在动（相邻两帧像素有变化）" ($maxDiff -gt 0.05) ("最大变化像素占比={0:P1}" -f $maxDiff)
}

# 出场：等它自己收场（7.5 秒片段 + 收场动画），看是不是露出界面
Start-Sleep -Seconds 8
$wEnd = 0; $hEnd = 0; $xEnd = 0; $yEnd = 0
$mainEnd = [IV]::Biggest([uint32]$procA.Id, [ref]$xEnd, [ref]$yEnd, [ref]$wEnd, [ref]$hEnd)
$imgEnd = Get-Pixels $xEnd $yEnd $wEnd $hEnd (Join-Path $ShotDir '启动动画-02-收场后界面.png')
$mEnd = Measure-Picture $imgEnd

Check "收场后仍然是首次启动给的大屏（没有被退回窗口模式）" `
    (($wEnd -ge ($screenW * 0.9)) -and ($hEnd -ge ($screenH * 0.85))) `
    ("${wA}x${hA} → ${wEnd}x${hEnd}（屏幕 ${screenW}x${screenH}）")
$brightDuring = 0.0
if ($samples.Count -gt 0) { $brightDuring = $best.Stats.BrightRatio }
Check "出场后遮罩已收起、界面露出来了" ($mEnd.BrightRatio -lt 0.15) `
    ("界面亮部占比={0:P1}（满屏动画时约 {1:P1}）" -f $mEnd.BrightRatio, $brightDuring)

Stop-Process -Id $procA.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

Write-Host ""
Write-Host "==== B. --no-intro：没有动画，直接就是界面 ===="
$dirB = Join-Path $env:TEMP 'intro-verify-b'
Remove-Item $dirB -Recurse -Force -ErrorAction SilentlyContinue
New-Settings $dirB $false
$procB = Start-Process -FilePath $Exe -ArgumentList '--no-intro', '--settings-root', $dirB -PassThru
Start-Sleep -Milliseconds 3000
$wB = 0; $hB = 0; $xB = 0; $yB = 0
$mainB = [IV]::Biggest([uint32]$procB.Id, [ref]$xB, [ref]$yB, [ref]$wB, [ref]$hB)
$imgB = Get-Pixels $xB $yB $wB $hB (Join-Path $work 'b1.png')
$mB = Measure-Picture $imgB
Check "--no-intro 时没有满屏动画（直接是深色界面）" ($mB.BrightRatio -lt 0.15) `
    ("亮部占比={0:P1}（有动画时 > 20%）" -f $mB.BrightRatio)
Stop-Process -Id $procB.Id -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("==== 入场动画验证：通过 {0} 项，失败 {1} 项 ====" -f $script:pass, $script:fail)
exit $script:fail
