# 截取启动闪屏（原生窗口，类名 CastReceiverNativeSplash）
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$CaptureAtMs = 1200
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public class SplashCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  delegate bool EnumProc(IntPtr h, IntPtr p);
  public static IntPtr Find(uint target, string cls) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) { var c = new StringBuilder(128); GetClassName(h, c, 128);
        if (c.ToString() == cls) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found; }
}
"@

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds $CaptureAtMs

$hwnd = [SplashCap]::Find([uint32]$p.Id, "CastReceiverNativeSplash")
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "未找到闪屏窗口（可能已经关闭）" -ForegroundColor Yellow; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }

$r = New-Object SplashCap+RECT
[SplashCap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[SplashCap]::PrintWindow($hwnd, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "已保存闪屏截图：$Out ($w x $h)" -ForegroundColor Green
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue