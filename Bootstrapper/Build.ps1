[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CatalogPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$IconPath = (Join-Path $PSScriptRoot '..\Launcher\Assets\ILauncher.ico')
)

$ErrorActionPreference = 'Stop'
$catalogFile = (Resolve-Path -LiteralPath $CatalogPath -ErrorAction Stop).Path
$iconFile = (Resolve-Path -LiteralPath $IconPath -ErrorAction Stop).Path
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$catalogBytes = [IO.File]::ReadAllBytes($catalogFile)
if ($catalogBytes.Length -gt 2MB) { throw 'Catalog exceeds the 2 MiB bootstrapper limit.' }
if ($catalogBytes.Length -ge 3 -and $catalogBytes[0] -eq 0xEF -and $catalogBytes[1] -eq 0xBB -and $catalogBytes[2] -eq 0xBF) {
    $catalogBytes = $catalogBytes[3..($catalogBytes.Length - 1)]
}
try { $catalogText = [Text.UTF8Encoding]::new($false, $true).GetString($catalogBytes) }
catch { throw 'Catalog must be valid UTF-8.' }
try { $catalog = $catalogText | ConvertFrom-Json -ErrorAction Stop }
catch { throw "Catalog is not valid JSON: $($_.Exception.Message)" }
if ($catalog.schemaVersion -ne 1 -or -not $catalog.version -or -not $catalog.source -or -not $catalog.runtime -or -not $catalog.apps) {
    throw 'Catalog must have schemaVersion 1, version, source, runtime, and apps.'
}
if (-not $catalog.source.serverUrl -or -not $catalog.source.apiUrl -or -not $catalog.source.repository -or -not $catalog.source.tagName) {
    throw 'Catalog source must have serverUrl, apiUrl, repository, and tagName.'
}
if (-not $catalog.runtime.version -or -not $catalog.runtime.asset -or $catalog.runtime.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$catalog.runtime.bytes -le 0) {
    throw 'Catalog runtime metadata is incomplete or invalid.'
}
if (@($catalog.apps).Count -lt 1) { throw 'Catalog must contain at least one app.' }
foreach ($app in $catalog.apps) {
    if (-not $app.id -or -not $app.name -or -not $app.executable -or -not $app.asset -or
        $app.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$app.bytes -le 0) {
        throw "App catalog entry is incomplete or invalid: $($app.id)"
    }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found. Install a .NET Framework 4.8 developer pack or build tools.'
}
$frameworkDir = Split-Path -Parent $csc
$appSource = Join-Path $PSScriptRoot 'App.cs'
if (-not (Test-Path -LiteralPath $appSource -PathType Leaf)) { throw "Bootstrapper source not found: $appSource" }
$coreDirectory = Join-Path $PSScriptRoot 'Core'
$coreSources = @()
if (Test-Path -LiteralPath $coreDirectory -PathType Container) {
    $coreSources = @(Get-ChildItem -LiteralPath $coreDirectory -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object FullName)
}
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('iluncher-bootstrap-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    $catalogResource = Join-Path $temporaryDirectory 'catalog.json'
    $hashResource = Join-Path $temporaryDirectory 'catalog.sha256'
    [IO.File]::WriteAllBytes($catalogResource, $catalogBytes)
    $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($catalogBytes)).Replace('-', '').ToLowerInvariant()
    [IO.File]::WriteAllText($hashResource, $sha, [Text.Encoding]::ASCII)

    $arguments = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-', '/langversion:5',
        ('/out:"' + $outputFullPath + '"'),
        ('/win32icon:"' + $iconFile + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Core.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Net.Http.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Drawing.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.IO.Compression.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.IO.Compression.FileSystem.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Windows.Forms.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Web.Extensions.dll') + '"'),
        ('/reference:"' + (Join-Path $frameworkDir 'System.Security.dll') + '"'),
        ('/resource:"' + $catalogResource + '",ILauncher.Bootstrapper.catalog.json'),
        ('/resource:"' + $hashResource + '",ILauncher.Bootstrapper.catalog.sha256')
    ) + @($appSource) + $coreSources
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "Bootstrapper compilation failed with csc exit code $LASTEXITCODE." }
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $tempFull = [IO.Path]::GetFullPath($temporaryDirectory)
    $expectedTemp = Join-Path $tempRoot ([IO.Path]::GetFileName($tempFull))
    if ($tempFull.Equals([IO.Path]::GetFullPath($expectedTemp), [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($tempFull) -match '^iluncher-bootstrap-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $tempFull -PathType Container)) {
        $tempItem = Get-Item -LiteralPath $tempFull -Force
        if (($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            foreach ($ownedName in @('catalog.json', 'catalog.sha256')) {
                $ownedPath = Join-Path $tempFull $ownedName
                if (Test-Path -LiteralPath $ownedPath -PathType Leaf) {
                    $ownedItem = Get-Item -LiteralPath $ownedPath -Force
                    if (($ownedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                        Remove-Item -LiteralPath $ownedPath -Force -ErrorAction SilentlyContinue
                    }
                }
            }
            Remove-Item -LiteralPath $tempFull -Force -ErrorAction SilentlyContinue
        }
    }
}
Write-Host "Built $outputFullPath ($([Math]::Round((Get-Item -LiteralPath $outputFullPath).Length / 1KB, 1)) KiB)"
