# Render the vector app mark at each native icon size; no font or raster source required.
Add-Type -AssemblyName PresentationCore,WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../SwiftCopy.App/Assets'))
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
 $visual = New-Object Windows.Media.DrawingVisual
 $dc = $visual.RenderOpen()
 $dc.PushTransform((New-Object Windows.Media.ScaleTransform(($size/256.0),($size/256.0))))
 $converter = New-Object Windows.Media.BrushConverter
 $dc.DrawRoundedRectangle($converter.ConvertFromString('#172B4D'),$null,(New-Object Windows.Rect(8,8,240,240)),54,54)
 $dc.DrawRoundedRectangle($converter.ConvertFromString('#7899FF'),$null,(New-Object Windows.Rect(44,42,110,142)),16,16)
 $dc.DrawRoundedRectangle($converter.ConvertFromString('#FFFFFF'),$null,(New-Object Windows.Rect(88,72,122,142)),18,18)
 $dc.DrawGeometry($converter.ConvertFromString('#315DED'),$null,[Windows.Media.Geometry]::Parse('M44 130H137V110L180 150L137 190V170H44Z'))
 $dc.Pop(); $dc.Close()
 $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $memory = New-Object IO.MemoryStream
 $encoder.Save($memory)
 $frames += ,$memory.ToArray()
 [IO.File]::WriteAllBytes((Join-Path $assetDirectory "SwiftCopy-$size.png"),$memory.ToArray())
 $memory.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assetDirectory 'SwiftCopy.ico'))
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index=0; $index -lt $sizes.Count; $index++) {
 $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
 $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
 $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
 $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
 $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
Write-Output "Created SVG-derived icon frames and ICO: $assetDirectory"
