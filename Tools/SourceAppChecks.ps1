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

    $unicodeRelative = "projects/$unicodeFolder"
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $unicodeHash = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($unicodeRelative))).Replace('-', '').ToLowerInvariant().Substring(0, 12) }
    finally { $algorithm.Dispose() }
    $unicodeId = Get-SourceAppStableId $root (Join-Path $root ("Projects\$unicodeFolder"))
    Check ($unicodeId -eq "app-$unicodeHash") 'Unicode-only folder ids use the normalized relative path hash.'
    Check ((Get-SourceAppStableId $root (Join-Path $root 'Projects\Demo')) -eq 'demo') 'ASCII folder ids use the normalized folder name.'

    $discovered = @(Find-SourceAppProjects $root)
    $actualIds = (@($discovered.Id | Sort-Object) -join ',')
    $expectedIds = (@('demo', 'mixed', $unicodeId, 'distributionsample', 'buildtools', 'templatetool', 'cacheviewer') | Sort-Object) -join ','
    Check ($actualIds -eq $expectedIds) 'Discovery includes runnable projects, excludes libraries and test folders, and keeps similarly named app folders.'
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
