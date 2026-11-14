# 다음 릴리스 버전: 마지막 정식 릴리스 + 1, 자리마다 0~9 (1.0.9 → 1.1.0, 1.9.9 → 2.0.0). 프로젝트 파일의 <Version> 은 바닥값 (거꾸로 가지 않음).
# 예) csproj 1.0.0, 마지막 릴리스 v1.0.7 → 1.0.8 / csproj 를 1.1.0 으로 올리면 → 1.1.0
# 버전을 정해 실행하면(workflow_dispatch 입력 version — API 서버의 릴리스 화면) 그 번호를 씀: 1.2.3 형식, 마지막 릴리스보다 커야 함.
# 환경 변수: GH_TOKEN, GITHUB_API_URL, GITHUB_REPOSITORY (Actions 가 넣어 줌), REQUESTED_VERSION (정한 버전, 비우면 위 규칙).
# 결과는 GITHUB_OUTPUT 의 version, previous.
param([Parameter(Mandatory = $true)][string]$Project, [string]$Requested = $env:REQUESTED_VERSION)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[xml]$xml = Get-Content -LiteralPath $Project -Encoding UTF8
$floorText = @($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$floor = if ($floorText) { [version]$floorText[0] } else { [version]'1.0.0' }
$latest = $null; $previous = ''
try {
    $release = Invoke-RestMethod -UseBasicParsing -Uri "$($env:GITHUB_API_URL.TrimEnd('/'))/repos/$env:GITHUB_REPOSITORY/releases/latest" `
        -Headers @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' }
    if ($release.tag_name -match '(\d+)\.(\d+)\.(\d+)') {
        $latest = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
        $previous = $release.tag_name
    }
} catch { Write-Host 'No previous release.' }
$next = if ($latest -and $latest -ge $floor) {
            $major = $latest.Major; $minor = $latest.Minor; $patch = $latest.Build + 1
            if ($patch -gt 9) { $patch = 0; $minor++ }
            if ($minor -gt 9) { $minor = 0; $patch = 0; $major++ }
            [version]::new($major, $minor, $patch)
        }
        else { [version]::new($floor.Major, $floor.Minor, [Math]::Max(0, $floor.Build)) }
if ($Requested -and $Requested.Trim()) {
    $wanted = $Requested.Trim().TrimStart('v', 'V')
    if ($wanted -notmatch '^\d+\.\d\.\d$') { throw "Version must look like 1.2.3 (middle and last 0-9): $wanted" }
    if ($latest -and [version]$wanted -le $latest) { throw "Version $wanted must be above the latest release $previous." }
    $next = [version]$wanted
}
Write-Host "Version $next (previous: $(if ($previous) { $previous } else { 'none' }), floor $floor)"
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$next" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "previous=$previous" -Encoding ASCII
}
