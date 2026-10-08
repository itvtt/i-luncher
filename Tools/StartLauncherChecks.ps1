$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ILauncherStartChecks-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Start-Launcher.ps1') -Destination $fixture
    $helper = @'
function Get-ILauncherWorkspaceInfo {
    param([string]$SourcePath)
    $script:CheckFixtureRoot = $SourcePath
    [pscustomobject]@{ SourcePath = $SourcePath; WorkspacePath = (Join-Path $SourcePath 'cache'); Key = [IO.Path]::GetFileName($SourcePath) }
}
function Assert-ILauncherInternalWorkspace { param([string]$WorkspacePath, [switch]$SkipTreeValidation) }
function Assert-ILauncherNoReparseAncestors { param([string]$Path, [string]$Description) }
function Test-ILauncherPathWithin {
    param([string]$CandidatePath, [string]$ParentPath)
    return [IO.Path]::GetFullPath($CandidatePath).StartsWith(
        [IO.Path]::GetFullPath($ParentPath).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Invoke-ILauncherIsolatedBuild {
    param([string]$SourcePath, [switch]$LauncherOnly)
    if (-not $LauncherOnly) { throw 'Startup requested all apps instead of launcher only.' }
    if (Test-Path -LiteralPath (Join-Path $SourcePath 'fail')) { throw 'Deliberate build failure' }
    $output = Join-Path $SourcePath 'cache\dist'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    [IO.File]::WriteAllText((Join-Path $output 'i-luncher.exe'), 'fixture executable')
    [IO.File]::AppendAllText((Join-Path $SourcePath 'builds.txt'), "build`n")
}
function Start-Process {
    param([string]$FilePath, [string]$WorkingDirectory)
    if (-not [IO.File]::Exists($FilePath)) { throw 'Tried to launch a missing executable.' }
    [IO.File]::AppendAllText((Join-Path $script:CheckFixtureRoot 'launches.txt'), "launch`n")
    [IO.File]::WriteAllText((Join-Path $script:CheckFixtureRoot 'last-launch.txt'), $FilePath)
}
'@
    [IO.File]::WriteAllText((Join-Path $fixture 'BuildWorkspace.ps1'), $helper)
    $starter = Join-Path $fixture 'Start-Launcher.ps1'
    $printed = & $starter -PrintWorkspace
    if ($printed -ne (Join-Path $fixture 'cache') -or (Test-Path -LiteralPath (Join-Path $fixture 'builds.txt'))) {
        throw 'PrintWorkspace changed files or returned the wrong path.'
    }
    Write-Host 'PASS: PrintWorkspace does not build or launch.'
    & $starter
    & $starter
    if (@(Get-Content -LiteralPath (Join-Path $fixture 'builds.txt')).Count -ne 1 -or
        @(Get-Content -LiteralPath (Join-Path $fixture 'launches.txt')).Count -ne 2) {
        throw 'Startup did not build once and launch twice.'
    }
    Write-Host 'PASS: absent launcher builds automatically; existing launcher launches without rebuilding.'
    Remove-Item -LiteralPath (Join-Path $fixture 'cache\dist\i-luncher.exe')
    [IO.File]::WriteAllText((Join-Path $fixture 'fail'), '')
    $failedAsExpected = $false
    try { & $starter } catch { $failedAsExpected = $_.Exception.Message -eq 'Deliberate build failure' }
    if (-not $failedAsExpected -or @(Get-Content -LiteralPath (Join-Path $fixture 'launches.txt')).Count -ne 2) {
        throw 'Startup swallowed build failure or launched after failure.'
    }
    Write-Host 'PASS: build failure stops startup and is reported.'
    $packageRoot = Join-Path $fixture 'cache\dist\releases\test\i-luncher'
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $packageRoot 'i-luncher.exe'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $packageRoot 'i-luncher.json'), '{"schemaVersion":1,"distribution":{"version":"test"}}')
    $pointerPath = Join-Path $fixture 'cache\dist\latest-distribution.json'
    [IO.File]::WriteAllText($pointerPath, (@{ path = $packageRoot; version = 'test' } | ConvertTo-Json))
    & $starter
    if ([IO.File]::ReadAllText((Join-Path $fixture 'last-launch.txt')) -ne (Join-Path $packageRoot 'i-luncher.exe') -or
        @(Get-Content -LiteralPath (Join-Path $fixture 'builds.txt')).Count -ne 1) {
        throw 'Finished distribution was not preferred without a build.'
    }
    Write-Host 'PASS: finished distribution launches directly without developer build.'
    [IO.File]::WriteAllText($pointerPath, (@{ path = $fixture; version = 'test' } | ConvertTo-Json))
    $rejected = $false
    try { & $starter } catch { $rejected = $_.Exception.Message -like '*inside this workspace*' }
    if (-not $rejected -or @(Get-Content -LiteralPath (Join-Path $fixture 'launches.txt')).Count -ne 3) {
        throw 'An out-of-workspace release pointer was accepted.'
    }
    Write-Host 'PASS: distribution pointer cannot escape the releases directory.'
    Remove-Item -LiteralPath $pointerPath
    $buildVersion = '0123456789abcdef0123456789abcdef'
    $buildRoot = Join-Path $fixture "cache\dist\builds\$buildVersion"
    [IO.Directory]::CreateDirectory($buildRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $buildRoot 'i-luncher.exe'), 'integrated fixture')
    $buildPointer = Join-Path $fixture 'cache\dist\latest-build.json'
    [IO.File]::WriteAllText($buildPointer, (@{ path = $buildRoot; version = $buildVersion } | ConvertTo-Json))
    & $starter
    if ([IO.File]::ReadAllText((Join-Path $fixture 'last-launch.txt')) -ne (Join-Path $buildRoot 'i-luncher.exe') -or
        @(Get-Content -LiteralPath (Join-Path $fixture 'builds.txt')).Count -ne 1) {
        throw 'A completed integrated build was not launched directly.'
    }
    Write-Host 'PASS: completed integrated build launches with no compilation.'
    [IO.File]::WriteAllText($pointerPath, (@{ path = $packageRoot; version = 'test' } | ConvertTo-Json))
    [IO.File]::SetLastWriteTimeUtc($pointerPath, [DateTime]::UtcNow.AddMinutes(-1))
    & $starter
    if ([IO.File]::ReadAllText((Join-Path $fixture 'last-launch.txt')) -ne (Join-Path $buildRoot 'i-luncher.exe')) {
        throw 'An older distribution masked a newer integrated build.'
    }
    Write-Host 'PASS: newer integrated build takes precedence over an older distribution.'
    Remove-Item -LiteralPath $pointerPath
    [IO.File]::WriteAllText($buildPointer, (@{ path = $fixture; version = $buildVersion } | ConvertTo-Json))
    $rejected = $false
    try { & $starter } catch { $rejected = $_.Exception.Message -like '*inside this workspace builds*' }
    if (-not $rejected -or @(Get-Content -LiteralPath (Join-Path $fixture 'launches.txt')).Count -ne 5) {
        throw 'An invalid integrated build pointer was accepted.'
    }
    Write-Host 'PASS: integrated build pointer rejects paths outside its version directory.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('ILauncherStartChecks-', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned test fixture.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
