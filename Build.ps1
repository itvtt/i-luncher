[CmdletBinding()]
param(
    [string]$App,
    [string]$Project,
    [switch]$LauncherOnly,
    [switch]$InWorkspace
)

$ErrorActionPreference = 'Stop'
$workspaceHelperPath = Join-Path $PSScriptRoot 'BuildWorkspace.ps1'
if (-not (Test-Path -LiteralPath $workspaceHelperPath -PathType Leaf)) {
    throw "Build workspace helper not found: $workspaceHelperPath"
}
. $workspaceHelperPath
$sourceAppsHelperPath = Join-Path $PSScriptRoot 'Tools\SourceApps.ps1'
if (-not (Test-Path -LiteralPath $sourceAppsHelperPath -PathType Leaf)) { throw "Source app helper not found: $sourceAppsHelperPath" }
. $sourceAppsHelperPath
$frameworkAppsHelperPath = Join-Path $PSScriptRoot 'Tools\FrameworkApps.ps1'
if (-not (Test-Path -LiteralPath $frameworkAppsHelperPath -PathType Leaf)) { throw "Framework app helper not found: $frameworkAppsHelperPath" }
. $frameworkAppsHelperPath

if (($App -or $Project) -and $LauncherOnly) { throw 'Use either -App/-Project or -LauncherOnly.' }
if ($Project -and -not $App) { throw '-Project requires -App so the source app identity is explicit.' }

if (-not $InWorkspace) {
    Invoke-ILauncherIsolatedBuild -SourcePath $PSScriptRoot -App $App -Project $Project -LauncherOnly:$LauncherOnly | Out-Null
    return
}

Assert-ILauncherInternalWorkspace -WorkspacePath $PSScriptRoot | Out-Null
$rootPath = [IO.Path]::GetFullPath($PSScriptRoot)

function Resolve-ManifestPath([string]$AppDirectory, [string]$ManifestPath) {
    if ([IO.Path]::IsPathRooted($ManifestPath)) {
        throw "App manifest paths must be relative: $ManifestPath"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $AppDirectory $ManifestPath))
    Assert-PathWithin $resolved $AppDirectory 'App manifest path'
    Assert-NoReparsePoints $resolved $rootPath 'App manifest path'
    return $resolved
}

function Test-PathWithin([string]$CandidatePath, [string]$ParentPath) {
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PathWithin([string]$CandidatePath, [string]$ParentPath, [string]$Description) {
    if (-not (Test-PathWithin $CandidatePath $ParentPath)) {
        throw "$Description must be inside '$ParentPath': $CandidatePath"
    }
}

function Assert-NoReparsePoints(
    [string]$CandidatePath,
    [string]$TrustedRoot,
    [string]$Description) {
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $trusted = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $trusted + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.Equals($trusted, [StringComparison]::OrdinalIgnoreCase) -and
        -not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be inside '$trusted': $candidate"
    }

    $current = [IO.Path]::GetPathRoot($candidate)
    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -Force -LiteralPath $current
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a reparse point: $current"
        }
    }

    $segments = $candidate.Substring($current.Length).Split(
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

function Assert-NoReparseTree([string]$TreePath, [string]$TrustedRoot, [string]$Description) {
    Assert-NoReparsePoints $TreePath $TrustedRoot $Description
    if (-not (Test-Path -LiteralPath $TreePath -PathType Container)) { return }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($TreePath))
    while ($pending.Count -gt 0) {
        $directoryPath = $pending.Pop()
        foreach ($item in (Get-Item -Force -LiteralPath $directoryPath).GetFileSystemInfos()) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description contains a reparse point: $($item.FullName)"
            }
            if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function Remove-StagingDirectory([string]$StagingPath, [string]$OutputRoot) {
    if (-not (Test-Path -LiteralPath $StagingPath)) { return }
    Assert-PathWithin $StagingPath $OutputRoot 'Staging directory'
    if (-not ([IO.Path]::GetFileName($StagingPath).StartsWith('.build-staging-', [StringComparison]::Ordinal))) {
        throw "Refusing to remove unexpected staging directory: $StagingPath"
    }
    Assert-NoReparseTree $StagingPath $rootPath 'Staging directory'
    Remove-Item -LiteralPath $StagingPath -Recurse -Force
}

function Publish-SingleFileProject([string]$ProjectPath, [string]$OutputPath) {
    Write-Host "Publishing $ProjectPath"
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $ProjectPath" }
}

function Publish-PayloadProject([string]$ProjectPath, [string]$OutputPath) {
    Write-Host "Publishing payload $ProjectPath"
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $ProjectPath" }
}

function Publish-FrameworkDependentProject([string]$ProjectPath, [string]$OutputPath) {
    if (Test-LegacyFrameworkProject $ProjectPath) {
        Publish-LegacyFrameworkProject $ProjectPath $OutputPath
        return
    }
    Write-Host "Publishing framework-dependent source app $ProjectPath"
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false `
        '-p:ILuncherIntegrated=false' '-p:UseAppHost=true' '-p:PublishSingleFile=false' `
        '-p:DebugSymbols=false' '-p:DebugType=None' -o $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $ProjectPath" }
}

function Publish-SourceApp([object]$SourceApp) {
    $outputRoot = Join-Path $rootPath (Join-Path 'dist\apps' $SourceApp.Id)
    $appsRoot = Join-Path $rootPath 'dist\apps'
    $buildId = [Guid]::NewGuid().ToString('N')
    $stagingPath = Join-Path $appsRoot ('.build-staging-' + $SourceApp.Id + '-' + $buildId)
    $previousPath = Join-Path $appsRoot ('.build-previous-' + $SourceApp.Id + '-' + $buildId)
    Assert-PathWithin $outputRoot $appsRoot 'Source app output directory'
    Assert-NoReparsePoints $appsRoot $rootPath 'Source apps directory'
    [IO.Directory]::CreateDirectory($appsRoot) | Out-Null
    Assert-NoReparsePoints $appsRoot $rootPath 'Source apps directory'
    if (Test-Path -LiteralPath $stagingPath) { throw "Fresh source app staging directory already exists: $stagingPath" }
    try {
        Publish-FrameworkDependentProject $SourceApp.ProjectPath $stagingPath
        $executablePath = Join-Path $stagingPath $SourceApp.ExecutableName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "Published apphost is missing: $executablePath" }
        $metadata = [ordered]@{
            schemaVersion = 1
            id = $SourceApp.Id
            project = $SourceApp.RelativeProjectPath.Replace('\', '/')
            executable = $SourceApp.ExecutableName
            builtUtc = [DateTime]::UtcNow.ToString('o')
        }
        [IO.File]::WriteAllText((Join-Path $stagingPath '.build-result.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Assert-NoReparseTree $stagingPath $rootPath 'Staged source app output'
        if (Test-Path -LiteralPath $outputRoot) {
            Assert-NoReparseTree $outputRoot $rootPath 'Previous source app output'
            Move-Item -LiteralPath $outputRoot -Destination $previousPath
        }
        try {
            Assert-NoReparsePoints $outputRoot $rootPath 'Source app destination'
            Move-Item -LiteralPath $stagingPath -Destination $outputRoot
        }
        catch {
            $promotionError = $_
            if ((Test-Path -LiteralPath $previousPath -PathType Container) -and -not (Test-Path -LiteralPath $outputRoot)) {
                Move-Item -LiteralPath $previousPath -Destination $outputRoot
            }
            throw "Source app promotion failed: $($promotionError.Exception.Message)"
        }
        if (Test-Path -LiteralPath $previousPath) {
            Assert-NoReparseTree $previousPath $rootPath 'Previous source app output'
            Remove-Item -LiteralPath $previousPath -Recurse -Force
        }
        Write-Host "Source app executable: $(Join-Path $outputRoot $SourceApp.ExecutableName)"
    }
    finally {
        Remove-StagingDirectory $stagingPath $appsRoot
    }
}

function Write-PackageHashManifest([string]$PackagePath) {
    Assert-NoReparseTree $PackagePath $rootPath 'Package directory'
    $packageRoot = [IO.Path]::GetFullPath($PackagePath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $packagePrefix = $packageRoot + [IO.Path]::DirectorySeparatorChar
    $hashManifestPath = Join-Path $packageRoot 'package.sha256'
    $relativePaths = [Collections.Generic.List[string]]::new()
    foreach ($filePath in [IO.Directory]::EnumerateFiles(
        $packageRoot,
        '*',
        [IO.SearchOption]::AllDirectories)) {
        $fullFilePath = [IO.Path]::GetFullPath($filePath)
        if ([string]::Equals(
            $fullFilePath,
            [IO.Path]::GetFullPath($hashManifestPath),
            [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $fullFilePath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package file is outside the package directory: $fullFilePath"
        }
        $relativePaths.Add($fullFilePath.Substring($packagePrefix.Length).Replace(
            [IO.Path]::DirectorySeparatorChar,
            '/'))
    }

    $sortedPaths = $relativePaths.ToArray()
    [Array]::Sort($sortedPaths, [StringComparer]::Ordinal)
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($relativePath in $sortedPaths) {
        $nativeRelativePath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $filePath = Join-Path $packageRoot $nativeRelativePath
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
        $lines.Add(('{0}  {1}' -f $hash, $relativePath))
    }

    $contents = if ($lines.Count -eq 0) { '' } else { ($lines -join "`n") + "`n" }
    [IO.File]::WriteAllText($hashManifestPath, $contents, [Text.UTF8Encoding]::new($false))
}

function Restore-PayloadAfterBootstrapFailure(
    [string]$PayloadPath,
    [string]$PreviousPayloadPath,
    [string]$OutputRoot,
    [string]$BuildId) {
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $PayloadPath) {
        $failedPayloadPath = Join-Path $OutputRoot "app.failed-$BuildId"
        try {
            Assert-NoReparsePoints $PayloadPath $rootPath 'New payload path'
            Assert-NoReparsePoints $failedPayloadPath $rootPath 'Failed payload backup path'
            Move-Item -LiteralPath $PayloadPath -Destination $failedPayloadPath
        }
        catch {
            $rollbackErrors.Add("could not preserve the new payload at '$failedPayloadPath': $($_.Exception.Message)")
        }
    }
    if ($PreviousPayloadPath -and (Test-Path -LiteralPath $PreviousPayloadPath)) {
        try {
            Assert-NoReparsePoints $PreviousPayloadPath $rootPath 'Previous payload path'
            Assert-NoReparsePoints $PayloadPath $rootPath 'Payload restore path'
            Move-Item -LiteralPath $PreviousPayloadPath -Destination $PayloadPath
        }
        catch {
            $rollbackErrors.Add("could not restore the previous payload from '$PreviousPayloadPath': $($_.Exception.Message)")
        }
    }
    return $rollbackErrors
}

function Publish-BootstrappedApp(
    [string]$PayloadProjectPath,
    [string]$BootstrapProjectPath,
    [string]$ExecutablePath) {
    $outputRoot = [IO.Path]::GetDirectoryName($ExecutablePath)
    Assert-PathWithin $outputRoot $rootPath 'Bootstrap output directory'
    Assert-NoReparsePoints $outputRoot $rootPath 'Bootstrap output directory'
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    Assert-NoReparsePoints $outputRoot $rootPath 'Bootstrap output directory'
    if ((Test-Path -LiteralPath $ExecutablePath) -and
        -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Bootstrap executable path exists but is not a file: $ExecutablePath"
    }
    Assert-NoReparsePoints $ExecutablePath $rootPath 'Bootstrap executable path'

    $buildId = [Guid]::NewGuid().ToString('N')
    $stagingRoot = Join-Path $outputRoot ".build-staging-$buildId"
    $stagedPayloadPath = Join-Path $stagingRoot 'app'
    $stagedBootstrapPath = Join-Path $stagingRoot 'bootstrap'
    $payloadPath = Join-Path $outputRoot 'app'
    $executableName = [IO.Path]::GetFileName($ExecutablePath)
    $stagedPayloadExecutable = Join-Path $stagedPayloadPath $executableName
    $stagedBootstrapExecutable = Join-Path $stagedBootstrapPath $executableName
    $previousPayloadPath = $null
    $previousExecutablePath = $null
    $executableMoved = $false

    Assert-PathWithin $stagingRoot $outputRoot 'Staging directory'
    Assert-NoReparsePoints $stagingRoot $rootPath 'Staging directory'
    if (Test-Path -LiteralPath $stagingRoot) {
        throw "Fresh staging directory already exists: $stagingRoot"
    }

    try {
        Publish-PayloadProject $PayloadProjectPath $stagedPayloadPath
        if (-not (Test-Path -LiteralPath $stagedPayloadExecutable -PathType Leaf)) {
            throw "Payload executable does not match launcher.app.json: $stagedPayloadExecutable"
        }
        Write-PackageHashManifest $stagedPayloadPath

        Publish-SingleFileProject $BootstrapProjectPath $stagedBootstrapPath
        if (-not (Test-Path -LiteralPath $stagedBootstrapExecutable -PathType Leaf)) {
            throw "Bootstrap executable does not match launcher.app.json: $stagedBootstrapExecutable"
        }

        if (Test-Path -LiteralPath $payloadPath) {
            $previousPayloadPath = Join-Path $outputRoot "app.previous-$buildId"
            Assert-NoReparsePoints $payloadPath $rootPath 'Current payload path'
            Assert-NoReparsePoints $previousPayloadPath $rootPath 'Previous payload backup path'
            Move-Item -LiteralPath $payloadPath -Destination $previousPayloadPath
        }
        try {
            Assert-NoReparsePoints $stagedPayloadPath $rootPath 'Staged payload path'
            Assert-NoReparsePoints $payloadPath $rootPath 'Payload destination path'
            Move-Item -LiteralPath $stagedPayloadPath -Destination $payloadPath
        }
        catch {
            $promotionError = $_
            if ($previousPayloadPath -and
                (Test-Path -LiteralPath $previousPayloadPath) -and
                -not (Test-Path -LiteralPath $payloadPath)) {
                try {
                    Assert-NoReparsePoints $previousPayloadPath $rootPath 'Previous payload path'
                    Assert-NoReparsePoints $payloadPath $rootPath 'Payload restore path'
                    Move-Item -LiteralPath $previousPayloadPath -Destination $payloadPath
                    $previousPayloadPath = $null
                }
                catch {
                    throw "Payload promotion failed ($($promotionError.Exception.Message)); restoring the previous payload also failed: $($_.Exception.Message)"
                }
            }
            throw "Payload promotion failed; the previous bootstrap was not changed: $($promotionError.Exception.Message)"
        }

        try {
            if (Test-Path -LiteralPath $ExecutablePath -PathType Leaf) {
                $baseName = [IO.Path]::GetFileNameWithoutExtension($executableName)
                $extension = [IO.Path]::GetExtension($executableName)
                $previousExecutablePath = Join-Path $outputRoot "$baseName.previous-$buildId$extension"
                Assert-NoReparsePoints $ExecutablePath $rootPath 'Bootstrap executable path'
                Assert-NoReparsePoints $previousExecutablePath $rootPath 'Previous bootstrap backup path'
                Move-Item -LiteralPath $ExecutablePath -Destination $previousExecutablePath
                $executableMoved = $true
            }
            Assert-NoReparsePoints $stagedBootstrapExecutable $rootPath 'Staged bootstrap path'
            Assert-NoReparsePoints $ExecutablePath $rootPath 'Bootstrap destination path'
            Copy-Item -LiteralPath $stagedBootstrapExecutable -Destination $ExecutablePath
        }
        catch {
            $bootstrapError = $_
            $rollbackErrors = [Collections.Generic.List[string]]::new()
            if ($executableMoved) {
                if (Test-Path -LiteralPath $ExecutablePath -PathType Leaf) {
                    try {
                        Assert-NoReparsePoints $ExecutablePath $rootPath 'Incomplete bootstrap path'
                        Remove-Item -LiteralPath $ExecutablePath -Force
                    }
                    catch {
                        $rollbackErrors.Add("could not remove the incomplete bootstrap: $($_.Exception.Message)")
                    }
                }
                if (Test-Path -LiteralPath $previousExecutablePath -PathType Leaf) {
                    try {
                        Assert-NoReparsePoints $previousExecutablePath $rootPath 'Previous bootstrap path'
                        Assert-NoReparsePoints $ExecutablePath $rootPath 'Bootstrap restore path'
                        Move-Item -LiteralPath $previousExecutablePath -Destination $ExecutablePath
                    }
                    catch {
                        $rollbackErrors.Add("could not restore the previous bootstrap from '$previousExecutablePath': $($_.Exception.Message)")
                    }
                }
            }
            foreach ($payloadRollbackError in @(Restore-PayloadAfterBootstrapFailure `
                $payloadPath $previousPayloadPath $outputRoot $buildId)) {
                $rollbackErrors.Add($payloadRollbackError)
            }
            if ($rollbackErrors.Count -gt 0) {
                throw "Bootstrap promotion failed ($($bootstrapError.Exception.Message)); rollback was incomplete: $($rollbackErrors -join '; ')"
            }
            throw "Bootstrap promotion failed; the previous package was restored: $($bootstrapError.Exception.Message)"
        }

        if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
            throw "Published executable does not match launcher.app.json: $ExecutablePath"
        }
        Write-Host "Published bootstrapped package: $outputRoot"
    }
    finally {
        Remove-StagingDirectory $stagingRoot $outputRoot
    }
}

$integratedAppIds = @('folder-compare', 'swift-copy', 'disk-atlas', 'deskcal')
$buildIntegratedHost = -not $App -or $App -in $integratedAppIds
$sourceApps = @()
if (-not $LauncherOnly -and $Project) {
    $validatedProject = Assert-SourceAppProjectPath $rootPath $Project $App
    $projectInfo = Get-SourceAppProjectInfo $validatedProject.ProjectPath
    if (-not $projectInfo.IsRunnable) { throw "Source app project must produce Exe or WinExe output: $Project (OutputType=$($projectInfo.OutputType))" }
    $sourceApps = @([pscustomobject]@{
        Id = $validatedProject.Id; AppFolder = $validatedProject.AppFolder
        ProjectPath = $validatedProject.ProjectPath; RelativeProjectPath = $Project.Replace('/', '\')
        Name = [IO.Path]::GetFileName($validatedProject.AppFolder); ExecutableName = $projectInfo.ExecutableName
        Manifest = $null; IsAutomatic = $true
    })
}
elseif (-not $LauncherOnly) {
    $sourceApps = @(Find-SourceAppProjects $rootPath)
    if ($App) { $sourceApps = @($sourceApps | Where-Object Id -CEQ $App) }
}

# Built-in tools are compiled through the host's project references in one publish.
# Keep the manifest-driven publishing path for additional, external tools.
if (-not $LauncherOnly -and -not ($App -in $integratedAppIds)) {
    $foundApp = $false
    foreach ($directoryPath in Get-SourceAppDirectories $rootPath) {
        $directory = Get-Item -LiteralPath $directoryPath
        $manifestPath = Join-Path $directory.FullName 'launcher.app.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) { continue }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.id -in $integratedAppIds) { continue }
        if ($App -and $manifest.id -ne $App) { continue }
        $foundApp = $true
        if (-not $manifest.build.project) {
            Write-Host "Skipping prebuilt app: $($manifest.name)"
            continue
        }

        $projectPath = Resolve-ManifestPath $directory.FullName $manifest.build.project
        $executablePath = Resolve-ManifestPath $directory.FullName $manifest.executable
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Project not found: $projectPath"
        }

        if ($manifest.build.bootstrapProject) {
            $bootstrapProjectPath = Resolve-ManifestPath `
                $directory.FullName $manifest.build.bootstrapProject
            if (-not (Test-Path -LiteralPath $bootstrapProjectPath -PathType Leaf)) {
                throw "Bootstrap project not found: $bootstrapProjectPath"
            }
            Publish-BootstrappedApp $projectPath $bootstrapProjectPath $executablePath
        }
        else {
            Publish-FrameworkDependentProject $projectPath ([IO.Path]::GetDirectoryName($executablePath))
            if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
                throw "Published executable does not match launcher.app.json: $executablePath"
            }
        }
    }
    if ($App -and -not $foundApp -and $sourceApps.Count -eq 0) { throw "Unknown app id: $App" }
}

if (-not $LauncherOnly) {
    foreach ($sourceApp in $sourceApps) { Publish-SourceApp $sourceApp }
}

if ($buildIntegratedHost) {
    $hostProject = Join-Path $rootPath 'Launcher/i-luncher.csproj'
    $buildsRoot = Join-Path $rootPath 'dist\builds'
    $buildId = [Guid]::NewGuid().ToString('N')
    $hostOutput = Join-Path $buildsRoot $buildId
    $stagedOutput = Join-Path $buildsRoot ".build-staging-$buildId"
    $latestPath = Join-Path $rootPath 'dist\latest-build.json'
    $temporaryPointer = Join-Path $rootPath "dist\.latest-build-$buildId.tmp"
    Assert-NoReparsePoints $buildsRoot $rootPath 'Integrated builds directory'
    Assert-NoReparsePoints $latestPath $rootPath 'Integrated build pointer'
    [IO.Directory]::CreateDirectory($buildsRoot) | Out-Null
    try {
        Write-Host 'Publishing i-luncher with all four integrated tools...'
        & dotnet publish $hostProject -c Release -r win-x64 --self-contained true `
            -p:ILuncherIntegrated=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
            -o $stagedOutput
        if ($LASTEXITCODE -ne 0) { throw 'Integrated i-luncher publish failed.' }
        foreach ($required in @('i-luncher.exe', 'i-luncher.dll', 'i-luncher.deps.json', 'i-luncher.runtimeconfig.json',
            'FolderCompareWPF.dll', 'SwiftCopy.dll', 'SwiftCopy.Core.dll', 'DiskAtlas.dll', 'DiskAtlas.Core.dll',
            'DeskCal.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $stagedOutput $required) -PathType Leaf)) {
                throw "Integrated publish output is missing: $required"
            }
        }
        Assert-PathWithin $stagedOutput $buildsRoot 'Staged integrated build'
        Assert-PathWithin $hostOutput $buildsRoot 'Integrated build destination'
        Assert-NoReparseTree $stagedOutput $rootPath 'Staged integrated build'
        Move-Item -LiteralPath $stagedOutput -Destination $hostOutput
        $latest = @{ schemaVersion = 1; path = $hostOutput; version = $buildId } | ConvertTo-Json
        [IO.File]::WriteAllText($temporaryPointer, $latest, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $latestPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPointer, $latestPath, [System.Management.Automation.Language.NullString]::Value)
        }
        else { [IO.File]::Move($temporaryPointer, $latestPath) }
        Write-Host "Integrated executable: $(Join-Path $hostOutput 'i-luncher.exe')"
    }
    finally {
        Remove-StagingDirectory $stagedOutput $buildsRoot
        if (Test-Path -LiteralPath $temporaryPointer -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPointer -Force
        }
    }
}
Write-Host 'Build complete.'
