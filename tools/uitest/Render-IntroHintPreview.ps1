# 离屏渲染「单击鼠标跳过」提示条的真实样式（不启动程序）
# 铺在深/中/浅三种底上各画一遍，方便判断在视频上读不读得出来、跟主界面搭不搭。
# 用法：powershell.exe -ExecutionPolicy Bypass -File Render-IntroHintPreview.ps1 [-Out docs\ui-intro-hint.png]
param(
    [string]$Out = "docs\ui-intro-hint.png",
    [string]$Theme = "Light"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$themeFile = Join-Path $repo ("src\CastReceiver.App\Themes\" + $Theme + ".xaml")
$stylesFile = Join-Path $repo "src\CastReceiver.App\Themes\Styles.xaml"
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repo $Out }
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null

function Load-Dict([string]$path) {
    $reader = New-Object System.Xml.XmlNodeReader ([xml](Get-Content $path -Raw -Encoding UTF8))
    return [System.Windows.Markup.XamlReader]::Load($reader)
}

$merged = New-Object System.Windows.ResourceDictionary
$merged.MergedDictionaries.Add((Load-Dict $themeFile))
$merged.MergedDictionaries.Add((Load-Dict $stylesFile))

# 提示条本体（跟 IntroHintWindow.xaml 里那段保持一致）
$pillXaml = @'
<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Background="{DynamicResource B.OverlayBgSoft}" BorderBrush="{DynamicResource B.Line}" BorderThickness="1"
        CornerRadius="999" Padding="14,7">
    <StackPanel Orientation="Horizontal">
        <Ellipse Width="6" Height="6" Fill="{DynamicResource B.Accent}" VerticalAlignment="Center" />
        <TextBlock Text="单击鼠标跳过" FontSize="12.5" Foreground="{DynamicResource B.Text}" Margin="8,0,0,0"
                   VerticalAlignment="Center" />
    </StackPanel>
</Border>
'@
$reader = New-Object System.Xml.XmlNodeReader ([xml]$pillXaml)
$pill = [System.Windows.Markup.XamlReader]::Load($reader)
$pill.Resources = $merged
# 字体跟随主题（和主界面一致）
$uiFont = $merged["F.Ui"]
if ($uiFont) { [System.Windows.Documents.TextElement]::SetFontFamily($pill, $uiFont) }
[System.Windows.Documents.TextElement]::SetFontFamily($pill, ($(if ($uiFont) { $uiFont } else { "Microsoft YaHei UI" })))

# 三种底 + 标题
$root = New-Object System.Windows.Controls.Canvas
$root.Width = 720
$root.Height = 300
$root.Background = [System.Windows.Media.Brushes]::Transparent

$swatches = @(
    @{ Y = 10;  Color = "#121316"; Label = "铺在深色画面上" },
    @{ Y = 106; Color = "#6E7278"; Label = "铺在中灰画面上" },
    @{ Y = 202; Color = "#E9EAEC"; Label = "铺在浅色画面上" }
)
foreach ($s in $swatches) {
    $rect = New-Object System.Windows.Shapes.Rectangle
    $rect.Width = 700; $rect.Height = 86
    $rect.Fill = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($s.Color))
    [System.Windows.Controls.Canvas]::SetLeft($rect, 10)
    [System.Windows.Controls.Canvas]::SetTop($rect, $s.Y)
    [void]$root.Children.Add($rect)

    $label = New-Object System.Windows.Controls.TextBlock
    $label.Text = $s.Label
    $label.FontSize = 11
    $label.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#8A8F98"))
    [System.Windows.Controls.Canvas]::SetLeft($label, 26)
    [System.Windows.Controls.Canvas]::SetTop($label, ($s.Y + 8))
    [void]$root.Children.Add($label)

    $copy = [System.Windows.Markup.XamlReader]::Parse([System.Windows.Markup.XamlWriter]::Save($pill))
    $copy.Resources = $merged
    if ($uiFont) { [System.Windows.Documents.TextElement]::SetFontFamily($copy, $uiFont) }
    [System.Windows.Controls.Canvas]::SetLeft($copy, 300)
    [System.Windows.Controls.Canvas]::SetTop($copy, ($s.Y + 42))
    [void]$root.Children.Add($copy)
}

$root.Measure((New-Object System.Windows.Size(720, 300)))
$root.Arrange((New-Object System.Windows.Rect(0, 0, 720, 300)))
$root.UpdateLayout()

$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(720, 300, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($root)
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$fs = [System.IO.File]::Create($outPath)
$encoder.Save($fs)
$fs.Close()
Write-Host "已生成 $outPath（主题 $Theme）"
