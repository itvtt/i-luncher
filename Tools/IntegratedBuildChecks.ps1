$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ILauncherIntegratedBuild-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Host "PASS: $Message"
}
try {
    Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Build.ps1') -Destination $fixture
    $sourceTools = Join-Path $fixture 'Tools'
    [IO.Directory]::CreateDirectory($sourceTools) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SourceApps.ps1') -Destination $sourceTools
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FrameworkApps.ps1') -Destination $sourceTools
    # Exercise the real staging/promotion code with a deterministic publisher.
    $helper = @'
function Assert-ILauncherInternalWorkspace { param([string]$WorkspacePath) }
function dotnet {
    $global:ILauncherCheckPublishCalls++
    $outputIndex = [Array]::IndexOf($args, '-o')
    if ($outputIndex -lt 0) { throw 'Publish output argument missing.' }
    $output = [string]$args[$outputIndex + 1]
    [IO.Directory]::CreateDirectory($output) | Out-Null
    if (@($args | Where-Object { [string]$_ -like '*Demo.csproj' }).Count -gt 0) {
        $sourceArgs = @($args | ForEach-Object { [string]$_ })
        $global:ILauncherCheckSourcePublishArgs = $sourceArgs
        $selfContainedIndex = [Array]::IndexOf($sourceArgs, '--self-contained')
        $global:ILauncherCheckSourcePublishIsFdd = $selfContainedIndex -ge 0 -and $sourceArgs[$selfContainedIndex + 1] -eq 'false' -and
            ($sourceArgs -contains '-p:ILuncherIntegrated=false') -and ($sourceArgs -contains '-p:UseAppHost=true')
        foreach ($name in @('Demo.exe', 'Demo.dll', 'Demo.deps.json', 'Demo.runtimeconfig.json')) {
            [IO.File]::WriteAllText((Join-Path $output $name), "fixture source publish $global:ILauncherCheckPublishCalls")
        }
        $global:LASTEXITCODE = if ($global:ILauncherCheckPublishMode -eq 'source-failure') { 1 } else { 0 }
        return
    }
    foreach ($name in @('i-luncher.exe', 'i-luncher.dll', 'i-luncher.deps.json', 'i-luncher.runtimeconfig.json',
        'FolderCompareWPF.dll', 'SwiftCopy.dll', 'SwiftCopy.Core.dll', 'DiskAtlas.dll', 'DiskAtlas.Core.dll',
        'DeskCal.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')) {
        if ($global:ILauncherCheckPublishMode -eq 'missing-native' -and $name -eq 'e_sqlite3.dll') { continue }
        [IO.File]::WriteAllText((Join-Path $output $name), "fixture publish $global:ILauncherCheckPublishCalls")
    }
    $global:LASTEXITCODE = if ($global:ILauncherCheckPublishMode -eq 'failure') { 1 } else { 0 }
}
'@
    [IO.File]::WriteAllText((Join-Path $fixture 'BuildWorkspace.ps1'), $helper)
    $builder = Join-Path $fixture 'Build.ps1'
    $pointer = Join-Path $fixture 'dist\latest-build.json'
    $global:ILauncherCheckPublishCalls = 0
    $global:ILauncherCheckPublishMode = 'success'
    & $builder -LauncherOnly -InWorkspace
    $first = Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json
    Check (Test-Path -LiteralPath (Join-Path $first.path 'i-luncher.exe')) 'First integrated publish is complete before its pointer is created.'
    & $builder -App swift-copy -InWorkspace
    $second = Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json
    Check ($first.path -ne $second.path -and (Test-Path -LiteralPath (Join-Path $first.path 'i-luncher.exe'))) 'A built-in app update publishes the whole host and preserves the previous version.'
    $goodPointer = [IO.File]::ReadAllText($pointer)
    foreach ($failureMode in @('failure', 'missing-native')) {
        $global:ILauncherCheckPublishMode = $failureMode
        $rejected = $false
        try { & $builder -LauncherOnly -InWorkspace } catch { $rejected = $true }
        Check ($rejected -and [IO.File]::ReadAllText($pointer) -eq $goodPointer) "$failureMode leaves the completed build pointer intact."
        Check (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'dist\builds') -Directory -Filter '.build-staging-*').Count -eq 0) "$failureMode removes only its incomplete staging directory."
    }
    $global:ILauncherCheckPublishMode = 'success'
    $external = Join-Path $fixture 'External'
    [IO.Directory]::CreateDirectory($external) | Out-Null
    [IO.File]::WriteAllText((Join-Path $external 'External.csproj'), '<Project />')
    [IO.File]::WriteAllText((Join-Path $external 'launcher.app.json'), '{"id":"external","name":"External","executable":"../../escape/External.exe","build":{"project":"External.csproj"}}')
    $before = $global:ILauncherCheckPublishCalls
    $rejected = $false
    try { & $builder -App external -InWorkspace } catch { $rejected = $_.Exception.Message -like '*must be inside*' }
    Check ($rejected -and $before -eq $global:ILauncherCheckPublishCalls) 'An external manifest cannot publish outside its app directory.'
    $projectDirectory = Join-Path $fixture 'Projects\Demo'
    [IO.Directory]::CreateDirectory($projectDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $projectDirectory 'Demo.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    $appOutput = Join-Path $fixture 'dist\apps\demo'
    [IO.Directory]::CreateDirectory($appOutput) | Out-Null
    [IO.File]::WriteAllText((Join-Path $appOutput 'old.marker'), 'previous good build')
    $global:ILauncherCheckPublishMode = 'source-failure'
    $rejected = $false
    try { & $builder -App demo -Project 'Projects\Demo\Demo.csproj' -InWorkspace } catch { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath (Join-Path $appOutput 'old.marker'))) 'A failed source app publish preserves the previous output.'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'dist\apps') -Directory -Filter '.build-staging-*').Count -eq 0) 'A failed source app publish removes its staging directory.'
    $global:ILauncherCheckPublishMode = 'success'
    & $builder -App demo -Project 'Projects\Demo\Demo.csproj' -InWorkspace
    Check ($global:ILauncherCheckSourcePublishIsFdd) "Source app publish shares the installed runtime and requests an apphost. Args: $($global:ILauncherCheckSourcePublishArgs -join ' ')"
    Check ((Test-Path -LiteralPath (Join-Path $appOutput 'Demo.exe')) -and
        (Test-Path -LiteralPath (Join-Path $appOutput '.build-result.json')) -and
        -not (Test-Path -LiteralPath (Join-Path $appOutput 'old.marker'))) 'A successful source publish promotes its executable and metadata atomically.'
    Write-Host "$checks integrated build checks passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('ILauncherIntegratedBuild-', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned fixture.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
