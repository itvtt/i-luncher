$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ILauncherSourceAppChecks-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'root'
[IO.Directory]::CreateDirectory($root) | Out-Null
. (Join-Path $PSScriptRoot 'SourceApps.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Host "PASS: $Message"
}
function Write-Project([string]$RelativePath, [string]$OutputType) {
    $path = Join-Path $root $RelativePath
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>$OutputType</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
}
try {
    $unicodeFolder = ([char]0xD55C).ToString() + [char]0xAE00
    Write-Project 'Projects\Demo\Demo.csproj' 'Exe'
    Write-Project ("Projects\$unicodeFolder\Unicode.csproj") 'WinExe'
    Write-Project 'Projects\Mixed\Mixed.csproj' 'Exe'
    Write-Project 'Projects\Mixed\Library\Library.csproj' 'Library'
    Write-Project 'Projects\LibraryOnly\LibraryOnly.csproj' 'Library'
    Write-Project 'Projects\Demo.Tests\Demo.Tests.csproj' 'Exe'
    Write-Project 'Projects\DistributionSample\DistributionSample.csproj' 'Exe'
    Write-Project 'Projects\BuildTools\BuildTools.csproj' 'Exe'
    Write-Project 'Projects\TemplateTool\TemplateTool.csproj' 'Exe'
    Write-Project 'Projects\CacheViewer\CacheViewer.csproj' 'Exe'
    Write-Project 'file_shared\One.csproj' 'Exe'
    Write-Project 'file_shared\Two.csproj' 'WinExe'
    Write-Project 'Projects\file_shared\FutureApp.csproj' 'Exe'
    foreach ($integratedDirectory in @('FolderCompareWPF', 'SwiftCopy', 'DiskAtlas', 'DeskCal')) {
        Write-Project "$integratedDirectory\$integratedDirectory.csproj" 'Exe'
        Write-Project "Projects\$integratedDirectory\$integratedDirectory.csproj" 'Exe'
    }
    Write-Project 'Projects\TerminalNotes\TerminalNotes.csproj' 'Exe'
    Write-Project 'Projects\NestedService\DeskCal\Nested.csproj' 'Exe'

    $unicodeRelative = "projects/$unicodeFolder"
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $unicodeHash = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($unicodeRelative))).Replace('-', '').ToLowerInvariant().Substring(0, 12) }
    finally { $algorithm.Dispose() }
    $unicodeId = Get-SourceAppStableId $root (Join-Path $root ("Projects\$unicodeFolder"))
    Check ($unicodeId -eq "app-$unicodeHash") 'Unicode-only folder ids use the normalized relative path hash.'
    Check ((Get-SourceAppStableId $root (Join-Path $root 'Projects\Demo')) -eq 'demo') 'ASCII folder ids use the normalized folder name.'

    $discovered = @(Find-SourceAppProjects $root)
    $actualIds = (@($discovered.Id | Sort-Object) -join ',')
    $expectedIds = (@('demo', 'mixed', $unicodeId, 'distributionsample', 'buildtools', 'templatetool', 'cacheviewer', 'terminalnotes', 'nestedservice', 'file_shared') | Sort-Object) -join ','
    Check ($actualIds -eq $expectedIds) 'Discovery excludes integrated folders and the root-only shared workspace, while keeping Projects apps and unrelated nested projects.'
    $sharedManifest = Join-Path $root 'file_shared\launcher.app.json'
    [IO.File]::WriteAllText($sharedManifest, '{}')
    $sharedRootIncluded = @((Get-SourceAppDirectories $root) | Where-Object { $_ -ieq (Join-Path $root 'file_shared') }).Count -eq 1
    Check $sharedRootIncluded 'The root file_shared directory is eligible when it has an explicit launcher manifest.'
    Remove-Item -LiteralPath $sharedManifest -Force
    $sharedRootExcluded = @((Get-SourceAppDirectories $root) | Where-Object { $_ -ieq (Join-Path $root 'file_shared') }).Count -eq 0
    Check $sharedRootExcluded 'The root file_shared directory stays excluded when it has no launcher manifest.'
    Check (($discovered | Where-Object Id -eq 'mixed').ProjectPath.EndsWith('Mixed.csproj')) 'A library project beside one runnable project does not make the app ambiguous.'

    Write-Project 'Projects\Ambiguous\One.csproj' 'Exe'
    Write-Project 'Projects\Ambiguous\Two.csproj' 'WinExe'
    $ambiguousRejected = $false
    try { Find-SourceAppProjects $root | Out-Null } catch { $ambiguousRejected = $_.Exception.Message -like '*multiple runnable*' }
    Check $ambiguousRejected 'Multiple runnable projects in one app folder are rejected as ambiguous.'

    $escapeRejected = $false
    try { Assert-SourceAppProjectPath $root '..\outside\Outside.csproj' | Out-Null } catch { $escapeRejected = $_.Exception.Message -like '*escapes*' }
    Check $escapeRejected 'Root-relative project paths cannot escape the source root.'
    $absoluteRejected = $false
    try { Assert-SourceAppProjectPath $root (Join-Path $root 'Projects\Demo\Demo.csproj') | Out-Null } catch { $absoluteRejected = $_.Exception.Message -like '*root-relative*' }
    Check $absoluteRejected 'Absolute project paths are rejected.'

    Write-Project 'Launcher\Launcher.csproj' 'Exe'
    $reservedRejected = $false
    try { Assert-SourceAppProjectPath $root 'Launcher\Launcher.csproj' | Out-Null } catch { $reservedRejected = $_.Exception.Message -like '*reserved*' }
    Check $reservedRejected 'Explicit projects inside launcher infrastructure are rejected.'
    $movedBuiltinRejected = $false
    try { Assert-SourceAppProjectPath $root 'Projects\DeskCal\DeskCal.csproj' | Out-Null } catch { $movedBuiltinRejected = $_.Exception.Message -like '*reserved*' }
    Check $movedBuiltinRejected 'Explicit Projects paths cannot republish a moved integrated app as an extra source app.'
    $nestedAppAllowed = $false
    try { Assert-SourceAppProjectPath $root 'Projects\NestedService\DeskCal\Nested.csproj' | Out-Null; $nestedAppAllowed = $true } catch { }
    Check $nestedAppAllowed 'A nested folder named like a built-in remains valid under an unrelated Projects app.'
    Write-Host "$checks source app checks passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('ILauncherSourceAppChecks-', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned fixture.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
