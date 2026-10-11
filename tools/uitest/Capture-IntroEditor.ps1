# 截「开场动画」编辑页（本身也是自检：控件都在、小画面能出画、更多设置能展开、位置/填充能切）
param([Parameter(Mandatory = $true)][string]$Exe, [string]$OutDir = "_temp\shots\intro-page")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class IEP {
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
    public static IntPtr Find(int pid, string title) {
        IntPtr hit = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
                if (sb.ToString() == title) { hit = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }
}
'@

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Exe = (Resolve-Path $Exe).Path
$out = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null
$work = Join-Path $repo "_temp\intro-page-work"
if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path (Join-Path $work "cfg"), (Join-Path $work "vault") | Out-Null
$json = Get-Content (Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$json.ShowIntro = $true; $json.Theme = "Light"; $json.AntiCapture = $false; $json.HasSeenTutorial = $true
$json.WindowLeft = 60; $json.WindowTop = 40; $json.WindowWidth = 1280; $json.WindowHeight = 800
[System.IO.File]::WriteAllText((Join-Path $work "cfg\settings.json"), ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

function Save([IntPtr]$h, [string]$file) {
    if ($h -eq [IntPtr]::Zero) { Write-Host "  找不到窗口：$file" -ForegroundColor Red; return }
    $r = New-Object IEP+R; [void][IEP]::GetWindowRect($h, [ref]$r)
    $w = $r.Rt - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [void][IEP]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "  已保存 $file ($w x $ht)"
}
function ByName($win, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function ClickName($win, [string]$name) {
    $el = ByName $win $name
    if (-not $el) { Write-Host "  找不到：$name" -ForegroundColor Yellow; return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { Write-Host "  点不动：$name" -ForegroundColor Yellow; return $false }
}

$p = Start-Process -FilePath $Exe -PassThru -ArgumentList "--no-intro --vault-root `"$work\vault`" --settings-root `"$work\cfg`" --instance-suffix `"-intropage`""
Start-Sleep -Seconds 10
$main = [System.Windows.Automation.AutomationElement]::FromHandle([IEP]::Find($p.Id, "投屏Window"))
ClickName $main "打开设置" | Out-Null
Start-Sleep -Seconds 4
$settings = [System.Windows.Automation.AutomationElement]::FromHandle([IEP]::Find($p.Id, "设置"))
if (-not $settings) { Write-Host "设置窗没起来" -ForegroundColor Red; Stop-Process -Id $p.Id -Force; exit 1 }
ClickName $settings "开场" | Out-Null
Start-Sleep -Seconds 4
ClickName $settings "试听片段" | Out-Null
Start-Sleep -Seconds 3
Save ([IEP]::Find($p.Id, "设置")) "intro-top.png"

ClickName $settings "更多设置" | Out-Null
Start-Sleep -Seconds 2
Save ([IEP]::Find($p.Id, "设置")) "intro-more.png"

ClickName $settings "完整显示" | Out-Null
ClickName $settings "位置 右下" | Out-Null
Start-Sleep -Seconds 2
Save ([IEP]::Find($p.Id, "设置")) "intro-fit-right.png"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "输出：$out"
