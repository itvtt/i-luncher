# 앱 릴리스 zip 을 out\ 에 만듭니다 (i-luncher AppKit). .github\workflows\release.yml 이 부르고, 손으로도 실행할 수 있습니다:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\Publish-App.ps1 -Project <앱.csproj> [-Version 1.2.3]
# 결과: out\<이름>-win-x64.zip  (i-luncher 가 설치·업데이트하고, 설치된 앱이 스스로 업데이트할 때 받는 패키지)
# .NET SDK 만 있으면 됩니다. nuget.org 가 막힌 망이면 -NuGetSource 또는 환경 변수 NUGET_SOURCE 로 NuGet 미러를 주세요.
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Version = '',
    [string]$OutputDirectory = 'out',
    [string]$NuGetSource = $env:NUGET_SOURCE
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$projectPath = (Resolve-Path -LiteralPath $Project).Path

[xml]$xml = Get-Content -LiteralPath $projectPath -Encoding UTF8
if (-not $Version) { $Version = @($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)[0] }
if (-not $Version) { $Version = '1.0.0' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

$out = [IO.Path]::Combine($root, $OutputDirectory)
$publish = Join-Path $out 'win-x64'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem -LiteralPath $out -Filter '*-win-x64.zip' -File | Remove-Item -Force

$restore = @()
if ($NuGetSource) { $restore = @('--source', $NuGetSource); Write-Host "NuGet source: $NuGetSource" }
$staged = Join-Path $out 'release.zip'
Write-Host "Publishing $([IO.Path]::GetFileNameWithoutExtension($projectPath)) $Version"
& dotnet publish $projectPath -c Release -r win-x64 --self-contained false -nologo -m:1 -o $publish "-p:Version=$Version" "-p:ReleaseZip=$staged" @restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# update.json 의 InstallName 으로 zip 이름을 정함 (앱이 스스로 업데이트할 때 찾는 이름과 같게)
$updateJson = Join-Path $publish 'update.json'
if (-not (Test-Path $updateJson)) { throw 'update.json is missing: add <Import Project="AppKit\AppKit.targets" /> to the project.' }
$update = Get-Content $updateJson -Raw | ConvertFrom-Json
if (-not $update.Repository) { Write-Warning 'update.json has no Repository: this build will not update itself (set UPDATE_REPOSITORY in .env or build on the Actions runner).' }
$exe = Join-Path $publish ($update.InstallName + '.exe')
if (-not (Test-Path $exe)) {
    $exe = @(Get-ChildItem -LiteralPath $publish -Filter '*.exe' -File | Where-Object { $_.Name -ne 'createdump.exe' } | Select-Object -First 1).FullName
}
if (-not $exe -or -not (Test-Path $exe)) { throw 'No executable in the published folder.' }
$productVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($productVersion -ne $Version) { throw "$([IO.Path]::GetFileName($exe)) reports version '$productVersion' instead of '$Version'." }

$zip = Join-Path $out $update.Asset
Move-Item -LiteralPath $staged -Destination $zip -Force
Write-Host ("Release zip: {0} ({1:N1} MB, {2} files) · update check: {3}" -f $zip, ((Get-Item $zip).Length / 1MB), @(Get-ChildItem $publish -Recurse -File).Count,
    $(if ($update.Repository) { "$($update.Api) $($update.Repository)" } else { 'off' }))
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zip" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$Version" -Encoding ASCII
}
