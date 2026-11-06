# 다음 릴리스 버전: 마지막 정식 릴리스의 다음 패치 번호. 프로젝트 파일의 <Version> 은 바닥값 (거꾸로 가지 않음).
# 예) csproj 1.0.0, 마지막 릴리스 v1.0.7 → 1.0.8 / csproj 를 1.1.0 으로 올리면 → 1.1.0
# 환경 변수: GH_TOKEN, GITHUB_API_URL, GITHUB_REPOSITORY (Actions 가 넣어 줌). 결과는 GITHUB_OUTPUT 의 version, previous.
param([Parameter(Mandatory = $true)][string]$Project)
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
$next = if ($latest -and $latest -ge $floor) { [version]::new($latest.Major, $latest.Minor, $latest.Build + 1) }
        else { [version]::new($floor.Major, $floor.Minor, [Math]::Max(0, $floor.Build)) }
Write-Host "Version $next (previous: $(if ($previous) { $previous } else { 'none' }), floor $floor)"
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$next" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "previous=$previous" -Encoding ASCII
}
