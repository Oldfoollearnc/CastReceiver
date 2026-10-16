# 设备识别（⑦）验证脚本（开发期用，不参与产品构建）
#
# 验的是整条链路：伪 iPhone 在 SETUP 里带上 model/name/osName/osVersion + User-Agent
#   → 接收端解析并识别 → 识别结果真的出现在界面上（小窗状态文字）
#
# 为什么要读界面而不是读日志：日志是缓冲写的，进程被强杀时内容会丢，
# 而且"日志里有"不等于"用户看得见"。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$SettingsRoot,
    [Parameter(Mandatory = $true)][string]$Py,
    [Parameter(Mandatory = $true)][string]$Vectors,
    [string]$Server = 'PC',
    [string]$Mac = '7A:1B:2C:3D:4E:5F',
    [int]$WaitReady = 16
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindByTitle(uint want, string title) {
        IntPtr hit = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != want || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == title) { hit = h; return false; }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    public static int[] Bounds(IntPtr h) {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
}
'@

$script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:fail++ }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

$ip = (Find-NetRoute -RemoteIPAddress 8.8.8.8 | Select-Object -First 1).IPAddress

$proc = Start-Process -FilePath $Exe -ArgumentList '--no-intro', '--settings-root', $SettingsRoot -PassThru
Start-Sleep -Seconds $WaitReady
Check '程序已启动' (-not $proc.HasExited) ("pid=" + $proc.Id)

# ---------------------------------------------------------------- 伪 iPhone 握手（含识别字段）
# 关键：让客户端**保持连接**。识别结果会在会话结束时被清掉，
# 之前就是在客户端已经 TEARDOWN 之后才去查界面，误判成"识别没生效"。
$clientScript = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $Vectors -Parent) '..\..\..\tools\airplay-port\airplay_fake_client.py'))
$clientLog = Join-Path $SettingsRoot 'client.out'
# 注意：工程路径里有空格（"投屏Window 桌面版"），Start-Process 不会自动加引号，
# 不手工加引号的话 python 只会拿到半截路径。
$argLine = @('-u', ('"' + $clientScript + '"'), $ip, ('"' + $Vectors + '"'), $Server, $Mac, '45') -join ' '
$client = Start-Process -FilePath $Py `
    -ArgumentList $argLine `
    -PassThru -NoNewWindow -RedirectStandardOutput $clientLog -RedirectStandardError (Join-Path $SettingsRoot 'client.err')
Start-Sleep -Seconds 10

# 用 -u 跑 Python：否则输出是块缓冲的，握手过程中的 ok/FAIL 在保持会话期间看不到
$clientText = ''
if (Test-Path $clientLog) { $clientText = [string](Get-Content $clientLog -Raw) }
$reachedHold = $clientText -match 'HOLDING'
$anyFail = $clientText -match 'FAIL'
Check '伪 iPhone 握手走到保持会话（过程中无 FAIL）' ($reachedHold -and -not $anyFail) `
    ("HOLDING=" + $reachedHold + " 有 FAIL=" + $anyFail)
Check '客户端仍在保持会话（未 TEARDOWN）' (-not $client.HasExited) ("client pid=" + $client.Id)

# ---------------------------------------------------------------- 打开小窗，读它显示的设备名
$root = [System.Windows.Automation.AutomationElement]::RootElement
$procCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)

function Find-All([string]$Name, [string]$ControlType) {
    $found = @()
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $procCond)
    foreach ($el in $all) {
        try {
            if ($el.Current.ControlType.ProgrammaticName -eq $ControlType -and $el.Current.Name -like $Name) {
                $found += $el
            }
        } catch { }
    }
    return $found
}

$clicked = $false
foreach ($el in (Find-All '小窗' 'ControlType.Button')) {
    try {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $clicked = $true
        break
    } catch { }
}
Check '点开小窗' $clicked 'UIAutomation'
Start-Sleep -Seconds 2

# 控制条是鼠标移上去才滑出的；不把鼠标挪过去，TxtState 是被裁剪掉的，
# 根本不会出现在自动化树里 —— 上一次就是这么误判成"界面没显示"的。
$miniHandle = [Win]::FindByTitle([uint32]$proc.Id, '小窗')
if ($miniHandle -ne [IntPtr]::Zero) {
    $mb = [Win]::Bounds($miniHandle)
    [void][Win]::SetCursorPos(($mb[0] + [int]($mb[2] / 2)), ($mb[1] + [int]($mb[3] / 2)))
    Start-Sleep -Milliseconds 900
}
Check '找到小窗窗口并把鼠标移上去' ($miniHandle -ne [IntPtr]::Zero) ($(if ($miniHandle -ne [IntPtr]::Zero) { 'ok' } else { '没找到小窗' }))

# 小窗里的状态文字应该已经变成识别出的型号。
# 注意要按"映射后的型号"来匹配：开机提示里也含 "iPhone" 字样，只搜 iPhone 会误判通过。
$texts = Find-All '*iPhone 14 Pro*' 'ControlType.Text'
$shown = ($texts | ForEach-Object { $_.Current.Name }) -join ' | '
Check '界面显示识别出的苹果型号' ($texts.Count -gt 0) ($(if ($shown) { $shown } else { '界面上没找到含「iPhone 14 Pro」的文字' }))

# 小窗很窄，按设计只显示「型号 · 协议」；完整描述（含 iOS 版本）由 Describe() 给出，
# 那部分由 --airplay-only 的 K 组用例覆盖，这里不重复要求。
$hasProtocol = $shown -match 'AirPlay'
Check '小窗显示「型号 · 协议」' ($texts.Count -gt 0 -and $hasProtocol) $shown

Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host ("==== 设备识别验证：失败 {0} 项 ====" -f $script:fail)
exit $script:fail
