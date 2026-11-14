# i-luncher AppKit

앱 저장소에 넣으면 다음이 갖춰집니다.

- **자체 업데이트**: 설치된 앱이 시작할 때 자기 저장소의 최신 GitHub Release를 확인하고, 새 버전을 받아 두었다가 앱을 닫을 때(또는 "지금 다시 시작"을 고르면 바로) 바꿉니다.
- **Actions 릴리스**: `main`에 푸시할 때마다 self-hosted Windows 러너가 다음 버전을 골라 `<이름>-win-x64.zip`을 릴리스합니다.
- **i-luncher 규약**: 런처가 같은 zip으로 설치·업데이트하고, 런처로 설치한 앱도 따로 실행하면 스스로 업데이트합니다.

github.com과 GitHub Enterprise 모두 동작합니다. .NET 6 이상 WPF·WinForms·콘솔 앱에 쓸 수 있습니다. .NET Framework 4.8 앱도 SDK 형식 프로젝트(`<TargetFramework>net48</TargetFramework>`, `<LangVersion>latest</LangVersion>`)에 `System.Text.Json` 패키지와 `System.IO.Compression.FileSystem`·`System.Net.Http` 참조를 더하면 같은 방식으로 씁니다(예: file_shared).

## 넣기

```powershell
# i-luncher 저장소에서
powershell -NoProfile -ExecutionPolicy Bypass -File AppKit\Install-AppKit.ps1 -Project <앱 저장소>\MyApp\MyApp.csproj -RepositoryRoot <앱 저장소>
```

| 만들어지는 것 | 내용 |
|---|---|
| `<프로젝트>\AppKit\AppUpdater.cs` | 업데이터 (파일 하나, NuGet 없음) |
| `<프로젝트>\AppKit\AppKit.targets` + csproj의 `<Import>` | 빌드할 때 `update.json` 생성, 릴리스 zip, 앱 옆 `dotnet\` 실행 환경 우선 |
| `build\Publish-App.ps1`, `Get-NextVersion.ps1`, `New-GitHubRelease.ps1` | 릴리스 빌드·버전·Release 생성 |
| `.github\workflows\release.yml` | Actions 릴리스 (이미 있으면 `release-appkit.yml`) |
| `.env.sample`, `.gitignore` | `UPDATE_*` 설명, `.env`·`out/` 무시 |

그리고 앱 시작 코드에 한 줄을 넣습니다 (WPF):

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
    {
        if (MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            ready.RestartNow();
            Shutdown();
        }
    }));
}
```

묻지 않고 종료할 때 적용만 하려면 `AppUpdater.StartBackground();`만 부르면 됩니다. 키트 원본이 바뀌면 같은 명령을 다시 실행해 파일을 새로 고칩니다.

## 동작

1. 빌드할 때 `.env`의 `UPDATE_*`(없으면 Actions 러너의 `GITHUB_API_URL`·`GITHUB_REPOSITORY`)로 실행 파일 옆 `update.json`을 만듭니다. 저장소가 비어 있으면(개발 빌드) 업데이트를 확인하지 않습니다.
2. 앱이 `%LOCALAPPDATA%\Programs\<설치이름>`에서 실행 중이고 `HKCU\…\Uninstall\<설치이름>`의 `InstallLocation`이 그 폴더일 때만 동작합니다 (i-luncher나 앱 설치 프로그램이 만든 설치본). `bin\`에서 실행하는 개발 빌드는 건드리지 않습니다.
3. 시작 5초 뒤 `releases/latest`를 확인하고, 태그 버전이 실행 중인 버전(`InformationalVersion`)보다 높으면 zip을 `%LOCALAPPDATA%\<설치이름>\updates`에 받습니다 (실행 파일이 들어 있는지 확인).
4. 앱이 끝나면 숨은 PowerShell이 프로세스 종료를 기다렸다가 zip을 설치 폴더에 덮어쓰고(robocopy), "앱" 목록 버전을 갱신합니다. 설치 폴더 밖의 데이터와 `dotnet\`은 건드리지 않습니다. 기록은 `updates\update.log`.
5. 오프라인·권한 없음 등은 조용히 넘어가고 다음 실행 때 다시 확인합니다.

## 설정 (`.env`, 빌드할 때만)

저장소에 `.env` 가 없으면 프로필 폴더(사용자 환경 변수 `APPS_ENV_DIR`)의 `<저장소 폴더 이름>.env` 를 읽습니다. 프로젝트 폴더에는 설정 파일을 두지 않는 것을 권장합니다.

| 키 | 뜻 |
|---|---|
| `UPDATE_GITHUB_API` | `https://api.github.com` 또는 `https://<호스트>/api/v3` (Actions에서는 비워도 됨) |
| `UPDATE_REPOSITORY` | `소유자/이름` (Actions에서는 비워도 그 저장소) |
| `UPDATE_TOKEN` | 비공개·internal 저장소면 **읽기 전용** 토큰. 앱에 포함되므로 쓰기 권한 금지 |
| `UPDATE_INSTALL_NAME` | 설치 폴더·"앱" 목록 이름 (기본: 실행 파일 이름). i-luncher 카탈로그의 `installName`과 같아야 함 |
| `UPDATE_ASSET` | 릴리스 파일 이름 (기본: `<설치이름>-win-x64.zip`) |

Actions에서는 Secret `RELEASE_ENV`에 `.env` 내용을 넣거나 러너 PC의 `.env` 경로를 러너 환경 변수 `RELEASE_ENV_PATH`로 알려 줍니다. nuget.org가 막힌 망이면 러너 환경 변수 `NUGET_SOURCE`에 NuGet 미러를 지정합니다.

## 버전

`Get-NextVersion.ps1`이 마지막 릴리스 + 1 (자리마다 0~9: `1.0.9` → `1.1.0`)을 고르고 `-p:Version`으로 넘깁니다. csproj의 `<Version>`은 바닥값입니다. `Publish-App.ps1`은 실행 파일의 버전이 릴리스 버전과 같은지 확인하고, 다르면 실패합니다.
