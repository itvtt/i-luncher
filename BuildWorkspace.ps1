$script:ILauncherWorkspaceMarkerName = '.i-luncher-workspace.json'
$script:ILauncherWorkspaceVersion = 1

function Get-ILauncherFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Equals($pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }
    return $fullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Get-ILauncherNormalizedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = Get-ILauncherFullPath $Path
    return $fullPath.ToUpperInvariant()
}

function Get-ILauncherPathKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$NormalizedSourcePath
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($NormalizedSourcePath)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-ILauncherWorkspaceInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [string]$LocalApplicationDataPath = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)
    )

    if ([string]::IsNullOrWhiteSpace($LocalApplicationDataPath)) {
        throw 'The LocalApplicationData directory could not be resolved.'
    }

    $sourceRoot = Get-ILauncherFullPath $SourcePath
    $normalizedSourcePath = Get-ILauncherNormalizedPath $sourceRoot
    $sourcePathHash = Get-ILauncherPathKey $normalizedSourcePath
    $key = $sourcePathHash.Substring(0, 24)
    $workspaceBase = [IO.Path]::GetFullPath((Join-Path $LocalApplicationDataPath 'i-luncher\Workspaces'))
    $workspacePath = [IO.Path]::GetFullPath((Join-Path $workspaceBase $key))

    [pscustomobject]@{
        SourcePath = $sourceRoot
        NormalizedSourcePath = $normalizedSourcePath
        SourcePathHash = $sourcePathHash
        Key = $key
        WorkspaceBase = $workspaceBase
        WorkspacePath = $workspacePath
        MarkerPath = Join-Path $workspacePath $script:ILauncherWorkspaceMarkerName
    }
}

function Test-ILauncherPathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    return $candidate.StartsWith(
        $parent + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ILauncherNoPathOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$WorkspacePath
    )

    $source = [IO.Path]::GetFullPath($SourcePath)
    $workspace = [IO.Path]::GetFullPath($WorkspacePath)
    if ($source.Equals($workspace, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-ILauncherPathWithin $source $workspace) -or
        (Test-ILauncherPathWithin $workspace $source)) {
        throw "The source and build workspace must not overlap: '$source' and '$workspace'."
    }
}

function Assert-ILauncherNoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($fullPath)
    if (Test-Path -LiteralPath $current) {
        $rootItem = Get-Item -Force -LiteralPath $current
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a reparse point: $current"
        }
    }

    $segments = $fullPath.Substring($current.Length).Split(
        [IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -Force -LiteralPath $current
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a reparse point: $current"
        }
    }
}

function Assert-ILauncherNoReparseTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Assert-ILauncherNoReparseAncestors $Path $Description
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }

    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $directory = Get-Item -Force -LiteralPath $pending.Pop()
        foreach ($item in $directory.GetFileSystemInfos()) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description contains a reparse point: $($item.FullName)"
            }
            if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function Assert-ILauncherWorkspaceLocation {
    param(
        [Parameter(Mandatory = $true)]$WorkspaceInfo
    )

    $expected = Get-ILauncherWorkspaceInfo -SourcePath $WorkspaceInfo.SourcePath
    if (-not ([IO.Path]::GetFullPath($WorkspaceInfo.WorkspacePath)).Equals(
        [IO.Path]::GetFullPath($expected.WorkspacePath),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing an unexpected build workspace: $($WorkspaceInfo.WorkspacePath)"
    }
    if (-not (Test-ILauncherPathWithin $WorkspaceInfo.WorkspacePath $expected.WorkspaceBase)) {
        throw "The build workspace must be inside '$($expected.WorkspaceBase)'."
    }
    Assert-ILauncherNoPathOverlap $WorkspaceInfo.SourcePath $WorkspaceInfo.WorkspacePath
    Assert-ILauncherNoReparseAncestors $WorkspaceInfo.SourcePath 'Source path'
    Assert-ILauncherNoReparseAncestors $WorkspaceInfo.WorkspacePath 'Build workspace path'
}

function Read-ILauncherWorkspaceMarker {
    param(
        [Parameter(Mandatory = $true)]$WorkspaceInfo
    )

    if (-not (Test-Path -LiteralPath $WorkspaceInfo.MarkerPath -PathType Leaf)) {
        throw "The build workspace marker is missing: $($WorkspaceInfo.MarkerPath)"
    }
    $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $WorkspaceInfo.MarkerPath | ConvertFrom-Json
    if ($marker.version -ne $script:ILauncherWorkspaceVersion -or
        $marker.key -ne $WorkspaceInfo.Key -or
        $marker.sourcePathHash -ne $WorkspaceInfo.SourcePathHash -or
        $marker.normalizedSourcePath -ne $WorkspaceInfo.NormalizedSourcePath) {
        throw "The build workspace marker does not match '$($WorkspaceInfo.SourcePath)'."
    }
    return $marker
}

function Write-ILauncherWorkspaceMarker {
    param(
        [Parameter(Mandatory = $true)]$WorkspaceInfo
    )

    $marker = [ordered]@{
        version = $script:ILauncherWorkspaceVersion
        key = $WorkspaceInfo.Key
        sourcePathHash = $WorkspaceInfo.SourcePathHash
        normalizedSourcePath = $WorkspaceInfo.NormalizedSourcePath
        sourcePath = $WorkspaceInfo.SourcePath
    }
    $contents = $marker | ConvertTo-Json
    [IO.File]::WriteAllText(
        $WorkspaceInfo.MarkerPath,
        $contents + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Get-ILauncherWorkspaceExcludedDirectories {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath
    )

    $sourceRoot = Get-ILauncherFullPath $SourcePath
    $appDirectories = New-Object 'Collections.Generic.List[string]'
    foreach ($directory in [IO.Directory]::EnumerateDirectories($sourceRoot)) {
        $appDirectories.Add($directory)
    }

    $projectsDirectory = Join-Path $sourceRoot 'Projects'
    if (Test-Path -LiteralPath $projectsDirectory -PathType Container) {
        foreach ($directory in [IO.Directory]::EnumerateDirectories($projectsDirectory)) {
            $appDirectories.Add($directory)
        }
    }

    $excludedDirectories = New-Object 'Collections.Generic.List[string]'
    foreach ($appDirectory in $appDirectories) {
        $manifestPath = Join-Path $appDirectory 'launcher.app.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }

        try {
            $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
        }
        catch {
            continue
        }

        if ($null -eq $manifest -or $manifest -is [string] -or $manifest -is [ValueType] -or
            $manifest -is [System.Collections.IEnumerable]) { continue }
        $buildProperty = $manifest.PSObject.Properties['build']
        if ($null -eq $buildProperty) { continue }
        $build = $buildProperty.Value
        if ($null -eq $build -or $build -is [string] -or $build -is [ValueType] -or
            $build -is [System.Collections.IEnumerable] -or $build -is [System.Collections.IDictionary]) { continue }

        $excludeProperty = $build.PSObject.Properties['excludeFromWorkspace']
        if ($null -eq $excludeProperty) { continue }
        $relativePaths = $excludeProperty.Value
        if ($null -eq $relativePaths -or $relativePaths -is [string] -or
            $relativePaths -isnot [System.Array]) {
            throw "Invalid app manifest '$manifestPath': 'build.excludeFromWorkspace' must be an array of relative directory paths."
        }

        foreach ($relativePath in $relativePaths) {
            if ($relativePath -isnot [string] -or [string]::IsNullOrWhiteSpace($relativePath)) {
                throw "Invalid app manifest '$manifestPath': each 'build.excludeFromWorkspace' entry must be a non-empty relative path."
            }
            if ([IO.Path]::IsPathRooted($relativePath)) {
                throw "Invalid app manifest '$manifestPath': exclusion '$relativePath' must be relative to its app directory."
            }

            $appRoot = Get-ILauncherFullPath $appDirectory
            $candidate = [IO.Path]::GetFullPath((Join-Path $appRoot $relativePath))
            if ($candidate.Equals($appRoot, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-ILauncherPathWithin $candidate $appRoot)) {
                throw "Invalid app manifest '$manifestPath': exclusion '$relativePath' must resolve to a directory inside this app, not the app root."
            }
            $relativeSegments = $relativePath.Split(
                @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
                [StringSplitOptions]::RemoveEmptyEntries)
            $existingPath = $appRoot
            foreach ($segment in $relativeSegments) {
                if ($segment -eq '.') { continue }
                if ($segment -eq '..') {
                    $existingPath = [IO.Directory]::GetParent($existingPath).FullName
                    continue
                }
                $existingPath = Join-Path $existingPath $segment
                if (-not (Test-Path -LiteralPath $existingPath)) { break }
                if (-not (Test-Path -LiteralPath $existingPath -PathType Container)) {
                    throw "Invalid app manifest '$manifestPath': exclusion path crosses an existing non-directory: '$relativePath'."
                }
            }
            Assert-ILauncherNoReparseAncestors $candidate "Workspace exclusion in '$manifestPath'"
            $excludedDirectories.Add($candidate)
        }
    }

    return $excludedDirectories.ToArray()
}

function Initialize-ILauncherWorkspace {
    param(
        [Parameter(Mandatory = $true)]$WorkspaceInfo
    )

    Assert-ILauncherWorkspaceLocation $WorkspaceInfo
    if (Test-Path -LiteralPath $WorkspaceInfo.WorkspacePath) {
        if (-not (Test-Path -LiteralPath $WorkspaceInfo.WorkspacePath -PathType Container)) {
            throw "The build workspace exists but is not a directory: $($WorkspaceInfo.WorkspacePath)"
        }
        Assert-ILauncherNoReparseTree $WorkspaceInfo.WorkspacePath 'Build workspace'
        if (Test-Path -LiteralPath $WorkspaceInfo.MarkerPath -PathType Leaf) {
            Read-ILauncherWorkspaceMarker $WorkspaceInfo | Out-Null
            return
        }
        if (@([IO.Directory]::EnumerateFileSystemEntries($WorkspaceInfo.WorkspacePath)).Count -ne 0) {
            throw "Refusing to use an unmarked, non-empty build workspace: $($WorkspaceInfo.WorkspacePath)"
        }
    }
    else {
        [IO.Directory]::CreateDirectory($WorkspaceInfo.WorkspacePath) | Out-Null
    }

    Assert-ILauncherNoReparseAncestors $WorkspaceInfo.WorkspacePath 'Build workspace path'
    Write-ILauncherWorkspaceMarker $WorkspaceInfo
}

function Sync-ILauncherWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$WorkspaceInfo
    )

    Initialize-ILauncherWorkspace $WorkspaceInfo
    Assert-ILauncherNoReparseTree $WorkspaceInfo.SourcePath 'Source tree'
    Assert-ILauncherNoReparseTree $WorkspaceInfo.WorkspacePath 'Build workspace'
    $appExcludedDirectories = @(Get-ILauncherWorkspaceExcludedDirectories $WorkspaceInfo.SourcePath)
    $appExcludedDestinations = @($appExcludedDirectories | ForEach-Object {
        $relative = $_.Substring((Get-ILauncherFullPath $WorkspaceInfo.SourcePath).Length).TrimStart(
            [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        [IO.Path]::GetFullPath((Join-Path $WorkspaceInfo.WorkspacePath $relative))
    })

    $arguments = @(
        $WorkspaceInfo.SourcePath,
        $WorkspaceInfo.WorkspacePath,
        '/MIR', '/XJ', '/R:1', '/W:1', '/NFL', '/NDL', '/NP',
        '/XD', 'bin', 'obj', 'dist', '.git', '.vs', '.vshistory', '.pullverse',
            '.pullverse_sender_staging', '.pullverse_sender_transport') + $appExcludedDirectories + $appExcludedDestinations + @(
        '/XF', $script:ILauncherWorkspaceMarkerName
    )
    & robocopy.exe @arguments | Out-Host
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -ge 8) {
        throw "Workspace synchronization failed (robocopy exit code $robocopyExitCode)."
    }

    Read-ILauncherWorkspaceMarker $WorkspaceInfo | Out-Null
    return $WorkspaceInfo.WorkspacePath
}

function Assert-ILauncherInternalWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspacePath,
        [switch]$SkipTreeValidation
    )

    $workspaceRoot = Get-ILauncherFullPath $WorkspacePath
    $markerPath = Join-Path $workspaceRoot $script:ILauncherWorkspaceMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw '-InWorkspace is only valid in a managed external build workspace.'
    }
    $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
    if (-not $marker.sourcePath) {
        throw 'The managed build workspace marker is invalid.'
    }
    $workspaceInfo = Get-ILauncherWorkspaceInfo -SourcePath ([string]$marker.sourcePath)
    if (-not $workspaceRoot.Equals(
        [IO.Path]::GetFullPath($workspaceInfo.WorkspacePath),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw '-InWorkspace was rejected outside the workspace assigned to this source path.'
    }
    Assert-ILauncherWorkspaceLocation $workspaceInfo
    if (-not $SkipTreeValidation) {
        Assert-ILauncherNoReparseTree $workspaceRoot 'Build workspace'
    }
    Read-ILauncherWorkspaceMarker $workspaceInfo | Out-Null
    return $workspaceInfo
}

function Invoke-ILauncherIsolatedBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [string]$App,
        [string]$Project,
        [switch]$LauncherOnly
    )

    $workspaceInfo = Get-ILauncherWorkspaceInfo -SourcePath $SourcePath
    $mutex = New-Object Threading.Mutex($false, "Local\i-luncher-build-$($workspaceInfo.Key)")
    $hasMutex = $false
    try {
        try {
            $hasMutex = $mutex.WaitOne()
        }
        catch [Threading.AbandonedMutexException] {
            $hasMutex = $true
        }
        if (-not $hasMutex) { throw 'Could not acquire the build workspace lock.' }

        $workspacePath = Sync-ILauncherWorkspace $workspaceInfo
        $workspaceBuildScript = Join-Path $workspacePath 'Build.ps1'
        if (-not (Test-Path -LiteralPath $workspaceBuildScript -PathType Leaf)) {
            throw "The synchronized build script is missing: $workspaceBuildScript"
        }

        $buildParameters = @{ InWorkspace = $true }
        if ($App) { $buildParameters.App = $App }
        if ($Project) { $buildParameters.Project = $Project }
        if ($LauncherOnly) { $buildParameters.LauncherOnly = $true }
        $previousCurrentDirectory = [Environment]::CurrentDirectory
        Push-Location -LiteralPath $workspacePath
        try {
            [Environment]::CurrentDirectory = $workspacePath
            & $workspaceBuildScript @buildParameters | Out-Host
            if (-not $?) { throw 'The isolated build failed.' }
        }
        finally {
            [Environment]::CurrentDirectory = $previousCurrentDirectory
            Pop-Location
        }
        return $workspacePath
    }
    finally {
        if ($hasMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
