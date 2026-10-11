# 主题与设置界面验证：捕获深/浅两套主题的主界面与设置界面，并检查最小化后浮层是否收起
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$StartupWaitSeconds = 12
)
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Text;using System.Collections.Generic;
public class ThCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  delegate bool EnumProc(IntPtr h, IntPtr p);
  public static IntPtr Find(uint target, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) { var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
        if (t.ToString() == title) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found; }
  public static List<string> VisibleTitles(uint target) {
    var r = new List<string>();
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target && IsWindowVisible(h)) { var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
        if (t.Length > 0) r.Add(t.ToString()); }
      return true; }, IntPtr.Zero);
    return r; }
}
"@
function Find-Any($pid_, $name) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
  $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc, $nc)))
}
function Click-It($el) { if ($el) { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false } } return $false }
# 单选按钮（主题分段、左侧导航）要用 SelectionItem 模式
function Select-It($el) {
  if (-not $el) { return $false }
  try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true }
  catch { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { return $false } }
}
function Capture($hwnd, $out, $pad = 16) {
  $r = New-Object ThCap+RECT
  [ThCap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  if ($w -le 0 -or $h -le 0) { Write-Host "  窗口尺寸异常，跳过 $out"; return }
  $bmp = New-Object System.Drawing.Bitmap ($w + $pad*2), ($h + $pad*2)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left - $pad, $r.Top - $pad, 0, 0, (New-Object System.Drawing.Size ($w + $pad*2), ($h + $pad*2)))
  $g.Dispose()
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Host "  已保存 $out ($w x $h)"
}

$root = (Get-Location).Path
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList "--no-intro" -PassThru
Write-Host "已启动，等待 $StartupWaitSeconds 秒…"
Start-Sleep -Seconds $StartupWaitSeconds
$p.Refresh(); $main = $p.MainWindowHandle
[ThCap]::SetForegroundWindow($main) | Out-Null
Start-Sleep -Milliseconds 600
Write-Host "① 深色主题 - 主界面"; Capture $main (Join-Path $root "docs\theme-dark-main.png")

Click-It (Find-Any $p.Id "设置") | Out-Null
Start-Sleep -Seconds 3
$sw = [ThCap]::Find([uint32]$p.Id, "设置")
Write-Host "② 深色主题 - 设置界面"; if ($sw -ne [IntPtr]::Zero) { Capture $sw (Join-Path $root "docs\theme-dark-settings.png") }

Write-Host ("   切浅色：" + (Select-It (Find-Any $p.Id "浅色")))
Start-Sleep -Seconds 2
$sw = [ThCap]::Find([uint32]$p.Id, "设置")
Write-Host "③ 浅色主题 - 设置界面"; if ($sw -ne [IntPtr]::Zero) { Capture $sw (Join-Path $root "docs\theme-light-settings.png") }

Write-Host ("   切画面页：" + (Select-It (Find-Any $p.Id "画面")))
Start-Sleep -Seconds 1
$sw = [ThCap]::Find([uint32]$p.Id, "设置")
Write-Host "④ 浅色主题 - 画面页"; if ($sw -ne [IntPtr]::Zero) { Capture $sw (Join-Path $root "docs\theme-light-visual.png") }

[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 2
[ThCap]::SetForegroundWindow($main) | Out-Null
Start-Sleep -Milliseconds 600
Write-Host "⑤ 浅色主题 - 主界面"; Capture $main (Join-Path $root "docs\theme-light-main.png")

Write-Host "⑥ 最小化后仍显示的窗口（应只有主窗口本身，不应有控制条/状态浮层）"
Click-It (Find-Any $p.Id "最小化") | Out-Null
Start-Sleep -Seconds 3
$titles = [ThCap]::VisibleTitles([uint32]$p.Id)
Write-Host ("   可见窗口：" + (($titles | Where-Object { $_ -notmatch "IME|Cicero" }) -join " | "))

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue