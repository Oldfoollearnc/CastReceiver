# ============================================================================
# 「电脑控制手机」（安卓 · adb **无线**）端到端自检 —— **完全不需要真机**
#
# 做法：用一个"桩 adb"（tools\FakeAdb）冒充手机。桩程序按 adb 的真实输出格式回话，
#       并把每次调用原样记进日志 —— 于是可以逐条验证"我们发出去的命令到底对不对"：
#         · 自动发现 → 自动连接（无线）是否真的会自己发生、花了多久；
#         · 点在画面上到底换算成了手机上的哪个点（坐标算错不会崩，只会点偏）；
#         · 系统键发的 keycode 对不对；
#         · 英文/中文文字经过 Windows argv 与设备 sh 两层引号后有没有走样（中文是 base64，解码后逐字比对）；
#         · 键盘直通敲的字有没有进手机；
#         · 手机截屏有没有真的取回一张 PNG 并加密入库；
#         · 只发现"配对端点"时（无线首次最容易卡住的那一步）有没有把人领到配对并把端口填好。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\ui-spike\adb-control-verify.ps1
#   （可选 -Exe <产品 exe 路径> / -FakeAdbExe <桩路径> / -KeepOpen）
#
# 说明：桩程序**不参与产品构建**，只在这个脚本里用；它不验证真机行为，
#       验的是"电脑这一侧发出的命令"（真机行为只能拿真手机验，见 README 的诚实清单）。
# ============================================================================
param(
    [string]$Exe = "",
    [string]$FakeAdbExe = "",
    [switch]$SkipBuild,
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Exe) {
    $candidates = @(
        (Join-Path $root "src\CastReceiver.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\投屏Window.exe"),
        (Join-Path $root "发布版\投屏Window\投屏Window.exe")
    )
    $Exe = ($candidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "找不到产品 exe：请用 -Exe 指定" }

if (-not $FakeAdbExe) { $FakeAdbExe = Join-Path $root "tools\FakeAdb\bin\Release\net10.0\FakeAdb.exe" }
if (-not $SkipBuild -and -not (Test-Path $FakeAdbExe)) {
    Write-Host "正在编译桩 adb（tools\FakeAdb）…" -ForegroundColor Cyan
    & dotnet build (Join-Path $root "tools\FakeAdb\FakeAdb.csproj") -c Release -v q --nologo | Out-Null
}
if (-not (Test-Path $FakeAdbExe)) { throw "找不到桩 adb：$FakeAdbExe" }

$work = Join-Path $root "_temp\adb-control-verify"
$shots = Join-Path $root "_temp\shots\adb-control"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work, $shots -Force | Out-Null

$script:fail = 0
$script:pass = 0
function Write-Ok([string]$text) { Write-Host ("  [通过] " + $text) -ForegroundColor Green; $script:pass++ }
function Write-Bad([string]$text) { Write-Host ("  [失败] " + $text) -ForegroundColor Red; $script:fail++ }
function Write-Info([string]$text) { Write-Host ("         " + $text) -ForegroundColor DarkGray }
function Write-Step([string]$text) { Write-Host ("`n=== " + $text + " ===") -ForegroundColor Cyan }

# ---------------------------------------------------------------- Win32：真实鼠标
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEELFLAG = 0x0800;
    public const int SW_RESTORE = 9;

    /// <summary>把窗口真正顶到最前。只调 SetForegroundWindow 经常被系统拒绝
    /// （前台锁），这里用 AttachThreadInput 把自己挂到当前前台线程上，成功率才够高。</summary>
    public static void ForceForeground(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return;
        try { ShowWindow(hwnd, SW_RESTORE); } catch { }
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != myThread) { attached = AttachThreadInput(myThread, fgThread, true); }
        try {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        } finally {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        System.Threading.Thread.Sleep(250);
    }

    public static bool IsForeground(IntPtr hwnd) { return GetForegroundWindow() == hwnd; }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Scroll(int x, int y, int notches) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(WHEELFLAG, 0, 0, notches * 120, UIntPtr.Zero);
    }
}
"@

# ---------------------------------------------------------------- UIA 小工具
function Get-AppWindow([int]$targetPid) {
    $r = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    $fallback = $null
    for ($i = 0; $i -lt 60; $i++) {
        try {
            # 必须按**标题**挑主窗口：同一个进程还有启动闪屏 / 提示条 / 悬浮控制条这些顶层窗口，
            # 拿"第一个属于这个 pid 的窗口"偶尔会拿到闪屏 —— 那上面一个元素都找不到（实测踩过一次）。
            foreach ($w in $r.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
                if ($w.Current.Name -like "*投屏Window*") { return $w }
                if (-not $fallback) { $fallback = $w }
            }
        } catch {
            Write-Info ("UIA 查询异常：" + $_.Exception.Message)
        }
        Start-Sleep -Milliseconds 500
    }
    return $fallback
}

function Find-ById($win, [string]$id) {
    if (-not $win) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-ChildWindow([int]$targetPid, [string]$titlePart, $ownerWin) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    # 归属窗口（Owner）在 UIA 树里挂在**主窗口下面**，不在桌面的顶层列表里 ——
    # 所以既要扫桌面，也要扫主窗口的子树（否则"对话框明明开着却找不到"）。
    $pools = @()
    if ($ownerWin) { $pools += $ownerWin }
    $pools += [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($pool in $pools) {
        try {
            foreach ($w in $pool.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
                if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and $w.Current.Name -like "*$titlePart*") { return $w }
            }
        } catch { }
    }
    return $null
}

function Get-Text($el) { if ($el) { return $el.Current.Name } else { return "" } }

function Invoke-El($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    catch { return $false }
}

function Set-SwitchOn($el, [bool]$on) {
    if (-not $el) { return $false }
    try {
        $t = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if (($t.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -ne $on) { $t.Toggle() }
        return $true
    } catch { return $false }
}

function Get-SwitchOn($el) {
    if (-not $el) { return $false }
    try { return $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On }
    catch { return $false }
}

function Set-ElementText($el, [string]$text) {
    if (-not $el) { return $false }
    try {
        $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
        Start-Sleep -Milliseconds 150
        return $true
    } catch { return $false }
}

function Save-Shot($win, [string]$name) {
    try {
        $r = $win.Current.BoundingRectangle
        if ($r.Width -lt 10) { return }
        $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        $path = Join-Path $shots ($name + ".png")
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Info ("截图：" + $path)
    } catch { Write-Info ("截图失败：" + $_.Exception.Message) }
}

# ---------------------------------------------------------------- 桩 adb 的日志
function Get-FakeLog([string]$path) {
    if (-not (Test-Path $path)) { return , @() }
    $out = New-Object System.Collections.ArrayList
    foreach ($line in (Get-Content $path -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        [void]$out.Add([string[]]($line -split ([char]0x1F)))
    }
    # 逗号很重要：不写的话 PowerShell 会把"数组的数组"摊平，自检就查不到任何一条注入
    return , $out.ToArray()
}

function Get-Injections([string]$path, [string]$kind) {
    $out = New-Object System.Collections.ArrayList
    foreach ($f in (Get-FakeLog $path)) {
        if ($f.Count -ge 3 -and $f[1] -eq "INJECT" -and $f[2] -eq $kind) { [void]$out.Add($f) }
    }
    return , $out.ToArray()
}

function Wait-Until([scriptblock]$test, [int]$timeoutSeconds = 25, [int]$intervalMs = 400) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $test) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

# ---------------------------------------------------------------- 准备一次运行的现场
function New-Scene([string]$name, [string]$discoveryContent) {
    $dir = Join-Path $work $name
    $cfgDir = Join-Path $dir "cfg"
    New-Item -ItemType Directory -Path $dir, $cfgDir, (Join-Path $dir "vault") -Force | Out-Null
    $cfgFile = Join-Path $cfgDir "settings.json"
    # 关掉防截屏（脚本要截图）与首次引导（会挡住界面），控制开关先关着 —— 由程序自己"投屏时自动打开"
    # AdbControlEnabled = $true 是必须的：「电脑控制手机」现在**默认关闭**，
    # 关着时侧栏卡片根本不出现（连 AdbMouseToggle 都找不到），这个脚本就没法验了。
    $settings = @{
        AntiCapture       = $false
        HasSeenTutorial   = $true
        ShowIntro         = $false
        ShowStats         = $false
        AdbControlEnabled = $true
        AdbMouseControl   = $false
        AdbAutoConnect    = $true
        DeviceName        = "自检机"
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($cfgFile, $settings, (New-Object System.Text.UTF8Encoding($false)))

    $discovery = Join-Path $dir "mdns.txt"
    [System.IO.File]::WriteAllText($discovery, $discoveryContent, (New-Object System.Text.UTF8Encoding($false)))

    $fakeLog = Join-Path $dir "fake-adb.log"
    if (Test-Path $fakeLog) { Remove-Item $fakeLog -Force }

    # 注意：--settings-root 要的是**目录**（参数名容易看错成文件，传成文件会让程序起不来）
    return [pscustomobject]@{ Dir = $dir; Cfg = $cfgDir; CfgFile = $cfgFile; Vault = (Join-Path $dir "vault"); Discovery = $discovery; Log = $fakeLog }
}

function Start-App($scene, [string]$suffix) {
    $env:FAKE_ADB_LOG = $scene.Log
    $env:FAKE_ADB_SCREEN = $script:phonePng
    $env:FAKE_ADB_MDNS = $scene.Discovery
    # 桩手机"装了 ADBKeyboard"：中文那条路才走得通（真机上装没装由程序现场探测）
    $env:FAKE_ADB_IME = "com.android.adbkeyboard/.AdbIME"
    $argLine = "--no-intro --demo-cast --settings-root `"$($scene.Cfg)`" --vault-root `"$($scene.Vault)`" " +
               "--adb-path `"$FakeAdbExe`" --adb-discovery `"$($scene.Discovery)`" --instance-suffix `"$suffix`""
    $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList $argLine -PassThru
    return $p
}

# ---------------------------------------------------------------- 造一张"手机截屏"
Add-Type -AssemblyName System.Drawing
$script:phonePng = Join-Path $work "phone-screen.png"
$bmp = New-Object System.Drawing.Bitmap(1080, 2400)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(24, 28, 36))
$font = New-Object System.Drawing.Font("Segoe UI", 80)
$g.DrawString("FAKE PHONE", $font, [System.Drawing.Brushes]::White, 120, 1100)
$g.Dispose()
$bmp.Save($script:phonePng, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "桩手机截屏素材：$script:phonePng" -ForegroundColor DarkGray

# ============================================================================
# 场景一：手机投上来 → 程序自己把「电脑控制手机」连好
# ============================================================================
Write-Step "场景一：投屏上来后自动连上手机（无线）"
$scene = New-Scene "scene1" "List of discovered mdns services`nadb-FAKE-CONNECT`t_adb-tls-connect._tcp`t127.0.0.1:37251`nadb-FAKE-PAIR`t_adb-tls-pairing._tcp`t127.0.0.1:41235`n"
$started = Get-Date
$proc = Start-App $scene "-adbctl1"
$win = Get-AppWindow $proc.Id
if (-not $win) {
    Write-Bad ("主窗口没起来（进程 " + $proc.Id + "，已退出=" + $proc.HasExited + "）")
    if (-not $KeepOpen) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    exit 1
}
Write-Info ("窗口：" + $win.Current.Name + "  " + $win.Current.BoundingRectangle)

# 1) 控制开关自动打开
$toggle = Find-ById $win "AdbMouseToggle"
$autoOn = Wait-Until { $toggle = Find-ById $win "AdbMouseToggle"; Get-SwitchOn $toggle } 30
if ($autoOn) { Write-Ok "投屏后自动打开了「电脑控制手机」开关（用户没点任何东西）" }
else { Write-Bad "控制开关没有自动打开" }

# 2) 自动连上手机（无线）
$statusEl = Find-ById $win "AdbStatus"
$connected = Wait-Until {
    $statusEl = Find-ById $win "AdbStatus"
    $t = Get-Text $statusEl
    return ($t -like "*无线*") -and ($t -notlike "*未连接*") -and ($t -notlike "*没发现*")
} 45
$elapsed = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)
if ($connected) {
    Write-Ok "自动发现并连上了手机（无线），从启动算起 $elapsed 秒"
    Write-Info ("状态：" + (Get-Text $statusEl))
} else {
    Write-Bad ("没有自动连上手机；状态：" + (Get-Text $statusEl))
}

# 3) 控制条出现（Border 本身没有 UIA 对等体，所以用条上的按钮/输入框来判断它在不在）
$sendBtn = Find-ById $win "PhoneSendButton"
if ($sendBtn -and $sendBtn.Current.BoundingRectangle.Width -gt 10) { Write-Ok "「电脑控制手机」控制条自动出现（发送按钮可见）" }
else { Write-Bad "控制条没有出现" }
Save-Shot $win "01-已连上-控制条自动出现"

# 4) 画面上的鼠标动作 → 手机上的点按 / 滑动
$host_ = Find-ById $win "MiracastHost"
if (-not $host_) { Write-Bad "找不到画面宿主，后面的鼠标验证跳过"; }
else {
    $r = $host_.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [Win32Mouse]::ForceForeground($hwnd)
    Write-Info ("窗口是否在前台：" + [Win32Mouse]::IsForeground($hwnd))
    Start-Sleep -Milliseconds 500
    # 点一下可能只是把窗口激活（第一次点击只用于聚焦），所以最多试 3 次
    $tapped = $false
    foreach ($attempt in 1..3) {
        [Win32Mouse]::Click($cx, $cy)
        $tapped = Wait-Until { (Get-Injections $scene.Log "tap").Count -ge 1 } 6
        if ($tapped) { break }
        Write-Info ("第 $attempt 次点击没有产生点按，重试…")
        [Win32Mouse]::ForceForeground($hwnd)
    }
    if ($tapped) {
        $f = (Get-Injections $scene.Log "tap")[0]
        $x = [int]$f[3]; $y = [int]$f[4]
        # 桩手机的屏幕是 1080×2400，点在画面正中 → 手机正中（540,1200）
        if ([Math]::Abs($x - 540) -le 40 -and [Math]::Abs($y - 1200) -le 60) {
            Write-Ok "画面正中点击 → 手机点按 ($x,$y)（期望 ≈540,1200）"
        } else {
            Write-Bad "点按坐标偏了：($x,$y)，期望 ≈(540,1200)"
        }
    } else { Write-Bad "画面点击没有产生 input tap" }

    [Win32Mouse]::Scroll($cx, $cy, 3)
    $swiped = Wait-Until { (Get-Injections $scene.Log "swipe").Count -ge 1 } 12
    if ($swiped) {
        $f = (Get-Injections $scene.Log "swipe")[0]
        Write-Ok ("画面滚轮 → 手机滑动 (" + $f[3] + "," + $f[4] + ") → (" + $f[5] + "," + $f[6] + ")")
    } else { Write-Bad "画面滚轮没有产生 input swipe" }
}

# 5) 系统键
$beforeKeys = (Get-Injections $scene.Log "keyevent").Count
Invoke-El (Find-ById $win "PhoneKeyBack") | Out-Null
$backOk = Wait-Until {
    $codes = (Get-Injections $scene.Log "keyevent") | ForEach-Object { $_[3] }
    return ($codes -contains "4")
} 10
if ($backOk) { Write-Ok "「返回」键 → 手机收到 keyevent 4（KEYCODE_BACK）" } else { Write-Bad "「返回」键没有发出 keyevent 4" }

Invoke-El (Find-ById $win "PhoneKeyVolumeUp") | Out-Null
$volOk = Wait-Until {
    $codes = (Get-Injections $scene.Log "keyevent") | ForEach-Object { $_[3] }
    return ($codes -contains "24")
} 10
if ($volOk) { Write-Ok "「音量 +」键 → 手机收到 keyevent 24（KEYCODE_VOLUME_UP）" } else { Write-Bad "「音量 +」键没有发出 keyevent 24" }

# 6) 英文文字：两层引号之后必须原样到达手机
$textBox = Find-ById $win "PhoneTextInput"
$english = "Hello 123"
Set-ElementText $textBox $english | Out-Null
Invoke-El (Find-ById $win "PhoneSendButton") | Out-Null
$textOk = Wait-Until { (Get-Injections $scene.Log "text").Count -ge 1 } 15
if ($textOk) {
    $got = (Get-Injections $scene.Log "text")[0][3]
    if ($got -eq $english) { Write-Ok ("英文文字原样到达手机：`"$got`"") }
    else { Write-Bad ("英文文字走样：手机收到 `"$got`"，期望 `"$english`"") }
} else { Write-Bad "英文文字没有发出去" }

# 7) 中文文字：桩手机装了 ADBKeyboard → base64 广播，解码后逐字比对
$chinese = "你好，世界"
Set-ElementText $textBox $chinese | Out-Null
Invoke-El (Find-ById $win "PhoneSendButton") | Out-Null
$cnOk = Wait-Until { (Get-Injections $scene.Log "b64text").Count -ge 1 } 15
if ($cnOk) {
    $got = (Get-Injections $scene.Log "b64text")[0][3]
    if ($got -eq $chinese) { Write-Ok ("中文文字（base64 广播）解码后正确：`"$got`"") }
    else { Write-Bad ("中文文字走样：`"$got`"，期望 `"$chinese`"") }
} else { Write-Bad "中文文字没有发出（ADBKeyboard 广播没出现）" }
Save-Shot $win "02-按键与文字已发送"

# 8) 键盘直通：敲键盘直接进手机
$kb = Find-ById $win "PhoneKeyboardToggle"
Set-SwitchOn $kb $true | Out-Null
Start-Sleep -Milliseconds 600
if (Get-SwitchOn (Find-ById $win "PhoneKeyboardToggle")) { Write-Ok "「键盘直通」已打开" } else { Write-Bad "「键盘直通」打不开" }

# 先把焦点从输入框移开（点一下画面），否则程序会认为"你在用输入框"而不转发按键
$host_ = Find-ById $win "MiracastHost"
if ($host_) {
    $r = $host_.Current.BoundingRectangle
    [Win32Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.30)) | Out-Null
    Start-Sleep -Milliseconds 400
}
$typedBefore = (Get-Injections $scene.Log "text").Count
$keysBefore = (Get-Injections $scene.Log "keyevent").Count
[System.Windows.Forms.SendKeys]::SendWait("ab")
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
$typed = Wait-Until {
    $texts = (Get-Injections $scene.Log "text") | ForEach-Object { $_[3] }
    $codes = (Get-Injections $scene.Log "keyevent") | Select-Object -Last 1
    return (($texts -contains "ab") -and ($codes[3] -eq "66"))
} 12
if ($typed) { Write-Ok "键盘直通：敲的 ab 进了手机（input text），回车 → keyevent 66（KEYCODE_ENTER）" }
else { Write-Bad "键盘直通没有生效（没有看到 input text ab / keyevent 66）" }
Set-SwitchOn (Find-ById $win "PhoneKeyboardToggle") $false | Out-Null

# 9) 手机截屏 → 加密入库
$itemsBefore = @(Get-ChildItem $scene.Vault -Recurse -File -ErrorAction SilentlyContinue).Count
Invoke-El (Find-ById $win "PhoneScreenshotButton") | Out-Null
# 第一次入库要先建账号：程序会弹出「创建加密账号」，等它出现后按真实流程把它建好
$setup = $null
$null = Wait-Until { $script:setupDlg = Get-ChildWindow $proc.Id "创建加密账号" $win; return ($null -ne $script:setupDlg) } 15
$setup = $script:setupDlg
if ($setup) {
    Write-Info "程序要求先创建账号，按真实流程创建（账号名 / 密码 / 再次输入密码 → 创建账号）…"
    $edits = $setup.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    Write-Info ("对话框里的输入框：" + $edits.Count + " 个")
    if ($edits.Count -ge 3) {
        Set-ElementText $edits[0] "自检账号" | Out-Null
        # 密码框：WPF PasswordBox 不支持 ValuePattern，只能真实按键（仓库里既有的做法）
        $edits[1].SetFocus(); Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait("selftest123")
        $edits[2].SetFocus(); Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait("selftest123")
        Start-Sleep -Milliseconds 300
    }
    $create = $setup.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "创建账号")))
    Invoke-El $create | Out-Null
    Start-Sleep -Seconds 4
} else {
    Write-Info "没有出现建账号对话框（可能已经有账号了）"
}
$screenSent = Wait-Until {
    $found = $false
    foreach ($f in (Get-FakeLog $scene.Log)) {
        if ($f.Count -ge 3 -and $f[1] -eq "CALL" -and ($f -join " ") -like "*exec-out*screencap*") { $found = $true }
    }
    return $found
} 20
if ($screenSent) { Write-Ok "手机截屏：程序执行了 adb exec-out screencap -p" } else { Write-Bad "没有执行手机截屏命令" }

$itemsAfter = @(Get-ChildItem $scene.Vault -Recurse -File -Force -ErrorAction SilentlyContinue).Count
# 只认"加密条目"文件（.crv）：光有 accounts.json 这种账号文件不算截屏入库。
# 注意 -Force：加密库目录是**隐藏**的（隐私设计），不带 -Force 会一个文件都看不到。
$inVault = Wait-Until { @(Get-ChildItem $scene.Vault -Recurse -File -Force -Filter *.crv -ErrorAction SilentlyContinue).Count -gt 0 } 25
if ($inVault) {
    $item = Get-ChildItem $scene.Vault -Recurse -File -Force -Filter *.crv | Select-Object -First 1
    Write-Ok ("手机截屏已写入加密库：" + $item.Name + "（" + $item.Length + " 字节，原图 1080×2400）")
} else {
    Write-Bad "手机截屏没有写进加密库（没有任何 .crv 条目）"
}
Save-Shot $win "03-手机截屏入库后"

if (-not $KeepOpen) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

# ============================================================================
# 场景二：只发现"配对端点"（手机上刚点了「使用配对码配对设备」）
#          → 必须自动把人领到配对，并且把端口填好
# ============================================================================
Write-Step "场景二：无线首次配对引导（只发现配对端点）"
$scene2 = New-Scene "scene2" "List of discovered mdns services`nadb-FAKE-PAIR`t_adb-tls-pairing._tcp`t127.0.0.1:41235`n"
$proc2 = Start-App $scene2 "-adbctl2"
$win2 = Get-AppWindow $proc2.Id
if (-not $win2) {
    Write-Bad "主窗口没起来（场景二）"
} else {
    $pairWin = $null
    $opened = Wait-Until {
        $w = Get-ChildWindow $proc2.Id "电脑控制手机" $win2
        if ($w) { $script:pairWin = $w; return $true }
        return $false
    } 45
    if ($opened) {
        $pw = $script:pairWin
        Write-Ok "手机在等配对时，程序自动打开了配对窗口"
        $portEl = $pw.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AdbPairPortInput")))
        $port = ""
        try { $port = $portEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        if ($port -eq "41235") { Write-Ok "配对端口已自动填好（41235）—— 用户只需要照抄手机上的 6 位码" }
        else { Write-Bad ("配对端口没有自动填好，实际值：`"$port`"") }
        Save-Shot $pw "04-只发现配对端点-自动打开配对窗口"
    } else {
        Write-Bad "只发现配对端点时没有自动打开配对窗口"
    }
    Save-Shot $win2 "05-配对引导时的主界面"
}
if (-not $KeepOpen) { Stop-Process -Id $proc2.Id -Force -ErrorAction SilentlyContinue }

# ============================================================================
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ("  电脑控制手机（安卓 · 无线）自检：通过 $script:pass 项 / 失败 $script:fail 项")
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ("现场与日志：" + $work)
Write-Host ("截图：" + $shots)
exit $script:fail
