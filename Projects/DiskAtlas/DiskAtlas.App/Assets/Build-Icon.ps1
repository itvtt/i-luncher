# Deterministically renders the DiskAtlas vector mark at native Windows icon sizes.
Add-Type -AssemblyName PresentationCore,WindowsBase

$assetDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
$converter = New-Object Windows.Media.BrushConverter

foreach ($size in $sizes) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 256.0),($size / 256.0))))
    $dc.DrawRoundedRectangle($converter.ConvertFromString('#151B2B'), $null, (New-Object Windows.Rect(8,8,240,240)), 54, 54)
    $ringPen = New-Object Windows.Media.Pen($converter.ConvertFromString('#45D9CB'),22)
    $dc.DrawEllipse($null, $ringPen, (New-Object Windows.Point(128,128)), 82, 82)
    $arc = [Windows.Media.StreamGeometry]::Parse('M128 46A82 82 0 0 1 202 163L166 147A43 43 0 0 0 128 85Z')
    $dc.DrawGeometry($converter.ConvertFromString('#8976F5'), $null, $arc)
    $hubPen = New-Object Windows.Media.Pen($converter.ConvertFromString('#E5FFFC'),10)
    $dc.DrawEllipse($converter.ConvertFromString('#111927'), $hubPen, (New-Object Windows.Point(128,128)), 28, 28)
    $linePen = New-Object Windows.Media.Pen($converter.ConvertFromString('#EAFBFA'),12)
    $linePen.StartLineCap = $linePen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $dc.DrawLine($linePen, (New-Object Windows.Point(69,184)), (New-Object Windows.Point(187,184)))
    $smallPen = New-Object Windows.Media.Pen($converter.ConvertFromString('#8976F5'),9)
    $smallPen.StartLineCap = $smallPen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $dc.DrawLine($smallPen, (New-Object Windows.Point(82,205)), (New-Object Windows.Point(174,205)))
    $dc.Pop()
    $dc.Close()

    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object IO.MemoryStream
    $encoder.Save($memory)
    $bytes = $memory.ToArray()
    $frames += ,$bytes
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetDirectory 'DiskAtlas.png'), $bytes) }
    $memory.Dispose()
}

$stream = [IO.File]::Create((Join-Path $assetDirectory 'DiskAtlas.ico'))
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
Write-Output "Created DiskAtlas.png and DiskAtlas.ico"
