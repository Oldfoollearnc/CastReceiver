# 截取正在运行的应用窗口（用于确认界面效果）。
# 用法：powershell.exe -ExecutionPolicy Bypass -File Capture-Window.ps1 -Exe "路径\投屏Window.exe" -Out "out.png" -WaitSeconds 35
param(
    [string]$ExtraArgs = "",
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitSeconds = 35
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
}
"@

$p = if ($ExtraArgs) { Start-Process -FilePath $Exe -ArgumentList $ExtraArgs -PassThru } else { Start-Process -FilePath $Exe -PassThru }
Write-Host "已启动，等待界面稳定 $WaitSeconds 秒…"
Start-Sleep -Seconds $WaitSeconds
if ($p.HasExited) { Write-Host "进程已退出" -ForegroundColor Red; exit 1 }

$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Host "未取到窗口句柄" -ForegroundColor Red; exit 1 }

[WinCap]::ShowWindow($h, 9) | Out-Null      # SW_RESTORE
[WinCap]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
Write-Host ("窗口位置 {0},{1} 尺寸 {2}x{3}" -f $r.Left, $r.Top, $w, $hgt)

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
# PrintWindow 直接渲染窗口内容，不受被其它窗口遮挡影响
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { Write-Host "PrintWindow 返回失败，改用屏幕抓取" -ForegroundColor Yellow; $g2 = [System.Drawing.Graphics]::FromImage($bmp); $g2.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size); $g2.Dispose() }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "已保存：" $Out -ForegroundColor Green

# 优雅关闭
$p.CloseMainWindow() | Out-Null
for ($i = 0; $i -lt 20 -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Write-Host "强杀" -ForegroundColor Yellow }
else { Write-Host "已优雅退出" -ForegroundColor Green }