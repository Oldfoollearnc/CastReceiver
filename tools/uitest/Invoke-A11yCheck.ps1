# 无障碍与提示浮层的回归检查（补齐现有 uitest 套件没覆盖的部分）
#
# 覆盖：
#   ① 隔离配置是否真的被读取（避免"以为在测隔离实例、其实在用真实配置"）
#   ② 设置页「全局快捷键」的真实注册状态行
#   ③ 启动时的快捷键冲突提示条（读屏可见性）
#   ④ 键盘焦点环（Tab 后截图，人工确认 2px 描边 + 强调色光晕）
#   ⑤ 画面遮挡面板的键盘路径（托盘菜单触发 → 断言可聚焦按钮 → 真实 Enter 恢复）
#   ⑥ 静态：XAML/代码的资源键完整性（缺键会在运行期抛异常，编译期查不出）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File Invoke-A11yCheck.ps1 -Exe "<exe 路径>"
#   powershell -ExecutionPolicy Bypass -File Invoke-A11yCheck.ps1 -Exe "<exe 路径>" -DryRun
#
# 说明：
#   · 全程用**隔离配置与隔离库** + 独立实例名，不碰用户真实设置/加密库
#   · 会 SetForegroundWindow 并注入按键（会短暂占用前台）→ 请在不用电脑时跑
#   · 需要写盘（隔离配置），若所在环境限制子进程写盘，请先把 TEMP/TMP 指到工作区内
#   · 截图落在 -OutDir（默认 _temp\shots\a11ycheck）
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [switch]$DryRun,
    [string]$OutDir = "_temp\shots\a11ycheck",
    [int]$StartupWaitSeconds = 14,
    # 写进隔离配置的设备名：主界面若显示出它，就证明读的是隔离配置
    [string]$ProbeDeviceName = "ZZTEST-隔离配置"
)

$ErrorActionPreference = "Continue"
$root = (Get-Location).Path
$script:fail = 0
$script:pass = 0
$script:manual = 0

function Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green; $script:pass++ }
function Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:fail++ }
function Manual($t) { Write-Host "  [需人工看图] $t" -ForegroundColor Yellow; $script:manual++ }
function Info($t) { Write-Host "  $t" }

# ---------------------------------------------------------------- 静态检查（DryRun 也会跑）
Step "① 静态：资源键完整性"
$names = @{}
foreach ($f in (Get-ChildItem -Recurse -File -Include *.xaml src -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })) {
    $t = Get-Content $f.FullName -Raw
    foreach ($m in [regex]::Matches($t, 'x:Key="([^"]+)"')) { $names[$m.Groups[1].Value] = 1 }
}
$refs = 0; $missing = @()
foreach ($f in (Get-ChildItem -Recurse -File -Include *.xaml,*.cs src -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })) {
    $t = Get-Content $f.FullName -Raw
    foreach ($m in [regex]::Matches($t, '\{(?:Static|Dynamic)Resource\s+([^}]+)\}')) {
        $k = $m.Groups[1].Value.Trim(); $refs++
        if ($k -match '^\{x:(Type|Static)\s+([^}]+)\}$') { $k = $Matches[2].Trim() }
        if ($k -match '^[A-Za-z_][A-Za-z0-9_.]*$' -and -not $names.ContainsKey($k)) { $missing += $k }
    }
    # 代码里往字典写键的也算已定义（例如 ApplyAccent 的 overlay["B.Accent"]）
    foreach ($m in [regex]::Matches($t, '\w+\["([A-Za-z][A-Za-z0-9_.]*)"\]\s*=')) { $names[$m.Groups[1].Value] = 1 }
    # 代码侧的键查找：本项目的资源键统一是 B./S./F./T. 前缀，按前缀收口。
    # （不收紧的话，FindResource(verdict switch { "ok" => "B.Ok", ... }) 里的分支标签 "ok"
    #   会被误当成资源键 —— 这个假阳性我自己踩过一次，DryRun 就是靠它抓出来的。）
    foreach ($m in [regex]::Matches($t, '(?:FindResource|TryFindResource|SetResourceReference)\([^;]*?"((?:B|S|F|T)\.[A-Za-z0-9_.]+)"')) {
        $refs++
        if (-not $names.ContainsKey($m.Groups[1].Value)) { $missing += $m.Groups[1].Value }
    }
}
if ($missing.Count -eq 0) { Ok "资源键完整：$refs 处引用全部有定义" }
else { Bad ("以下资源键未定义（运行期会抛异常）：" + (($missing | Select-Object -Unique) -join ', ')) }

Step "② 静态：.ps1 的 BOM 与 PowerShell 5.1 可解析性"
$scripts = @(Get-ChildItem -Recurse -Filter *.ps1 |
             Where-Object { $_.FullName -notmatch '\\(obj|bin|_temp)\\' })
$badScripts = @()
foreach ($f in $scripts) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) { $badScripts += "缺BOM:$($f.Name)"; continue }
    & powershell.exe -NoProfile -Command "`$e=`$null; `$null=[System.Management.Automation.Language.Parser]::ParseFile('$($f.FullName)',[ref]`$null,[ref]`$e); if(`$e.Count){exit 1}" 2>$null
    if ($LASTEXITCODE -ne 0) { $badScripts += "5.1解析失败:$($f.Name)" }
}
if ($badScripts.Count -eq 0) { Ok "$($scripts.Count) 个 .ps1：BOM 与 5.1 解析均通过" }
else { Bad ("脚本检查失败：" + ($badScripts -join ', ')) }

# ---------------------------------------------------------------- 前置条件
Step "③ 前置条件"
if (-not (Test-Path $Exe)) { Bad "找不到可执行文件：$Exe"; exit 1 } else { Ok "可执行文件存在" }
if (-not (Test-Path (Join-Path $root $OutDir))) { New-Item -ItemType Directory -Force -Path (Join-Path $root $OutDir) | Out-Null }
Ok "截图目录：$OutDir"

if ($DryRun) {
    Write-Host "`n[DryRun] 只跑了静态与前置条件检查，未启动任何程序。" -ForegroundColor Yellow
    Write-Host "通过 $script:pass 项，失败 $script:fail 项。"
    exit $script:fail
}

# ---------------------------------------------------------------- 依赖
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AX {
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
    private const uint KEYUP = 0x0002;
    public const byte VK_DOWN = 0x28, VK_RETURN = 0x0D, VK_ESC = 0x1B, VK_TAB = 0x09;
    public static void Tap(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
    }
    public static List<IntPtr> Handles(int pid) {
        var r = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) r.Add(h);
            return true;
        }, IntPtr.Zero);
        return r;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
    public static IntPtr Find(int pid, string title) {
        foreach (IntPtr h in Handles(pid)) if (Title(h) == title) return h;
        return IntPtr.Zero;
    }
}
'@

# ---------------------------------------------------------------- 启动隔离实例
Step "④ 启动隔离实例（独立配置/库/实例名）"
$work = Join-Path $root "_temp\a11ycheck-work"
if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path (Join-Path $work "cfg"), (Join-Path $work "vault") | Out-Null
$cfg = [pscustomobject]@{
    ShowIntro       = $false
    Theme           = "Dark"      # 刻意用 Dark：真实配置若是 Light，就能反向验证读的是哪一份
    AntiCapture     = $false
    HasSeenTutorial = $true
    MinimizeToTray  = $false
    GlobalHotkeys   = $true
    DeviceName      = $ProbeDeviceName
}
[System.IO.File]::WriteAllText((Join-Path $work "cfg\settings.json"),
    ($cfg | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) `
     -ArgumentList "--no-intro --vault-root `"$work\vault`" --settings-root `"$work\cfg`" --instance-suffix `"-a11ycheck`"" -PassThru
Info "PID $($p.Id)，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds

$main = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
if (-not $main) { Bad "主窗口没起来"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Ok "主窗口就绪：$($main.Current.Name)"

function Find-Any($scope, [string]$name, [bool]$partial = $false) {
    foreach ($e in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                                  [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = $e.Current.Name
        if (-not $n) { continue }
        if ($partial) { if ($n -like "*$name*") { return $e } } elseif ($n -eq $name) { return $e }
    }
    return $null
}
function Capture([IntPtr]$h, [string]$file) {
    if ($h -eq [IntPtr]::Zero) { return $false }
    [void][AX]::SetForegroundWindow($h); Start-Sleep -Milliseconds 400
    $r = New-Object AX+R; [void][AX]::GetWindowRect($h, [ref]$r)
    $w = $r.Rt - $r.L; $ht = $r.B - $r.T
    if ($w -le 0 -or $ht -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
    $g.Dispose()
    $bmp.Save((Join-Path $root (Join-Path $OutDir $file)), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return $true
}
Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------- ① 隔离配置是否被读取
Step "⑤ 隔离配置是否真的被读取"
$probe = Find-Any $main $ProbeDeviceName $true
if ($probe) { Ok "主界面出现了隔离配置里的设备名「$ProbeDeviceName」→ 读的是隔离配置" }
else { Bad "主界面找不到隔离配置的设备名（可能在别的页面/被截断），无法确认隔离生效" }

# ---------------------------------------------------------------- ② 焦点环
Step "⑥ 键盘焦点环（Tab 后截图）"
[void][AX]::SetForegroundWindow([AX]::Find($p.Id, $main.Current.Name))
Start-Sleep -Milliseconds 500
[AX]::Tap([AX]::VK_TAB); Start-Sleep -Milliseconds 300
[AX]::Tap([AX]::VK_TAB); Start-Sleep -Milliseconds 500
if (Capture ([AX]::Find($p.Id, $main.Current.Name)) "focus-ring.png") {
    Manual "已保存 focus-ring.png：请确认被聚焦控件周围有 2px 描边 + 强调色光晕"
} else { Bad "焦点环截图失败" }

# ---------------------------------------------------------------- ③ 底栏文案 / 顶栏分隔线
if (Capture ([AX]::Find($p.Id, $main.Current.Name)) "main-window.png") {
    Manual "已保存 main-window.png：请确认底栏右侧是「F11 进入全屏」、且「设置」与「最小化」之间有 1px 分隔线"
} else { Bad "主窗口截图失败" }

# ---------------------------------------------------------------- ④ 设置页快捷键状态行
Step "⑦ 设置页「全局快捷键」状态行"
$openBtn = Find-Any $main "设置" $false
if (-not $openBtn) { $openBtn = Find-Any $main "打开设置" $false }
if ($openBtn) {
    try { $openBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
    Start-Sleep -Seconds 4
    $swH = [AX]::Find($p.Id, "设置")
    if ($swH -ne [IntPtr]::Zero) {
        $sw = [System.Windows.Automation.AutomationElement]::FromHandle($swH)
        $nav = Find-Any $sw "通用" $false
        if ($nav) { try { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }; Start-Sleep -Milliseconds 900 }
        $st = Find-Any $sw "全局快捷键" $true
        $found = $null
        foreach ($e in $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $n = $e.Current.Name
            if ($n -and ($n -like "*被其它程序占用*" -or $n -like "*已全部生效*" -or $n -like "*已关闭*")) { $found = $n; break }
        }
        if ($found) { Ok "状态行：$found" } else { Bad "找不到快捷键状态行（TxtHotkeyState）" }
        [AX]::Tap([AX]::VK_ESC); Start-Sleep -Seconds 1
    } else { Bad "设置窗口没起来" }
} else { Bad "主窗口里找不到设置入口" }

# ---------------------------------------------------------------- ⑤ 遮挡面板键盘路径（走托盘菜单）
Step "⑧ 遮挡面板键盘路径（托盘菜单 → 真实 Enter 恢复）"
Info "本机 Ctrl+Alt+P 很可能被别的程序占用，所以走托盘菜单；失败时请看 tray-menu.png"
$callback = 0x8000 + 17      # TrayIconService.CallbackMessage = WM_APP + 17
$wmContext = 0x007B
if (-not ([System.Management.Automation.PSTypeName]'AX2.P').Type) {
    Add-Type -Namespace AX2 -Name P -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);'
}
$mainH = [AX]::Find($p.Id, $main.Current.Name)
[void][AX2.P]::PostMessage($mainH, $callback, [IntPtr]::Zero, [IntPtr]$wmContext)
Start-Sleep -Milliseconds 900
# 截图在下面的候选次数循环里做（那里才知道菜单是否真的打开了）
function Find-Mask {
    foreach ($e in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                                 [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -eq "画面已遮挡，恢复查看") { return $e }
    }
    return $null
}

# 菜单项顺序（见 TrayIconService.ShowMenu）：显示主窗口/全屏/截取/录制/打开我的库/锁定/遮挡画面…
# 但**未投屏时"截取/录制"是灰显的，而 Windows 菜单的方向键会跳过禁用项** ——
# 所以到「遮挡画面」要按几次 Down 取决于当时状态（未投屏 5 次、投屏中 7 次）。
# 不写死次数，按候选值依次试，命中即停。
$mask = $null; $usedDowns = 0
foreach ($downs in 5, 7, 6, 4) {
    [void][AX2.P]::PostMessage($mainH, $callback, [IntPtr]::Zero, [IntPtr]$wmContext)
    Start-Sleep -Milliseconds 900
    if ($usedDowns -eq 0 -and (Capture $mainH "tray-menu.png")) {
        Manual "已保存 tray-menu.png：请确认菜单文案（进入/退出全屏、遮挡画面…）与勾选/灰显状态"
    }
    for ($i = 0; $i -lt $downs; $i++) { [AX]::Tap([AX]::VK_DOWN); Start-Sleep -Milliseconds 70 }
    [AX]::Tap([AX]::VK_RETURN)
    Start-Sleep -Milliseconds 1500
    $usedDowns = $downs
    $mask = Find-Mask
    if ($mask) { break }
    [AX]::Tap([AX]::VK_ESC); Start-Sleep -Milliseconds 600   # 没命中就把菜单关掉再试下一个次数
}

if ($mask) {
    Ok "遮挡面板出现（Down x$usedDowns 命中），且是可访问元素：$($mask.Current.ControlType.ProgrammaticName)"
    if ($mask.Current.IsKeyboardFocusable) { Ok "IsKeyboardFocusable = True（Enter/空格可用）" } else { Bad "面板不可键盘聚焦" }
    [AX]::Tap([AX]::VK_RETURN); Start-Sleep -Milliseconds 1200
    if (Find-Mask) { Bad "按 Enter 后面板仍在（键盘无法恢复画面）" } else { Ok "按 Enter 后面板消失 → 键盘可自行恢复画面" }
} else {
    Bad "托盘菜单路线没能触发遮挡面板（候选次数都试过；看 tray-menu.png 判断原因）"
}

# ---------------------------------------------------------------- 收尾
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Step "结果"
Write-Host "  通过 $script:pass 项，失败 $script:fail 项，需人工看图 $script:manual 项"
Write-Host "  截图目录：$OutDir"
if ($script:fail -gt 0) { exit $script:fail }
