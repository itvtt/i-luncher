[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw 'Version is invalid.' }
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$onlineDirectory = Join-Path $packageRoot 'online'
$offlineBundle = Join-Path $packageRoot 'i-luncher'
$zipName = "i-luncher-$Version-win-x64.zip"
$zipPath = Join-Path $packageRoot $zipName
$checksumPath = "$zipPath.sha256"
$sidecarPath = Join-Path $onlineDirectory 'i-luncher-updates.json'
if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $offlineBundle -PathType Container) -or
    -not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw 'Online sidecar, offline bundle, or distribution ZIP is missing.'
}
$sidecar = Get-Content -LiteralPath $sidecarPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
if ($sidecar.schemaVersion -ne 1 -or $sidecar.starterExecutable -ne 'i-luncher-start.exe' -or
    $sidecar.starterSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'The update sidecar does not contain a supported starter executable binding.'
}
$onlineStarterPath = Join-Path $onlineDirectory $sidecar.starterExecutable
if (-not (Test-Path -LiteralPath $onlineStarterPath -PathType Leaf) -or
    -not ((Get-FileHash -Algorithm SHA256 -LiteralPath $onlineStarterPath).Hash.Equals($sidecar.starterSha256, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'The online starter executable does not match its sidecar digest.'
}
[IO.File]::Copy($onlineStarterPath, (Join-Path $offlineBundle $sidecar.starterExecutable), $true)
[IO.File]::Copy($sidecarPath, (Join-Path $offlineBundle 'i-luncher-updates.json'), $true)

$manifestPath = Join-Path $offlineBundle 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
$manifestFiles = @($manifest.files | Where-Object { $_.path -ne $sidecar.starterExecutable -and $_.path -ne 'i-luncher-updates.json' })
foreach ($relativePath in @($sidecar.starterExecutable, 'i-luncher-updates.json')) {
    $filePath = Join-Path $offlineBundle $relativePath
    $manifestFiles += [pscustomobject]@{
        path = $relativePath
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $filePath).Length
    }
}
$manifest.files = @($manifestFiles | Sort-Object path)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 30), [Text.UTF8Encoding]::new($false))

$stagedZip = Join-Path $packageRoot ('.' + $zipName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp.zip')
try {
    Compress-Archive -LiteralPath $offlineBundle -DestinationPath $stagedZip -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $stagedZip -PathType Leaf)) { throw 'Could not rebuild offline ZIP with the update starter.' }
    Move-Item -LiteralPath $stagedZip -Destination $zipPath -Force
}
finally {
    if (Test-Path -LiteralPath $stagedZip -PathType Leaf) { Remove-Item -LiteralPath $stagedZip -Force }
}
$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, "$zipHash  $zipName`n", [Text.UTF8Encoding]::new($false))
Write-Host "Repacked offline distribution with starter and update catalog: $zipPath"
Write-Host "SHA-256: $zipHash"
