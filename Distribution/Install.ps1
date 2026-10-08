[CmdletBinding()]
param(
    [Parameter()]
    [string]$PackageRoot = $PSScriptRoot,

    [Parameter()]
    [Alias('InstallRoot')]
    [string]$PerUserInstallRoot = (Join-Path $env:LOCALAPPDATA 'i-luncher'),

    [Parameter()]
    [Alias('StartMenuRoot')]
    [string]$StartMenuPath = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path is empty.'
    }

    return [IO.Path]::GetFullPath($Path)
}

function Assert-SafeVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version.Length -gt 64 -or $Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw "The release version is not safe for an installation directory: $Version"
    }

    if ($Version.EndsWith('.') -or $Version.EndsWith(' ') -or
        $Version -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
        throw "The release version is not safe for Windows: $Version"
    }
}

function ConvertTo-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains(':')) {
        throw "The release manifest contains an unsafe path: $Path"
    }

    $normalized = $Path.Replace('/', '\')
    $segments = $normalized.Split([char]'\')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "The release manifest contains an unsafe path: $Path"
        }
    }

    return [string]::Join('\', $segments)
}

function Get-ContainedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $fullRoot = Get-FullPath $Root
    $fullPath = Get-FullPath (Join-Path $fullRoot $RelativePath)
    $prefix = $fullRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A path escapes its expected root: $RelativePath"
    }

    return $fullPath
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $fullPath = Get-FullPath $Path
    $fullParent = Get-FullPath $Parent
    $prefix = $fullParent.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description is outside its expected parent: $fullPath"
    }
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $current = Get-FullPath $Path
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            throw "$Description has no existing filesystem ancestor: $Path"
        }
        $current = $parent
    }

    while (-not [string]::IsNullOrEmpty($current)) {
        $item = Get-Item -Force -LiteralPath $current
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description uses a reparse-point ancestor: $($item.FullName)"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            break
        }
        $current = $parent
    }
}

function Assert-NoReparsePoints {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $rootItem = Get-Item -Force -LiteralPath $Root
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed in the release bundle: $($rootItem.FullName)"
    }

    foreach ($item in @(Get-ChildItem -Force -LiteralPath $Root -Recurse)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in the release bundle: $($item.FullName)"
        }
    }
}

function Assert-WindowsPathLength {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [bool]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $fullPath = Get-FullPath $Path
    $maximumLength = if ($Directory) { 247 } else { 259 }
    if ($fullPath.Length -gt $maximumLength) {
        throw "$Description exceeds the Windows PowerShell path limit ($($fullPath.Length) characters): $fullPath"
    }
}

function Assert-PackageDestinationLengths {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Package,

        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Assert-WindowsPathLength -Path $DestinationRoot -Directory $true -Description "$Description root"
    foreach ($entry in $Package.Entries) {
        $destinationPath = Get-ContainedPath -Root $DestinationRoot -RelativePath $entry.RelativePath
        Assert-WindowsPathLength -Path (Split-Path -Parent $destinationPath) -Directory $true -Description "$Description directory"
        Assert-WindowsPathLength -Path $destinationPath -Directory $false -Description "$Description file"
    }
    $destinationManifest = Join-Path $DestinationRoot 'release-manifest.json'
    Assert-WindowsPathLength -Path $destinationManifest -Directory $false -Description "$Description manifest"
}

function Remove-OwnedShortcutFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ShortcutDirectory
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }
    Assert-PathWithin -Path $Path -Parent $ShortcutDirectory -Description 'Temporary shortcut file'
    $name = [IO.Path]::GetFileName($Path)
    if (-not $name.StartsWith('.il-', [StringComparison]::Ordinal) -or
        (-not $name.EndsWith('.tmp.lnk', [StringComparison]::Ordinal) -and
         -not $name.EndsWith('.backup.lnk', [StringComparison]::Ordinal))) {
        throw "Refusing to remove an unexpected shortcut file: $Path"
    }
    Assert-NoReparseAncestors -Path $ShortcutDirectory -Description 'Start Menu directory'
    $item = Get-Item -Force -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove a shortcut reparse point: $Path"
    }
    Remove-Item -Force -LiteralPath $Path
}

function Read-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fullRoot = Get-FullPath $Root
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "The release bundle directory does not exist: $fullRoot"
    }
    Assert-NoReparsePoints $fullRoot

    $launcherPath = Join-Path $fullRoot 'i-luncher.exe'
    $configurationPath = Join-Path $fullRoot 'i-luncher.json'
    $manifestPath = Join-Path $fullRoot 'release-manifest.json'
    foreach ($requiredFile in @($launcherPath, $configurationPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "The release bundle is missing a required file: $requiredFile"
        }
    }

    try {
        $configuration = Get-Content -Raw -Encoding UTF8 -LiteralPath $configurationPath | ConvertFrom-Json
    }
    catch {
        throw "i-luncher.json is not valid JSON: $($_.Exception.Message)"
    }

    if ([long]$configuration.schemaVersion -ne 1 -or $null -eq $configuration.distribution) {
        throw 'i-luncher.json is not a schema version 1 distribution bundle.'
    }

    $version = [string]$configuration.distribution.version
    Assert-SafeVersion $version
    $runtimeName = [string]$configuration.distribution.runtime
    $framework = [string]$configuration.distribution.framework
    $runtimeVersion = [string]$configuration.distribution.runtimeVersion
    if ($runtimeName -cne 'runtime' -or $framework -cne 'net10.0' -or
        $runtimeVersion -notmatch '^10\.[0-9]+(?:\.[0-9]+){0,2}(?:[-+][0-9A-Za-z.-]+)?$') {
        throw 'i-luncher.json contains unsupported distribution runtime metadata.'
    }

    $runtimePath = Join-Path $fullRoot 'runtime'
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Container)) {
        throw "The release bundle is missing its shared runtime directory: $runtimePath"
    }

    try {
        $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "release-manifest.json is not valid JSON: $($_.Exception.Message)"
    }

    if ([long]$manifest.schemaVersion -ne 1 -or [string]$manifest.version -cne $version -or
        [string]$manifest.rid -cne 'win-x64' -or [string]$manifest.framework -cne $framework -or
        [string]$manifest.runtimeVersion -cne $runtimeVersion -or $null -eq $manifest.files) {
        throw 'release-manifest.json does not match the distribution metadata.'
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $listedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relativePath = ConvertTo-SafeRelativePath ([string]$entry.path)
        if ($relativePath -ieq 'release-manifest.json' -or -not $listedPaths.Add($relativePath)) {
            throw "release-manifest.json contains a duplicate or reserved path: $relativePath"
        }

        $expectedHash = [string]$entry.sha256
        $expectedSize = 0L
        if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or -not [long]::TryParse([string]$entry.size, [ref]$expectedSize) -or $expectedSize -lt 0) {
            throw "release-manifest.json contains invalid file metadata: $relativePath"
        }

        $sourcePath = Get-ContainedPath -Root $fullRoot -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "A file listed in release-manifest.json is missing: $relativePath"
        }
        $sourceItem = Get-Item -Force -LiteralPath $sourcePath
        if ($sourceItem.Length -ne $expectedSize) {
            throw "Release file size verification failed: $relativePath"
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
        if ($actualHash -ine $expectedHash) {
            throw "Release file hash verification failed: $relativePath"
        }

        $entries.Add([pscustomobject]@{
            RelativePath = $relativePath
            Hash = $expectedHash.ToLowerInvariant()
            Size = $expectedSize
        })
    }

    foreach ($requiredRelativePath in @('i-luncher.exe', 'i-luncher.json', 'Install.ps1', 'Install.cmd')) {
        if (-not $listedPaths.Contains($requiredRelativePath)) {
            throw "release-manifest.json does not list a required file: $requiredRelativePath"
        }
    }
    foreach ($requiredRuntimePath in @(
        'runtime\dotnet.exe',
        "runtime\host\fxr\$runtimeVersion\hostfxr.dll",
        "runtime\shared\Microsoft.NETCore.App\$runtimeVersion\coreclr.dll"
    )) {
        if (-not $listedPaths.Contains($requiredRuntimePath)) {
            throw "release-manifest.json does not list a required shared runtime file: $requiredRuntimePath"
        }
    }
    if (-not @($entries | Where-Object { $_.RelativePath.StartsWith('runtime\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw 'release-manifest.json does not list any shared runtime files.'
    }

    $actualPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-ChildItem -Force -File -LiteralPath $fullRoot -Recurse)) {
        $relativePath = $file.FullName.Substring($fullRoot.TrimEnd('\').Length).TrimStart('\').Replace('/', '\')
        if ($relativePath -ine 'release-manifest.json') {
            [void]$actualPaths.Add($relativePath)
        }
    }
    if ($actualPaths.Count -ne $listedPaths.Count) {
        throw 'release-manifest.json does not cover every file in the release bundle.'
    }
    foreach ($relativePath in $actualPaths) {
        if (-not $listedPaths.Contains($relativePath)) {
            throw "release-manifest.json does not list a bundle file: $relativePath"
        }
    }

    return [pscustomobject]@{
        Root = $fullRoot
        Version = $version
        Entries = $entries
        ManifestPath = $manifestPath
    }
}

function Copy-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Package,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($entry in $Package.Entries) {
        $sourcePath = Get-ContainedPath -Root $Package.Root -RelativePath $entry.RelativePath
        $destinationPath = Get-ContainedPath -Root $Destination -RelativePath $entry.RelativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        [void][IO.Directory]::CreateDirectory($destinationDirectory)

        $source = [IO.File]::Open($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $target = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $source.CopyTo($target)
            }
            finally {
                $target.Dispose()
            }
        }
        finally {
            $source.Dispose()
        }
    }

    [IO.File]::Copy($Package.ManifestPath, (Join-Path $Destination 'release-manifest.json'), $false)
}

function Assert-IdenticalInstalledRelease {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SourcePackage,

        [Parameter(Mandatory = $true)]
        [string]$InstalledRoot
    )

    $installedPackage = Read-ReleasePackage $InstalledRoot
    $sourceManifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $SourcePackage.ManifestPath).Hash
    $installedManifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installedPackage.ManifestPath).Hash
    if ($installedPackage.Version -cne $SourcePackage.Version -or $sourceManifestHash -ine $installedManifestHash) {
        throw "Release '$($SourcePackage.Version)' is already installed with different contents. A release version is immutable."
    }
}

function New-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $fullShortcutDirectory = Get-FullPath $ShortcutDirectory
    Assert-WindowsPathLength -Path $fullShortcutDirectory -Directory $true -Description 'Start Menu directory'
    Assert-NoReparseAncestors -Path $fullShortcutDirectory -Description 'Start Menu directory'
    [void][IO.Directory]::CreateDirectory($fullShortcutDirectory)
    Assert-NoReparseAncestors -Path $fullShortcutDirectory -Description 'Start Menu directory'
    $shortcutPath = Join-Path $fullShortcutDirectory 'i-luncher.lnk'
    $shortcutToken = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $temporaryPath = Join-Path $fullShortcutDirectory ('.il-' + $shortcutToken + '.tmp.lnk')
    $backupPath = Join-Path $fullShortcutDirectory ('.il-' + $shortcutToken + '.backup.lnk')
    foreach ($shortcutFilePath in @($shortcutPath, $temporaryPath, $backupPath)) {
        Assert-WindowsPathLength -Path $shortcutFilePath -Directory $false -Description 'Start Menu shortcut file'
    }

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($temporaryPath)
        $shortcut.TargetPath = $ExecutablePath
        $shortcut.WorkingDirectory = Split-Path -Parent $ExecutablePath
        $shortcut.Description = 'i-luncher'
        $shortcut.IconLocation = $ExecutablePath + ',0'
        $shortcut.Save()

        Assert-NoReparseAncestors -Path $fullShortcutDirectory -Description 'Start Menu directory'
        $temporaryItem = Get-Item -Force -LiteralPath $temporaryPath
        if (($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The temporary shortcut is a reparse point: $temporaryPath"
        }
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            $existingShortcut = Get-Item -Force -LiteralPath $shortcutPath
            if (($existingShortcut.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The existing Start Menu shortcut is a reparse point: $shortcutPath"
            }
            [IO.File]::Replace($temporaryPath, $shortcutPath, $backupPath)
            try {
                Remove-OwnedShortcutFile -Path $backupPath -ShortcutDirectory $fullShortcutDirectory
            }
            catch {
                Write-Warning "The previous shortcut backup could not be safely removed: $($_.Exception.Message)"
            }
        }
        else {
            [IO.File]::Move($temporaryPath, $shortcutPath)
        }
    }
    finally {
        if ($null -ne $shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ($null -ne $shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
        try {
            Remove-OwnedShortcutFile -Path $temporaryPath -ShortcutDirectory $fullShortcutDirectory
        }
        catch {
            Write-Warning "The temporary shortcut could not be safely removed: $($_.Exception.Message)"
        }
    }
}

$package = Read-ReleasePackage $PackageRoot
$installRoot = Get-FullPath $PerUserInstallRoot
$releasesRoot = Join-Path $installRoot 'Releases'
$releaseRoot = Get-ContainedPath -Root $releasesRoot -RelativePath $package.Version
$stageName = '.s-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stageRoot = Get-ContainedPath -Root $releasesRoot -RelativePath $stageName
$stageCreated = $false

Assert-WindowsPathLength -Path $installRoot -Directory $true -Description 'Per-user installation root'
Assert-WindowsPathLength -Path $releasesRoot -Directory $true -Description 'Releases directory'
Assert-PackageDestinationLengths -Package $package -DestinationRoot $releaseRoot -Description 'Installed release'
Assert-PackageDestinationLengths -Package $package -DestinationRoot $stageRoot -Description 'Installation staging copy'
$fullStartMenuPath = Get-FullPath $StartMenuPath
Assert-WindowsPathLength -Path $fullStartMenuPath -Directory $true -Description 'Start Menu directory'
foreach ($shortcutFileName in @('i-luncher.lnk', '.il-12345678.tmp.lnk', '.il-12345678.backup.lnk')) {
    Assert-WindowsPathLength -Path (Join-Path $fullStartMenuPath $shortcutFileName) -Directory $false -Description 'Start Menu shortcut file'
}

$mutexBytes = [Text.Encoding]::UTF8.GetBytes($releaseRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexKey = ([BitConverter]::ToString($sha256.ComputeHash($mutexBytes))).Replace('-', '').Substring(0, 32)
}
finally {
    $sha256.Dispose()
}
$mutex = New-Object Threading.Mutex($false, "Local\i-luncher-install-$mutexKey")
$hasMutex = $false
try {
    try {
        $hasMutex = $mutex.WaitOne()
    }
    catch [Threading.AbandonedMutexException] {
        $hasMutex = $true
    }
    if (-not $hasMutex) {
        throw 'Could not acquire the installation lock.'
    }

    Assert-NoReparseAncestors -Path $installRoot -Description 'Per-user installation root'
    [void][IO.Directory]::CreateDirectory($installRoot)
    Assert-NoReparseAncestors -Path $installRoot -Description 'Per-user installation root'
    Assert-NoReparseAncestors -Path $releasesRoot -Description 'Releases directory'
    [void][IO.Directory]::CreateDirectory($releasesRoot)
    Assert-NoReparseAncestors -Path $releasesRoot -Description 'Releases directory'
    if (Test-Path -LiteralPath $releaseRoot) {
        if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
            throw "The release destination is not a directory: $releaseRoot"
        }
        Assert-IdenticalInstalledRelease -SourcePackage $package -InstalledRoot $releaseRoot
    }
    else {
        Assert-PathWithin -Path $stageRoot -Parent $releasesRoot -Description 'Installation staging directory'
        Assert-NoReparseAncestors -Path $stageRoot -Description 'Installation staging directory'
        if (Test-Path -LiteralPath $stageRoot) {
            throw "The fresh installation staging directory already exists: $stageRoot"
        }
        [void][IO.Directory]::CreateDirectory($stageRoot)
        Assert-NoReparseAncestors -Path $stageRoot -Description 'Installation staging directory'
        $stageCreated = $true
        Copy-ReleasePackage -Package $package -Destination $stageRoot
        Assert-IdenticalInstalledRelease -SourcePackage $package -InstalledRoot $stageRoot
        Assert-PathWithin -Path $stageRoot -Parent $releasesRoot -Description 'Installation staging directory'
        Assert-NoReparseAncestors -Path $stageRoot -Description 'Installation staging directory'
        Assert-NoReparsePoints -Root $stageRoot
        Assert-NoReparseAncestors -Path $releaseRoot -Description 'Release destination'
        [IO.Directory]::Move($stageRoot, $releaseRoot)
        $stageCreated = $false
    }

    New-StartMenuShortcut -ShortcutDirectory $StartMenuPath -ExecutablePath (Join-Path $releaseRoot 'i-luncher.exe')
    Write-Host "i-luncher $($package.Version) was installed for the current user."
    Write-Host "Installed location: $releaseRoot"
}
finally {
    if ($stageCreated -and (Test-Path -LiteralPath $stageRoot -PathType Container)) {
        try {
            Assert-PathWithin -Path $stageRoot -Parent $releasesRoot -Description 'Installation staging directory'
            Assert-NoReparseAncestors -Path $stageRoot -Description 'Installation staging directory'
            Assert-NoReparsePoints -Root $stageRoot
            [IO.Directory]::Delete($stageRoot, $true)
        }
        catch {
            Write-Warning "The installation staging directory could not be safely removed: $($_.Exception.Message)"
        }
    }
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
