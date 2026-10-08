[CmdletBinding()]
param(
    [switch]$PrintWorkspace
)

$ErrorActionPreference = 'Stop'
$workspaceHelperPath = Join-Path $PSScriptRoot 'BuildWorkspace.ps1'
if (-not (Test-Path -LiteralPath $workspaceHelperPath -PathType Leaf)) {
    throw "Build workspace helper not found: $workspaceHelperPath"
}
. $workspaceHelperPath

$managedMarkerPath = Join-Path $PSScriptRoot '.i-luncher-workspace.json'
$invokedFromManagedWorkspace = Test-Path -LiteralPath $managedMarkerPath -PathType Leaf
$workspaceInfo = if ($invokedFromManagedWorkspace) {
    $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $managedMarkerPath | ConvertFrom-Json
    if (-not $marker.sourcePath) { throw 'The managed build workspace marker is invalid.' }
    Get-ILauncherWorkspaceInfo -SourcePath ([string]$marker.sourcePath)
}
else {
    Get-ILauncherWorkspaceInfo -SourcePath $PSScriptRoot
}
if ($PrintWorkspace) {
    if ($invokedFromManagedWorkspace) {
        Assert-ILauncherInternalWorkspace -WorkspacePath $PSScriptRoot -SkipTreeValidation | Out-Null
    }
    Write-Output $workspaceInfo.WorkspacePath
    return
}

function Start-ExistingLauncher {
    $distributionPointer = Join-Path $workspaceInfo.WorkspacePath 'dist\latest-distribution.json'
    $buildPointer = Join-Path $workspaceInfo.WorkspacePath 'dist\latest-build.json'
    $hasBuild = Test-Path -LiteralPath $buildPointer -PathType Leaf
    if ((Test-Path -LiteralPath $distributionPointer -PathType Leaf) -and
        (-not $hasBuild -or [IO.File]::GetLastWriteTimeUtc($distributionPointer) -ge [IO.File]::GetLastWriteTimeUtc($buildPointer))) {
        Assert-ILauncherNoReparseAncestors $distributionPointer 'Distribution pointer'
        $release = Get-Content -Raw -Encoding UTF8 -LiteralPath $distributionPointer | ConvertFrom-Json
        if (-not $release.path) { throw 'The distribution pointer has no package path.' }
        $packageRoot = [IO.Path]::GetFullPath([string]$release.path)
        if (-not (Test-ILauncherPathWithin $packageRoot (Join-Path $workspaceInfo.WorkspacePath 'dist\releases'))) {
            throw 'The distribution package must be inside this workspace releases directory.'
        }
        $packageManifestPath = Join-Path $packageRoot 'i-luncher.json'
        $packageLauncher = Join-Path $packageRoot 'i-luncher.exe'
        Assert-ILauncherNoReparseAncestors $packageManifestPath 'Distribution manifest'
        Assert-ILauncherNoReparseAncestors $packageLauncher 'Distribution launcher'
        $packageManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $packageManifestPath | ConvertFrom-Json
        if (-not $packageManifest.distribution -or
            $packageManifest.distribution.version -ne $release.version -or
            -not (Test-Path -LiteralPath $packageLauncher -PathType Leaf)) {
            throw 'The distribution package is incomplete. Run Publish-Distribution.ps1 again.'
        }
        Start-Process -FilePath $packageLauncher -WorkingDirectory $packageRoot
        return $true
    }

    if (Test-Path -LiteralPath $buildPointer -PathType Leaf) {
        Assert-ILauncherInternalWorkspace -WorkspacePath $workspaceInfo.WorkspacePath -SkipTreeValidation | Out-Null
        Assert-ILauncherNoReparseAncestors $buildPointer 'Integrated build pointer'
        $build = Get-Content -Raw -Encoding UTF8 -LiteralPath $buildPointer | ConvertFrom-Json
        if (-not $build.path) { throw 'The integrated build pointer has no path.' }
        $buildRoot = [IO.Path]::GetFullPath([string]$build.path)
        if (-not (Test-ILauncherPathWithin $buildRoot (Join-Path $workspaceInfo.WorkspacePath 'dist\builds')) -or
            [IO.Path]::GetFileName($buildRoot) -cne [string]$build.version -or
            [string]$build.version -notmatch '^[0-9a-f]{32}$') {
            throw 'The integrated build must be inside this workspace builds directory with a matching version.'
        }
        $buildLauncher = Join-Path $buildRoot 'i-luncher.exe'
        Assert-ILauncherNoReparseAncestors $buildLauncher 'Integrated executable'
        if (-not (Test-Path -LiteralPath $buildLauncher -PathType Leaf)) {
            throw 'The integrated build is incomplete. Run Build.ps1 again.'
        }
        Start-Process -FilePath $buildLauncher -WorkingDirectory $buildRoot
        return $true
    }

    $launcherPath = Join-Path $workspaceInfo.WorkspacePath 'dist\i-luncher.exe'
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) { return $false }
    Assert-ILauncherInternalWorkspace -WorkspacePath $workspaceInfo.WorkspacePath -SkipTreeValidation | Out-Null
    Assert-ILauncherNoReparseAncestors $launcherPath 'Launcher executable'
    Start-Process -FilePath $launcherPath -WorkingDirectory $workspaceInfo.WorkspacePath
    return $true
}

# A ready executable needs neither a source-tree scan nor the build lock.
if (Start-ExistingLauncher) { return }

$mutex = New-Object Threading.Mutex($false, "Local\i-luncher-build-$($workspaceInfo.Key)")
$hasMutex = $false
try {
    try { $hasMutex = $mutex.WaitOne() }
    catch [Threading.AbandonedMutexException] { $hasMutex = $true }
    if (-not $hasMutex) { throw 'Could not acquire the build workspace lock.' }
    if (Start-ExistingLauncher) { return }
    $launcherPath = Join-Path $workspaceInfo.WorkspacePath 'dist\i-luncher.exe'
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        Write-Host 'Preparing i-luncher and all integrated tools for first use...'
        if ($invokedFromManagedWorkspace) {
            Assert-ILauncherInternalWorkspace -WorkspacePath $PSScriptRoot | Out-Null
            & (Join-Path $PSScriptRoot 'Build.ps1') -LauncherOnly -InWorkspace
        }
        else {
            Invoke-ILauncherIsolatedBuild -SourcePath $workspaceInfo.SourcePath -LauncherOnly | Out-Null
        }
    }
    if (-not (Start-ExistingLauncher)) { throw 'The integrated launcher is not ready after building.' }
}
finally {
    if ($hasMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
