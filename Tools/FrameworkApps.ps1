# Build and publish classic .NET Framework projects for launcher app payloads.

function Test-LegacyFrameworkProject([string]$ProjectPath) {
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) { return $false }
    try {
        [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
        $framework = @($project.SelectNodes("//*[local-name()='TargetFrameworkVersion']") |
            ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ })
        return $framework.Count -gt 0
    }
    catch { return $false }
}

function Get-LegacyMSBuildPath {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) { return $command.Source }

    $vswhereCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    foreach ($vswhere in $vswhereCandidates) {
        $installations = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null)
        foreach ($installation in $installations) {
            if (Test-Path -LiteralPath $installation -PathType Leaf) { return $installation }
        }
    }
    throw 'Cannot publish a classic .NET Framework app because MSBuild was not found on PATH or through Visual Studio Installer (vswhere). Install Visual Studio Build Tools with the .NET desktop build tools.'
}

function Invoke-LegacyMSBuild([string]$MSBuildPath, [string]$ProjectPath, [string[]]$Arguments, [string]$Stage) {
    $output = @(& $MSBuildPath $ProjectPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = (@($output | ForEach-Object { $_.ToString() } | Select-Object -Last 12) -join [Environment]::NewLine)
        throw "Classic .NET Framework $Stage failed for '$ProjectPath' (MSBuild exit code $exitCode).`n$details"
    }
}

function Publish-LegacyFrameworkProject([string]$ProjectPath, [string]$OutputPath) {
    $project = [IO.Path]::GetFullPath($ProjectPath)
    if (-not (Test-LegacyFrameworkProject $project)) { throw "Not a classic .NET Framework project: $ProjectPath" }
    $output = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $projectDirectory = [IO.Path]::GetDirectoryName($project)
    $msbuild = Get-LegacyMSBuildPath

    Write-Host "Restoring classic .NET Framework packages for $project"
    Invoke-LegacyMSBuild $msbuild $project @(
        '/nologo', '/verbosity:minimal', '/target:Restore', '/p:RestorePackagesConfig=true',
        "/p:SolutionDir=$($projectDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))$([IO.Path]::DirectorySeparatorChar)",
        "/p:RestorePackagesPath=$(Join-Path $projectDirectory 'packages')"
    ) 'package restore'

    Write-Host "Building classic .NET Framework app $project"
    $outputDirectory = $output.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Invoke-LegacyMSBuild $msbuild $project @(
        '/nologo', '/verbosity:minimal', '/target:Build', '/p:Configuration=Release', '/p:Platform=AnyCPU',
        "/p:OutputPath=$outputDirectory", "/p:OutDir=$outputDirectory", '/p:DebugSymbols=false', '/p:DebugType=None'
    ) 'Release AnyCPU build'
}
