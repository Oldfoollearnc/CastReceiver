# ⚠ ⑧「安卓 adb 无线鼠标控制」目前已按用户要求停用（界面卡片折叠、代码注释保留），
#   所以本脚本现在**跑不过**（B/C/D 段依赖界面上的鼠标控制卡片）。等 ⑧ 恢复后再跑，它是 ⑧ 的验证证据。
# 安卓 adb 鼠标控制（⑧）验证脚本（开发期用，不参与产品构建）
#
# 需要一个安卓设备/模拟器（Android 11+）。脚本做两件事：
#
#   A. 坐标空间实测：用「显示指针位置」的标记反查落点，确认 adb shell input tap
#      用的是**当前方向**的显示坐标系（与 ScreenCoordinateMapper 的默认约定一致）。
#      这一步不依赖本程序，是"标尺的校准"。
#   B. 端到端：本程序 --demo-cast 放一段本地画面（等价于安卓这一路的画面），
#      把 adb 连到模拟器，然后用**真实鼠标**点画面上的两个已知位置，
#      再用 A 的方法读回落点，验证"鼠标点哪儿，手机就点在哪儿"。
#      还包含两条否定用例：点在黑边（留黑区）不发送；开关关掉后不发送。
#
# 为什么不用 OCR/看截图：落点必须由设备自己的帧缓冲量出来，否则"看着像对的"不算证据。
param(
    [string]$Exe,
    [string]$SettingsRoot,
    [string]$ShotDir,
    [string]$Adb,
    [string]$Robot,          # adb-pointer-probe.py
    [string]$Python,
    [string]$Device = 'emulator-5554',
    [int]$TcpPort = 5555,
    [int]$WaitReady = 14
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AV {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_SHOWWINDOW = 0x0040;

    public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr MainWindow(uint want) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != want || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == "投屏Window") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static int[] Bounds(IntPtr h) {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(140);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    public static void WheelAt(int x, int y, int notches) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(160);
        mouse_event(WHEEL, 0, 0, notches * 120, UIntPtr.Zero);
    }
}
'@

$script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $tag = if ($Ok) { "PASS" } else { "FAIL" }
    if (-not $Ok) { $script:fail++ }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

function AdbRun([string[]]$Arguments) {
    $out = & $Adb @Arguments 2>&1
    return ($out -join "`n").Trim()
}

function Shot([string]$Path) {
    # 走"设备上存文件 + pull"这条路：把二进制抓进 PowerShell 变量会被按文本解码，PNG 会被改坏
    $remote = '/sdcard/dsh-shot.png'
    AdbRun @('-s', $script:Serial, 'shell', 'screencap', '-p', $remote) | Out-Null
    AdbRun @('-s', $script:Serial, 'pull', $remote, $Path) | Out-Null
    if (-not (Test-Path $Path)) { throw "截图失败：$Path" }
}

# 把手机界面恢复到桌面（每次测量前做）：手机上点一下可能打开某个界面，
# 那会让"前后两张截图"的差异里混进一大堆界面变化，落点就淹没在里面了。
function Settle {
    AdbRun @('-s', $script:Serial, 'shell', 'am', 'start', '-a', 'android.intent.action.MAIN',
             '-c', 'android.intent.category.HOME') | Out-Null
    Start-Sleep -Milliseconds 1400
}

# 停在固定的角落，把上一笔的触摸标记"挪走"
function Park([string]$Tag) {
    Settle
    AdbRun @('-s', $script:Serial, 'shell', 'input', 'tap', '3', '200') | Out-Null
    Start-Sleep -Milliseconds 250
    Shot (Join-Path $env:TEMP "adb-park-$Tag.png")
    return (Join-Path $env:TEMP "adb-park-$Tag.png")
}

# 读回落点（设备自己帧缓冲上的标记）。
# 只认"小方块"那种触摸标记（MARK），并排除停靠点(3,200) 那一团。
function ReadMark([string]$Before, [string]$After) {
    $out = & $Python $Robot $Before $After 2>&1
    $all = @()
    foreach ($line in $out) {
        if ($line -like 'MARK *') {
            $parts = $line -split '\s+'
            $x = [double]$parts[1]; $y = [double]$parts[2]
            $all += ("({0},{1})" -f $x, $y)
            $nearPark = ([math]::Abs($x - 2.5) -le 12) -and ([math]::Abs($y - 199.5) -le 12)
            if (-not $nearPark) {
                return @{ X = $x; Y = $y; Raw = ($out -join ' || ') }
            }
        }
    }
    return @{ X = -1.0; Y = -1.0; Raw = (($out -join ' || ') + " 全部标记=" + ($all -join ',')) }
}

function ExpectMark([string]$Name, [int]$PhoneX, [int]$PhoneY, [int]$Tolerance, [string]$Tag) {
    AssertLandscape | Out-Null
    $before = Park $Tag
    AdbRun @('-s', $script:Serial, 'shell', 'input', 'tap', "$PhoneX", "$PhoneY") | Out-Null
    Start-Sleep -Milliseconds 350
    $after = Join-Path $env:TEMP "adb-after-$Tag.png"
    Shot $after
    $mark = ReadMark $before $after
    $dx = [math]::Abs($mark.X - $PhoneX)
    $dy = [math]::Abs($mark.Y - $PhoneY)
    Check $Name (($mark.X -ge 0) -and ($dx -le $Tolerance) -and ($dy -le $Tolerance)) `
        ("期望 ({0},{1})，实测 ({2},{3})，偏差 ({4:N1},{5:N1})" -f $PhoneX, $PhoneY, $mark.X, $mark.Y, $dx, $dy)
    return $mark
}

# 断点前的护栏：屏幕尺寸可能被系统/别的工具改回去（实测遇到过），
# 每次都确认一遍"当前方向"仍是 2340x1080，不是就重新设置并等程序把新尺寸读进去。
function AssertLandscape {
    $size = AdbRun @('-s', $script:Serial, 'shell', 'wm', 'size')
    if ($size -match 'Override size:\s*2340x1080') { return $true }
    Write-Host "    （注意：屏幕尺寸被改回去了，重新设为 2340x1080 后继续）"
    AdbRun @('-s', $script:Serial, 'shell', 'wm', 'size', '2340x1080') | Out-Null
    Start-Sleep -Seconds 6      # 等程序那边（每 4 秒重读一次屏幕信息）跟上
    return $true
}

# 只比较"已发送 N 次"这个前缀：开关状态会让文案多一个后缀
function CountPrefix([string]$Text) {
    if ($Text -match '已发送\s*\d+\s*次') { return $Matches[0] } else { return $Text }
}

# 界面读取（注入计数）
function Get-UiaText([int]$ProcessId, [string]$AutomationId) {
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)))
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el.Current.Name }
    } catch { }
    return ''
}

New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Write-Host "==== A. 坐标空间实测（adb shell input tap 用哪个方向的坐标）===="
$script:Serial = $Device
Check "设备在线" ((AdbRun @('-s', $Device, 'get-state')) -eq 'device') (AdbRun @('-s', $Device, 'get-state'))

$sdk = AdbRun @('-s', $Device, 'shell', 'getprop', 'ro.build.version.sdk')
Check "系统是 Android 11+（无线配对要求 API 30+）" ([int]$sdk -ge 30) ("API " + $sdk)

# 把屏幕改成"横屏形状"（2340x1080）。这台模拟器拒绝真正旋转（device_state_rotation_lock），
# 但 wm size 覆盖同样能造出"当前方向 ≠ 自然方向"的局面，正好用来分辨两种坐标约定：
#   自然方向 1080x2340 / 当前方向 2340x1080 —— 点 (2000,400) 只在当前方向里合法。
AdbRun @('-s', $Device, 'shell', 'wm', 'size', '2340x1080') | Out-Null
AdbRun @('-s', $Device, 'shell', 'settings', 'put', 'system', 'pointer_location', '1') | Out-Null
Start-Sleep -Seconds 3
$sizeText = AdbRun @('-s', $Device, 'shell', 'wm', 'size')
Check "屏幕已被改成 2340x1080（当前方向）" ($sizeText -match 'Override size:\s*2340x1080') $sizeText

$m1 = ExpectMark "落点就是坐标本身（当前方向内的点 400,400）" 400 400 6 'both'
$m2 = ExpectMark "x=2000 超出自然宽度 1080，仍然落在 2000（说明用的是当前方向坐标）" 2000 400 6 'current'
$before = Park 'outside'
AdbRun @('-s', $Device, 'shell', 'input', 'tap', '400', '2000') | Out-Null
Start-Sleep -Milliseconds 350
$after = Join-Path $env:TEMP 'adb-after-outside.png'
Shot $after
$m3 = ReadMark $before $after
Check "y=2000 超出当前高度 1080：被丢弃（自然方向里的合法点也不生效）" ($m3.X -lt 0) $m3.Raw
Write-Host ("    结论：input tap 使用【当前方向】的显示坐标系 —— 与 ScreenCoordinateMapper 的默认约定一致")

Write-Host ""
Write-Host "==== B. 端到端：本程序画面上的鼠标 → 手机上的触摸 ===="
# 切到 TCP 连接（无线调试走的就是这条），让程序像连真机一样去连模拟器
AdbRun @('-s', $Device, 'tcpip', "$TcpPort") | Out-Null
Start-Sleep -Seconds 3
$connectText = AdbRun @('connect', "127.0.0.1:$TcpPort")
Check "adb connect 127.0.0.1:$TcpPort 成功" ($connectText -match 'connected to') $connectText
$script:Serial = "127.0.0.1:$TcpPort"
Check "TCP 传输在线" ((AdbRun @('-s', $script:Serial, 'get-state')) -eq 'device') (AdbRun @('-s', $script:Serial, 'get-state'))

# 程序的验证用配置：adb 鼠标控制打开、自动连本机模拟器、关反截屏（外部截屏才拍得到）
New-Item -ItemType Directory -Force -Path $SettingsRoot | Out-Null
@{
    DeviceName = 'PC'; AntiCapture = $false; AutoStartAirPlay = $false; AutoStartMiracast = $false
    AutoStartDlna = $false; HasSeenTutorial = $true; MiniOnMinimize = $false; MiniPinned = $false
    ShowStats = $false; AutoFullscreen = $false; NotifyOnConnect = $false; GlobalHotkeys = $false
    AdbControlEnabled = $true; AdbMouseControl = $true; AdbAutoConnect = $true; AdbHost = '127.0.0.1'; AdbConnectPort = "$TcpPort"
} | ConvertTo-Json | Set-Content (Join-Path $SettingsRoot 'settings.json') -Encoding UTF8

$proc = Start-Process -FilePath $Exe -ArgumentList '--demo-cast', '--settings-root', $SettingsRoot -PassThru
Start-Sleep -Seconds $WaitReady
Check "程序已启动（演示模式，无手机也能画出画面）" (-not $proc.HasExited) ("pid=" + $proc.Id)
if ($proc.HasExited) { exit 1 }

$main = [AV]::MainWindow([uint32]$proc.Id)
Check "找到主窗口" ($main -ne [IntPtr]::Zero) ""
if ($main -eq [IntPtr]::Zero) { Stop-Process -Id $proc.Id -Force; exit 1 }
[void][AV]::SetWindowPos($main, [AV]::HWND_TOPMOST, 60, 30, 1500, 900, [AV]::SWP_SHOWWINDOW)
Start-Sleep -Milliseconds 900

# 等 adb 连上（界面上的状态文字会变）
$adbState = ''
for ($i = 0; $i -lt 20; $i++) {
    $adbState = Get-UiaText $proc.Id 'AdbStatus'
    if ($adbState -like '*127.0.0.1*' -or $adbState -like '*sdk_gphone*' -or $adbState -like '*已连接*') { break }
    Start-Sleep -Milliseconds 700
}
Check "程序自己连上了模拟器（界面状态说明）" ($adbState -like '*sdk_gphone*' -or $adbState -like '*127.0.0.1*') ("AdbStatus='" + $adbState + "'")

$b = [AV]::Bounds($main)
$dpi = [AV]::GetDpiForWindow($main) / 96.0
$sideW = [int](300 * $dpi)
$stageLeft = $b[0] + [int](17 * $dpi)
$stageTop = $b[1] + [int](50 * $dpi)
$stageW = $b[2] - [int](17 * $dpi) * 2 - $sideW
$stageH = $b[3] - [int](50 * $dpi) - [int](40 * $dpi)
Write-Host ("    画面区推算：{0},{1} {2}x{3}（窗口 {4},{5} {6}x{7}）" -f $stageLeft, $stageTop, $stageW, $stageH, $b[0], $b[1], $b[2], $b[3])

# 演示画面是 1280x720，宿主里按"适应"摆放 —— 脚本自己再算一遍（独立实现，用来交叉验证程序的换算）
$videoW = 1280.0; $videoH = 720.0
$scale = [math]::Min($stageW / $videoW, $stageH / $videoH)
$boxW = $videoW * $scale; $boxH = $videoH * $scale
$boxX = $stageLeft + ($stageW - $boxW) / 2
$boxY = $stageTop + ($stageH - $boxH) / 2
Write-Host ("    画面矩形：{0:N0},{1:N0} {2:N0}x{3:N0}" -f $boxX, $boxY, $boxW, $boxH)

$phoneW = 2340; $phoneH = 1080
$injected0 = Get-UiaText $proc.Id 'AdbInjected'

function VideoPoint([double]$u, [double]$v) {
    return @{ X = [int]($boxX + $u * $boxW); Y = [int]($boxY + $v * $boxH) }
}

# --- 用例 1：画面 25%/25% 处点一下 → 手机上应当落在 (585,270)
$p1 = VideoPoint 0.25 0.25
AssertLandscape | Out-Null
$before = Park 'app1'
[AV]::Click($p1.X, $p1.Y)
Start-Sleep -Milliseconds 900
$after = Join-Path $ShotDir 'adb-鼠标点击-落点1.png'
Shot $after
$m4 = ReadMark $before $after
$exp1x = [int](0.25 * $phoneW); $exp1y = [int](0.25 * $phoneH)
$status1 = Get-UiaText $proc.Id 'StatusText'
Check "鼠标点画面 25%/25% → 手机落在 (${exp1x},${exp1y})" `
    (($m4.X -ge 0) -and ([math]::Abs($m4.X - $exp1x) -le 8) -and ([math]::Abs($m4.Y - $exp1y) -le 8)) `
    ("实测 ({0},{1})，偏差 ({2:N1},{3:N1})；程序说：{4}" -f $m4.X, $m4.Y, [math]::Abs($m4.X - $exp1x), [math]::Abs($m4.Y - $exp1y), $status1)

# --- 用例 2：画面 75%/75% 处点一下 → 手机上应当落在 (1755,810)
$p2 = VideoPoint 0.50 0.60
AssertLandscape | Out-Null
$before = Park 'app2'
[AV]::Click($p2.X, $p2.Y)
Start-Sleep -Milliseconds 900
$after = Join-Path $ShotDir 'adb-鼠标点击-落点2.png'
Shot $after
$m5 = ReadMark $before $after
$exp2x = [int](0.50 * $phoneW); $exp2y = [int](0.60 * $phoneH)
$status2 = Get-UiaText $proc.Id 'StatusText'
Check "鼠标点画面 50%/60% → 手机落在 (${exp2x},${exp2y})" `
    (($m5.X -ge 0) -and ([math]::Abs($m5.X - $exp2x) -le 8) -and ([math]::Abs($m5.Y - $exp2y) -le 8)) `
    ("实测 ({0},{1})，偏差 ({2:N1},{3:N1})；程序说：{4}" -f $m5.X, $m5.Y, [math]::Abs($m5.X - $exp2x), [math]::Abs($m5.Y - $exp2y), $status2)

$injected1 = Get-UiaText $proc.Id 'AdbInjected'
Check "界面上的注入计数增加了" ((CountPrefix $injected1) -ne (CountPrefix $injected0)) ("之前='" + $injected0 + "' 之后='" + $injected1 + "'")

# --- 用例 3（否定）：点在留黑边上 → 不发（否则会点到手机屏幕上莫名其妙的地方）
$letterboxY = [int]($stageTop + 6)
AssertLandscape | Out-Null
$before = Park 'app3'
[AV]::Click([int]($boxX + $boxW / 2), $letterboxY)
Start-Sleep -Milliseconds 900
$after = Join-Path $ShotDir 'adb-鼠标点击-黑边不发.png'
Shot $after
$m6 = ReadMark $before $after
$injected2 = Get-UiaText $proc.Id 'AdbInjected'
Check "点在画面外的黑边上：手机没有收到触摸" ($m6.X -lt 0) ("实测标记=" + $m6.Raw)
Check "点在黑边上不计入注入次数" ((CountPrefix $injected2) -eq (CountPrefix $injected1)) ("计数='" + $injected2 + "'")

# --- 用例 4：滚轮 → 滑动
$center = VideoPoint 0.5 0.5
AssertLandscape | Out-Null
$before = Park 'app4'
[AV]::WheelAt($center.X, $center.Y, 2)
Start-Sleep -Milliseconds 1400
$after = Join-Path $ShotDir 'adb-鼠标滚轮.png'
Shot $after
$injected3 = Get-UiaText $proc.Id 'AdbInjected'
Check "滚轮向上滚：手机上发了一次滑动（注入计数增加）" ((CountPrefix $injected3) -ne (CountPrefix $injected2)) ("之前='" + $injected2 + "' 之后='" + $injected3 + "'")
$diff = (& $Python $Robot $before $after 2>&1) -join ' || '
Check "滚轮确实在手机屏幕上留下了痕迹" ($diff -notlike 'NO-BLOB*') $diff

# --- 用例 5（否定）：关掉开关 → 不再发送
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AdbMouseToggle')))
    $toggle = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($toggle) {
        $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
} catch { }
Start-Sleep -Milliseconds 900
AssertLandscape | Out-Null
$before = Park 'app5'
[AV]::Click($p1.X, $p1.Y)
Start-Sleep -Milliseconds 900
$after = Join-Path $ShotDir 'adb-关开关后不发.png'
Shot $after
$m7 = ReadMark $before $after
$injected4 = Get-UiaText $proc.Id 'AdbInjected'
Check "关掉开关后：鼠标点击不再发到手机" (($m7.X -lt 0) -and ((CountPrefix $injected4) -eq (CountPrefix $injected3))) `
    ("标记=" + $m7.Raw + "；计数='" + $injected4 + "'（开关前 '" + $injected3 + "'）")

# ---------------------------------------------------------------- 收尾（先停 B 段的程序）
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==== C. 只打开开关（一个地址都不填）→ 程序自己发现并连上 → 鼠标就能用 ===="
# 这一节验的就是"投屏之后不该再让人填 IP 与端口"这件事。
# 模拟器广播的是它自己的 NAT 地址（宿主机连不上），所以这里用 --adb-discovery 注入一段
# **真实格式**的发现结果（真机上这段来自 adb mdns services）；连接与注入仍然真跑。
$discoveryFile = Join-Path $env:TEMP 'adb-discovery-listing.txt'
@"
List of discovered mdns services
adb-DSH-SELFTEST-CONNECT	_adb-tls-connect._tcp	127.0.0.1:$TcpPort
"@ | Set-Content -LiteralPath $discoveryFile -Encoding ASCII

$settingsC = Join-Path $env:TEMP 'adb-verify-settings-c'
New-Item -ItemType Directory -Force -Path $settingsC | Out-Null
@{
    DeviceName = 'PC'; AntiCapture = $false; AutoStartAirPlay = $false; AutoStartMiracast = $false
    HasSeenTutorial = $true; MiniOnMinimize = $false; ShowStats = $false; AutoFullscreen = $false
    NotifyOnConnect = $false; GlobalHotkeys = $false
    AdbControlEnabled = $true; AdbMouseControl = $true; AdbAutoConnect = $true
    AdbHost = ''; AdbConnectPort = ''; AdbPairPort = ''      # 注意：一个地址都不填
} | ConvertTo-Json | Set-Content (Join-Path $settingsC 'settings.json') -Encoding UTF8

$procC = Start-Process -FilePath $Exe -ArgumentList '--demo-cast', '--settings-root', $settingsC,
    '--adb-discovery', $discoveryFile -PassThru
Start-Sleep -Seconds $WaitReady

$adbStateC = ''
for ($i = 0; $i -lt 20; $i++) {
    $adbStateC = Get-UiaText $procC.Id 'AdbStatus'
    if ($adbStateC -like '*127.0.0.1*' -or $adbStateC -like '*sdk_gphone*') { break }
    Start-Sleep -Milliseconds 800
}
$connectedC = ($adbStateC -like '*sdk_gphone*') -or ($adbStateC -like '*127.0.0.1*')
Check "不填地址也能连上（只靠自动发现）" $connectedC ("AdbStatus='" + $adbStateC + "'（配置里 AdbHost/Port 都是空的）")

if ($connectedC) {
    $mainC = [AV]::MainWindow([uint32]$procC.Id)
    [void][AV]::SetWindowPos($mainC, [AV]::HWND_TOPMOST, 60, 30, 1500, 900, [AV]::SWP_SHOWWINDOW)
    Start-Sleep -Milliseconds 900
    [void][AV]::SetForegroundWindow($mainC)
    Start-Sleep -Milliseconds 700

    $wC = [AV]::Bounds($mainC)
    $dpiC = [AV]::GetDpiForWindow($mainC) / 96.0
    $sLeft = $wC[0] + [int](17 * $dpiC)
    $sTop = $wC[1] + [int](50 * $dpiC)
    $sW = $wC[2] - [int](17 * $dpiC) * 2 - [int](300 * $dpiC)
    $sH = $wC[3] - [int](50 * $dpiC) - [int](40 * $dpiC)
    $sc = [math]::Min($sW / 1280.0, $sH / 720.0)
    $bW = 1280.0 * $sc; $bH = 720.0 * $sc
    $bX = $sLeft + ($sW - $bW) / 2; $bY = $sTop + ($sH - $bH) / 2

    $before = Park 'app6'
    [AV]::Click([int]($bX + 0.30 * $bW), [int]($bY + 0.70 * $bH))
    Start-Sleep -Milliseconds 1200
    $after = Join-Path $ShotDir 'adb-只开开关-鼠标可用.png'
    Shot $after
    $m8 = ReadMark $before $after
    $expX = [int](0.30 * $phoneW); $expY = [int](0.70 * $phoneH)
    $inj6 = Get-UiaText $procC.Id 'AdbInjected'
    Check "只打开开关的情况下：鼠标点画面 → 手机真的被点到" `
        (($m8.X -ge 0) -and ([math]::Abs($m8.X - $expX) -le 10) -and ([math]::Abs($m8.Y - $expY) -le 10)) `
        ("期望 ({0},{1})，实测 ({2},{3})；计数='{4}'" -f $expX, $expY, $m8.X, $m8.Y, $inj6)
}

Stop-Process -Id $procC.Id -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==== D. 发现了手机但连不上 → 必须直接把人领到「配对」那一步（不能一直转圈）===="
# 复现用户的处境：服务能被发现，但连接被拒（未配对时手机就是这样）
$badFile = Join-Path $env:TEMP 'adb-discovery-bad.txt'
@"
List of discovered mdns services
adb-DSH-BAD	_adb-tls-connect._tcp	127.0.0.1:59999
adb-DSH-BAD	_adb-tls-pairing._tcp	127.0.0.1:59998
"@ | Set-Content -LiteralPath $badFile -Encoding ASCII

$settingsD = Join-Path $env:TEMP 'adb-verify-settings-d'
New-Item -ItemType Directory -Force -Path $settingsD | Out-Null
@{
    DeviceName = 'PC'; AntiCapture = $false; AutoStartAirPlay = $false; AutoStartMiracast = $false
    HasSeenTutorial = $true; MiniOnMinimize = $false; ShowStats = $false; AutoFullscreen = $false
    NotifyOnConnect = $false; GlobalHotkeys = $false
    AdbControlEnabled = $true; AdbMouseControl = $true; AdbAutoConnect = $true; AdbHost = ''; AdbConnectPort = ''
} | ConvertTo-Json | Set-Content (Join-Path $settingsD 'settings.json') -Encoding UTF8

$procD = Start-Process -FilePath $Exe -ArgumentList '--demo-cast', '--settings-root', $settingsD,
    '--adb-discovery', $badFile -PassThru
Start-Sleep -Seconds 16

$statusD = Get-UiaText $procD.Id 'AdbStatus'
Check "连不上时状态里写明"还没配对过"并给出下一步" ($statusD -like '*配对码*') ("AdbStatus='" + $statusD + "'")

$dialogD = Get-UiaText $procD.Id 'AdbDialogState'
Check "自动打开了配对窗口" ($dialogD.Length -gt 0) ("配对窗口状态='" + $dialogD + "'")

# 配对窗口里 IP 与两个端口应当已被自动发现填好（读 TextBox 要用 ValuePattern）
try {
    $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procD.Id)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AdbPairPortInput')))
    $box = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $pairPort = ''
    if ($box) { $pairPort = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    Check "配对端口已自动填好（用户只需填 6 位配对码）" ($pairPort -eq '59998') ("配对端口='" + $pairPort + "'")
} catch {
    Check "配对端口已自动填好（用户只需填 6 位配对码）" $false $_.Exception.Message
}

# 诊断信息：一键复制/落盘，出问题时用户直接把它发出来
try {
    $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procD.Id)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AdbDiagnoseButton')))
    $btn = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Start-Sleep -Seconds 6
    $report = Get-UiaText $procD.Id 'AdbDialogOutput'
    $ok = ($report -like '*诊断*') -and ($report -like '*mdns services*') -and ($report -like '*本机网络*')
    Check "诊断信息能一键生成（含 adb 原话 / 发现结果 / 本机网络）" $ok ("长度=" + $report.Length)
} catch {
    Check "诊断信息能一键生成（含 adb 原话 / 发现结果 / 本机网络）" $false $_.Exception.Message
}

Stop-Process -Id $procD.Id -Force -ErrorAction SilentlyContinue
AdbRun @('-s', $script:Serial, 'shell', 'settings', 'put', 'system', 'pointer_location', '0') | Out-Null
AdbRun @('-s', $script:Serial, 'shell', 'wm', 'size', 'reset') | Out-Null
Write-Host ""
Write-Host ("==== 安卓 adb 鼠标控制验证：失败 {0} 项 ====" -f $script:fail)
exit $script:fail
