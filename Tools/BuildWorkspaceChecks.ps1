[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'BuildWorkspace.ps1')

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Write-Fixture([string]$Root, [string]$Relative, [string]$Content) {
    $path = Join-Path $Root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Content)
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ILauncherWorkspaceChecks-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source with spaces'
[IO.Directory]::CreateDirectory($source) | Out-Null
$info = Get-ILauncherWorkspaceInfo -SourcePath $source
try {
    $driveRoot = [IO.Path]::GetPathRoot($source)
    $driveInfo = Get-ILauncherWorkspaceInfo -SourcePath $driveRoot
    Assert-Check ($driveInfo.SourcePath -eq $driveRoot) 'A drive-root source became a drive-relative path.'
    Assert-Check ((Get-ILauncherNormalizedPath $driveRoot) -eq $driveRoot.ToUpperInvariant()) 'Drive-root normalization lost its separator.'
    Write-Fixture $source 'i-luncher.json' '{"schemaVersion":1}'
    Write-Fixture $source 'src\app.cs' 'version one'
    Write-Fixture $source 'old.cs' 'stale source'
    Write-Fixture $source 'artifacts\icon.png' 'asset'
    Write-Fixture $source 'Projects\TerminalNotes\launcher.app.json' '{"build":{"project":"TerminalNotes.csproj","excludeFromWorkspace":["artifacts","future-output"]}}'
    Write-Fixture $source 'Projects\TerminalNotes\artifacts\large-output.bin' 'generated output'
    Write-Fixture $source 'Projects\TerminalNotes\Assets\logo.png' 'project asset'
    Write-Fixture $source 'UnrelatedApp\launcher.app.json' '{malformed json'
    Write-Fixture $source 'Projects\NoBuild\launcher.app.json' '{"build":null}'
    foreach ($relative in @('bin\old.exe', 'obj\old.cache', 'dist\old.exe',
        'nested\BIN\old.dll', 'nested\obj\old.cache', 'nested\dist\old.exe',
        '.vs\locked.cache', '.git\config', '.pullverse_sender_staging\old.cs')) {
        Write-Fixture $source $relative 'excluded'
    }
    Sync-ILauncherWorkspace $info | Out-Null
    Assert-Check ([IO.File]::ReadAllText((Join-Path $info.WorkspacePath 'src\app.cs')) -eq 'version one') 'Source was not copied.'
    Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'artifacts\icon.png')) 'Source assets were omitted.'
    Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'Projects\TerminalNotes\Assets\logo.png')) 'Project assets were omitted.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'Projects\TerminalNotes\artifacts'))) 'App-specific generated artifacts were copied.'
    Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'UnrelatedApp\launcher.app.json')) 'An unrelated malformed manifest blocked copying.'
    foreach ($relative in @('bin', 'obj', 'dist', 'nested\BIN', 'nested\obj', 'nested\dist', '.vs', '.git', '.pullverse_sender_staging')) {
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $info.WorkspacePath $relative))) "Excluded source directory was copied: $relative"
    }
    Write-Host 'PASS: source and assets copied; generated/internal directories excluded at every depth.'

    Write-Fixture $info.WorkspacePath 'Projects\TerminalNotes\future-output\previous-build.bin' 'preserve prior workspace output'
    Sync-ILauncherWorkspace $info | Out-Null
    Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'Projects\TerminalNotes\future-output\previous-build.bin')) 'An excluded prior workspace output was removed.'
    Write-Host 'PASS: missing source output directories are allowed and prior workspace outputs are preserved.'

    Write-Fixture $source 'preflight-probe.txt' 'must not copy on invalid metadata'
    Write-Fixture $source 'Projects\TerminalNotes\existing-file' 'not a directory'
    foreach ($invalidManifest in @(
        '{"build":{"excludeFromWorkspace":[".."]}}',
        '{"build":{"excludeFromWorkspace":[""]}}',
        '{"build":{"excludeFromWorkspace":"artifacts"}}',
        '{"build":{"excludeFromWorkspace":["."]}}',
        '{"build":{"excludeFromWorkspace":["existing-file"]}}')) {
        Write-Fixture $source 'Projects\TerminalNotes\launcher.app.json' $invalidManifest
        $rejected = $false
        try { Sync-ILauncherWorkspace $info | Out-Null }
        catch { $rejected = $true }
        Assert-Check $rejected "Invalid app exclusion metadata was accepted: $invalidManifest"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'preflight-probe.txt'))) 'Invalid exclusion metadata was rejected after files had been copied.'
    }
    Write-Fixture $source 'Projects\TerminalNotes\launcher.app.json' '{"build":{"project":"TerminalNotes.csproj","excludeFromWorkspace":["artifacts","future-output"]}}'
    Sync-ILauncherWorkspace $info | Out-Null
    Write-Host 'PASS: app-specific artifact exclusion preserves root/project assets; invalid exclusions fail before copying.'

    Write-Fixture $info.WorkspacePath 'dist\running.exe' 'external output'
    Write-Fixture $info.WorkspacePath 'nested\obj\incremental.cache' 'keep incremental build'
    $held = [IO.File]::Open((Join-Path $info.WorkspacePath 'dist\running.exe'),
        [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Write-Fixture $source 'src\app.cs' 'version two with different length'
        [IO.File]::Delete((Join-Path $source 'old.cs'))
        Sync-ILauncherWorkspace $info | Out-Null
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'old.cs'))) 'Deleted source was retained in the mirror.'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $info.WorkspacePath 'src\app.cs')) -eq 'version two with different length') 'Updated source was not synchronized.'
        Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'nested\obj\incremental.cache')) 'Incremental output was deleted.'
    }
    finally { $held.Dispose() }
    Assert-Check ([IO.File]::ReadAllText((Join-Path $info.WorkspacePath 'dist\running.exe')) -eq 'external output') 'Existing runtime output changed during sync.'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $source 'bin\old.exe')) -eq 'excluded') 'Original generated files were changed.'
    Write-Host 'PASS: synchronization updates/removes mirrored source while preserving locked external outputs and original files.'

    $rejected = $false
    try { Assert-ILauncherInternalWorkspace -WorkspacePath $source | Out-Null }
    catch { $rejected = $true }
    Assert-Check $rejected 'Internal build bypass was accepted in the original source.'
    $rejected = $false
    try { Assert-ILauncherNoPathOverlap -SourcePath $source -WorkspacePath (Join-Path $source 'child') }
    catch { $rejected = $true }
    Assert-Check $rejected 'Overlapping source and workspace were accepted.'

    $markerText = [IO.File]::ReadAllText($info.MarkerPath)
    try {
        [IO.File]::WriteAllText($info.MarkerPath, '{"version":1,"key":"wrong","normalizedSourcePath":"wrong"}')
        $rejected = $false
        try { Sync-ILauncherWorkspace $info | Out-Null }
        catch { $rejected = $true }
        Assert-Check $rejected 'Mismatched workspace identity was accepted.'
        Assert-Check (Test-Path -LiteralPath (Join-Path $info.WorkspacePath 'dist\running.exe')) 'A rejected sync changed the workspace.'
    }
    finally { [IO.File]::WriteAllText($info.MarkerPath, $markerText) }
    Write-Host 'PASS: in-source bypass, path overlap, and mismatched workspace identity rejected before copying.'
}
finally {
    # Both targets belong to this unique fixture; validate their resolved locations before recursive deletion.
    $expected = Get-ILauncherWorkspaceInfo -SourcePath $source
    if ($info.WorkspacePath -ne $expected.WorkspacePath -or
        -not (Test-ILauncherPathWithin $info.WorkspacePath $expected.WorkspaceBase)) {
        throw 'Refusing cleanup outside the fixture workspace.'
    }
    if (Test-Path -LiteralPath $info.WorkspacePath) {
        Read-ILauncherWorkspaceMarker $info | Out-Null
        Assert-ILauncherNoReparseTree $info.WorkspacePath 'Test workspace'
        Remove-Item -LiteralPath $info.WorkspacePath -Recurse -Force
    }
    if (-not (Test-ILauncherPathWithin $fixture ([IO.Path]::GetTempPath())) -or
        -not ([IO.Path]::GetFileName($fixture)).StartsWith('ILauncherWorkspaceChecks-')) {
        throw 'Refusing cleanup outside the temporary fixture.'
    }
    Assert-ILauncherNoReparseTree $fixture 'Test fixture'
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
