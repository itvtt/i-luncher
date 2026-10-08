[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\\..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw ".NET Framework C# compiler is missing: $compiler" }

$runId = [string]$env:GITHUB_RUN_ID
$attempt = [string]$env:GITHUB_RUN_ATTEMPT
if ($runId -notmatch '^[0-9]+$' -or $attempt -notmatch '^[0-9]+$' -or
    [string]::IsNullOrWhiteSpace([string]$env:RUNNER_TEMP)) {
    throw 'Bootstrapper checks require an Actions runner temporary directory.'
}
$checksDirectory = [IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP "i-luncher-bootstrapper-checks-$runId-$attempt"))
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $checksDirectory.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $checksDirectory)) {
    throw 'Bootstrapper checks temporary path is unsafe or already exists.'
}
[IO.Directory]::CreateDirectory($checksDirectory) | Out-Null
$outputPath = Join-Path $checksDirectory 'Bootstrapper.Checks.exe'

try {
    $sources = @(
        Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Bootstrapper\\Core') -File -Filter '*.cs' |
            Sort-Object Name | ForEach-Object FullName
    ) + (Join-Path $sourceRoot 'Bootstrapper\\Checks\\Program.cs')
    $arguments = @(
        '/nologo', '/target:exe', '/langversion:5', ('/out:"{0}"' -f $outputPath),
        '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll',
        '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.CSharp.dll'
    ) + $sources
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Bootstrapper check compilation failed with exit code $LASTEXITCODE." }
    & $outputPath
    if ($LASTEXITCODE -ne 0) { throw "Bootstrapper checks failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $checksDirectory -PathType Container) {
        $directory = Get-Item -Force -LiteralPath $checksDirectory
        $isOwnedDirectory = ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $directory.FullName.Equals($checksDirectory, [StringComparison]::OrdinalIgnoreCase) -and
            $checksDirectory.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)
        if ($isOwnedDirectory -and (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
            $output = Get-Item -Force -LiteralPath $outputPath
            if (($output.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
                $output.FullName.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase)) {
                [IO.File]::Delete($outputPath)
            }
        }
        if ($isOwnedDirectory) {
            try { [IO.Directory]::Delete($checksDirectory, $false) }
            catch { Write-Warning "Leaving non-empty bootstrapper check directory in place: $checksDirectory" }
        }
    }
}
