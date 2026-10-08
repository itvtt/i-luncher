# Applies the staged build only after the current application has exited.
# Keeps the previous distribution for recovery; never terminates an app or deletes files.
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = (Resolve-Path -LiteralPath (Join-Path $projectDirectory 'artifacts')).Path
$installedDirectory = Join-Path $artifactsDirectory 'win-x64'
$stagedDirectory = Join-Path $artifactsDirectory 'win-x64-commander'
$installedExecutable = Join-Path $installedDirectory 'TerminalNotes.exe'
$receiptPath = Join-Path $artifactsDirectory 'commander-update.json'
$lockPath = Join-Path $artifactsDirectory 'commander-update.lock'
$updateLock = $null
$backupDirectory = $null

function Assert-StateFile([string] $path) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Update state must be an ordinary file: $path"
        }
    }
}

function Assert-DistributionDirectory([string] $directory, [string] $expectedLeaf) {
    $resolved = (Resolve-Path -LiteralPath $directory).Path
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), $artifactsDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFileName($resolved), $expectedLeaf, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected distribution target: $resolved"
    }
    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Distribution targets must be real directories: $resolved"
    }
    foreach ($name in @('TerminalNotes.exe', 'TerminalNotes.dll', 'TerminalNotes.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $resolved $name) -PathType Leaf)) {
            throw "Incomplete distribution: $resolved"
        }
    }
}

function Write-UpdateReceipt([string] $state, [string] $detail) {
    Assert-StateFile $receiptPath
    @{ state = $state; detail = $detail; backup = $backupDirectory; updatedAt = [DateTimeOffset]::Now.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
}

try {
    Assert-StateFile $lockPath
    Assert-StateFile $receiptPath
    $updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Assert-DistributionDirectory $installedDirectory 'win-x64'
    Assert-DistributionDirectory $stagedDirectory 'win-x64-commander'
    Write-UpdateReceipt 'waiting' 'Waiting for the existing Terminal Notes application to exit.'
    do {
        # Query local process handles; WMI may be unavailable to a standard/restricted user.
        # An unreadable live process path aborts the update instead of assuming it is safe to replace.
        $runningInstances = @()
        foreach ($instance in [Diagnostics.Process]::GetProcessesByName('TerminalNotes')) {
            try {
                if ($instance.HasExited) { continue }
                $instancePath = $instance.MainModule.FileName
                if ([string]::IsNullOrWhiteSpace($instancePath)) { throw 'Cannot verify a running TerminalNotes process path.' }
                if ([string]::Equals($instancePath, $installedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
                    $runningInstances += $instance.Id
                }
            }
            finally { $instance.Dispose() }
        }
        if ($runningInstances.Count -gt 0) { Start-Sleep -Seconds 1 }
    } while ($runningInstances.Count -gt 0)

    # Resolve again immediately before the two recoverable directory moves.
    Assert-DistributionDirectory $installedDirectory 'win-x64'
    Assert-DistributionDirectory $stagedDirectory 'win-x64-commander'
    $backupDirectory = Join-Path $artifactsDirectory ('win-x64-before-commander-' + [Guid]::NewGuid().ToString('N'))
    $resolvedBackup = [IO.Path]::GetFullPath($backupDirectory)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolvedBackup), $artifactsDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $resolvedBackup)) { throw 'Invalid backup destination.' }
    # Directory.Move is an exact, same-volume rename: it rejects an existing
    # destination instead of nesting the source inside a newly created directory.
    [IO.Directory]::Move($installedDirectory, $resolvedBackup)
    try {
        [IO.Directory]::Move($stagedDirectory, $installedDirectory)
        Assert-DistributionDirectory $installedDirectory 'win-x64'
    }
    catch {
        if (-not (Test-Path -LiteralPath $installedDirectory)) {
            Assert-DistributionDirectory $resolvedBackup ([IO.Path]::GetFileName($resolvedBackup))
            [IO.Directory]::Move($resolvedBackup, $installedDirectory)
        }
        throw
    }
    Write-UpdateReceipt 'applied' 'The new build is available at the existing shortcut path. The previous build is backed up.'
}
catch {
    if ($updateLock) { Write-UpdateReceipt 'failed' $_.Exception.Message }
    throw
}
finally {
    if ($updateLock) { $updateLock.Dispose() }
}
