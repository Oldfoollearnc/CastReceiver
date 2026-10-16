# 小窗验证脚本（开发期用，不参与产品构建）
#
# 依次验证：
#   A) 主窗口最小化 → 小窗自动出现**且有画面**；恢复 → 小窗自动收起
#   B) 点悬浮条上的「小窗」按钮 → 小窗手动打开并与主窗口并存**且有画面**
#
# 关键：不止检查"窗口在不在"，还检查小窗里**是不是真有画面**（采样亮度）。
# 只查窗口存在的话，"出来一片黑"会被误判成通过 —— 这个脚本第一次跑就踩过这个坑。
param(
    [string]$Exe,
    [string]$SettingsRoot,
    [string]$ShotDir,
    [int]$WaitReady = 9
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);

    public static void MouseDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void MouseUp() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }

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

    // 返回 left, top, width, height
    public static int[] Bounds(IntPtr h) {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static string Rect(IntPtr h) {
        int[] b = Bounds(h);
        return string.Format("{0},{1} {2}x{3}", b[0], b[1], b[2], b[3]);
    }
}
'@

function Get-AppWindows([int]$ProcessId) {
    $rows = @()
    foreach ($h in [Win]::ForProcess([uint32]$ProcessId)) {
        $rows += [pscustomobject]@{ Handle = $h; Title = [Win]::Title($h); Rect = [Win]::Rect($h) }
    }
    return $rows
}

function Find-Mini([int]$ProcessId) {
    return (Get-AppWindows $ProcessId | Where-Object { $_.Title -eq '小窗' } | Select-Object -First 1)
}

function Shot([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# 采样亮度：返回非暗像素占比。全黑窗口约为 0，有画面的窗口明显更高。
function PictureRatio([int]$X, [int]$Y, [int]$W, [int]$H) {
    if ($W -le 2 -or $H -le 2) { return 0.0 }
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

$script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $tag = if ($Ok) { "PASS" } else { "FAIL" }
    if (-not $Ok) { $script:fail++ }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

# ---------------------------------------------------------------- 启动
$proc = Start-Process -FilePath $Exe -ArgumentList '--no-intro', '--demo-cast', '--settings-root', $SettingsRoot -PassThru
Start-Sleep -Seconds $WaitReady
Check "程序已启动" (-not $proc.HasExited) ("pid=" + $proc.Id)

$windows = Get-AppWindows $proc.Id
$main = $windows | Where-Object { $_.Title -eq '投屏Window' } | Select-Object -First 1
Check "找到主窗口" ($null -ne $main) (($windows | ForEach-Object { "$($_.Title) [$($_.Rect)]" }) -join ' | ')
if ($null -eq $main) { Stop-Process -Id $proc.Id -Force; exit 1 }

$mini0 = Find-Mini $proc.Id
Check "初始没有小窗" ($null -eq $mini0) ("小窗数=" + @($mini0).Count)

# ---------------------------------------------------------------- A) 最小化 → 自动出现且有画面
[void][Win]::ShowWindow($main.Handle, 6)      # SW_MINIMIZE
Start-Sleep -Milliseconds 2000
$miniA = Find-Mini $proc.Id
Check "A: 最小化后小窗自动出现" ($null -ne $miniA) ($(if ($miniA) { $miniA.Rect } else { "未出现" }))

if ($miniA) {
    # 把鼠标移到小窗上：控制条会自动滑出，截图才能体现它的样子（窗口也会随之长高）
    $b0 = [Win]::Bounds($miniA.Handle)
    [void][Win]::SetCursorPos(($b0[0] + [int]($b0[2] / 2)), ($b0[1] + [int]($b0[3] / 2)))
    Start-Sleep -Milliseconds 900

    $miniA = Find-Mini $proc.Id
    $b = [Win]::Bounds($miniA.Handle)
    $ratio = PictureRatio $b[0] $b[1] $b[2] $b[3]
    Check "A: 小窗里有画面（不是黑屏）" ($ratio -gt 0.05) ("非暗像素占比=" + $ratio + " 尺寸=" + $b[2] + "x" + $b[3])
    $script:sizeA = @($b[2], $b[3])      # A 关掉之后句柄就失效了，尺寸要先记下来
    Shot (Join-Path $ShotDir 'mini-A-minimized.png') ($b[0] - 20) ($b[1] - 20) ($b[2] + 40) ($b[3] + 40)
}

# ---------------------------------------------------------------- A) 恢复 → 自动收起
[void][Win]::ShowWindow($main.Handle, 9)      # SW_RESTORE
Start-Sleep -Milliseconds 2000
$miniAfterRestore = Find-Mini $proc.Id
Check "A: 恢复后小窗自动收起" ($null -eq $miniAfterRestore) ($(if ($miniAfterRestore) { "仍在：" + $miniAfterRestore.Rect } else { "已收起" }))

# ---------------------------------------------------------------- B) 点按钮 → 手动打开并与主窗口并存
[void][Win]::SetForegroundWindow($main.Handle)
Start-Sleep -Milliseconds 500

$clicked = $false
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $procCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $procCond)
    foreach ($el in $found) {
        try {
            if ($el.Current.Name -eq '小窗' -and $el.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') {
                $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $clicked = $true
                break
            }
        } catch { }
    }
} catch { Write-Host ("UIAutomation 不可用：" + $_.Exception.Message) }
Check "B: 找到并点击「小窗」按钮" $clicked "UIAutomation"

Start-Sleep -Milliseconds 1800
$miniB = Find-Mini $proc.Id
$mainStill = Get-AppWindows $proc.Id | Where-Object { $_.Title -eq '投屏Window' } | Select-Object -First 1
Check "B: 小窗已打开" ($null -ne $miniB) ($(if ($miniB) { $miniB.Rect } else { "未出现" }))
Check "B: 主窗口仍正常显示（并存）" ($null -ne $mainStill) ($(if ($mainStill) { $mainStill.Rect } else { "主窗口不见了" }))

if ($miniB) {
    $b0 = [Win]::Bounds($miniB.Handle)
    [void][Win]::SetCursorPos(($b0[0] + [int]($b0[2] / 2)), ($b0[1] + [int]($b0[3] / 2)))
    Start-Sleep -Milliseconds 900

    $miniB = Find-Mini $proc.Id
    $b2 = [Win]::Bounds($miniB.Handle)
    $ratio2 = PictureRatio $b2[0] $b2[1] $b2[2] $b2[3]
    Check "B: 小窗里有画面（不是黑屏）" ($ratio2 -gt 0.05) ("非暗像素占比=" + $ratio2 + " 尺寸=" + $b2[2] + "x" + $b2[3])
    # 同一个基准尺寸 + 同一个展开的控制条，两次的窗口大小必须一致。
    # 不一致就说明尺寸在会话之间"爬高"了（存了展开高度那类 bug）。
    if ($null -ne $script:sizeA) {
        Check "A/B 小窗尺寸一致（没有逐次爬高）" `
            (($script:sizeA[0] -eq $b2[2]) -and ($script:sizeA[1] -eq $b2[3])) `
            ("A=" + $script:sizeA[0] + "x" + $script:sizeA[1] + "  B=" + $b2[2] + "x" + $b2[3])
    }
    Shot (Join-Path $ShotDir 'mini-B-picture-in-picture.png') ($b2[0] - 260) ($b2[1] - 190) ($b2[2] + 300) ($b2[3] + 230)

    # 拖画面移动小窗：画面是 HwndHost 的子窗口，鼠标消息只到窗口级（实测），
    # 所以这条专门验证"拖画面"确实能挪窗口 —— 之前只在注释里写着能用。
    $dragFromX = $b2[0] + [int]($b2[2] / 2)
    $dragFromY = $b2[1] + [int]($b2[3] / 4)
    [void][Win]::SetCursorPos($dragFromX, $dragFromY)
    Start-Sleep -Milliseconds 250
    [Win]::MouseDown()
    Start-Sleep -Milliseconds 250
    [void][Win]::SetCursorPos($dragFromX - 120, $dragFromY + 80)
    Start-Sleep -Milliseconds 300
    [Win]::MouseUp()
    Start-Sleep -Milliseconds 700
    $b3 = [Win]::Bounds($miniB.Handle)
    $movedX = $b2[0] - $b3[0]
    $movedY = $b3[1] - $b2[1]
    Check "B: 拖画面能把小窗挪走" (($movedX -gt 90) -and ($movedY -gt 50)) `
        ("期望约 (120,80)，实际 (${movedX},${movedY})")
}

# ---------------------------------------------------------------- 收尾
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host ("==== 小窗验证：失败 {0} 项 ====" -f $script:fail)
exit $script:fail
