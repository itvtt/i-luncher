[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$ApiUrl,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$TagName,
    [switch]$PublishRelease
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$releaseToken = [string]$env:GITHUB_TOKEN
Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue

function Assert-ReleaseTagTarget {
    param(
        [Parameter(Mandatory = $true)][string]$ServerUrl,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$TagName,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$Token
    )

    if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw 'GITHUB_SHA is not a valid commit identifier.'
    }
    $remoteUrl = $ServerUrl.TrimEnd('/') + "/$Repository.git"
    $exactRef = "refs/tags/$TagName"
    $peeledRef = $exactRef + '^{}'
    $authBytes = [Text.Encoding]::UTF8.GetBytes("x-access-token:$Token")
    $authHeader = 'AUTHORIZATION: basic ' + [Convert]::ToBase64String($authBytes)
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = 'http.extraheader'
    $env:GIT_CONFIG_VALUE_0 = $authHeader
    try {
        $tagLines = @(& git ls-remote --tags $remoteUrl $exactRef $peeledRef)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not query the requested release tag from the configured Git server.'
        }
    }
    finally {
        Remove-Item Env:GIT_CONFIG_COUNT -ErrorAction SilentlyContinue
        Remove-Item Env:GIT_CONFIG_KEY_0 -ErrorAction SilentlyContinue
        Remove-Item Env:GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue
        $authBytes = $null
        $authHeader = $null
    }

    $resolvedCommit = $null
    foreach ($line in $tagLines) {
        $parts = ([string]$line) -split '\s+', 2
        if ($parts.Count -ne 2) { continue }
        if ($parts[1] -eq $peeledRef) {
            $resolvedCommit = $parts[0]
            break
        }
        if ($parts[1] -eq $exactRef) { $resolvedCommit = $parts[0] }
    }
    if ($resolvedCommit -and -not $resolvedCommit.Equals($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release tag '$TagName' already points to a different commit; refusing to publish mismatched assets."
    }
}

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'GITHUB_REPOSITORY must be in owner/name form.'
}
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw 'Version must contain 1 to 64 letters, digits, dots, underscores, or hyphens.'
}
if ($PublishRelease -and [string]::IsNullOrWhiteSpace($TagName)) {
    throw 'A release requires a tag name.'
}
if ($PublishRelease -and $TagName -notmatch '^v[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw 'Release tag names must start with v and use the supported version characters.'
}
if ($PublishRelease -and -not $TagName.Substring(1).Equals($Version, [StringComparison]::Ordinal)) {
    throw 'The release tag and package version must match.'
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$packageDirectory = Join-Path $sourceRoot "dist\releases\$Version"
$zipName = "i-luncher-$Version-win-x64.zip"
$zipPath = Join-Path $packageDirectory $zipName
$checksumPath = "$zipPath.sha256"
$onlineDirectory = Join-Path $packageDirectory 'online'

Write-Host 'Running headless launcher service checks.'
& dotnet run --project (Join-Path $sourceRoot 'Launcher.Checks\Launcher.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Launcher service checks failed with exit code $LASTEXITCODE." }

Write-Host 'Running headless online bootstrapper checks.'
& (Join-Path $PSScriptRoot 'Invoke-BootstrapperChecks.ps1')
if (-not $?) { throw 'Bootstrapper checks failed.' }

Write-Host "Publishing distribution version $Version from the runner checkout."
& (Join-Path $sourceRoot 'Publish-Distribution.ps1') -Version $Version -UseCurrentWorkspace
if (-not $?) { throw 'Distribution publishing failed.' }
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "The distribution publisher did not create the expected ZIP: $zipPath"
}

Write-Host "Publishing online app catalog and hash-addressed packages for $Version."
$catalogTagName = if ([string]::IsNullOrWhiteSpace($TagName)) { "v$Version" } else { $TagName }
& (Join-Path $sourceRoot 'Publish-OnlineDistribution.ps1') -Version $Version -ServerUrl $ServerUrl `
    -ApiUrl $ApiUrl -Repository $Repository -TagName $catalogTagName -UseCurrentWorkspace
if (-not $?) { throw 'Online distribution publishing failed.' }
if (-not (Test-Path -LiteralPath $onlineDirectory -PathType Container)) {
    throw "The online distribution publisher did not create its output directory: $onlineDirectory"
}
$onlineLauncherPath = Join-Path $onlineDirectory 'i-luncher-start.exe'
if (-not (Test-Path -LiteralPath $onlineLauncherPath -PathType Leaf)) {
    throw "The online publisher did not create the small start launcher: $onlineLauncherPath"
}
$onlineLauncherSizeKiB = [Math]::Round((Get-Item -LiteralPath $onlineLauncherPath).Length / 1KB, 1)
$onlineLauncherUrl = $ServerUrl.TrimEnd('/') + "/$Repository/releases/download/" +
    [Uri]::EscapeDataString($TagName) + '/i-luncher-start.exe'
$releaseNotes = @"
Windows x64 release. 첫 다운로드는 [i-luncher-start.exe (${onlineLauncherSizeKiB} KiB)]($onlineLauncherUrl) 하나만 받으면 됩니다. 실행 후 앱과 공유 .NET 런타임은 최초 사용 시 내려받습니다. 비공개 저장소는 앱 설정에서 액세스 토큰을 입력하고, 사내망에서는 패키지 미러를 설정할 수 있습니다.

오프라인 설치가 필요하면 아래의 전체 배포 ZIP과 SHA-256 체크섬을 사용하세요. Catalog, 런타임, 앱별 패키지에도 체크섬 파일을 함께 제공합니다.
"@

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, "$zipHash  $zipName`n", [Text.UTF8Encoding]::new($false))
Write-Host "Distribution ZIP: $zipPath"
Write-Host "SHA-256: $zipHash"

if (-not $PublishRelease) {
    Write-Host 'Build validation completed; release publication was not requested.'
    Write-Host "Online bootstrapper and packages: $onlineDirectory"
    $releaseToken = $null
    return
}

$token = $releaseToken
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'GITHUB_TOKEN is required to publish the release.'
}
$apiRoot = $ApiUrl.TrimEnd('/')
$apiUri = $null
$serverUri = $null
if (-not [Uri]::TryCreate($ApiUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    -not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$serverUri) -or
    $apiUri.Scheme -ne 'https' -or $serverUri.Scheme -ne 'https') {
    throw 'The configured GitHub API and server URLs must use HTTPS.'
}
$apiIsPublicGitHub = $apiUri.Host.Equals('api.github.com', [StringComparison]::OrdinalIgnoreCase) -and
    $serverUri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase) -and
    $apiUri.IsDefaultPort -and $serverUri.IsDefaultPort
$apiMatchesServer = $apiUri.Host.Equals($serverUri.Host, [StringComparison]::OrdinalIgnoreCase) -and
    $apiUri.Port -eq $serverUri.Port
if (-not ($apiIsPublicGitHub -or $apiMatchesServer)) {
    throw 'The configured GitHub API URL is not on the GitHub server origin.'
}

Assert-ReleaseTagTarget -ServerUrl $ServerUrl -Repository $Repository -TagName $TagName `
    -ExpectedCommit ([string]$env:GITHUB_SHA) -Token $token

$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
}
$releaseEndpoint = "$apiRoot/repos/$Repository/releases"
$releaseBodyJson = [ordered]@{
    tag_name = $TagName
    target_commitish = [string]$env:GITHUB_SHA
    name = "i-luncher $Version"
    body = $releaseNotes.Trim()
    draft = $false
    prerelease = $false
} | ConvertTo-Json -Depth 4
$releaseBody = [Text.UTF8Encoding]::new($false).GetBytes($releaseBodyJson)

Write-Host "Creating release $TagName using the configured GitHub-compatible API."
$release = Invoke-RestMethod -Uri $releaseEndpoint -Method Post -Headers $headers `
    -ContentType 'application/json; charset=utf-8' -Body $releaseBody -MaximumRedirection 0
$uploadBase = ([string]$release.upload_url) -replace '\{\?.*$', ''
if ([string]::IsNullOrWhiteSpace($uploadBase)) {
    throw 'The release API response did not include an asset upload URL.'
}
$uploadUri = $null
if (-not [Uri]::TryCreate($uploadBase, [UriKind]::Absolute, [ref]$uploadUri) -or
    $uploadUri.Scheme -ne 'https' -or $uploadUri.UserInfo -or $uploadUri.Query -or $uploadUri.Fragment) {
    throw 'The release upload URL is not a safe HTTPS endpoint.'
}
$uploadIsPublicGitHub = $apiIsPublicGitHub -and
    $uploadUri.Host.Equals('uploads.github.com', [StringComparison]::OrdinalIgnoreCase) -and
    $uploadUri.IsDefaultPort
$uploadMatchesServer = $uploadUri.Host.Equals($serverUri.Host, [StringComparison]::OrdinalIgnoreCase) -and
    $uploadUri.Port -eq $serverUri.Port
if (-not ($uploadIsPublicGitHub -or $uploadMatchesServer)) {
    throw 'The release upload URL is outside the configured GitHub server origin.'
}
$apiPath = $apiUri.AbsolutePath.TrimEnd('/')
if ($apiIsPublicGitHub) {
    $uploadPathPrefix = ''
}
elseif ($apiPath -match '^(.*)/api/v3$') {
    $uploadPathPrefix = $Matches[1] + '/api/uploads'
}
else {
    throw 'The configured GitHub Enterprise API path does not have the expected /api/v3 suffix.'
}
$expectedUploadPath = $uploadPathPrefix + "/repos/$Repository/releases/$($release.id)/assets"
$actualUploadPath = [Uri]::UnescapeDataString($uploadUri.AbsolutePath).TrimEnd('/')
if (-not $actualUploadPath.Equals($expectedUploadPath, [StringComparison]::OrdinalIgnoreCase) -or
    [string]$release.id -notmatch '^[0-9]+$') {
    throw 'The release upload URL path does not match the created repository release.'
}
$uploadBase = $uploadUri.GetLeftPart([UriPartial]::Path)

$assets = [Collections.Generic.List[object]]::new()
$assets.Add([pscustomobject]@{ Path = $zipPath; ContentType = 'application/zip' })
$assets.Add([pscustomobject]@{ Path = $checksumPath; ContentType = 'text/plain' })
foreach ($onlineAsset in Get-ChildItem -LiteralPath $onlineDirectory -File | Sort-Object Name) {
    $contentType = switch -Regex ($onlineAsset.Extension) {
        '^\.zip$' { 'application/zip'; break }
        '^\.json$' { 'application/json'; break }
        '^\.exe$' { 'application/vnd.microsoft.portable-executable'; break }
        default { 'text/plain' }
    }
    $assets.Add([pscustomobject]@{ Path = $onlineAsset.FullName; ContentType = $contentType })
}

foreach ($asset in $assets) {
    $assetName = [IO.Path]::GetFileName($asset.Path)
    $uploadUri = $uploadBase + '?name=' + [Uri]::EscapeDataString($assetName)
    Write-Host "Uploading release asset $assetName."
    Invoke-WebRequest -Uri $uploadUri -Method Post -Headers $headers -ContentType $asset.ContentType `
        -InFile $asset.Path -UseBasicParsing -MaximumRedirection 0 | Out-Null
}

Write-Host "Release published: $($release.html_url)"
$releaseToken = $null
$token = $null
