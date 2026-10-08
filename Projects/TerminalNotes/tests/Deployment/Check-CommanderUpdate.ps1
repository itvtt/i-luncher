$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixtureDirectory = Join-Path $temporaryDirectory ('TerminalNotes Update ' + [Guid]::NewGuid().ToString('N'))
$fixtureTools = Join-Path $fixtureDirectory 'tools'
$fixtureArtifacts = Join-Path $fixtureDirectory 'artifacts'
$installedDirectory = Join-Path $fixtureArtifacts 'win-x64'
$stagedDirectory = Join-Path $fixtureArtifacts 'win-x64-commander'
$succeeded = $false
try {
    foreach ($directory in @($fixtureTools, $installedDirectory, $stagedDirectory)) {
        [void][IO.Directory]::CreateDirectory($directory)
    }
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'tools\Apply-CommanderUpdate.ps1') -Destination $fixtureTools
    foreach ($name in @('TerminalNotes.exe', 'TerminalNotes.dll', 'TerminalNotes.runtimeconfig.json')) {
        [IO.File]::WriteAllText((Join-Path $installedDirectory $name), 'original build')
        [IO.File]::WriteAllText((Join-Path $stagedDirectory $name), 'updated build')
    }
    $userPlan = Join-Path $fixtureDirectory 'plan.md'
    [IO.File]::WriteAllText($userPlan, 'saved user plan')
    & (Join-Path $fixtureTools 'Apply-CommanderUpdate.ps1')
    $receipt = Get-Content -Raw -LiteralPath (Join-Path $fixtureArtifacts 'commander-update.json') | ConvertFrom-Json
    if ($receipt.state -ne 'applied' -or
        [IO.File]::ReadAllText((Join-Path $installedDirectory 'TerminalNotes.dll')) -ne 'updated build' -or
        [IO.File]::ReadAllText((Join-Path $receipt.backup 'TerminalNotes.dll')) -ne 'original build' -or
        [IO.File]::ReadAllText($userPlan) -ne 'saved user plan' -or
        (Test-Path -LiteralPath $stagedDirectory)) { throw 'The staged update or backup preservation check failed.' }

    # An incomplete stage must fail before moving the installed distribution.
    [void][IO.Directory]::CreateDirectory($stagedDirectory)
    [IO.File]::WriteAllText((Join-Path $stagedDirectory 'TerminalNotes.exe'), 'incomplete')
    $rejected = $false
    try { & (Join-Path $fixtureTools 'Apply-CommanderUpdate.ps1') }
    catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $installedDirectory 'TerminalNotes.dll')) -ne 'updated build') {
        throw 'An incomplete build changed the installed distribution.'
    }
    $succeeded = $true
    Write-Output 'PASS staged update, preserved backup and user plan, incomplete build rejection.'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureDirectory)
    if ($succeeded -and [IO.Path]::GetDirectoryName($resolvedFixture) -eq $temporaryDirectory -and
        [IO.Path]::GetFileName($resolvedFixture).StartsWith('TerminalNotes Update ')) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $fixtureDirectory) { Write-Output "Fixture retained: $fixtureDirectory" }
}
