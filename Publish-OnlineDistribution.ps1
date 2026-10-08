[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Version,

    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$ApiUrl,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$TagName,

    [string]$DotNetRoot,
    [switch]$UseCurrentWorkspace
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$framework = 'net10.0'
$rid = 'win-x64'

if (-not $TagName.Substring(1).Equals($Version, [StringComparison]::Ordinal)) {
    throw 'The online catalog version must match its immutable release tag.'
}

$sourceAppsHelper = Join-Path $PSScriptRoot 'Tools\SourceApps.ps1'
if (-not (Test-Path -LiteralPath $sourceAppsHelper -PathType Leaf)) { throw "Source app helper is missing: $sourceAppsHelper" }
. $sourceAppsHelper
$workspaceHelper = Join-Path $PSScriptRoot 'BuildWorkspace.ps1'
if (-not (Test-Path -LiteralPath $workspaceHelper -PathType Leaf)) { throw "Build workspace helper is missing: $workspaceHelper" }
. $workspaceHelper

$rootPath = [IO.Path]::GetFullPath($PSScriptRoot)
if ($UseCurrentWorkspace) {
    $runId = [string]$env:GITHUB_RUN_ID
    $runAttempt = [string]$env:GITHUB_RUN_ATTEMPT
    $runnerTemp = [string]$env:RUNNER_TEMP
    if ($env:GITHUB_ACTIONS -ne 'true' -or $runId -notmatch '^[0-9]+$' -or
        $runAttempt -notmatch '^[0-9]+$' -or [string]::IsNullOrWhiteSpace($runnerTemp)) {
        throw '-UseCurrentWorkspace requires a validated Actions runner checkout.'
    }
    $expectedCheckout = [IO.Path]::GetFullPath((Join-Path $runnerTemp "i-luncher-source-$runId-$runAttempt"))
    if (-not $rootPath.Equals($expectedCheckout, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The current workspace is not the unique checkout assigned to this Actions run: $rootPath"
    }
    Assert-ILauncherNoReparseTree $rootPath 'Actions checkout'
}

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if (-not $DotNetRoot) {
    $standardRoot = Join-Path $programFiles 'dotnet'
    if (Test-Path -LiteralPath (Join-Path $standardRoot 'dotnet.exe') -PathType Leaf) {
        $DotNetRoot = $standardRoot
    }
    else {
        $dotnetCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $DotNetRoot = Split-Path -Parent $dotnetCommand.Source
    }
}
$dotnetRoot = [IO.Path]::GetFullPath($DotNetRoot)
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) { throw ".NET host is missing: $dotnetExe" }

function Get-OnlineRuntimeVersion {
    param([Parameter(Mandatory = $true)][string]$Root)
    $components = @('host\fxr', 'shared\Microsoft.NETCore.App', 'shared\Microsoft.WindowsDesktop.App')
    $maps = @()
    foreach ($component in $components) {
        $path = Join-Path $Root $component
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Required runtime component is missing: $path" }
        $versions = @{}
        foreach ($directory in Get-ChildItem -LiteralPath $path -Directory) {
            try { $parsed = [Version]$directory.Name } catch { continue }
            if ($parsed.Major -eq 10) { $versions[$directory.Name] = $parsed }
        }
        $maps += ,$versions
    }
    $common = foreach ($version in $maps[0].Keys) {
        if ($maps[1].ContainsKey($version) -and $maps[2].ContainsKey($version)) {
            [pscustomobject]@{ Name = $version; Parsed = $maps[0][$version] }
        }
    }
    $selected = $common | Sort-Object Parsed -Descending | Select-Object -First 1
    if (-not $selected) { throw "No matching .NET 10 x64 desktop runtime is installed under '$Root'." }
    return $selected.Name
}

function Copy-OnlineTree {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Package source directory is missing: $Source" }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in (Get-Item -Force -LiteralPath $Source).GetFileSystemInfos()) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to package reparse point: $($item.FullName)" }
        $target = Join-Path $Destination $item.Name
        if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            Copy-OnlineTree $item.FullName $target
        }
        else {
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Write-OnlineJson {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)][string]$Path)
    $json = $Value | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-OnlineManifestFilePath {
    param([Parameter(Mandatory = $true)][string]$AppDirectory, [Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Description)
    if ([IO.Path]::IsPathRooted($Value)) { throw "$Description must be relative to the app directory: $Value" }
    $resolved = [IO.Path]::GetFullPath((Join-Path $AppDirectory $Value))
    if (-not (Test-ILauncherPathWithin $resolved $AppDirectory)) { throw "$Description escapes its app directory: $Value" }
    return $resolved
}

function New-DeterministicOnlineZip {
    param([Parameter(Mandatory = $true)][string]$SourceDirectory, [Parameter(Mandatory = $true)][string]$DestinationPath)
    Add-Type -AssemblyName System.IO.Compression
    $files = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Force -Recurse | Sort-Object FullName)
    $sourcePrefix = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $output = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to package reparse point: $($file.FullName)" }
                $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $entryStream = $entry.Open()
                    try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose() }
                }
                finally { $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $output.Dispose() }
}

function Remove-OnlineOwnedStaging {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$StagingPath
    )
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $staging = [IO.Path]::GetFullPath($StagingPath)
    if (-not [IO.Path]::GetDirectoryName($staging).Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($staging) -notmatch '^\.online-(?:stage|work)-[0-9a-f]{32}$') {
        throw 'Refusing to remove a staging path outside its exact owned parent directory.'
    }
    Assert-ILauncherNoReparseAncestors $parent 'Online staging parent'
    if (-not (Test-Path -LiteralPath $staging)) { return }
    if (([IO.File]::GetAttributes($staging) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing to remove an unexpected reparse-point staging root.'
    }

    $pending = New-Object 'Collections.Generic.Stack[object]'
    $pending.Push([pscustomobject]@{ Path = $staging; PostOrder = $false })
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        $path = [string]$entry.Path
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { [IO.Directory]::Delete($path, $false) }
            else { [IO.File]::Delete($path) }
            continue
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            [IO.File]::Delete($path)
            continue
        }
        if ($entry.PostOrder) {
            [IO.Directory]::Delete($path, $false)
            continue
        }
        $pending.Push([pscustomobject]@{ Path = $path; PostOrder = $true })
        foreach ($child in [IO.Directory]::GetFileSystemEntries($path)) {
            $pending.Push([pscustomobject]@{ Path = $child; PostOrder = $false })
        }
    }
}

function Get-OnlineIconBase64 {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    $source = $null
    $bitmap = $null
    $graphics = $null
    $stream = $null
    try {
        $source = [Drawing.Image]::FromFile($Path)
        $bitmap = New-Object Drawing.Bitmap 64, 64
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scale = [Math]::Min(64.0 / $source.Width, 64.0 / $source.Height)
        $width = [Math]::Max(1, [int][Math]::Round($source.Width * $scale))
        $height = [Math]::Max(1, [int][Math]::Round($source.Height * $scale))
        $graphics.DrawImage($source, [int]((64 - $width) / 2), [int]((64 - $height) / 2), $width, $height)
        $stream = New-Object IO.MemoryStream
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $pngBytes = $stream.ToArray()
        $icoBytes = New-Object byte[] (22 + $pngBytes.Length)
        $icoBytes[2] = 1
        $icoBytes[4] = 1
        $icoBytes[6] = 64
        $icoBytes[7] = 64
        [BitConverter]::GetBytes([UInt16]1).CopyTo($icoBytes, 10)
        [BitConverter]::GetBytes([UInt16]32).CopyTo($icoBytes, 12)
        [BitConverter]::GetBytes([UInt32]$pngBytes.Length).CopyTo($icoBytes, 14)
        [BitConverter]::GetBytes([UInt32]22).CopyTo($icoBytes, 18)
        [Buffer]::BlockCopy($pngBytes, 0, $icoBytes, 22, $pngBytes.Length)
        return [Convert]::ToBase64String($icoBytes)
    }
    catch {
        Write-Warning "Could not encode app icon '$Path' for the online catalog; the installed executable icon will be used."
        return $null
    }
    finally {
        if ($stream) { $stream.Dispose() }
        if ($graphics) { $graphics.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
        if ($source) { $source.Dispose() }
    }
}

function Publish-OnlineApp {
    param(
        [Parameter(Mandatory = $true)]$Definition,
        [Parameter(Mandatory = $true)][string]$WorkRoot,
        [Parameter(Mandatory = $true)][string]$RuntimeVersion
    )
    $appId = [string]$Definition.id
    if ($appId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$') { throw "App id is not safe for an online package: $appId" }
    $projectPath = [IO.Path]::GetFullPath([string]$Definition.project)
    if (-not (Test-ILauncherPathWithin $projectPath $rootPath) -or -not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "App project is missing or escapes the source root: $projectPath"
    }
    $executableName = [IO.Path]::GetFileName([string]$Definition.executable)
    if (-not $executableName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { throw "App executable must be an .exe: $appId" }

    $outputPath = Join-Path (Join-Path $WorkRoot 'app-output') $appId
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    Write-Host "Publishing online app $appId"
    $arguments = @(
        'publish', $projectPath, '-c', 'Release', '-r', $rid, '--self-contained', 'false',
        '-p:PublishSingleFile=false', '-p:DebugSymbols=false', '-p:DebugType=None',
        '-p:AppHostDotNetSearch=EnvironmentVariable',
        "-p:RuntimeFrameworkVersion=$RuntimeVersion", '-p:ILuncherIntegrated=false', '-o', $outputPath
    )
    & $dotnetExe @arguments 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Publishing app failed: $projectPath" }
    $exePath = Join-Path $outputPath $executableName
    $baseName = [IO.Path]::GetFileNameWithoutExtension($executableName)
    foreach ($required in @($exePath, (Join-Path $outputPath "$baseName.dll"),
        (Join-Path $outputPath "$baseName.deps.json"), (Join-Path $outputPath "$baseName.runtimeconfig.json"))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published app file is missing: $required" }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $outputPath "$baseName.runtimeconfig.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.tfm -ne $framework) { throw "App '$appId' does not target $framework." }

    $iconBase64 = $null
    if ($Definition.icon -and (Test-Path -LiteralPath $Definition.icon -PathType Leaf)) {
        $iconBase64 = Get-OnlineIconBase64 ([string]$Definition.icon)
        $iconName = [IO.Path]::GetFileName([string]$Definition.icon)
        $targetIcon = Join-Path $outputPath $iconName
        if (-not ([IO.Path]::GetFullPath([string]$Definition.icon)).Equals([IO.Path]::GetFullPath($targetIcon), [StringComparison]::OrdinalIgnoreCase)) {
            [IO.File]::Copy([string]$Definition.icon, $targetIcon, $true)
        }
    }

    $appManifest = [ordered]@{
        id = $appId
        name = [string]$Definition.name
        executable = $executableName
        category = [string]$Definition.category
    }
    if ($Definition.description) { $appManifest.description = [string]$Definition.description }
    if ($Definition.icon) { $appManifest.icon = [IO.Path]::GetFileName([string]$Definition.icon) }
    Write-OnlineJson $appManifest (Join-Path $outputPath 'launcher.app.json')

    $assetName = "i-luncher-online-app-$appId-$Version-win-x64.zip"
    $assetPath = Join-Path $WorkRoot $assetName
    New-DeterministicOnlineZip $outputPath $assetPath
    $file = Get-Item -LiteralPath $assetPath
    $result = [ordered]@{
        id = $appId
        name = [string]$Definition.name
        description = [string]$Definition.description
        category = [string]$Definition.category
        executable = $executableName
        asset = $assetName
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
        bytes = [long]$file.Length
    }
    if ($iconBase64) { $result.iconBase64 = $iconBase64 }
    return $result
}

$releaseRoot = Join-Path $rootPath "dist\releases\$Version"
if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) { throw "Run Publish-Distribution.ps1 before the online publisher: $releaseRoot" }
$finalOnlinePath = Join-Path $releaseRoot 'online'
if (Test-Path -LiteralPath $finalOnlinePath) { throw "Online release already exists: $finalOnlinePath" }
$stagePath = Join-Path $releaseRoot ('.online-stage-' + [Guid]::NewGuid().ToString('N'))
$workParent = if (-not [string]::IsNullOrWhiteSpace([string]$env:RUNNER_TEMP)) {
    [IO.Path]::GetFullPath($env:RUNNER_TEMP)
}
elseif (-not [string]::IsNullOrWhiteSpace([string]$env:TEMP)) {
    [IO.Path]::GetFullPath($env:TEMP)
}
else {
    [IO.Path]::GetTempPath()
}
if (-not (Test-Path -LiteralPath $workParent -PathType Container)) { throw "Online staging parent is missing: $workParent" }
Assert-ILauncherNoReparseAncestors $workParent 'Online work directory parent'
$workPath = Join-Path $workParent ('.online-work-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $workPath) { throw "Online work directory already exists: $workPath" }
[IO.Directory]::CreateDirectory($workPath) | Out-Null

try {
    Assert-ILauncherNoReparseAncestors $releaseRoot 'Online release directory'
    if (Test-Path -LiteralPath $stagePath) { throw "Online stage directory already exists: $stagePath" }
    [IO.Directory]::CreateDirectory($stagePath) | Out-Null

    $runtimeVersion = Get-OnlineRuntimeVersion $dotnetRoot
    $definitions = [Collections.Generic.List[object]]::new()
    $manifestFolders = [Collections.Generic.List[string]]::new()
    foreach ($directory in Get-ChildItem -LiteralPath $rootPath -Directory -Force) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($directory.Attributes -band [IO.FileAttributes]::Hidden) -ne 0) { continue }
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'launcher.app.json') -PathType Leaf) { $manifestFolders.Add($directory.FullName) }
        if ($directory.Name -ieq 'Projects') {
            foreach ($projectDirectory in Get-ChildItem -LiteralPath $directory.FullName -Directory -Force) {
                if (($projectDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
                    ($projectDirectory.Attributes -band [IO.FileAttributes]::Hidden) -eq 0 -and
                    (Test-Path -LiteralPath (Join-Path $projectDirectory.FullName 'launcher.app.json') -PathType Leaf)) {
                    $manifestFolders.Add($projectDirectory.FullName)
                }
            }
        }
    }
    foreach ($folder in $manifestFolders) {
        $manifestPath = Join-Path $folder 'launcher.app.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $manifest.id -or -not $manifest.name -or -not $manifest.executable -or -not $manifest.build.project) {
            throw "Online app manifest requires id, name, executable, and build.project: $manifestPath"
        }
        $project = Get-OnlineManifestFilePath $folder ([string]$manifest.build.project) 'build.project'
        $icon = $null
        if ($manifest.icon) {
            $icon = Get-OnlineManifestFilePath $folder ([string]$manifest.icon) 'icon'
            if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "App icon is missing: $icon" }
        }
        $definitions.Add([pscustomobject]@{
            id = [string]$manifest.id; name = [string]$manifest.name; description = [string]$manifest.description
            category = [string]$manifest.category; executable = [string]$manifest.executable
            project = $project; icon = $icon; source = 'manifest'
        })
    }
    foreach ($sourceApp in Find-SourceAppProjects $rootPath) {
        $definitions.Add([pscustomobject]@{
            id = [string]$sourceApp.Id; name = [string]$sourceApp.Name; description = ''; category = 'Apps'
            executable = [string]$sourceApp.ExecutableName; project = [string]$sourceApp.ProjectPath
            icon = $null; source = 'discovered'
        })
    }
    $duplicates = @($definitions | Group-Object id | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "Duplicate online app id: $($duplicates[0].Name)" }
    if ($definitions.Count -lt 1) { throw 'No online apps were discovered.' }

    $apps = [Collections.Generic.List[object]]::new()
    foreach ($definition in $definitions) {
        $apps.Add((Publish-OnlineApp $definition $workPath $runtimeVersion))
    }

    $runtimeSource = Join-Path $workPath 'runtime-source'
    [IO.Directory]::CreateDirectory($runtimeSource) | Out-Null
    foreach ($license in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $licensePath = Join-Path $dotnetRoot $license
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw ".NET license file is missing: $licensePath" }
        [IO.File]::Copy($licensePath, (Join-Path $runtimeSource $license), $false)
    }
    [IO.File]::Copy($dotnetExe, (Join-Path $runtimeSource 'dotnet.exe'), $false)
    Copy-OnlineTree (Join-Path $dotnetRoot "host\fxr\$runtimeVersion") (Join-Path $runtimeSource "host\fxr\$runtimeVersion")
    Copy-OnlineTree (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$runtimeVersion") (Join-Path $runtimeSource "shared\Microsoft.NETCore.App\$runtimeVersion")
    Copy-OnlineTree (Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App\$runtimeVersion") (Join-Path $runtimeSource "shared\Microsoft.WindowsDesktop.App\$runtimeVersion")
    $runtimeAssetName = "i-luncher-online-runtime-$runtimeVersion-win-x64.zip"
    $runtimeAssetPath = Join-Path $workPath $runtimeAssetName
    New-DeterministicOnlineZip $runtimeSource $runtimeAssetPath
    $runtimeFile = Get-Item -LiteralPath $runtimeAssetPath

    $catalog = [ordered]@{
        schemaVersion = 1
        version = $Version
        source = [ordered]@{ serverUrl = $ServerUrl.TrimEnd('/'); apiUrl = $ApiUrl.TrimEnd('/'); repository = $Repository; tagName = $TagName }
        runtime = [ordered]@{
            version = $runtimeVersion
            asset = $runtimeAssetName
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeAssetPath).Hash.ToLowerInvariant()
            bytes = [long]$runtimeFile.Length
        }
        apps = @($apps.ToArray())
    }
    $catalogPath = Join-Path $workPath "i-luncher-online-catalog-$Version.json"
    Write-OnlineJson $catalog $catalogPath
    $launcherPath = Join-Path $workPath 'i-luncher-start.exe'
    $bootstrapperBuilder = Join-Path $rootPath 'Bootstrapper\Build.ps1'
    if (-not (Test-Path -LiteralPath $bootstrapperBuilder -PathType Leaf)) { throw "Bootstrapper builder is missing: $bootstrapperBuilder" }
    & $bootstrapperBuilder -CatalogPath $catalogPath -OutputPath $launcherPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) { throw 'Online bootstrapper build failed.' }
    & $launcherPath --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Online bootstrapper self-test failed.' }

    foreach ($asset in Get-ChildItem -LiteralPath $workPath -File | Where-Object Extension -in @('.exe', '.json', '.zip')) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($asset.FullName + '.sha256', "$hash  $($asset.Name)`n", [Text.UTF8Encoding]::new($false))
    }
    foreach ($file in Get-ChildItem -LiteralPath $workPath -File) {
        [IO.File]::Move($file.FullName, (Join-Path $stagePath $file.Name))
    }
    Remove-OnlineOwnedStaging $workParent $workPath
    [IO.Directory]::Move($stagePath, $finalOnlinePath)

    Write-Host "Published online bootstrapper and app packages: $finalOnlinePath"
    Write-Host "Runtime package: $runtimeAssetName ($([Math]::Round($runtimeFile.Length / 1MB, 1)) MiB compressed)"
    Write-Host "Online apps: $($apps.Count)"
}
catch {
    if (Test-Path -LiteralPath $workPath) {
        Remove-OnlineOwnedStaging $workParent $workPath
    }
    if (Test-Path -LiteralPath $stagePath) {
        Remove-OnlineOwnedStaging $releaseRoot $stagePath
    }
    throw
}
