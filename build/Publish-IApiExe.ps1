# API 서버 i-api 를 파일 하나(i-api.exe)로 만들어 CDEP\exe\i-api.exe 에 둡니다.
# 보내기에 이 파일이 같이 가서, 받는 쪽은 빌드 · 릴리스 없이 서버 PC 에 바로 씁니다 (git · 가져오기에는 안 들어감: .gitignore 의 CDEP/exe/).
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\Publish-IApiExe.ps1 [-SelfContained] [-SkipChecks]
#   - 기본: .NET 10 데스크톱 런타임이 있는 PC 용 (릴리스의 i-api-win-x64.zip 과 같은 조건, 몇 MB)
#   - -SelfContained: 런타임이 없는 PC 에서도 도는 파일 하나 (크기가 큼)
# 쓰는 법: 서버 PC 의 빈 폴더(예: %LOCALAPPDATA%\Programs\i-api)에 i-api.exe 를 복사해 실행 → 관리 창에서 포트를 넣고 [실행].
#   settings.txt 는 그 옆에 생기고, 새 버전은 관리 창 ⋯ → [업데이트] (릴리스의 zip 으로 바꿈) 또는 이 파일을 다시 복사.
# 이 빌드에는 업데이트 받을 저장소가 들어 있지 않습니다 (릴리스 러너 빌드만 품음): [업데이트]를 쓰려면 settings.txt 의 RELAY_UPDATE_REPOSITORY.
# nuget.org 가 막힌 망이면 -NuGetSource 또는 환경 변수 NUGET_SOURCE 로 NuGet 미러를 주세요 (i-api 는 NuGet 패키지를 쓰지 않음).
param(
    [switch]$SelfContained,
    [switch]$SkipChecks,
    [string]$NuGetSource = $env:NUGET_SOURCE
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$restore = @()
if ($NuGetSource) { $restore = @('--source', $NuGetSource); Write-Host "NuGet source: $NuGetSource" }

# 릴리스와 같은 확인 (임시 포트 · 임시 폴더만 씀). 실패하면 만들지 않음
if (-not $SkipChecks) {
    Write-Host 'Running i-api.Checks'
    & dotnet run --project (Join-Path $root 'i-api.Checks\i-api.Checks.csproj') -c Release -nologo -m:1 @restore
    if ($LASTEXITCODE -ne 0) { throw "i-api.Checks failed (exit $LASTEXITCODE)." }
}

$stage = Join-Path $root 'out\i-api-exe'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$contained = if ($SelfContained) { 'true' } else { 'false' }
$extra = if ($SelfContained) { @('-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') } else { @() }
& dotnet publish (Join-Path $root 'CDEP\i-api.csproj') -c Release -r win-x64 --self-contained $contained -nologo -m:1 -o $stage `
    -p:PublishSingleFile=true @extra @restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exe = Join-Path $stage 'i-api.exe'
if (-not (Test-Path $exe)) { throw 'i-api.exe was not made.' }
$sent = Join-Path $root 'CDEP\exe'
New-Item -ItemType Directory -Force -Path $sent | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $sent 'i-api.exe') -Force
Remove-Item $stage -Recurse -Force
$made = Get-Item (Join-Path $sent 'i-api.exe')
Write-Host ("보내기용 i-api.exe: {0} ({1:N1} MB, v{2})" -f $made.FullName, ($made.Length / 1MB), $made.VersionInfo.ProductVersion)
