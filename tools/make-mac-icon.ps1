# 파란전화기 macOS 앱 아이콘 (1024×1024 PNG) — macOS 아이콘 규격: 824px 둥근 사각형 + 여백
param([string]$Out = "$PSScriptRoot\..\mac\packaging\AppIcon-1024.png")

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$handset = 'M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z'
$s = 1024
$w = 824
$m = ($s - $w) / 2.0
$r = $w * 0.2237

$dv = New-Object System.Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
$rect = New-Object System.Windows.Rect($m, $m, $w, $w)
# 아래쪽 그림자
$shadow = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(40, 0, 0, 0))
$dc.DrawRoundedRectangle($shadow, $null, (New-Object System.Windows.Rect($m, ($m + 10), $w, $w)), $r, $r)
$grad = New-Object System.Windows.Media.LinearGradientBrush(
    [System.Windows.Media.Color]::FromRgb(0x3A, 0x74, 0xFF),
    [System.Windows.Media.Color]::FromRgb(0x14, 0x46, 0xD8), 90.0)
$dc.DrawRoundedRectangle($grad, $null, $rect, $r, $r)
$hl = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(28, 255, 255, 255))
$dc.PushClip((New-Object System.Windows.Media.RectangleGeometry($rect, $r, $r)))
$dc.DrawRectangle($hl, $null, (New-Object System.Windows.Rect($m, $m, $w, ($w * 0.46))))
$dc.Pop()
$g = [System.Windows.Media.Geometry]::Parse($handset).Clone()
$scale = ($w * 0.56) / 18.0
$tg = New-Object System.Windows.Media.TransformGroup
$tg.Children.Add((New-Object System.Windows.Media.TranslateTransform(-12, -12)))
$tg.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
$tg.Children.Add((New-Object System.Windows.Media.TranslateTransform(($s / 2.0), ($s / 2.0))))
$g.Transform = $tg
$dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $g)
$dc.Close()

$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($s, $s, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($dv)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$enc.Save($fs)
$fs.Close()
Write-Output "wrote $Out"
