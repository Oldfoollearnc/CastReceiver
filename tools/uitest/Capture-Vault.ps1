# 截取加密库这条链路的三扇窗：创建账号 / 解锁 / 我的库
# 用法：powershell.exe -ExecutionPolicy Bypass -File Capture-Vault.ps1 -Exe "发布版\投屏Window\投屏Window.exe" -OutDir docs
# 说明：全程用独立的库目录与单实例名，不会碰用户真实的加密库；顺便当界面自检用。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "_temp\shots\vault",
    [string]$WorkRoot = "",
    [string]$Account = "vaultdemo",
    [string]$Password = "Kx7Pass1234",
    [int]$WaitSeconds = 12,
    [switch]$SkipSetupShot,
    # 当前 UI 里「我的库」入口只在投屏会话中可见；用 --demo-cast 让入口出现（本地样例视频冒充镜像）
    [switch]$DemoCast
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class VCap {
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }

    public static List<IntPtr> Handles(int pid) {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr Find(int pid, string title) {
        foreach (IntPtr h in Handles(pid)) {
            var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
            if (sb.ToString() == title) return h;
        }
        return IntPtr.Zero;
    }
}
'@

$script:failed = 0
function Write-Ok([string]$t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad([string]$t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failed++ }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Exe = (Resolve-Path $Exe).Path
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot "_temp\vault-capture" }
$outFull = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $outFull | Out-Null
if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot "cfg"), (Join-Path $WorkRoot "vault") | Out-Null

$template = Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json"
$json = if (Test-Path $template) { Get-Content $template -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
$json.ShowIntro = $false
$json.Theme = "Light"
$json.Accent = "Mint"
$json.AntiCapture = $false
$json.AutoStartDlna = $false; $json.AutoStartMiracast = $false; $json.AutoStartAirPlay = $false
$json.HasSeenTutorial = $true; $json.MinimizeToTray = $false
$json.WindowLeft = 60; $json.WindowTop = 40; $json.WindowWidth = 1280; $json.WindowHeight = 800
[System.IO.File]::WriteAllText((Join-Path $WorkRoot "cfg\settings.json"), ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

function Get-WindowByTitle([int]$targetPid, [string]$title, [int]$tries = 25) {
    for ($i = 0; $i -lt $tries; $i++) {
        $h = [VCap]::Find($targetPid, $title)
        if ($h -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($h) }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Find-ByName($win, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-Input($win, [string]$name) {
    $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $byType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($byName, $byType)))
}

function Click([int]$targetPid, [string]$title, [string]$name) {
    $win = Get-WindowByTitle $targetPid $title 6
    if (-not $win) { Write-Bad ("找不到窗口：" + $title); return }
    $el = Find-ByName $win $name
    if (-not $el) { Write-Bad ("窗口 " + $title + " 里找不到：" + $name); return }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Ok ("已点击 " + $title + " → " + $name)
}

# 「我的库」这个入口的名字变过（曾经叫「我的库」，现在叫「打开我的库」），而且只在投屏会话里可见。
# 逐个名字试，并给出能照着做的提示——不然脚本只会丢一句"找不到"，看不出是改名了还是没投屏。
function Click-Any([int]$targetPid, [string]$title, [string[]]$names) {
    $win = Get-WindowByTitle $targetPid $title 6
    if (-not $win) { Write-Bad ("找不到窗口：" + $title); return $false }
    foreach ($n in $names) {
        $el = Find-ByName $win $n
        if ($el) {
            $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Ok ("已点击 " + $title + " → " + $n)
            return $true
        }
    }
    Write-Bad ("窗口 " + $title + " 里找不到这些控件：" + ($names -join " / "))
    Write-Host "        提示：未投屏时主窗口不再提供「我的库」入口（只在投屏会话卡片里，或走托盘菜单）。" -ForegroundColor Yellow
    Write-Host "        加 -DemoCast 重跑即可让该入口出现（用本地样例视频冒充镜像）。" -ForegroundColor Yellow
    return $false
}

function Type-Into([int]$targetPid, [string]$title, [string]$name, [string]$text) {
    $win = Get-WindowByTitle $targetPid $title 6
    if (-not $win) { Write-Bad ("找不到窗口：" + $title); return }
    $el = Find-Input $win $name
    if (-not $el) { Write-Bad ("窗口 " + $title + " 里找不到输入框：" + $name); return }
    # 必须先把窗口置前再 SetFocus：否则 SendKeys 的按键会飞到当时的前台窗口去，
    # 对话框里留空 → 点"创建账号"被校验挡下（症状是"已输入"打印了、但什么都没建成）。
    $h = $win.Current.NativeWindowHandle
    if ($h) { [void][VCap]::SetForegroundWindow([IntPtr]$h); Start-Sleep -Milliseconds 250 }
    # 优先用 ValuePattern 直接赋值：SendKeys 会经过中文输入法，
    # 实测 "diaguser" 被拆成 "dia'gu'se'r"、密码框甚至收不到内容 → 校验不过、建不成账号。
    # PasswordBox 出于安全**不支持 ValuePattern** —— 这种情况只能退回 SendKeys，
    # 并且无法读回校验（不能因此判失败；密码是否真的对，由后面"能不能建成账号"间接证明）。
    # WPF 的 PasswordBox 支持 ValuePattern（可写），但出于安全读出来永远是空 ——
    # 所以密码类元素只能"写进去、不读回"（用 UIA 的 IsPassword 识别）。
    # 密码框：ValuePattern.SetValue 在 WPF PasswordBox 上"看起来成功但值没进去"（实测），
    # 所以密码一律走真实按键；窗口已置前 + 输入法已禁用（见 XAML 的 IsInputMethodEnabled）。
    if ($el.Current.IsPassword) {
        $el.SetFocus()
        Start-Sleep -Milliseconds 350
        if (-not $el.Current.HasKeyboardFocus) { Write-Host ("     提示：" + $name + " 未取得键盘焦点") -ForegroundColor Yellow }
        [System.Windows.Forms.SendKeys]::SendWait("^a")      # 先全选，避免与已有内容拼在一起
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait($text)
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait("{TAB}")   # 明确把焦点交出去，促使校验状态更新
        Start-Sleep -Milliseconds 250
        Write-Ok ("已输入 " + $name + "（密码框：真实按键，按设计不可读回）")
        return
    }
    $vp = $null
    try { $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) } catch { $vp = $null }
    if ($vp) {
        $vp.SetValue($text)
        Start-Sleep -Milliseconds 200
        if ($vp.Current.Value -eq $text) { Write-Ok ("已输入 " + $name + "（ValuePattern，已读回校验）"); return }
        Write-Bad ("输入未生效：" + $name + "（读回与期望不一致）"); return
    }
    $el.SetFocus()
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Start-Sleep -Milliseconds 200
    Write-Ok ("已输入 " + $name + "（密码框不支持 ValuePattern，无法读回；由能否建成账号间接验证）")
}

function Save([int]$targetPid, [string]$title, [string]$file) {
    $h = [VCap]::Find($targetPid, $title)
    if ($h -eq [IntPtr]::Zero) { Write-Bad ("截图失败，找不到窗口：" + $title); return }
    [void][VCap]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
    $r = New-Object VCap+R
    [void][VCap]::GetWindowRect($h, [ref]$r)
    $w = $r.Rt - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][VCap]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $path = Join-Path $outFull $file
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Ok ("已保存 " + $file + "（" + $w + "x" + $ht + "）")
}

$argLine = "--no-intro --vault-root `"$WorkRoot\vault`" --settings-root `"$WorkRoot\cfg`" --instance-suffix `"-vaultcap`""
if ($DemoCast) { $argLine += " --demo-cast" }
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList $argLine
Write-Host "已启动 pid=$($p.Id)，等待 $WaitSeconds 秒…"
Start-Sleep -Seconds $WaitSeconds

Click-Any $p.Id "投屏Window" @("打开我的库", "我的库") | Out-Null
Start-Sleep -Seconds 3
if (-not $SkipSetupShot) { Save $p.Id "创建加密账号" "vault-setup.png" }

Type-Into $p.Id "创建加密账号" "账号名" $Account
Type-Into $p.Id "创建加密账号" "密码" $Password
Type-Into $p.Id "创建加密账号" "再次输入密码" $Password
Start-Sleep -Milliseconds 400
Click $p.Id "创建加密账号" "创建账号"
Start-Sleep -Seconds 3

# 点完创建却没等到解锁窗口：把对话框里的文字打出来 —— 用户看到的报错就在里面
$dlg = Get-WindowByTitle $p.Id "创建加密账号" 2
if ($dlg) {
    Write-Host "     对话框当前文字（含报错）：" -ForegroundColor Yellow
    $tc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($e in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) {
        if ($e.Current.Name) { Write-Host ("       「" + $e.Current.Name + "」") -ForegroundColor Yellow }
    }
}
Save $p.Id "解锁我的库" "vault-unlock.png"
Type-Into $p.Id "解锁我的库" "解锁密码" $Password
Start-Sleep -Milliseconds 400
Click $p.Id "解锁我的库" "解锁"
Start-Sleep -Seconds 4

Save $p.Id "我的库" "vault-window.png"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($script:failed -eq 0) { Write-Host "完成，输出目录：$outFull" -ForegroundColor Green } else { Write-Host ("有 " + $script:failed + " 项失败") -ForegroundColor Red }
exit $script:failed
