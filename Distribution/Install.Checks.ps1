[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$installerPath = Join-Path $PSScriptRoot 'Install.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('i-luncher-install-checks-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'installed'
$startMenuRoot = Join-Path $testRoot 'start-menu'
$junctionPath = $null

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function New-TestBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [string]$Payload = 'fixture'
    )

    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'runtime\host\fxr\10.0.0'))
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'runtime\shared\Microsoft.NETCore.App\10.0.0'))
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'DeskCal'))
    [IO.File]::WriteAllText((Join-Path $Root 'i-luncher.exe'), 'launcher')
    [IO.File]::WriteAllText((Join-Path $Root 'runtime\dotnet.exe'), 'runtime host')
    [IO.File]::WriteAllText((Join-Path $Root 'runtime\host\fxr\10.0.0\hostfxr.dll'), 'hostfxr')
    [IO.File]::WriteAllText((Join-Path $Root 'runtime\shared\Microsoft.NETCore.App\10.0.0\coreclr.dll'), 'coreclr')
    [IO.File]::WriteAllText((Join-Path $Root 'DeskCal\DeskCal.exe'), $Payload)
    [IO.File]::Copy($installerPath, (Join-Path $Root 'Install.ps1'))
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $Root 'Install.cmd'))

    Write-Utf8Json -Path (Join-Path $Root 'i-luncher.json') -Value ([ordered]@{
        schemaVersion = 1
        distribution = [ordered]@{
            version = $Version
            runtime = 'runtime'
            framework = 'net10.0'
            runtimeVersion = '10.0.0'
        }
    })

    $files = @(
        Get-ChildItem -File -Recurse -LiteralPath $Root | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($Root.TrimEnd('\').Length).TrimStart('\').Replace('\', '/')
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
                size = $_.Length
            }
        }
    )
    Write-Utf8Json -Path (Join-Path $Root 'release-manifest.json') -Value ([ordered]@{
        schemaVersion = 1
        version = $Version
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        rid = 'win-x64'
        framework = 'net10.0'
        runtimeVersion = '10.0.0'
        files = $files
    })
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

try {
    $bundle = Join-Path $testRoot 'bundle'
    New-TestBundle -Root $bundle -Version '20260915-check.1'
    & $installerPath -PackageRoot $bundle -PerUserInstallRoot $installRoot -StartMenuPath $startMenuRoot

    $releaseRoot = Join-Path $installRoot 'Releases\20260915-check.1'
    Assert-True (Test-Path -LiteralPath (Join-Path $releaseRoot 'runtime\host\fxr\10.0.0\hostfxr.dll') -PathType Leaf) 'The runtime was not installed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $releaseRoot 'DeskCal\DeskCal.exe') -PathType Leaf) 'The app payload was not installed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $startMenuRoot 'i-luncher.lnk') -PathType Leaf) 'The Start Menu shortcut was not created.'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenuRoot 'i-luncher.lnk'))
    try {
        Assert-True ($shortcut.TargetPath -eq (Join-Path $releaseRoot 'i-luncher.exe')) 'The Start Menu shortcut has the wrong target.'
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }

    & $installerPath -PackageRoot $bundle -PerUserInstallRoot $installRoot -StartMenuPath $startMenuRoot

    $conflictBundle = Join-Path $testRoot 'conflict-bundle'
    New-TestBundle -Root $conflictBundle -Version '20260915-check.1' -Payload 'different payload'
    $conflictRejected = $false
    try {
        & $installerPath -PackageRoot $conflictBundle -PerUserInstallRoot $installRoot -StartMenuPath $startMenuRoot
    }
    catch {
        $conflictRejected = $_.Exception.Message -like '*immutable*'
    }
    Assert-True $conflictRejected 'A different bundle with an existing version was not rejected.'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $releaseRoot 'DeskCal\DeskCal.exe')) -eq 'fixture') 'The immutable installed release was overwritten.'

    $unsafeBundle = Join-Path $testRoot 'unsafe-bundle'
    New-TestBundle -Root $unsafeBundle -Version '20260915-check.2'
    $unsafeManifestPath = Join-Path $unsafeBundle 'release-manifest.json'
    $unsafeManifest = Get-Content -Raw -LiteralPath $unsafeManifestPath | ConvertFrom-Json
    $unsafeManifest.files[0].path = '../outside.exe'
    Write-Utf8Json -Path $unsafeManifestPath -Value $unsafeManifest
    $unsafeRejected = $false
    try {
        & $installerPath -PackageRoot $unsafeBundle -PerUserInstallRoot $installRoot -StartMenuPath $startMenuRoot
    }
    catch {
        $unsafeRejected = $_.Exception.Message -like '*unsafe path*'
    }
    Assert-True $unsafeRejected 'A traversal path in the release manifest was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Releases\20260915-check.2'))) 'An invalid release created an installed version.'

    $incompleteBundle = Join-Path $testRoot 'incomplete-runtime-bundle'
    New-TestBundle -Root $incompleteBundle -Version '20260915-check.3'
    $incompleteManifestPath = Join-Path $incompleteBundle 'release-manifest.json'
    $incompleteManifest = Get-Content -Raw -LiteralPath $incompleteManifestPath | ConvertFrom-Json
    $incompleteManifest.files = @($incompleteManifest.files | Where-Object { $_.path -ne 'runtime/shared/Microsoft.NETCore.App/10.0.0/coreclr.dll' })
    Remove-Item -Force -LiteralPath (Join-Path $incompleteBundle 'runtime\shared\Microsoft.NETCore.App\10.0.0\coreclr.dll')
    Write-Utf8Json -Path $incompleteManifestPath -Value $incompleteManifest
    $incompleteRuntimeRejected = $false
    try {
        & $installerPath -PackageRoot $incompleteBundle -PerUserInstallRoot $installRoot -StartMenuPath $startMenuRoot
    }
    catch {
        $incompleteRuntimeRejected = $_.Exception.Message -like '*required shared runtime file*'
    }
    Assert-True $incompleteRuntimeRejected 'A bundle missing coreclr.dll was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Releases\20260915-check.3'))) 'An incomplete runtime created an installed version.'

    $junctionTarget = Join-Path $testRoot 'junction-target'
    $junctionPath = Join-Path $testRoot 'junction-start-menu'
    [void][IO.Directory]::CreateDirectory($junctionTarget)
    & cmd.exe /d /c mklink /J $junctionPath $junctionTarget > $null
    if ($LASTEXITCODE -eq 0) {
        $junctionRejected = $false
        try {
            & $installerPath -PackageRoot $bundle -PerUserInstallRoot $installRoot -StartMenuPath $junctionPath
        }
        catch {
            $junctionRejected = $_.Exception.Message -like '*reparse-point ancestor*'
        }
        Assert-True $junctionRejected 'A Start Menu reparse point was not rejected.'
    }

    $longInstallRoot = Join-Path $testRoot ([string]::new([char]'x', 180))
    $longPathRejected = $false
    try {
        & $installerPath -PackageRoot $bundle -PerUserInstallRoot $longInstallRoot -StartMenuPath $startMenuRoot
    }
    catch {
        $longPathRejected = $_.Exception.Message -like '*path limit*'
    }
    Assert-True $longPathRejected 'An installation destination beyond the PowerShell path limit was not rejected.'
    Assert-True (-not (Test-Path -LiteralPath $longInstallRoot)) 'A path-length failure wrote to the installation destination.'

    $stages = @(Get-ChildItem -Force -LiteralPath (Join-Path $installRoot 'Releases') -Filter '.s-*')
    Assert-True ($stages.Count -eq 0) 'A staging directory was left behind.'
    $shortcutTemporaryFiles = @(Get-ChildItem -Force -LiteralPath $startMenuRoot -Filter '.il-*.lnk')
    Assert-True ($shortcutTemporaryFiles.Count -eq 0) 'A temporary or backup shortcut was left behind.'
    Write-Host 'PASS distribution installer checks'
}
finally {
    if ($null -ne $junctionPath -and (Test-Path -LiteralPath $junctionPath -PathType Container)) {
        [IO.Directory]::Delete($junctionPath, $false)
    }
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
