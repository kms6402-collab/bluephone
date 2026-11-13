# 파란전화기 앱 아이콘 생성기 (WPF 렌더링 → 다중 해상도 ICO)
param([string]$Out = "$PSScriptRoot\..\src\app.ico")

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$handset = 'M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z'

function Render([int]$s) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $m = if ($s -le 20) { 0.5 } else { [Math]::Round($s * 0.04, 1) }
    $w = $s - 2 * $m
    $rect = New-Object System.Windows.Rect($m, $m, $w, $w)
    $grad = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(0x3A, 0x74, 0xFF),
        [System.Windows.Media.Color]::FromRgb(0x14, 0x46, 0xD8), 90.0)
    $r = $w * 0.24
    $dc.DrawRoundedRectangle($grad, $null, $rect, $r, $r)
    # 위쪽 아주 옅은 하이라이트 (평면감 완화)
    if ($s -ge 32) {
        $hl = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(28, 255, 255, 255))
        $clip = New-Object System.Windows.Media.RectangleGeometry($rect, $r, $r)
        $dc.PushClip($clip)
        $dc.DrawRectangle($hl, $null, (New-Object System.Windows.Rect($m, $m, $w, $w * 0.46)))
        $dc.Pop()
    }
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
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = Render $s
    if ($s -ge 128) {
        $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
        $ms = New-Object System.IO.MemoryStream
        $enc.Save($ms)
        $images += , @($s, $ms.ToArray())
    } else {
        # 32bpp DIB (BGRA, bottom-up) + AND mask : 구형 API 호환
        $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($bmp, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $stride = $s * 4
        $px = New-Object byte[] ($stride * $s)
        $conv.CopyPixels($px, $stride, 0)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write([int]($stride * $s + $maskStride * $s))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $stride, $stride) }
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
        $images += , @($s, $ms.ToArray())
    }
}

$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | ForEach-Object { Join-Path $_ (Split-Path $Out -Leaf) }))
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $w.Write($img[1]) }
$w.Close()

# 미리보기 PNG
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create((Render 256)))
$pf = [System.IO.File]::Create("$PSScriptRoot\icon-preview.png"); $enc.Save($pf); $pf.Close()
"ok"
