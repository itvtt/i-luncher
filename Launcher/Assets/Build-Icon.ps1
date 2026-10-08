# Render the i-luncher launcher mark from vector geometry at native icon sizes.
# The script has no package dependencies and can be run from any working directory.
Add-Type -AssemblyName PresentationCore,WindowsBase

$assetDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()

foreach ($size in $sizes) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 256.0),($size / 256.0))))
    $brushes = New-Object Windows.Media.BrushConverter

    # Dark indigo identity tile with a subtle edge for desktop backgrounds.
    $dc.DrawRoundedRectangle($brushes.ConvertFromString('#172B4D'), $null, (New-Object Windows.Rect(8,8,240,240)), 54, 54)

    # Four app panes: the grid reads clearly even at 16px.
    $dc.DrawRoundedRectangle($brushes.ConvertFromString('#7899FF'), $null, (New-Object Windows.Rect(40,40,78,78)), 19, 19)
    $dc.DrawRoundedRectangle($brushes.ConvertFromString('#A9B8FF'), $null, (New-Object Windows.Rect(126,40,78,78)), 19, 19)
    $dc.DrawRoundedRectangle($brushes.ConvertFromString('#315DED'), $null, (New-Object Windows.Rect(40,126,78,78)), 19, 19)
    $dc.DrawRoundedRectangle($brushes.ConvertFromString('#6382F4'), $null, (New-Object Windows.Rect(126,126,78,78)), 19, 19)

    # Launch arrow tucked into the lower-right pane.
    $arrow = [Windows.Media.StreamGeometry]::Parse('M139 160H166V145L204 183L166 221V206H139Z')
    $dc.DrawGeometry($brushes.ConvertFromString('#FFFFFF'), $null, $arrow)
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
    if ($size -eq 256) {
        [IO.File]::WriteAllBytes((Join-Path $assetDirectory 'ILauncher.png'), $bytes)
    }
    $memory.Dispose()
}

$icoPath = Join-Path $assetDirectory 'ILauncher.ico'
$stream = [IO.File]::Create($icoPath)
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()

Write-Output "Created ILauncher.svg-derived PNG and ICO in $assetDirectory"
