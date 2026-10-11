# 截屏 / 录制 端到端冒烟测试（不需要手机）
#
# 用法：
#   powershell.exe -ExecutionPolicy Bypass -File Invoke-RecordSmoke.ps1 -Exe "..\..\发布版\投屏Window\投屏Window.exe"
#
# 原理：程序支持 --demo-cast（用本地样例视频冒充手机镜像），因此这条链路可以全自动验证：
#   建账号 → 截屏 → 录制 6 秒 → 停止 → 校验磁盘上出现「已完成」的加密条目 → 打开库看到条目
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 14,
    [string]$VaultRoot = "$env:TEMP\CastReceiverRecordSmoke",
    [string]$Account = "recsmoke",
    [string]$Password = "Smoke12345",
    [int]$RecordSeconds = 6
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder s, int n);
'@

$ErrorActionPreference = "Stop"
$script:failures = 0

function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Write-Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures++ }

function Get-ProcessWindows([int]$targetPid) {
    $list = New-Object System.Collections.ArrayList
    $cb = [W.Api+EnumProc] {
        param($h, $l)
        [uint32]$owner = 0
        [void][W.Api]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $targetPid -and [W.Api]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][W.Api]::GetWindowTextW($h, $sb, 256)
            if ($sb.Length -gt 0) { [void]$list.Add([pscustomobject]@{ Handle = $h; Title = $sb.ToString() }) }
        }
        return $true
    }
    [void][W.Api]::EnumWindows($cb, [IntPtr]::Zero)
    return $list
}

function Find-Window([int]$targetPid, [string]$title, [int]$tries = 30) {
    for ($i = 0; $i -lt $tries; $i++) {
        foreach ($w in Get-ProcessWindows $targetPid) {
            if ($w.Title -like "*$title*") { return [System.Windows.Automation.AutomationElement]::FromHandle($w.Handle) }
        }
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

function Invoke-ByName($win, [string]$name) {
    $el = Find-ByName $win $name
    if (-not $el) { Write-Bad "未找到控件：$name"; return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Ok "已点击：$name"; return $true }
    catch { Write-Bad "点击失败：$name（$($_.Exception.Message)）"; return $false }
}

function Type-Into($win, [string]$name, [string]$text) {
    $el = Find-Input $win $name
    if (-not $el) { Write-Bad "未找到输入框：$name"; return $false }
    try { $el.SetFocus(); Start-Sleep -Milliseconds 250; [System.Windows.Forms.SendKeys]::SendWait($text); Write-Ok "已输入 $name"; return $true }
    catch { Write-Bad "输入失败：$name（$($_.Exception.Message)）"; return $false }
}

function Get-ItemFlags([string]$path) {
    # 用 .NET 读字节：PowerShell 5.1 没有 Get-Content -AsByteStream
    $bytes = [System.IO.File]::ReadAllBytes($path)
    return [pscustomobject]@{
        Magic     = [Text.Encoding]::ASCII.GetString($bytes, 0, 8)
        Completed = (($bytes[10] -band 1) -ne 0)
        HasThumb  = (($bytes[10] -band 2) -ne 0)
    }
}

# ---------------------------------------------------------------- 启动

if (-not (Test-Path $Exe)) { Write-Bad "找不到可执行文件：$Exe"; exit 2 }
if (Test-Path $VaultRoot) { Remove-Item $VaultRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "隔离库目录：$VaultRoot"

$logPath = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
$logLinesAtStart = if (Test-Path $logPath) { (Get-Content $logPath -Encoding UTF8 | Measure-Object -Line).Lines } else { 0 }

Write-Step "启动程序（演示投屏 + 隔离库）"
$p = Start-Process -FilePath $Exe -ArgumentList @("--demo-cast", "--vault-root", $VaultRoot, "--instance-suffix", "recsmoke") -PassThru
Start-Sleep -Seconds $StartupWaitSeconds

$main = Find-Window $p.Id "投屏Window"
if (-not $main) { Write-Bad "未找到主窗口"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Ok "主窗口已就绪"

# 把窗口提到前台（抓屏路径要求投屏窗口在最前；UIA 点击不会自动激活窗口）
$mainHandle = [IntPtr]$main.Current.NativeWindowHandle
[void][W.Api]::SetForegroundWindow($mainHandle)
Start-Sleep -Milliseconds 800

# ---------------------------------------------------------------- 建账号（然后取消解锁，回到主界面）

Write-Step "创建账号"
Invoke-ByName $main "我的库" | Out-Null
Start-Sleep -Seconds 3
$setup = Find-Window $p.Id "创建加密账号" 20
if (-not $setup) { Write-Bad "没有出现创建账号窗口"; Stop-Process -Id $p.Id -Force; exit 1 }
Type-Into $setup "账号名" $Account | Out-Null
Type-Into $setup "密码" $Password | Out-Null
Type-Into $setup "再次输入密码" $Password | Out-Null
Start-Sleep -Milliseconds 400
Invoke-ByName $setup "创建账号" | Out-Null
Start-Sleep -Seconds 3

$unlock = Find-Window $p.Id "解锁我的库" 20
if ($unlock) { Invoke-ByName $unlock "取消" | Out-Null; Write-Ok "已取消解锁，回到主界面" }
Start-Sleep -Seconds 2

# ---------------------------------------------------------------- 截屏

Write-Step "点「截屏」"
Invoke-ByName $main "截屏" | Out-Null
Start-Sleep -Seconds 4

# ---------------------------------------------------------------- 录制

Write-Step "点「录制」开始，$RecordSeconds 秒后停止"
Invoke-ByName $main "录制" | Out-Null
Start-Sleep -Seconds $RecordSeconds
Invoke-ByName $main "录制" | Out-Null
Start-Sleep -Seconds 4

# ---------------------------------------------------------------- 校验磁盘

Write-Step "校验加密条目"
$items = @()
if (Test-Path $VaultRoot) {
    # 库目录是隐藏目录：必须加 -Force 才能枚举到
    $items = Get-ChildItem $VaultRoot -Recurse -File -Filter "*.crv" -Force -ErrorAction SilentlyContinue
}
if ($items.Count -eq 0) { Write-Bad "库里没有任何条目（截屏/录制都没写进去）" }
else {
    $shots = 0; $records = 0; $incomplete = 0
    foreach ($f in $items) {
        $flags = Get-ItemFlags $f.FullName
        if ($flags.Magic -ne "CRWVAULT") { Write-Bad "文件头不是加密容器：$($f.Name)"; continue }
        if (-not $flags.Completed) { $incomplete++; continue }
        if ($f.Length -lt 20KB) { $records++; $shots++ }   # 截图条目很小，录像条目更大
        else { $records++ }
    }
    Write-Host "  条目总数=$($items.Count)  未完成=$incomplete"
    if ($incomplete -gt 0) { Write-Bad "存在未完成的空条目（录制应当被丢弃而不是留垃圾）" }
    if ($items.Count -ge 2) { Write-Ok "至少写入了 2 条内容（截图 + 录像）" } else { Write-Bad "只写入了 $($items.Count) 条" }

    $biggest = ($items | Sort-Object Length -Descending | Select-Object -First 1)
    if ($biggest.Length -gt 50KB) { Write-Ok "最大条目 $([math]::Round($biggest.Length/1KB))KB（录像有实际画面帧）" }
    else { Write-Bad "最大的条目只有 $([math]::Round($biggest.Length/1KB))KB —— 很可能一帧都没录到" }
}

# ---------------------------------------------------------------- 库内确认

Write-Step "打开我的库确认"
Invoke-ByName $main "我的库" | Out-Null
Start-Sleep -Seconds 3
$unlock2 = Find-Window $p.Id "解锁我的库" 20
if (-not $unlock2) { Write-Bad "没有出现解锁窗口" }
else {
    Type-Into $unlock2 "解锁密码" $Password | Out-Null
    Start-Sleep -Milliseconds 400
    Invoke-ByName $unlock2 "解锁" | Out-Null
    Start-Sleep -Seconds 4

    $vault = Find-Window $p.Id "我的库" 20
    if (-not $vault) { Write-Bad "库窗口没有打开" }
    else {
        $textCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        $status = $null
        foreach ($t in $vault.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
            if ($t.Current.Name -like "共 *") { $status = $t.Current.Name; break }
        }
        if ($status) { Write-Ok "库状态栏：$status" } else { Write-Bad "没有取到库状态栏" }
        if ($status -match "录像 1") { Write-Ok "库里有 1 条录像" } else { Write-Bad "库里没有录像：$status" }
        if ($status -match "截图 1") { Write-Ok "库里有 1 条截图" } else { Write-Bad "库里没有截图：$status" }

        Write-Step "删除当前条目（验证删除不再失败）"
        Invoke-ByName $vault "删除" | Out-Null
        Start-Sleep -Seconds 2

        # 删除确认是一个原生 MessageBox：先按名字找「是」，找不到就直接发 Y 键（Win32 消息框的助记符）
        $confirm = Find-Window $p.Id "删除内容" 10
        if ($confirm) {
            $yes = $null
            $btnCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            foreach ($b in $confirm.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
                if ($b.Current.Name -like "是*" -or $b.Current.Name -like "Yes*" -or $b.Current.Name -like "&Y*") { $yes = $b; break }
            }
            if ($yes) {
                $yes.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Ok "已确认删除（按钮：$($yes.Current.Name)）"
            }
            else {
                [void][W.Api]::SetForegroundWindow([IntPtr]$confirm.Current.NativeWindowHandle)
                Start-Sleep -Milliseconds 400
                [System.Windows.Forms.SendKeys]::SendWait("y")
                Write-Ok "已用键盘确认删除（Y）"
            }
        }
        else { Write-Bad "没有出现删除确认框" }
        Start-Sleep -Seconds 3

        $after = $null
        foreach ($t in $vault.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
            if ($t.Current.Name -match "共 \d+ 条") { $after = $t.Current.Name; break }
        }
        if ($after) { Write-Ok "删除后库状态：$after" } else { Write-Bad "删除后取不到库状态栏" }
        if ($after -match "共 1 条") { Write-Ok "删除成功（少了一条）" }
        elseif ($after -match "共 2 条") { Write-Bad "删除失败（条数没变）" }
    }
}

# ---------------------------------------------------------------- 收尾

Write-Step "退出"
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) { $p.Kill() } else { Write-Ok "已退出" }

if (Test-Path $logPath) {
    $fresh = Get-Content $logPath -Encoding UTF8 | Select-Object -Skip $logLinesAtStart
    $errors = ($fresh | Select-String -Pattern "界面线程异常|未处理异常" | Measure-Object).Count
    $frameFail = ($fresh | Select-String -Pattern "录制取帧失败" | Measure-Object).Count
    Write-Host "`n本次运行日志：界面异常 $errors 条；取帧失败 $frameFail 条"
    if ($errors -gt 0) { Write-Bad "日志里出现界面异常" }
    if ($frameFail -gt 0) { Write-Bad "日志里出现取帧失败" }
}

Write-Host "`n========================================"
Write-Host $(if ($script:failures -eq 0) { "截屏/录制冒烟：全部通过 ✓" } else { "截屏/录制冒烟：$script:failures 项失败" }) -ForegroundColor $(if ($script:failures -eq 0) { "Green" } else { "Red" })
exit $script:failures
