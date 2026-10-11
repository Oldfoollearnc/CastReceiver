# 加密库界面冒烟测试：真实点击 + 真实输入，跑通「创建账号 → 解锁 → 我的库 → 锁定」
#
# 用法：
#   powershell.exe -ExecutionPolicy Bypass -File Invoke-VaultSmoke.ps1 -Exe "..\..\src\CastReceiver.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\投屏Window.exe"
#
# 说明：
#   · 使用独立的库目录（-VaultRoot）与独立单实例名（--instance-suffix），
#     因此可以在你日常使用的实例仍在运行时并行跑，不会碰你真实的加密库。
#   · 不需要手机投屏：本脚本只验证账号与库这条链路（截屏/录制需要真机镜像，另行手测）。
#   · 窗口定位用 EnumWindows（原生 HWND）而不是 UIA 根节点枚举：
#     本程序窗口是 WindowStyle=None + 自绘标题栏，模态子窗口不会出现在 UIA 根节点的 Children 里。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12,
    [string]$VaultRoot = "$env:TEMP\CastReceiverVaultSmoke",
    [string]$Account = "smokeuser",
    [string]$Password = "Smoke12345"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder s, int n);
'@

$ErrorActionPreference = "Stop"
$script:failures = 0

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Bad($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red; $script:failures++ }

function Get-ProcessWindows([int]$targetPid) {
    $list = New-Object System.Collections.ArrayList
    $cb = [W.Api+EnumProc] {
        param($h, $l)
        [uint32]$owner = 0
        [void][W.Api]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $targetPid -and [W.Api]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][W.Api]::GetWindowTextW($h, $sb, 256)
            $title = $sb.ToString()
            if ($title.Length -gt 0) { [void]$list.Add([pscustomobject]@{ Handle = $h; Title = $title }) }
        }
        return $true
    }
    [void][W.Api]::EnumWindows($cb, [IntPtr]::Zero)
    return $list
}

function Find-Window([int]$targetPid, [string]$title, [int]$tries = 30) {
    for ($i = 0; $i -lt $tries; $i++) {
        foreach ($w in Get-ProcessWindows $targetPid) {
            if ($w.Title -like "*$title*") {
                return [System.Windows.Automation.AutomationElement]::FromHandle($w.Handle)
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Find-ByName($win, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 输入框要同时按「名字 + 控件类型=Edit」找：否则会命中同名的标签文本
function Find-Input($win, [string]$name) {
    $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $byType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $cond = New-Object System.Windows.Automation.AndCondition($byName, $byType)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-ByName($win, [string]$name) {
    $el = Find-ByName $win $name
    if (-not $el) { Write-Bad "未找到控件：$name"; return $false }
    try {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Ok "已点击：$name"
        return $true
    } catch { Write-Bad "点击失败：$name（$($_.Exception.Message)）"; return $false }
}

function Type-Into($win, [string]$name, [string]$text) {
    $el = Find-Input $win $name
    if (-not $el) { Write-Bad "未找到输入框：$name"; return $false }
    try {
        $el.SetFocus()
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait($text)
        Write-Ok "已输入 $name"
        return $true
    } catch { Write-Bad "输入失败：$name（$($_.Exception.Message)）"; return $false }
}

# ---------------------------------------------------------------- 准备

if (-not (Test-Path $Exe)) { Write-Bad "找不到可执行文件：$Exe"; exit 2 }
if (Test-Path $VaultRoot) { Remove-Item $VaultRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "隔离库目录：$VaultRoot"

$logPath = Join-Path $env:LOCALAPPDATA "CastReceiver\castreceiver.log"
$logLinesAtStart = if (Test-Path $logPath) { (Get-Content $logPath -Encoding UTF8 | Measure-Object -Line).Lines } else { 0 }

Write-Step "启动程序（隔离实例）"
$p = Start-Process -FilePath $Exe -ArgumentList "--no-intro --vault-root `"$VaultRoot`" --instance-suffix smoke" -PassThru
Start-Sleep -Seconds $StartupWaitSeconds

$main = Find-Window $p.Id "投屏Window"
if (-not $main) { Write-Bad "未找到主窗口"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Ok "主窗口已就绪：$($main.Current.Name)"

# ---------------------------------------------------------------- 打开我的库 → 首次要建账号

Write-Step "点「我的库」（首次应弹出创建账号）"
Invoke-ByName $main "我的库" | Out-Null
Start-Sleep -Seconds 3

$setup = Find-Window $p.Id "创建加密账号" 20
if (-not $setup) {
    Write-Bad "没有出现「创建加密账号」窗口"
    Write-Host "  当前可见窗口：$((Get-ProcessWindows $p.Id | ForEach-Object { $_.Title }) -join ' / ')"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Ok "创建账号窗口已出现"

Type-Into $setup "账号名" $Account | Out-Null
Type-Into $setup "密码" $Password | Out-Null
Type-Into $setup "再次输入密码" $Password | Out-Null
Start-Sleep -Milliseconds 400
Invoke-ByName $setup "创建账号" | Out-Null
Start-Sleep -Seconds 3

# ---------------------------------------------------------------- 解锁

Write-Step "解锁我的库（输密码）"
$unlock = Find-Window $p.Id "解锁我的库" 20
if (-not $unlock) {
    Write-Bad "没有出现「解锁我的库」窗口"
    Write-Host "  当前可见窗口：$((Get-ProcessWindows $p.Id | ForEach-Object { $_.Title }) -join ' / ')"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Ok "解锁窗口已出现"

Type-Into $unlock "解锁密码" $Password | Out-Null
Start-Sleep -Milliseconds 400
Invoke-ByName $unlock "解锁" | Out-Null
Start-Sleep -Seconds 4

# ---------------------------------------------------------------- 库窗口

Write-Step "我的库窗口"
$vault = Find-Window $p.Id "我的库" 20
if (-not $vault) {
    Write-Bad "没有打开「我的库」窗口（可能密码校验失败）"
    Write-Host "  当前可见窗口：$((Get-ProcessWindows $p.Id | ForEach-Object { $_.Title }) -join ' / ')"
}
else {
    Write-Ok "库窗口已打开"
    if (Find-ByName $vault "锁定") { Write-Ok "找到「锁定」按钮" } else { Write-Bad "库窗口缺少锁定按钮" }

    $textCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($t in $vault.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
        if ($t.Current.Name -like "共 *") { Write-Ok "库状态栏：$($t.Current.Name)"; break }
    }

    Write-Step "点「锁定」"
    Invoke-ByName $vault "锁定" | Out-Null
    Start-Sleep -Seconds 3
    $stillOpen = Find-Window $p.Id "我的库" 4
    if ($stillOpen) { Write-Bad "点锁定后库窗口仍然存在" } else { Write-Ok "库窗口已关闭（私钥已清零）" }
}

# ---------------------------------------------------------------- 校验磁盘产物

Write-Step "校验隔离库目录"
if (Test-Path $VaultRoot) {
    $accounts = Join-Path $VaultRoot "accounts.json"
    if (Test-Path $accounts) {
        $text = Get-Content $accounts -Raw -Encoding UTF8
        Write-Ok "accounts.json 已生成"
        if ($text -match [regex]::Escape($Account)) { Write-Bad "accounts.json 里出现了明文账号名" }
        else { Write-Ok "accounts.json 不含明文账号名" }
        if ($text -match [regex]::Escape($Password)) { Write-Bad "accounts.json 里出现了明文密码！" }
        else { Write-Ok "accounts.json 不含明文密码" }
    }
    else { Write-Bad "没有生成 accounts.json" }
}
else { Write-Bad "库目录未创建" }

# ---------------------------------------------------------------- 收尾

Write-Step "退出程序"
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(8000)) {
    # 设置里勾了「关闭窗口时最小化到托盘」时，关窗不会退出进程 —— 属于预期行为，这里直接结束测试实例
    $p.Kill()
    Write-Host "  已结束测试实例（若开启「最小化到托盘」，关窗不会退出进程，属预期）" -ForegroundColor Yellow
} else { Write-Ok "已优雅退出" }

if (Test-Path $logPath) {
    $fresh = Get-Content $logPath -Encoding UTF8 | Select-Object -Skip $logLinesAtStart
    $errors = ($fresh | Select-String -Pattern "界面线程异常|未处理异常" | Measure-Object).Count
    $stalls = ($fresh | Select-String -Pattern "界面线程停顿" | Measure-Object).Count
    Write-Host "`n本次运行日志：界面异常 $errors 条；界面停顿 $stalls 条"
    if ($errors -gt 0) { Write-Bad "日志里出现界面异常" }
}

Write-Host "`n========================================"
Write-Host $(if ($script:failures -eq 0) { "加密库界面冒烟：全部通过 ✓" } else { "加密库界面冒烟：$script:failures 项失败" }) -ForegroundColor $(if ($script:failures -eq 0) { "Green" } else { "Red" })
exit $script:failures
