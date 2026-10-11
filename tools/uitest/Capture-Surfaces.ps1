# 一次性把各个界面截下来（主界面 + 设置各页），用于设计自检。
# 用法：powershell.exe -ExecutionPolicy Bypass -File Capture-Surfaces.ps1 -Exe "发布版\投屏Window\投屏Window.exe" -OutDir "_temp\shots" -Theme Light -Accent Mint
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "_temp\shots",
    [string]$Theme = "Light",
    [string]$Accent = "Mint",
    [int]$Width = 1280,
    [int]$Height = 800,
    [int]$WaitSeconds = 12,
    [string]$WorkRoot = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Cap {
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr Find(int pid, string title) {
        IntPtr hit = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
                if (sb.ToString() == title) { hit = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }
}
'@

function Save-Window([IntPtr]$hwnd, [string]$path) {
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host "  找不到窗口：$path" -ForegroundColor Red; return }
    $r = New-Object Cap+RECT
    [void][Cap]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Write-Host "  窗口尺寸异常：$path" -ForegroundColor Red; return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][Cap]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  已保存 $path（${w}x${h}）"
}

function Find-El([int]$targetPid, [string]$name, $type, [int]$tries = 30) {
    for ($t = 0; $t -lt $tries; $t++) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $wc)) {
            $conds = New-Object System.Collections.ArrayList
            if ($name) { [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
            if ($type) { [void]$conds.Add((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type))) }
            $cond = if ($conds.Count -eq 1) { $conds[0] } else { New-Object System.Windows.Automation.AndCondition($conds.ToArray()) }
            $e = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot "_temp\capture" }
$Exe = (Resolve-Path $Exe).Path
$outFull = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $outFull | Out-Null

if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot "cfg"), (Join-Path $WorkRoot "vault") | Out-Null

$template = Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json"
$json = if (Test-Path $template) { Get-Content $template -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
$json.ShowIntro = $false
$json.Theme = $Theme
$json.Accent = $Accent
$json.AntiCapture = $false
$json.AutoStartDlna = $false
$json.AutoStartMiracast = $false
$json.AutoStartAirPlay = $false
$json.HasSeenTutorial = $true
$json.MinimizeToTray = $false
$json.RunAtStartup = $false
$json.ShowStats = $false
$json.WindowLeft = 60; $json.WindowTop = 40; $json.WindowWidth = $Width; $json.WindowHeight = $Height
[System.IO.File]::WriteAllText((Join-Path $WorkRoot "cfg\settings.json"), ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

$argLine = "--vault-root `"$WorkRoot\vault`" --settings-root `"$WorkRoot\cfg`" --instance-suffix `"-capture`""
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList $argLine
Write-Host "已启动 pid=$($p.Id)，等待 $WaitSeconds 秒…"
Start-Sleep -Seconds $WaitSeconds

$main = [Cap]::Find($p.Id, "投屏Window")
[void][Cap]::SetForegroundWindow($main)
Start-Sleep -Milliseconds 600
Save-Window $main (Join-Path $outFull "main.png")

$open = Find-El $p.Id "打开设置" $null
if ($open) {
    $open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3
    $settings = [Cap]::Find($p.Id, "设置")
    foreach ($page in @("通用", "画面", "网络", "安全", "诊断", "关于")) {
        $nav = Find-El $p.Id $page ([System.Windows.Automation.ControlType]::RadioButton)
        if ($nav) {
            $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 900
            Save-Window $settings (Join-Path $outFull ("settings-" + $page + ".png"))
        } else { Write-Host "  找不到导航：$page" -ForegroundColor Yellow }
    }
} else {
    Write-Host "找不到「打开设置」" -ForegroundColor Red
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "完成，输出目录：$outFull"
