# 开场编辑页的快检（一趟约 40 秒，只做两件不看运气的事）：
#   ① 有素材时画布够大（用户报的"太窄看不出效果"）
#   ② 没素材时给出提示、试听按钮禁用（内置素材已全部去掉，新用户一进来就是这个状态）
#
# 「换素材 → 小画面跟着换」那条链路交给用户手动点一遍确认：驱动 Windows 文件对话框
# 又慢又脆，性价比太低，不再写这种自动化。
#
# 用法：powershell.exe -ExecutionPolicy Bypass -File Invoke-IntroEditorTest.ps1 -Exe "发布版\投屏Window\投屏Window.exe"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "_temp\shots\intro-editor-test"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class IET {
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

$script:failures = 0
function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures++ }
function Write-Info($t) { Write-Host "  · $t" }

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Exe = (Resolve-Path $Exe).Path
$out = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Find-El($win, [string]$name, $type) {
    $cond = if ($name) { New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name) }
            else { [System.Windows.Automation.Condition]::TrueCondition }
    foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $type -or $el.Current.ControlType -eq $type) { return $el }
    }
    return $null
}
function Click-El($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { return $false }
}
function Save([IntPtr]$h, [string]$file) {
    if ($h -eq [IntPtr]::Zero) { return }
    $r = New-Object IET+R; [void][IET]::GetWindowRect($h, [ref]$r)
    $w = $r.Rt - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [void][IET]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
# 量窗口截图里那块"视频黑区"有多高
function Measure-Canvas($png) {
    if (-not (Test-Path $png)) { return 0 }
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    $rows = 0
    for ($y = 90; $y -lt 560; $y += 2) {
        $dark = 0
        for ($x = 240; $x -lt [Math]::Min(940, $bmp.Width - 20); $x += 12) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -lt 40 -and $c.G -lt 40 -and $c.B -lt 40) { $dark++ }
        }
        if ($dark -gt 40) { $rows += 2 }
    }
    $bmp.Dispose()
    return $rows
}
function Open-IntroPage([string]$settingsRoot, [string]$vaultRoot, [string]$suffix) {
    $proc = Start-Process -FilePath $Exe -PassThru -ArgumentList "--no-intro --vault-root `"$vaultRoot`" --settings-root `"$settingsRoot`" --instance-suffix `"$suffix`""
    Start-Sleep -Seconds 10
    $main = [System.Windows.Automation.AutomationElement]::FromHandle([IET]::Find($proc.Id, "投屏Window"))
    if (-not $main) { return @{ Proc = $proc; Settings = $null } }
    # 顶栏齿轮的可访问名是「设置」（"打开设置" 只是它的 ToolTip 文本，UIA Name 里没有）。
    # 先试「设置」，再退回旧名，避免改名后整个脚本静默失效。
    if (-not (Click-El (Find-El $main "设置" $null))) {
        Click-El (Find-El $main "打开设置" $null) | Out-Null
    }
    Start-Sleep -Seconds 4
    $settings = [System.Windows.Automation.AutomationElement]::FromHandle([IET]::Find($proc.Id, "设置"))
    if ($settings) {
        Click-El (Find-El $settings "开场" ([System.Windows.Automation.ControlType]::RadioButton)) | Out-Null
        Start-Sleep -Seconds 5
    }
    return @{ Proc = $proc; Settings = $settings }
}
function New-Seed($work, [string]$introSource) {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path (Join-Path $work "cfg"), (Join-Path $work "vault") | Out-Null
    $json = Get-Content (Join-Path $env:LOCALAPPDATA "CastReceiver\settings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.ShowIntro = $true
    $json.Theme = "Light"
    $json.AntiCapture = $false
    $json.HasSeenTutorial = $true
    $json.IntroSource = $introSource
    $json.IntroStart = 0
    $json.IntroEnd = 3
    $json.WindowLeft = 60; $json.WindowTop = 40; $json.WindowWidth = 1280; $json.WindowHeight = 720
    [System.IO.File]::WriteAllText((Join-Path $work "cfg\settings.json"), ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------- ① 有素材：画布要大
$clip = Get-ChildItem "C:\Users\Administrator\Desktop\MP4" -Filter *.mp4 -ErrorAction SilentlyContinue | Sort-Object Length | Select-Object -First 1
$work = Join-Path $repo "_temp\intro-editor-test-work"
if ($clip) {
    Write-Step "① 有素材时：画布要够大"
    New-Seed $work ("file:" + $clip.FullName)
    $r = Open-IntroPage (Join-Path $work "cfg") (Join-Path $work "vault") "-introeditor"
    Save ([IET]::Find($r.Proc.Id, "设置")) "with-material.png"
    Stop-Process -Id $r.Proc.Id -Force -ErrorAction SilentlyContinue
    $h = Measure-Canvas (Join-Path $out "with-material.png")
    Write-Info ("画布黑区高度约 $h px（旧版只有 216）")
    if ($h -ge 240) { Write-Ok "画布够大" } else { Write-Bad "画布还是太扁：$h px" }
} else {
    Write-Info "桌面 MP4 里没找到素材，跳过①"
}

# ---------------------------------------------------------------- ② 没素材：要有提示、试听禁用
Write-Step "② 没素材时：给提示、按钮禁用"
New-Seed $work ""
$r = Open-IntroPage (Join-Path $work "cfg") (Join-Path $work "vault") "-introempty"
Save ([IET]::Find($r.Proc.Id, "设置")) "no-material.png"
if ($r.Settings) {
    $play = Find-El $r.Settings "试听片段" $null
    if ($play -and -not $play.Current.IsEnabled) { Write-Ok "「试听片段」已禁用" }
    elseif ($play) { Write-Bad "「试听片段」还是可点的（没素材时应该禁用）" }
    else { Write-Bad "找不到「试听片段」" }

    $hint = Find-El $r.Settings "还没有素材：点下面的「选择本地文件…」挑一个视频" $null
    if ($hint) { Write-Ok "画布上有「还没有素材」的提示" } else { Write-Bad "画布上没有提示文字" }
} else { Write-Bad "设置窗没起来" }
Stop-Process -Id $r.Proc.Id -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failures -eq 0) { Write-Host "开场编辑页快检：全部通过" -ForegroundColor Green } else { Write-Host ("开场编辑页快检：" + $script:failures + " 项失败") -ForegroundColor Red }
exit $script:failures
