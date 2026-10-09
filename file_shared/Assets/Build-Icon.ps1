# Rebuilds the transparent multi-size ICO from the original generated PNG without changing its composition.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assetDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$source = [Windows.Media.Imaging.BitmapImage]::new()
$source.BeginInit()
$source.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$source.UriSource = [Uri]::new((Join-Path $assetDirectory 'FileShared.png'))
$source.EndInit()
$source.Freeze()
$sizes = @(16,24,32,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
    $scale = [Windows.Media.ScaleTransform]::new(($size / [double]$source.PixelWidth), ($size / [double]$source.PixelHeight))
    $scaled = [Windows.Media.Imaging.TransformedBitmap]::new($source, $scale)
    $scaled.Freeze()
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $memory = [IO.MemoryStream]::new()
    $encoder.Save($memory)
    $frames += ,$memory.ToArray()
    $memory.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assetDirectory 'FileShared.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Created FileShared.ico ($($sizes -join ', ') px)"
