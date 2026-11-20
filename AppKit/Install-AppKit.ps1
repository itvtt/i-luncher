# 앱 저장소에 i-luncher AppKit 을 넣습니다 (자체 업데이트 + Actions 릴리스). 여러 번 실행해도 됩니다 (키트 파일만 새로 고침).
#   powershell -NoProfile -ExecutionPolicy Bypass -File AppKit\Install-AppKit.ps1 -Project <앱 저장소>\MyApp\MyApp.csproj [-RepositoryRoot <앱 저장소>]
# 하는 일:
#   <프로젝트 폴더>\AppKit\AppUpdater.cs, AppUsage.cs, AppKit.targets    복사 (원본이 바뀌면 다시 실행해 갱신)
#   <프로젝트>.csproj                                         <Import Project="AppKit\AppKit.targets" /> 추가 (없을 때만)
#   <저장소>\build\Publish-App.ps1, Get-NextVersion.ps1, New-GitHubRelease.ps1   복사
#   <저장소>\.github\workflows\release.yml                    없을 때만 만듦 (있으면 release-appkit.yml 로 만들고 알림)
#   <저장소>\.env.sample, .gitignore                          UPDATE_* 설명, .env·out\ 무시 추가 (없을 때만)
# 마지막으로 앱 시작 코드에 넣을 한 줄을 보여 줍니다.
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$launcherRoot = Split-Path -Parent $kit
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$projectDir = Split-Path -Parent $projectPath
if (-not $RepositoryRoot) {
    # .git 이 있는 가장 가까운 상위 폴더, 없으면 프로젝트 폴더
    $RepositoryRoot = $projectDir
    for ($dir = $projectDir; $dir; $dir = Split-Path -Parent $dir) { if (Test-Path (Join-Path $dir '.git')) { $RepositoryRoot = $dir; break } }
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not $projectPath.StartsWith($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "The project must be inside the repository root ($RepositoryRoot)." }
$utf8Bom = New-Object Text.UTF8Encoding($true)
$utf8 = New-Object Text.UTF8Encoding($false)

[xml]$xml = Get-Content -LiteralPath $projectPath -Encoding UTF8
$assembly = @($xml.Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1)
$name = if ($assembly) { $assembly[0] } else { [IO.Path]::GetFileNameWithoutExtension($projectPath) }
$relativeProject = $projectPath.Substring($RepositoryRoot.Length).TrimStart('\')

# 1. 키트 파일
$target = Join-Path $projectDir 'AppKit'
New-Item -ItemType Directory -Force -Path $target | Out-Null
foreach ($file in 'AppUpdater.cs', 'AppUsage.cs', 'AppKit.targets') { Copy-Item -LiteralPath (Join-Path $kit $file) -Destination (Join-Path $target $file) -Force }
Write-Host "Kit files -> $target"

# 2. 프로젝트에 Import
$projectText = [IO.File]::ReadAllText($projectPath)
if ($projectText -notmatch 'AppKit\\AppKit\.targets') {
    $index = $projectText.LastIndexOf('</Project>')
    if ($index -lt 0) { throw "Not an MSBuild project: $projectPath" }
    $newline = if ($projectText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $line = "  <!-- i-luncher AppKit: 자체 업데이트(update.json)와 릴리스 zip -->$newline  <Import Project=`"AppKit\AppKit.targets`" />$newline"
    $projectText = $projectText.Substring(0, $index) + $line + $projectText.Substring($index)
    $hasBom = ([IO.File]::ReadAllBytes($projectPath) | Select-Object -First 3) -join ',' -eq '239,187,191'
    [IO.File]::WriteAllText($projectPath, $projectText, $(if ($hasBom) { $utf8Bom } else { $utf8 }))
    Write-Host "Import added -> $projectPath"
}

# 3. 빌드 스크립트
$build = Join-Path $RepositoryRoot 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
Copy-Item -LiteralPath (Join-Path $kit 'templates\Publish-App.ps1') -Destination (Join-Path $build 'Publish-App.ps1') -Force
foreach ($file in 'Get-NextVersion.ps1', 'New-GitHubRelease.ps1') {
    $destination = Join-Path $build $file
    if ((Test-Path $destination) -and $file -eq 'New-GitHubRelease.ps1' -and -not (Select-String -LiteralPath $destination -Pattern 'RELEASE_ASSETS' -Quiet)) {
        Write-Warning "$destination already exists with a different interface; kept it. The AppKit workflow needs the AppKit version (RELEASE_ASSETS)."
        continue
    }
    Copy-Item -LiteralPath (Join-Path $launcherRoot "build\$file") -Destination $destination -Force
}
Write-Host "Build scripts -> $build"

# 4. 워크플로
$workflows = Join-Path $RepositoryRoot '.github\workflows'
New-Item -ItemType Directory -Force -Path $workflows | Out-Null
$workflow = Join-Path $workflows 'release.yml'
if ((Test-Path $workflow) -and -not (Select-String -LiteralPath $workflow -Pattern 'Publish-App\.ps1' -Quiet)) {
    $workflow = Join-Path $workflows 'release-appkit.yml'
    Write-Warning "release.yml already exists; wrote $workflow instead. Keep only one release workflow."
}
$template = [IO.File]::ReadAllText((Join-Path $kit 'templates\release.yml'))
[IO.File]::WriteAllText($workflow, $template.Replace('{{NAME}}', $name).Replace('{{PROJECT}}', $relativeProject), $utf8)
Write-Host "Workflow -> $workflow"

# 5. .env.sample / .gitignore
$sample = Join-Path $RepositoryRoot '.env.sample'
if (-not (Test-Path $sample) -or -not (Select-String -LiteralPath $sample -Pattern '^UPDATE_REPOSITORY=' -Quiet)) {
    $header = if (Test-Path $sample) { '' } else { "# $name 빌드 설정 예시: .env 로 복사해 값을 채우세요. .env 는 git 에 올리지 않습니다.$([Environment]::NewLine)" }
    [IO.File]::AppendAllText($sample, $header + [IO.File]::ReadAllText((Join-Path $kit 'templates\env.sample')), $utf8)
    Write-Host ".env.sample updated"
}
$ignore = Join-Path $RepositoryRoot '.gitignore'
$ignoreText = if (Test-Path $ignore) { [IO.File]::ReadAllText($ignore) } else { '' }
$additions = @('.env', 'out/', 'bin/', 'obj/') | Where-Object { $ignoreText -notmatch ('(?m)^/?' + [regex]::Escape($_.TrimEnd('/')) + '/?\s*$') }
if ($additions) {
    $prefix = if ($ignoreText.Length -gt 0 -and -not $ignoreText.EndsWith("`n")) { [Environment]::NewLine } else { '' }
    [IO.File]::AppendAllText($ignore, $prefix + ($additions -join [Environment]::NewLine) + [Environment]::NewLine, $utf8)
    Write-Host ".gitignore: added $($additions -join ', ')"
}

Write-Host ''
Write-Host "Done. Add this to the app's startup (WPF App.OnStartup, after base.OnStartup):" -ForegroundColor Green
Write-Host @'
    ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
    {
        if (MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            ready.RestartNow();
            Shutdown();
        }
    }));
    ILuncher.AppKit.AppUsage.Start();   // 사용 시간·작업량 집계 (상주·감시 앱은 AppUsage.Start(ILuncher.AppKit.UsageKind.Resident))
'@
Write-Host "Install name (folder, Windows apps list, release zip): $name  ->  $name-win-x64.zip"
