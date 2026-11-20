# i-luncher 릴리스를 out\ 에 만듭니다:
#   i-luncher-Setup.exe     처음 설치용 (사용자별, 관리자 권한 없음; 시작 메뉴·바탕화면 바로 가기, Windows "앱" 목록 등록;
#                           Windows 에 들어 있는 .NET Framework 로 돌아가며, .NET 10 이 없는 PC 에는 .NET 10 실행 환경을 넣어 줌)
#   i-luncher-Team-Setup.exe  부서용 처음 설치 (위와 같고 설치할 때 부서 표시를 남김: 스토어에 API 서버 앱 목록도 보임).
#                           릴리스에 올리지 않음 (release.yml 이 러너 PC 의 폴더에 둠)
#   i-luncher-win-x64.zip   설치된 런처가 GitHub Release 에서 내려받아 스스로 업데이트하는 패키지 (일반 · 부서 같음)
#   win-x64\                게시된 런처 폴더 (zip 의 내용)
# .NET 10 SDK 만 있으면 됩니다 (NuGet 패키지 없음).
# .github\workflows\release.yml 이 부르고, 손으로도 실행할 수 있습니다:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\Publish-Release.ps1 [-Version 1.0.3] [-SkipChecks]
param(
    [string]$Version = '',
    [string]$OutputDirectory = 'out',
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$sdk = ''
try { $sdk = (& dotnet --version 2>$null | Select-Object -First 1) } catch { }
if (-not $sdk -or [int]($sdk.Split('.')[0]) -lt 10) { throw ".NET 10 SDK is required on the build machine (found: '$sdk')." }

$launcherProject = Join-Path $root 'Launcher\i-luncher.csproj'
if (-not $Version) {
    [xml]$project = Get-Content $launcherProject -Encoding UTF8
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
if (-not (Test-Path (Join-Path $root '.env'))) {
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host '.env not found: the launcher uses this repository for its updates and app list, without a built-in token.' }
    else { Write-Warning '.env not found: this build has no release repository (no self-update, local app list only).' }
}

# 핵심 동작 확인 (가짜 GitHub·임시 폴더만 씀). 실패하면 릴리스하지 않음
if (-not $SkipChecks) {
    Write-Host 'Running Launcher.Checks'
    & dotnet run --project (Join-Path $root 'Launcher.Checks\Launcher.Checks.csproj') -c Release -nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Launcher.Checks failed (exit $LASTEXITCODE)." }
}

$out = [IO.Path]::Combine($root, $OutputDirectory)
$publish = Join-Path $out 'win-x64'
$zip = Join-Path $out 'i-luncher-win-x64.zip'
$setup = Join-Path $out 'i-luncher-Setup.exe'
$teamSetup = Join-Path $out 'i-luncher-Team-Setup.exe'
$setupStage = Join-Path $out 'setup-build'
foreach ($old in $publish, $setupStage) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
foreach ($old in $zip, $setup, $teamSetup) { if (Test-Path $old) { Remove-Item $old -Force } }

Write-Host "Publishing i-luncher $Version (SDK $sdk)"
& dotnet publish $launcherProject -c Release -r win-x64 --self-contained false -nologo -m:1 `
    -o $publish "-p:Version=$Version" "-p:ReleaseZip=$zip"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
foreach ($required in 'i-luncher.exe', 'i-luncher.dll', 'launcher.env') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "$required is missing from the published folder." }
}
$productVersion = (Get-Item (Join-Path $publish 'i-luncher.dll')).VersionInfo.ProductVersion
if ($productVersion -ne $Version) { throw "i-luncher.dll reports version '$productVersion' instead of '$Version'." }
if (-not (Test-Path $zip)) { throw 'The release zip was not created.' }
Write-Host ("launcher.env: " + ((Get-Content (Join-Path $publish 'launcher.env') | Where-Object { $_ -notmatch '^#' } | ForEach-Object { ($_ -split '=', 2)[0] }) -join ', '))

# .NET 10 이 없는 PC 용 실행 환경: 이 빌드 PC 의 .NET 에서 복사 (SDK 가 함께 설치함; 다운로드 없음).
$runtimeStage = Join-Path $out 'runtime-build'
$uninstallStage = Join-Path $out 'uninstall-build'
foreach ($old in $runtimeStage, $uninstallStage) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
$desktopRuntimes = @(& dotnet --list-runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.WindowsDesktop\.App (10\.\d+\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Text = $Matches[1]; Path = $Matches[2] } }
} | Sort-Object Version -Descending)
if ($desktopRuntimes.Count -eq 0) { throw 'The .NET 10 Desktop Runtime (installed with the .NET 10 SDK on Windows) was not found on the build machine.' }
$runtimeVersion = $desktopRuntimes[0].Text
$dotnetRoot = Split-Path -Parent (Split-Path -Parent $desktopRuntimes[0].Path)
$fxr = @(Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'host\fxr') -Directory | Where-Object { $_.Name -match '^(\d+)\.\d+\.\d+$' -and [int]$Matches[1] -ge 10 } |
    Sort-Object { [version]$_.Name } -Descending)
if ($fxr.Count -eq 0) { throw "No .NET 10 host resolver (host\fxr) on the build machine under $dotnetRoot." }
foreach ($part in "host\fxr\$($fxr[0].Name)", "shared\Microsoft.NETCore.App\$runtimeVersion", "shared\Microsoft.WindowsDesktop.App\$runtimeVersion") {
    $source = Join-Path $dotnetRoot $part
    if (-not (Test-Path -LiteralPath $source)) { throw "The .NET runtime part is missing on the build machine: $source" }
    $destination = Join-Path $runtimeStage $part
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Recurse
}
foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $path = Join-Path $dotnetRoot $notice
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $runtimeStage }
}
Write-Host "Bundled .NET runtime $runtimeVersion (host $($fxr[0].Name)) for PCs without .NET 10"

# 설치 프로그램 (.NET Framework 4.8): 작은 uninstall.exe 를 먼저, 그다음 릴리스 zip·실행 환경·그 제거 프로그램을 품은 Setup
$setupProject = Join-Path $root 'installer\i-luncher.Setup.csproj'
Write-Host 'Building uninstall.exe'
& dotnet publish $setupProject -c Release -nologo -m:1 -o $uninstallStage "-p:Version=$Version" -p:BuildUninstaller=true
if ($LASTEXITCODE -ne 0) { throw "Uninstaller build failed (exit $LASTEXITCODE)." }
$installerObj = Join-Path $root 'installer\obj'
if (Test-Path $installerObj) { Remove-Item $installerObj -Recurse -Force }
Write-Host 'Building i-luncher-Setup.exe'
& dotnet publish $setupProject -c Release -nologo -m:1 -o $setupStage "-p:Version=$Version" "-p:PayloadZip=$zip" `
    "-p:RuntimeDirectory=$runtimeStage" "-p:UninstallerExe=$(Join-Path $uninstallStage 'i-luncher-Setup.exe')"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit $LASTEXITCODE)." }
Move-Item -LiteralPath (Join-Path $setupStage 'i-luncher-Setup.exe') -Destination $setup -Force

# 부서용: 같은 설치 프로그램을 TEAM_SETUP 으로 한 번 더 (설치할 때 store-group.txt 에 team)
if (Test-Path $installerObj) { Remove-Item $installerObj -Recurse -Force }
if (Test-Path $setupStage) { Remove-Item $setupStage -Recurse -Force }
Write-Host 'Building i-luncher-Team-Setup.exe'
& dotnet publish $setupProject -c Release -nologo -m:1 -o $setupStage "-p:Version=$Version" "-p:PayloadZip=$zip" `
    "-p:RuntimeDirectory=$runtimeStage" "-p:UninstallerExe=$(Join-Path $uninstallStage 'i-luncher-Setup.exe')" -p:SetupGroup=team
if ($LASTEXITCODE -ne 0) { throw "Team installer build failed (exit $LASTEXITCODE)." }
Move-Item -LiteralPath (Join-Path $setupStage 'i-luncher-Setup.exe') -Destination $teamSetup -Force

foreach ($stage in $setupStage, $uninstallStage, $runtimeStage) { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force } }
foreach ($intermediate in 'bin', 'obj') {
    $path = Join-Path (Join-Path $root 'installer') $intermediate
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Host ("Release zip: {0} ({1:N1} MB, {2} files)" -f $zip, ((Get-Item $zip).Length / 1MB), @(Get-ChildItem $publish -Recurse -File).Count)
Write-Host ("Installer:   {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host ("Team setup:  {0} ({1:N1} MB)" -f $teamSetup, ((Get-Item $teamSetup).Length / 1MB))
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zip" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "setup=$setup" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "team_setup=$teamSetup" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$Version" -Encoding ASCII
}
