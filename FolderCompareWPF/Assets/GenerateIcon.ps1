$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$iconPath = Join-Path $PSScriptRoot 'FolderCompare.ico'
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 64.0), ($size / 64.0)))
    $dc.DrawRoundedRectangle([System.Windows.Media.BrushConverter]::new().ConvertFromString('#0C1422'), $null, [System.Windows.Rect]::new(0, 0, 64, 64), 12, 12)
    $dc.DrawGeometry([System.Windows.Media.BrushConverter]::new().ConvertFromString('#619FFF'), $null, [System.Windows.Media.Geometry]::Parse('M 7,15 L 20,15 25,21 35,21 35,43 7,43 Z'))
    $dc.DrawGeometry([System.Windows.Media.BrushConverter]::new().ConvertFromString('#AF8AFF'), $null, [System.Windows.Media.Geometry]::Parse('M 29,24 L 41,24 46,30 57,30 57,51 29,51 Z'))
    $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, [System.Windows.Media.Geometry]::Parse('M 16,31 L 38,31 38,26 49,36 38,46 38,41 16,41 Z'))
    $dc.Pop()
    $dc.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'FolderCompare.png'), $stream.ToArray()) }
    $stream.Dispose()
}
$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $output.Dispose() }
