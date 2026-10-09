[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$unicodeName = [string][char]0xD55C + [char]0xAE00
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ILauncherDistribution-' + $unicodeName + ' space-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}
try {
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $fixture
    $bundle = Join-Path $fixture 'i-luncher'
    $metadata = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $bundle 'i-luncher.json') | ConvertFrom-Json
    Assert-Check ($metadata.distribution.framework -eq 'net10.0') 'Distribution targets one .NET generation.'
    Assert-Check ($metadata.distribution.integrated -eq $true) 'Distribution uses the integrated host.'
    $runtimeRoot = Join-Path $bundle 'runtime'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'sdk'))) 'No SDK is included.'
    $coreFiles = @(Get-ChildItem -LiteralPath $bundle -Filter coreclr.dll -File -Recurse)
    Assert-Check ($coreFiles.Count -eq 1 -and $coreFiles[0].FullName.StartsWith($runtimeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Exactly one shared CoreCLR is included.'
    $desktopFiles = @(Get-ChildItem -LiteralPath $bundle -Filter PresentationFramework.dll -File -Recurse)
    Assert-Check ($desktopFiles.Count -eq 1) 'Exactly one WPF runtime is included.'
    Assert-Check (@(Get-ChildItem -LiteralPath $bundle -File -Recurse | Where-Object { $_.Extension -in '.cs', '.csproj' -or $_.Name -eq 'Build.ps1' }).Count -eq 0) 'Distribution contains no source projects or build script.'
    $manifests = @(Get-ChildItem -LiteralPath $bundle -Directory | ForEach-Object {
        $path = Join-Path $_.FullName 'launcher.app.json'
        if (Test-Path -LiteralPath $path) { [pscustomobject]@{ Directory = $_.FullName; Data = (Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json) } }
    })
    Assert-Check (@($manifests | Where-Object { $_.Data.id -eq 'file_shared' }).Count -eq 1) 'The file_shared standalone app is included in the distribution.'
    foreach ($module in @('FolderCompareWPF.dll', 'SwiftCopy.dll', 'SwiftCopy.Core.dll', 'DiskAtlas.dll', 'DiskAtlas.Core.dll', 'DeskCal.dll')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $bundle $module) -PathType Leaf) "Integrated module $module is bundled."
    }
    $appExecutables = @(Get-ChildItem -LiteralPath $bundle -Filter '*.exe' -File)
    Assert-Check ($appExecutables.Count -eq 1 -and $appExecutables[0].Name -eq 'i-luncher.exe') 'One application executable hosts all four built-in tools.'
    foreach ($manifest in $manifests) {
        Assert-Check (-not $manifest.Data.build -and -not $manifest.Data.install) "$($manifest.Data.id) has no first-run build or individual installer."
    }
    $launches = @([pscustomobject]@{ Name = 'i-luncher'; Path = (Join-Path $bundle 'i-luncher.exe'); Arguments = '' })
    foreach ($appId in @('folder-compare', 'swift-copy', 'deskcal')) {
        $launches += [pscustomobject]@{ Name = $appId; Path = (Join-Path $bundle 'i-luncher.exe'); Arguments = "--app $appId" }
    }
    foreach ($manifest in $manifests) {
        $executable = Join-Path $manifest.Directory $manifest.Data.executable
        Assert-Check (Test-Path -LiteralPath $executable -PathType Leaf) "$($manifest.Data.id) executable exists."
        $runtimeConfigPath = [IO.Path]::ChangeExtension($executable, '.runtimeconfig.json')
        if (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) {
            $runtimeConfig = Get-Content -Raw -Encoding UTF8 -LiteralPath $runtimeConfigPath | ConvertFrom-Json
            Assert-Check ($runtimeConfig.runtimeOptions.tfm -eq 'net10.0' -and -not $runtimeConfig.runtimeOptions.includedFrameworks) "$($manifest.Data.id) uses the shared .NET 10 framework."
            if ($manifest.Data.id -ne 'disk-atlas') {
                $launches += [pscustomobject]@{ Name = $manifest.Data.id; Path = $executable; Arguments = '' }
            }
        }
        else {
            Assert-Check (Test-Path -LiteralPath "$($executable).config" -PathType Leaf) "$($manifest.Data.id) includes its .NET Framework configuration."
            Assert-Check (Test-Path -LiteralPath (Join-Path $manifest.Directory 'Newtonsoft.Json.dll') -PathType Leaf) "$($manifest.Data.id) includes Newtonsoft.Json."
        }
    }
    # Launch only the processes owned by this test. DiskAtlas requires UAC and is checked structurally above.
    foreach ($launch in $launches) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $launch.Path
        $start.Arguments = $launch.Arguments
        $start.WorkingDirectory = [IO.Path]::GetDirectoryName($launch.Path)
        $start.UseShellExecute = $false
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.EnvironmentVariables['PATH'] = Join-Path $fixture 'no-tools'
        $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $fixture 'no-global-runtime'
        $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $fixture 'no-global-runtime'
        $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.EnvironmentVariables['DESKCAL_DATA_DIR'] = Join-Path $fixture 'calendar-data'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        try {
            do {
                Start-Sleep -Milliseconds 50
                $process.Refresh()
            } while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and $timer.Elapsed.TotalSeconds -lt 20)
            Assert-Check (-not $process.HasExited -and $process.MainWindowHandle -ne [IntPtr]::Zero) "$($launch.Name) opens a window without SDK/tools or global runtime paths."
            $elapsed = $timer.ElapsedMilliseconds
            $loadedRuntime = @($process.Modules | Where-Object { $_.ModuleName -ieq 'coreclr.dll' })
            Assert-Check ($loadedRuntime.Count -eq 1 -and $loadedRuntime[0].FileName.StartsWith($runtimeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) "$($launch.Name) loads the bundled shared runtime after relocation."
            if ($launch.Name -eq 'i-luncher') {
                Add-Type -AssemblyName UIAutomationClient
                Add-Type -AssemblyName UIAutomationTypes
                $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
                $runButtons = @($buttons | Where-Object { $_.Current.AutomationId -like 'Launch_*' })
                $installButtons = @($buttons | Where-Object { $_.Current.AutomationId -like 'Install_*' -and -not $_.Current.IsOffscreen })
                Assert-Check ($runButtons.Count -eq (4 + $manifests.Count) -and @($runButtons | Where-Object { -not $_.Current.IsEnabled }).Count -eq 0) 'Distribution launcher exposes all built-in and manifest apps.'
                Assert-Check ($installButtons.Count -eq 0) 'Distribution hides individual installation actions.'
            }
            $results.Add([pscustomobject]@{ app = $launch.Name; windowMilliseconds = $elapsed; coreclr = $loadedRuntime[0].FileName })
        }
        finally {
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
            }
            $process.Dispose()
        }
    }
    Assert-Check (Test-Path -LiteralPath (Join-Path $fixture 'calendar-data\deskcal.db')) 'DeskCal creates its database on .NET 10 in isolated test data.'
    $installRoot = Join-Path $fixture 'installed'
    $startMenu = Join-Path $fixture 'start-menu'
    & (Join-Path $bundle 'Install.ps1') -PerUserInstallRoot $installRoot -StartMenuPath $startMenu
    Assert-Check (Test-Path -LiteralPath (Join-Path $startMenu 'i-luncher.lnk')) 'Whole-product installer creates the launcher shortcut.'
    $installedBundle = Join-Path $installRoot ('Releases\' + $metadata.distribution.version)
    Assert-Check (@(Get-ChildItem -LiteralPath $installedBundle -Filter coreclr.dll -Recurse -File).Count -eq 1) 'Installed product also contains only one shared runtime.'
    $report = [pscustomobject]@{
        version = $metadata.distribution.version
        zipBytes = (Get-Item -LiteralPath $ZipPath).Length
        unpackedBytes = (Get-ChildItem -LiteralPath $bundle -File -Recurse | Measure-Object -Property Length -Sum).Sum
        apps = $results.ToArray()
        diskAtlas = 'Integrated module and host runtime checked; interactive administrator launch not performed.'
        installer = 'Complete bundle copied to isolated temporary installation and shortcut verified.'
    }
    if ($ReportPath) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))) | Out-Null
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    }
    $report | ConvertTo-Json -Depth 6
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('ILauncherDistribution-', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned test fixture.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
