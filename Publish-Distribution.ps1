[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Version,

    [string]$DotNetRoot,

    [switch]$InWorkspace,

    [switch]$UseCurrentWorkspace
)

$ErrorActionPreference = 'Stop'
$framework = 'net10.0'
$rid = 'win-x64'
$workspaceHelperPath = Join-Path $PSScriptRoot 'BuildWorkspace.ps1'
if (-not (Test-Path -LiteralPath $workspaceHelperPath -PathType Leaf)) {
    throw "Build workspace helper not found: $workspaceHelperPath"
}
. $workspaceHelperPath
$sourceAppsHelperPath = Join-Path $PSScriptRoot 'Tools\SourceApps.ps1'
if (-not (Test-Path -LiteralPath $sourceAppsHelperPath -PathType Leaf)) { throw "Source app helper not found: $sourceAppsHelperPath" }
. $sourceAppsHelperPath

if (-not $Version) {
    $Version = '{0}-{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
}

if ($InWorkspace -and $UseCurrentWorkspace) {
    throw 'Use either -InWorkspace or -UseCurrentWorkspace.'
}

if ($UseCurrentWorkspace) {
    $runId = [string]$env:GITHUB_RUN_ID
    $runAttempt = [string]$env:GITHUB_RUN_ATTEMPT
    $runnerTemp = [string]$env:RUNNER_TEMP
    if ($env:GITHUB_ACTIONS -ne 'true' -or [string]::IsNullOrWhiteSpace($runId) -or
        [string]::IsNullOrWhiteSpace($runAttempt) -or [string]::IsNullOrWhiteSpace($runnerTemp) -or
        $runId -notmatch '^[0-9]+$' -or $runAttempt -notmatch '^[0-9]+$') {
        throw '-UseCurrentWorkspace is restricted to a validated GitHub Actions runner checkout.'
    }
    $expectedCheckout = [IO.Path]::GetFullPath((Join-Path $runnerTemp "i-luncher-source-$runId-$runAttempt"))
    $actualCheckout = Get-ILauncherFullPath $PSScriptRoot
    if (-not $actualCheckout.Equals($expectedCheckout, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The current workspace is not the unique checkout assigned to this Actions run: $actualCheckout"
    }
    Assert-ILauncherNoReparseTree $actualCheckout 'Actions checkout'
}

if (-not $InWorkspace -and -not $UseCurrentWorkspace) {
    $workspaceInfo = Get-ILauncherWorkspaceInfo -SourcePath $PSScriptRoot
    $mutex = [Threading.Mutex]::new($false, "Local\i-luncher-build-$($workspaceInfo.Key)")
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
        $workspaceScript = Join-Path $workspacePath 'Publish-Distribution.ps1'
        if (-not (Test-Path -LiteralPath $workspaceScript -PathType Leaf)) {
            throw "The synchronized distribution script is missing: $workspaceScript"
        }

        $parameters = @{ InWorkspace = $true; Version = $Version }
        if ($DotNetRoot) { $parameters.DotNetRoot = $DotNetRoot }
        $previousCurrentDirectory = [Environment]::CurrentDirectory
        Push-Location -LiteralPath $workspacePath
        try {
            [Environment]::CurrentDirectory = $workspacePath
            & $workspaceScript @parameters
            if (-not $?) { throw 'The isolated distribution publish failed.' }
        }
        finally {
            [Environment]::CurrentDirectory = $previousCurrentDirectory
            Pop-Location
        }
        return
    }
    finally {
        if ($hasMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

if (-not $UseCurrentWorkspace) {
    Assert-ILauncherInternalWorkspace -WorkspacePath $PSScriptRoot | Out-Null
}
$rootPath = Get-ILauncherFullPath $PSScriptRoot
$distRoot = Join-Path $rootPath 'dist'
$releasesRoot = Join-Path $distRoot 'releases'
$releasePath = Join-Path $releasesRoot $Version
$stagePath = Join-Path $releasesRoot ('.s-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$bundlePath = Join-Path $stagePath 'i-luncher'
$zipName = "i-luncher-$Version-$rid.zip"
$stagedZipPath = Join-Path $stagePath $zipName
$finalBundlePath = Join-Path $releasePath 'i-luncher'
$finalZipPath = Join-Path $releasePath $zipName

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-ILauncherPathWithin $Path $Parent)) {
        throw "$Description must be inside '$Parent': $Path"
    }
}

function Remove-OwnedStage {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-ChildPath $Path $releasesRoot 'Distribution staging directory'
    if ([IO.Path]::GetFileName($Path) -notmatch '^\.s-[0-9a-f]{8}$') {
        throw "Refusing to remove an unexpected directory: $Path"
    }
    Assert-ILauncherNoReparseTree $Path 'Distribution staging directory'
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Resolve-DotNetRoot {
    param([string]$RequestedRoot)

    if ($RequestedRoot) {
        return Get-ILauncherFullPath $RequestedRoot
    }

    $programFilesRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if ($programFilesRoot) {
        $standardRoot = Join-Path $programFilesRoot 'dotnet'
        if (Test-Path -LiteralPath (Join-Path $standardRoot 'dotnet.exe') -PathType Leaf) {
            return Get-ILauncherFullPath $standardRoot
        }
    }

    $dotnetCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    return Get-ILauncherFullPath ([IO.Path]::GetDirectoryName($dotnetCommand.Source))
}

function Assert-X64Executable {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows executable: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) { throw "Invalid PE header: $Path" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        if ($reader.ReadUInt16() -ne 0x8664) { throw "The selected dotnet host is not x64: $Path" }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-InstalledRuntimeVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $componentRoots = @(
        (Join-Path $Root 'host\fxr'),
        (Join-Path $Root 'shared\Microsoft.NETCore.App'),
        (Join-Path $Root 'shared\Microsoft.WindowsDesktop.App')
    )
    foreach ($componentRoot in $componentRoots) {
        if (-not (Test-Path -LiteralPath $componentRoot -PathType Container)) {
            throw "Required .NET runtime component directory is missing: $componentRoot"
        }
    }

    $versionsByRoot = @()
    foreach ($componentRoot in $componentRoots) {
        $versions = @{}
        foreach ($directory in Get-ChildItem -LiteralPath $componentRoot -Directory) {
            try { $parsed = [Version]$directory.Name } catch { continue }
            if ($parsed.Major -eq 10) { $versions[$directory.Name] = $parsed }
        }
        $versionsByRoot += ,$versions
    }

    $common = foreach ($entry in $versionsByRoot[0].GetEnumerator()) {
        if ($versionsByRoot[1].ContainsKey($entry.Key) -and $versionsByRoot[2].ContainsKey($entry.Key)) {
            [pscustomobject]@{ Text = $entry.Key; Parsed = $entry.Value }
        }
    }
    $selected = $common | Sort-Object Parsed -Descending | Select-Object -First 1
    if (-not $selected) {
        throw "No matching .NET 10 x64 host, Microsoft.NETCore.App, and Microsoft.WindowsDesktop.App version was found under '$Root'."
    }
    return $selected.Text
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Directory to package is missing: $Source"
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in (Get-Item -Force -LiteralPath $Source).GetFileSystemInfos()) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to copy a runtime reparse point: $($item.FullName)"
        }
        $target = Join-Path $Destination $item.Name
        if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            Copy-DirectoryContents $item.FullName $target
        }
        else {
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Assert-LegacyFilePathLength {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -gt 259) {
        throw "$Description exceeds the Windows 260-character compatibility limit " +
            "($($fullPath.Length) characters). Choose a shorter -Version value. Path: $fullPath"
    }
}

function Assert-LegacyDirectoryPathLength {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -gt 247) {
        throw "$Description exceeds the Windows 248-character directory compatibility limit " +
            "($($fullPath.Length) characters). Choose a shorter -Version value. Path: $fullPath"
    }
}

function Assert-RuntimeDestinationPathLengths {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeRoot,
        [Parameter(Mandatory = $true)][string]$RuntimeVersion
    )

    $mappings = @(
        [pscustomobject]@{
            Source = Join-Path $RuntimeRoot "host\fxr\$RuntimeVersion"
            Relative = "runtime\host\fxr\$RuntimeVersion"
        },
        [pscustomobject]@{
            Source = Join-Path $RuntimeRoot "shared\Microsoft.NETCore.App\$RuntimeVersion"
            Relative = "runtime\shared\Microsoft.NETCore.App\$RuntimeVersion"
        },
        [pscustomobject]@{
            Source = Join-Path $RuntimeRoot "shared\Microsoft.WindowsDesktop.App\$RuntimeVersion"
            Relative = "runtime\shared\Microsoft.WindowsDesktop.App\$RuntimeVersion"
        }
    )
    foreach ($mapping in $mappings) {
        $sourcePrefix = (Get-ILauncherFullPath $mapping.Source) + [IO.Path]::DirectorySeparatorChar
        foreach ($sourceDirectory in [IO.Directory]::EnumerateDirectories(
            $mapping.Source,
            '*',
            [IO.SearchOption]::AllDirectories)) {
            $relativeDirectory = ([IO.Path]::GetFullPath($sourceDirectory)).Substring($sourcePrefix.Length)
            $packageRelative = Join-Path $mapping.Relative $relativeDirectory
            Assert-LegacyDirectoryPathLength (Join-Path $bundlePath $packageRelative) 'Staged runtime directory path'
            Assert-LegacyDirectoryPathLength (Join-Path $finalBundlePath $packageRelative) 'Final runtime directory path'
        }
        foreach ($sourceFile in [IO.Directory]::EnumerateFiles(
            $mapping.Source,
            '*',
            [IO.SearchOption]::AllDirectories)) {
            $relativeFile = ([IO.Path]::GetFullPath($sourceFile)).Substring($sourcePrefix.Length)
            $packageRelative = Join-Path $mapping.Relative $relativeFile
            Assert-LegacyFilePathLength (Join-Path $bundlePath $packageRelative) 'Staged runtime file path'
            Assert-LegacyFilePathLength (Join-Path $finalBundlePath $packageRelative) 'Final runtime file path'
        }
    }
    foreach ($rootFile in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        Assert-LegacyFilePathLength (Join-Path $bundlePath "runtime\$rootFile") 'Staged runtime file path'
        Assert-LegacyFilePathLength (Join-Path $finalBundlePath "runtime\$rootFile") 'Final runtime file path'
    }
}

function Assert-PromotionPathLengths {
    foreach ($stagedDirectory in [IO.Directory]::EnumerateDirectories($stagePath, '*', [IO.SearchOption]::AllDirectories)) {
        $relativePath = ([IO.Path]::GetFullPath($stagedDirectory)).Substring($stagePath.Length).TrimStart(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        Assert-LegacyDirectoryPathLength (Join-Path $releasePath $relativePath) 'Final release directory path'
    }
    foreach ($stagedFile in [IO.Directory]::EnumerateFiles($stagePath, '*', [IO.SearchOption]::AllDirectories)) {
        $relativePath = ([IO.Path]::GetFullPath($stagedFile)).Substring($stagePath.Length).TrimStart(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        Assert-LegacyFilePathLength (Join-Path $releasePath $relativePath) 'Final release file path'
    }
}

function Get-ManifestFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$AppDirectory,
        [Parameter(Mandatory = $true)][string]$ManifestValue,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([IO.Path]::IsPathRooted($ManifestValue)) {
        throw "$Description must be relative to its app directory: $ManifestValue"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $AppDirectory $ManifestValue))
    if (-not (Test-ILauncherPathWithin $resolved $AppDirectory)) {
        throw "$Description escapes its app directory: $ManifestValue"
    }
    return $resolved
}

function Invoke-FrameworkDependentPublish {
    param(
        [Parameter(Mandatory = $true)][string]$DotNetExe,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$RelativeRuntime,
        [Parameter(Mandatory = $true)][string]$RuntimeVersion
    )

    Write-Host "Publishing $ProjectPath"
    $arguments = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', $rid,
        '--self-contained', 'false',
        '-p:PublishSingleFile=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:AppHostDotNetSearch=AppRelative',
        "-p:AppHostRelativeDotNet=$RelativeRuntime",
        "-p:RuntimeFrameworkVersion=$RuntimeVersion",
        '-o', $OutputPath
    )
    if ([IO.Path]::GetFileName($ProjectPath) -eq 'i-luncher.csproj') {
        $arguments += '-p:ILuncherIntegrated=true'
    }
    else { $arguments += '-p:ILuncherIntegrated=false' }
    & $DotNetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $ProjectPath" }
}

function Assert-RuntimeConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BundledVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Published runtime configuration is missing: $Path"
    }
    $config = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json
    if ($config.runtimeOptions.tfm -ne $framework) {
        throw "Published app targets '$($config.runtimeOptions.tfm)' but this distribution bundles only $framework. Retarget the app to $framework or distribute its matching shared runtime separately: $Path"
    }
    $requestedFrameworks = @()
    if ($config.runtimeOptions.framework) { $requestedFrameworks += $config.runtimeOptions.framework }
    if ($config.runtimeOptions.frameworks) { $requestedFrameworks += @($config.runtimeOptions.frameworks) }
    if ($config.runtimeOptions.includedFrameworks) { $requestedFrameworks += @($config.runtimeOptions.includedFrameworks) }
    $requiredNames = @('Microsoft.NETCore.App')
    if ($requestedFrameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App') { $requiredNames += 'Microsoft.WindowsDesktop.App' }
    foreach ($requiredName in $requiredNames) {
        $entry = $requestedFrameworks | Where-Object name -EQ $requiredName | Select-Object -First 1
        if (-not $entry) { throw "Runtime configuration does not declare $requiredName`: $Path" }
        try { $requiredVersion = [Version]$entry.version } catch { throw "Runtime configuration contains an invalid version for $requiredName`: $Path" }
        $availableVersion = [Version]$BundledVersion
        if ($requiredVersion.Major -ne 10 -or $requiredVersion.Minor -ne 0 -or $availableVersion -lt $requiredVersion) {
            throw "Bundled runtime $BundledVersion does not satisfy $requiredName $requiredVersion`: $Path"
        }
    }
}

function Assert-PublishedApplicationFiles {
    param(
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [Parameter(Mandatory = $true)][string]$RuntimeVersion,
        [switch]$RequireWindowsDesktop
    )

    $baseName = [IO.Path]::GetFileNameWithoutExtension($ExecutableName)
    foreach ($requiredName in @(
        $ExecutableName,
        "$baseName.dll",
        "$baseName.deps.json",
        "$baseName.runtimeconfig.json")) {
        $requiredPath = Join-Path $OutputPath $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Published application file is missing: $requiredPath"
        }
    }
    $runtimeConfigPath = Join-Path $OutputPath "$baseName.runtimeconfig.json"
    Assert-RuntimeConfig $runtimeConfigPath $RuntimeVersion
    if ($RequireWindowsDesktop) {
        $config = Get-Content -Raw -Encoding UTF8 -LiteralPath $runtimeConfigPath | ConvertFrom-Json
        $frameworks = @($config.runtimeOptions.framework)
        if ($config.runtimeOptions.frameworks) { $frameworks += @($config.runtimeOptions.frameworks) }
        if ($config.runtimeOptions.includedFrameworks) { $frameworks += @($config.runtimeOptions.includedFrameworks) }
        if (-not ($frameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App')) {
            throw "Launcher runtime configuration does not declare Microsoft.WindowsDesktop.App: $runtimeConfigPath"
        }
    }
}

function New-ReleaseAppManifest {
    param(
        [Parameter(Mandatory = $true)]$SourceManifest,
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [Parameter(Mandatory = $true)][string]$IconName
    )

    $releaseManifest = [ordered]@{}
    foreach ($property in $SourceManifest.PSObject.Properties) {
        if ($property.Name -in @('build', 'install')) { continue }
        if ($property.Name -eq 'executable') {
            $releaseManifest.executable = $ExecutableName
        }
        elseif ($property.Name -eq 'icon') {
            $releaseManifest.icon = $IconName
        }
        else {
            $releaseManifest[$property.Name] = $property.Value
        }
    }
    return $releaseManifest
}

function Write-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$RuntimeVersion
    )

    Assert-ILauncherNoReparseTree $PackagePath 'Distribution bundle'
    $packageRoot = Get-ILauncherFullPath $PackagePath
    $prefix = $packageRoot + [IO.Path]::DirectorySeparatorChar
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    $files = [Collections.Generic.List[object]]::new()
    foreach ($filePath in [IO.Directory]::EnumerateFiles($packageRoot, '*', [IO.SearchOption]::AllDirectories)) {
        $fullPath = [IO.Path]::GetFullPath($filePath)
        if ($fullPath.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package file escaped the bundle: $fullPath"
        }
        $relativePath = $fullPath.Substring($prefix.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
        if (($relativePath -split '/') -contains 'bin' -or
            ($relativePath -split '/') -contains 'obj' -or
            ($relativePath -split '/') -contains '.git') {
            throw "Build or source-control output must not be packaged: $relativePath"
        }
        $fileInfo = Get-Item -LiteralPath $fullPath
        $files.Add([ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullPath).Hash.ToLowerInvariant()
            size = $fileInfo.Length
        })
    }

    $sortedFiles = @($files | Sort-Object { $_.path })
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        createdUtc = [DateTime]::UtcNow.ToString('o')
        rid = $rid
        framework = $framework
        runtimeVersion = $RuntimeVersion
        files = $sortedFiles
    }
    Write-Utf8Json $manifest $manifestPath
}

function Write-LatestDistribution {
    param(
        [Parameter(Mandatory = $true)][string]$Bundle,
        [Parameter(Mandatory = $true)][string]$Zip
    )

    $latestPath = Join-Path $distRoot 'latest-distribution.json'
    $temporaryPath = Join-Path $distRoot ('.latest-distribution-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $latest = [ordered]@{
        schemaVersion = 1
        version = $Version
        path = [IO.Path]::GetFullPath($Bundle)
        zipPath = [IO.Path]::GetFullPath($Zip)
    }
    try {
        Write-Utf8Json $latest $temporaryPath
        if (Test-Path -LiteralPath $latestPath) {
            if (-not (Test-Path -LiteralPath $latestPath -PathType Leaf)) {
                throw "Latest distribution marker is not a file: $latestPath"
            }
            [IO.File]::Replace($temporaryPath, $latestPath, [System.Management.Automation.Language.NullString]::Value)
        }
        else {
            [IO.File]::Move($temporaryPath, $latestPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

if (Test-Path -LiteralPath $releasePath) {
    throw "Release already exists; choose a unique version: $releasePath"
}
[IO.Directory]::CreateDirectory($releasesRoot) | Out-Null
Assert-ChildPath $stagePath $releasesRoot 'Distribution staging directory'
if (Test-Path -LiteralPath $stagePath) { throw "Fresh staging directory already exists: $stagePath" }

$resolvedDotNetRoot = Resolve-DotNetRoot $DotNetRoot
$dotnetExe = Join-Path $resolvedDotNetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) { throw ".NET host not found: $dotnetExe" }
Assert-ILauncherNoReparseAncestors $resolvedDotNetRoot '.NET root'
Assert-X64Executable $dotnetExe
$runtimeVersion = Get-InstalledRuntimeVersion $resolvedDotNetRoot
Assert-LegacyDirectoryPathLength $stagePath 'Distribution staging directory path'
Assert-LegacyDirectoryPathLength $bundlePath 'Staged bundle directory path'
Assert-LegacyDirectoryPathLength $releasePath 'Final release directory path'
Assert-LegacyDirectoryPathLength $finalBundlePath 'Final bundle directory path'
Assert-LegacyFilePathLength $stagedZipPath 'Staged ZIP path'
Assert-LegacyFilePathLength $finalZipPath 'Final ZIP path'
Assert-RuntimeDestinationPathLengths $resolvedDotNetRoot $runtimeVersion

try {
    [IO.Directory]::CreateDirectory($bundlePath) | Out-Null

    $launcherProject = Join-Path $rootPath 'Launcher\i-luncher.csproj'
    if (-not (Test-Path -LiteralPath $launcherProject -PathType Leaf)) {
        throw "Launcher project not found: $launcherProject"
    }
    Invoke-FrameworkDependentPublish $dotnetExe $launcherProject $bundlePath 'runtime' $runtimeVersion
    Assert-PublishedApplicationFiles $bundlePath 'i-luncher.exe' $runtimeVersion -RequireWindowsDesktop
    foreach ($module in @('FolderCompareWPF.dll', 'SwiftCopy.dll', 'SwiftCopy.Core.dll', 'DiskAtlas.dll',
        'DiskAtlas.Core.dll', 'DeskCal.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bundlePath $module) -PathType Leaf)) {
            throw "Integrated application module is missing: $module"
        }
    }

    $rootConfig = [ordered]@{
        schemaVersion = 1
        distribution = [ordered]@{
            integrated = $true
            version = $Version
            runtime = 'runtime'
            framework = $framework
            runtimeVersion = $runtimeVersion
        }
    }
    Write-Utf8Json $rootConfig (Join-Path $bundlePath 'i-luncher.json')

    $appCount = 4
    foreach ($appDirectoryPath in Get-SourceAppDirectories $rootPath) {
        $appDirectory = Get-Item -LiteralPath $appDirectoryPath
        $sourceManifestPath = Join-Path $appDirectory.FullName 'launcher.app.json'
        if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { continue }
        $sourceManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $sourceManifestPath | ConvertFrom-Json
        if ($sourceManifest.id -in @('folder-compare', 'swift-copy', 'disk-atlas', 'deskcal')) {
            continue
        }
        if (-not $sourceManifest.id -or -not $sourceManifest.name) {
            throw "App manifest requires id and name: $sourceManifestPath"
        }
        if (-not $sourceManifest.build.project) {
            throw "Source app manifest requires an explicit build.project: $sourceManifestPath"
        }
        if (-not $sourceManifest.executable) { throw "App manifest requires executable: $sourceManifestPath" }

        $projectPath = Get-ManifestFilePath $appDirectory.FullName ([string]$sourceManifest.build.project) 'build.project'
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "App project not found: $projectPath" }
        $iconPath = $null
        if ($sourceManifest.icon) {
            $iconPath = Get-ManifestFilePath $appDirectory.FullName ([string]$sourceManifest.icon) 'icon'
            if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "App icon not found: $iconPath" }
        }
        $executableName = [IO.Path]::GetFileName([string]$sourceManifest.executable)
        if (-not $executableName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
            throw "App manifest executable must name an .exe file: $sourceManifestPath"
        }

        $appOutputPath = Join-Path $bundlePath $appDirectory.Name
        [IO.Directory]::CreateDirectory($appOutputPath) | Out-Null
        Invoke-FrameworkDependentPublish $dotnetExe $projectPath $appOutputPath '../runtime' $runtimeVersion
        $publishedExecutable = Join-Path $appOutputPath $executableName
        Assert-PublishedApplicationFiles $appOutputPath $executableName $runtimeVersion

        $iconName = ''
        if ($iconPath) {
            $iconName = [IO.Path]::GetFileName($iconPath)
            $packagedIconPath = Join-Path $appOutputPath $iconName
            if (-not ([IO.Path]::GetFullPath($iconPath)).Equals([IO.Path]::GetFullPath($packagedIconPath), [StringComparison]::OrdinalIgnoreCase)) {
                [IO.File]::Copy($iconPath, $packagedIconPath, $true)
            }
        }
        $releaseAppManifest = New-ReleaseAppManifest $sourceManifest $executableName $iconName
        Write-Utf8Json $releaseAppManifest (Join-Path $appOutputPath 'launcher.app.json')
        $appCount++
    }
    foreach ($sourceApp in Find-SourceAppProjects $rootPath) {
        $appOutputPath = Join-Path $bundlePath $sourceApp.Id
        [IO.Directory]::CreateDirectory($appOutputPath) | Out-Null
        Invoke-FrameworkDependentPublish $dotnetExe $sourceApp.ProjectPath $appOutputPath '../runtime' $runtimeVersion
        Assert-PublishedApplicationFiles $appOutputPath $sourceApp.ExecutableName $runtimeVersion
        $releaseAppManifest = [ordered]@{
            id = $sourceApp.Id
            name = $sourceApp.Name
            executable = $sourceApp.ExecutableName
            category = 'Apps'
        }
        Write-Utf8Json $releaseAppManifest (Join-Path $appOutputPath 'launcher.app.json')
        $appCount++
    }
    if ($appCount -eq 0) { throw 'No source app manifests were found to publish.' }

    $runtimePath = Join-Path $bundlePath 'runtime'
    [IO.Directory]::CreateDirectory($runtimePath) | Out-Null
    foreach ($licenseName in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $licensePath = Join-Path $resolvedDotNetRoot $licenseName
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw ".NET license file is missing: $licensePath"
        }
        [IO.File]::Copy($licensePath, (Join-Path $runtimePath $licenseName), $false)
    }
    [IO.File]::Copy($dotnetExe, (Join-Path $runtimePath 'dotnet.exe'), $false)
    Copy-DirectoryContents `
        (Join-Path $resolvedDotNetRoot "host\fxr\$runtimeVersion") `
        (Join-Path $runtimePath "host\fxr\$runtimeVersion")
    Copy-DirectoryContents `
        (Join-Path $resolvedDotNetRoot "shared\Microsoft.NETCore.App\$runtimeVersion") `
        (Join-Path $runtimePath "shared\Microsoft.NETCore.App\$runtimeVersion")
    Copy-DirectoryContents `
        (Join-Path $resolvedDotNetRoot "shared\Microsoft.WindowsDesktop.App\$runtimeVersion") `
        (Join-Path $runtimePath "shared\Microsoft.WindowsDesktop.App\$runtimeVersion")

    $startCommand = "@echo off`r`nstart `"`" `"%~dp0i-luncher.exe`" %*`r`n"
    [IO.File]::WriteAllText((Join-Path $bundlePath 'Start-Launcher.cmd'), $startCommand, [Text.Encoding]::ASCII)
    foreach ($installerName in @('Install.ps1', 'Install.cmd')) {
        $installerPath = Join-Path $rootPath "Distribution\$installerName"
        if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
            throw "Consolidated installer is missing: $installerPath"
        }
        [IO.File]::Copy($installerPath, (Join-Path $bundlePath $installerName), $false)
    }

    Write-ReleaseManifest $bundlePath $runtimeVersion
    Compress-Archive -LiteralPath $bundlePath -DestinationPath $stagedZipPath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $stagedZipPath -PathType Leaf)) { throw "ZIP creation failed: $stagedZipPath" }

    Assert-PromotionPathLengths
    Assert-ChildPath $releasePath $releasesRoot 'Release directory'
    Move-Item -LiteralPath $stagePath -Destination $releasePath
    Write-LatestDistribution $finalBundlePath $finalZipPath

    Write-Host "Published distribution: $finalBundlePath"
    Write-Host "Published ZIP: $finalZipPath"
    Write-Host "Bundled .NET runtime: $runtimeVersion"
}
catch {
    $publishError = $_
    try { Remove-OwnedStage $stagePath } catch { Write-Warning "Could not clean distribution staging directory: $($_.Exception.Message)" }
    throw $publishError
}
