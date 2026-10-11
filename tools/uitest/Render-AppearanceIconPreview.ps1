# 离线渲染顶栏「外观」按钮的真实样式：不启动应用、不抓屏，因此不受窗口遮挡影响。
# 用途：肉眼确认深色主题下月亮图标可见。
# 背景：旧月亮路径的内弧半径（6.4）小于弦长的一半（8），按 SVG 规则被等比放大到 8 后
#       与外弧完全重合、图形面积归零 —— 深色主题下这个按钮就是"看不见"的。
# 用法：powershell -ExecutionPolicy Bypass -File tools\uitest\Render-AppearanceIconPreview.ps1
param(
    [string]$Out = "docs\verify-appearance-icon-render.png",
    [int]$Zoom = 3
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$themesDir = Join-Path $root "src\CastReceiver.App\Themes"

function Load-Dict([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try { return [System.Windows.Markup.XamlReader]::Load($fs) } finally { $fs.Dispose() }
}

# 与 MainWindow.xaml / MainWindow.xaml.cs 里的常量保持一致
$moonOld = "M12,2 A8,8 0 1 0 12,18 A6.4,6.4 0 0 1 12,2 Z"          # 修复前（退化成空图形）
$moonFixed = "M12,2 A8,8 0 1 0 12,18 A8.9,8.9 0 0 1 12,2 Z"        # 修复后
$sun = "M10,6.2 A3.8,3.8 0 1 1 9.99,6.2 Z M10,1.4 V3.2 M10,16.8 V18.6 M1.4,10 H3.2 M16.8,10 H18.6 " +
       "M3.9,3.9 L5.2,5.2 M14.8,14.8 L16.1,16.1 M16.1,3.9 L14.8,5.2 M5.2,14.8 L3.9,16.1"

function New-AppearanceButton($styles, [string]$data, [bool]$stroke, $fillKey) {
    $b = New-Object System.Windows.Controls.Button
    $b.Style = $styles["S.BtnIcon"]
    $p = New-Object System.Windows.Shapes.Path
    $p.Width = 15; $p.Height = 15
    $p.Stretch = [System.Windows.Media.Stretch]::Uniform
    $p.Data = [System.Windows.Media.Geometry]::Parse($data)
    if ($stroke) {
        $p.SetResourceReference([System.Windows.Shapes.Shape]::StrokeProperty, $fillKey)
        $p.StrokeThickness = 1.5
        $p.StrokeStartLineCap = [System.Windows.Media.PenLineCap]::Round
        $p.StrokeEndLineCap = [System.Windows.Media.PenLineCap]::Round
    } else {
        $p.SetResourceReference([System.Windows.Shapes.Shape]::FillProperty, $fillKey)
    }
    $b.Content = $p
    return $b
}

function New-GlyphButton($styles, [string]$glyph) {
    $b = New-Object System.Windows.Controls.Button
    $b.Style = $styles["S.BtnIcon"]
    $b.Content = $glyph
    return $b
}

# 一整行 = 一个主题下的顶栏右侧（外观 + 最小化 + 最大化 + 关闭），与 MainWindow 顶栏一致
function New-Row([string]$theme, [string]$title, [string]$data, [bool]$stroke) {
    $row = New-Object System.Windows.Controls.StackPanel
    $row.Margin = "0,0,0,10"

    $label = New-Object System.Windows.Controls.TextBlock
    $label.Text = $title
    $label.Foreground = [System.Windows.Media.Brushes]::Gainsboro
    $label.FontSize = 12
    $label.Margin = "2,0,0,4"
    $row.Children.Add($label) | Out-Null

    $bar = New-Object System.Windows.Controls.Border
    $bar.Resources.MergedDictionaries.Add((Load-Dict (Join-Path $themesDir "$theme.xaml")))
    $bar.Resources.MergedDictionaries.Add((Load-Dict (Join-Path $themesDir "Styles.xaml")))
    $bar.SetResourceReference([System.Windows.Controls.Border]::BackgroundProperty, "B.Bg")
    $bar.SetResourceReference([System.Windows.Controls.Border]::BorderBrushProperty, "B.LineSoft")
    $bar.BorderThickness = "0,0,0,1"
    $bar.Padding = "0,6,10,6"

    $panel = New-Object System.Windows.Controls.StackPanel
    $panel.Orientation = [System.Windows.Controls.Orientation]::Horizontal
    $panel.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Right
    $panel.Children.Add((New-AppearanceButton $bar.Resources $data $stroke "B.TextDim")) | Out-Null
    foreach ($g in @([char]0xE921, [char]0xE922, [char]0xE8BB)) {
        $panel.Children.Add((New-GlyphButton $bar.Resources ([string]$g))) | Out-Null
    }
    $bar.Child = $panel
    $row.Children.Add($bar) | Out-Null
    return $row
}

$page = New-Object System.Windows.Controls.StackPanel
$page.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x20, 0x21, 0x24))
$page.Width = 460
$page.Margin = "0"
$inner = New-Object System.Windows.Controls.StackPanel
$inner.Margin = "14"
$page.Children.Add($inner) | Out-Null

$inner.Children.Add((New-Row "Dark" "① 深色主题 · 修复前的月亮路径（面积归零 → 空白，看不出有按钮）" $moonOld $false)) | Out-Null
$inner.Children.Add((New-Row "Dark" "② 深色主题 · 修复后（月亮可见）" $moonFixed $false)) | Out-Null
$inner.Children.Add((New-Row "Light" "③ 浅色主题 · 修复后（太阳，描边绘制）" $sun $true)) | Out-Null

$w = $page.Width
$page.Measure([System.Windows.Size]::new($w, [double]::PositiveInfinity))
$h = [Math]::Ceiling($page.DesiredSize.Height)
$page.Arrange([System.Windows.Rect]::new(0, 0, $w, $h))
$page.UpdateLayout()

$scale = $Zoom
$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap -ArgumentList @(
    [int]($w * $scale), [int]($h * $scale), [double](96 * $scale), [double](96 * $scale),
    [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($page)

$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb)) | Out-Null
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$dir = Split-Path $outPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($outPath)
try { $encoder.Save($fs) } finally { $fs.Dispose() }
Write-Host ("已保存 {0} ({1}x{2} @{3}x)" -f $outPath, [int]($w * $scale), [int]($h * $scale), $scale)
