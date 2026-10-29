$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$target = Join-Path $PSScriptRoot '..\DomainDesk\Assets'
$target = [IO.Path]::GetFullPath($target)
function Render-Logo([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 512.0), ($size / 512.0)))
    $brush = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.ColorConverter]::ConvertFromString('#123B3D')), ([Windows.Media.ColorConverter]::ConvertFromString('#08282D')), 45
    $context.DrawRoundedRectangle($brush, $null, (New-Object Windows.Rect 8,8,496,496),112,112)
    $mint = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#6EE1BC'))
    $white = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#F0FFF9'))
    $pen = New-Object Windows.Media.Pen $mint,32
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $context.DrawGeometry($null,$pen,[Windows.Media.Geometry]::Parse('M136 130H333L400 197V316L280 400H136Z'))
    $pen2 = New-Object Windows.Media.Pen $mint,24
    $pen2.LineJoin = [Windows.Media.PenLineJoin]::Round
    $context.DrawGeometry($null,$pen2,[Windows.Media.Geometry]::Parse('M136 265H272L343 197 M272 265L343 336'))
    foreach($r in @(@(110,239,52,52),@(248,241,48,48),@(319,173,48,48),@(319,312,48,48))) {
        $context.DrawRoundedRectangle($white,$null,(New-Object Windows.Rect $r[0],$r[1],$r[2],$r[3]),12,12)
    }
    $context.Pop(); $context.Close()
    $image = New-Object Windows.Media.Imaging.RenderTargetBitmap $size,$size,96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
    $memory = New-Object IO.MemoryStream
    $encoder.Save($memory)
    return ,$memory.ToArray()
}
[IO.File]::WriteAllBytes((Join-Path $target 'logo.png'),(Render-Logo 512))
$sizes = @(16,20,24,32,48,64,128,256)
$frames = @($sizes | ForEach-Object { ,(Render-Logo $_) })
$stream = [IO.File]::Create((Join-Path $target 'DomainDesk.ico'))
$writer = New-Object IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension = if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame)}
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Host 'Generated logo.png and eight-size DomainDesk.ico from editable vector geometry.'
