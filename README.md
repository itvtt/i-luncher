# i-luncher

FolderCompareWPF, SwiftCopy, DiskAtlas, DeskCal을 함께 빌드하는 통합 WPF 프로그램입니다. 실행 파일은 `i-luncher.exe` 하나이며 네 도구는 함께 배포되는 DLL에 포함됩니다.

FolderCompareWPF·SwiftCopy·DeskCal은 실행 버튼을 누르면 런처 프로세스 안에서 창을 바로 엽니다. 처음 클릭할 때 별도 빌드, SDK 확인, EXE 압축 해제 또는 소스 복사를 하지 않습니다. 이미 열린 도구를 다시 누르면 기존 창을 앞으로 가져옵니다. 런처 창을 닫아도 열린 도구는 계속 사용할 수 있습니다.

DiskAtlas는 디스크 직접 읽기에 필요한 관리자 권한을 받기 위해 같은 `i-luncher.exe --app disk-atlas`를 별도 프로세스로 실행합니다. Windows 관리자 권한 확인은 유지됩니다. 기존 DiskAtlas 작업 스케줄러 등록 도우미는 통합 배포본에서 사용하지 않습니다.

## 실행과 배포

```powershell
# 배포용 ZIP 생성: 네 도구를 포함한 호스트 한 번 게시 + 공통 .NET 10 런타임
powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-Distribution.ps1
```

완료 시 표시되는 ZIP을 전달하세요. 받는 사람은 압축을 전부 풀고 `i-luncher.exe`를 실행하면 됩니다. .NET SDK 설치나 최초 실행 빌드는 필요하지 않습니다. `Install.cmd`는 제품 전체를 사용자 폴더에 설치하고 시작 메뉴 바로가기를 만듭니다.

```text
i-luncher/
├─ i-luncher.exe
├─ i-luncher.dll + 설정 파일
├─ FolderCompareWPF.dll
├─ SwiftCopy.dll + SwiftCopy.Core.dll
├─ DiskAtlas.dll + DiskAtlas.Core.dll
├─ DeskCal.dll + SQLite 의존 파일
├─ runtime/                 # 공통 .NET 10 실행 환경
└─ Install.cmd / Install.ps1
```

DLL, SQLite 의존 파일, `runtime/`을 포함한 **전체 폴더를 함께 이동**하세요. 실행 속도를 위해 자체 압축 해제하는 단일 파일 배포는 사용하지 않습니다. 기본 네 도구는 외부 `launcher.app.json`이나 소스 폴더 없이 표시됩니다.

## 실행과 개별 설치

각 앱 카드 오른쪽의 **실행**은 설치 등록 없이 앱을 바로 실행합니다. 필요한 파일은 처음에 다운로드하거나 빌드하고 이후에는 재사용합니다. 카드 오른쪽 위의 **설치 아이콘**은 개별 설치를 등록하고 시작 메뉴에 앱별 바로가기를 만듭니다. 설치 후에는 시작 메뉴에서 앱 이름을 찾아 해당 앱만 직접 실행할 수 있습니다. 기본 네 도구의 바로가기는 공용 실행 파일에 `--app <앱 ID>`를 전달하므로 런처 목록 창을 열지 않습니다. DiskAtlas의 관리자 권한 확인은 유지됩니다.

런처가 관리하는 앱의 설치가 완료되면 오른쪽 위 아이콘이 붉은 **제거 아이콘**으로 바뀝니다. 아이콘 위에 마우스를 올리면 설치 또는 제거 설명을 확인할 수 있습니다. 제거하면 해당 앱의 개별 설치 등록과 시작 메뉴 바로가기를 지우고 다시 **설치 아이콘**으로 돌아갑니다. 다운로드한 앱 파일, 공용 실행 환경, 사용자 데이터는 유지하므로 런처에서 바로 실행하거나 다시 설치할 수 있습니다.

기본 도구의 실행 파일과 의존 파일은 같은 버전의 공용 설치본 한 벌을 공유합니다. 배포본의 추가 앱도 같은 설치 묶음과 `runtime/`을 사용합니다. 같은 버전을 다시 설치하면 검증된 설치본을 재사용하며, 처음 필요한 설치 공간과 새 앱 고유 파일의 용량은 발생합니다. 설치본은 원본 소스나 압축을 푼 배포 폴더와 별도로 보관합니다.

소스 프로젝트도 먼저 실행할 필요 없이 **설치**를 누를 수 있습니다. 필요한 빌드 결과를 준비한 뒤 실행에 필요한 파일만 설치합니다. 개발 중 추가 .NET 앱은 호환되는 설치된 런타임을 사용하므로, 다른 PC에 전달할 때는 공용 런타임이 포함된 배포 ZIP을 사용하세요.

온라인 시작 프로그램은 기존 통합 WPF 런처에서 개별 설치한 앱도 인식합니다. 기존 설치 기록, 실행 파일, 시작 메뉴 바로가기가 일치하면 오른쪽의 설치 아이콘이 **제거 아이콘**으로 바뀌고, **실행** 시에도 기존 설치본을 재사용합니다. 카드에는 상시 설치 상태 문구 없이 설명을 표시하며, 설치·제거 아이콘은 상단 분류 배지 옆에, 실행 버튼은 설명 오른쪽에 배치합니다. 작업 중에는 설명 자리에 진행 상황을 표시합니다. 제거할 때는 해당 앱의 설치 등록과 바로가기만 지우며 앱 파일과 사용자 데이터는 유지합니다.

## 개발 빌드

```powershell
# 통합 프로그램 게시
.\Build.ps1

# 아래 호환 옵션도 네 도구를 포함한 통합 프로그램을 갱신
.\Build.ps1 -LauncherOnly
.\Build.ps1 -App swift-copy

# 가장 최근 완료한 통합 개발 빌드 또는 배포본 실행
.\Start-Launcher.cmd
```

소스만 있는 새 PC에서 `Start-Launcher.cmd`를 실행하면 처음 한 번 통합 프로그램을 빌드하므로 Windows와 .NET 10 SDK가 필요합니다. 기본 네 도구는 이후 클릭할 때 추가 빌드가 없습니다. 새로 추가한 소스 프로젝트는 해당 도구를 처음 실행할 때 빌드합니다. SDK 없는 PC에는 소스 대신 위 ZIP을 전달하세요.

빌드와 실행 결과는 `%LOCALAPPDATA%\i-luncher\Workspaces\<원본 경로 식별자>`에 둡니다. Send/Receive의 원본 폴더 교체를 방해하지 않도록 소스를 로컬 작업 복사본에 동기화한 뒤 빌드합니다. 이미 준비된 실행본을 시작할 때는 전체 작업 폴더를 재귀 검사하거나 빌드 잠금을 기다리지 않습니다.

```powershell
$workspace = .\Start-Launcher.ps1 -PrintWorkspace
Set-Location $workspace
# 원본 소스 변경 후에는 원본에서 Build.ps1을 실행해 복사본 갱신

dotnet run --project .\Launcher\i-luncher.csproj
dotnet run --project .\Launcher.Checks -c Release
dotnet run --project .\Launcher.UiChecks -c Release -- --launch-apps
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\StartLauncherChecks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\IntegratedBuildChecks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\DistributionChecks.ps1 -ZipPath <배포 ZIP 경로>
```

`i-luncher.sln`에 런처와 기본 네 도구 프로젝트가 포함됩니다. 기본 도구의 소스 폴더와 내부 Core 프로젝트는 유지하며 런처의 `ProjectReference`로 함께 컴파일합니다. 저장소 기본값은 `ILuncherIntegrated=true`이며 기본 도구는 라이브러리로 빌드됩니다. 기존 독립 실행 프로그램이 필요하면 해당 도구 프로젝트를 `-p:ILuncherIntegrated=false`로 직접 빌드하세요. 새 외부 프로젝트를 추가할 때는 이 솔루션이나 런처의 참조 목록을 수정할 필요가 없습니다.

개발 게시 결과는 `dist/builds/<버전>`에 완성한 뒤 `dist/latest-build.json`을 원자적으로 갱신합니다. 실패한 빌드는 이전 실행본을 덮어쓰지 않습니다. 시작 스크립트는 개발 빌드와 배포본 중 마지막으로 완료된 것을 사용합니다.

각 도구의 화면 리소스는 창 안에 분리해 서로 다른 테마가 섞이지 않게 합니다. FolderCompare 경로 설정과 DeskCal 일정 데이터의 저장 위치는 기존과 같습니다. DeskCal 자동 시작은 통합 실행 파일과 `--app deskcal` 인수를 등록합니다.

## 프로젝트 폴더째 추가

실행 가능한 .NET 프로젝트 폴더를 루트 바로 아래 또는 `Projects/<프로젝트 폴더>/`에 복사하고 런처에서 새로 고침하세요. 프로젝트를 찾는 동안에도 런처 창은 응답하며, 목록을 표시하기 위해 프로젝트를 빌드하거나 앱 어셈블리를 불러오지 않습니다.

```text
i-luncher/
└─ Projects/
   └─ MyTool/
      ├─ MyTool.csproj      # OutputType: Exe 또는 WinExe
      └─ ...               # 소스와 리소스를 함께 복사
```

폴더 안에서 실행 프로젝트가 하나로 정해지면 자동으로 카드가 생깁니다. 라이브러리·테스트 프로젝트와 `bin`, `obj` 등 빌드 결과는 실행 대상으로 등록하지 않습니다. 실행 프로젝트가 여러 개이거나 설정을 자동으로 읽을 수 없으면 아래 `launcher.app.json`으로 사용할 프로젝트를 지정하세요. 명시한 설정은 자동 검색보다 우선합니다.

새 도구는 사용자가 실행하거나 설치할 때 빌드하고, 소스와 빌드 설정이 그대로라면 원본 소스 밖의 로컬 작업 공간에 저장한 결과를 재사용합니다. 참조 프로젝트나 공통 빌드 설정이 바뀌면 다시 빌드합니다. 입력 파일을 안전하게 추적할 수 없는 동적 설정은 캐시를 재사용하지 않습니다. 새 도구를 추가해도 런처에 `ProjectReference`를 추가하거나 실행 분기 코드를 작성할 필요가 없습니다. 기본 네 도구의 기존 통합 실행 방식은 유지합니다.

외부 .NET 도구는 런타임을 앱마다 포함하지 않는 방식으로 게시합니다. 개발 중에는 호환되는 설치된 .NET 런타임을 사용하며, 배포본은 공통 `runtime/`을 사용합니다. 프로젝트의 코드·리소스·고유 의존 파일만큼의 용량 증가는 발생합니다. 다른 버전의 런타임이나 별도 실행 환경이 필요한 프로젝트까지 용량 증가 없이 실행할 수 있는 것은 아닙니다. [Microsoft의 .NET 배포 방식 설명](https://learn.microsoft.com/en-us/dotnet/core/deploying/)을 참고하세요.

현재 배포 묶음은 .NET 10 런타임과 호환되는 도구를 지원하며, 호환되지 않는 런타임 요구 사항은 게시할 때 오류로 알립니다. .NET 소스 자동 검색 외의 프로그램은 아래 설정으로 실행 파일을 등록하세요.

## 외부 도구 설정

기본 네 도구 외의 외부 프로그램은 기존 `launcher.app.json` 방식으로 등록할 수 있습니다. 루트 바로 아래 하위 폴더 또는 `Projects/<프로젝트 폴더>/`에 아래 설정을 둡니다.

```json
{
  "id": "my-tool",
  "name": "My Tool",
  "description": "외부 도구 설명",
  "category": "도구",
  "executable": "dist/win-x64/MyTool.exe",
  "icon": "Assets/MyTool.png",
  "arguments": [],
  "order": 100,
  "install": { "type": "portable" },
  "build": { "project": "MyTool.csproj" }
}
```

이 경로는 설정 파일이 있는 폴더 기준입니다. 미리 빌드한 외부 도구는 `build`를 생략하고, 개별 설치를 지원하려면 위 예시처럼 `install.type: "portable"`을 지정합니다. `build`가 있는 소스 프로젝트는 `install`을 생략해도 빌드된 게시 폴더를 설치합니다. 이때 실행 파일과 작업 디렉터리가 게시 폴더 안에 있어야 합니다. 별도 설치 프로그램을 실행하는 기존 `install.type: "command"` 설정도 유지합니다. 기본 도구와 같은 ID의 외부 설정은 통합 도구를 대체하지 않습니다.

프로젝트에 기존 배포본이나 검증 산출물이 많으면 `build.excludeFromWorkspace`에 프로젝트 폴더 기준의 디렉터리 경로를 배열로 지정할 수 있습니다. 예를 들어 `"excludeFromWorkspace": ["artifacts"]`는 해당 앱의 `artifacts`를 로컬 빌드 작업 공간에 복사하지 않습니다. 원본 자료는 그대로 보존하며, 빌드에 필요한 소스와 리소스 폴더는 이 목록에 넣지 않습니다.

`Projects/TerminalNotes`는 `Terminal Notes` 카드로 등록되어 있습니다. 메인 `TerminalNotes.csproj`를 실행 대상으로 지정하고 자체 아이콘과 설명을 사용합니다.

통합 빌드·화면·배포 실행 확인과 측정값은 [검증 기록](artifacts/integration-verification.md)에 있습니다.

## GitHub Actions CI and Windows releases

The repository includes `.github/workflows/windows-release.yml`. It uses a self-hosted Windows x64 runner, the existing `Publish-Distribution.ps1` packaging flow, and no third-party Actions. A push to `main` performs headless service checks and builds a ZIP without publishing a release. Pushing a `v*` tag builds the ZIP and publishes it with a `.sha256` checksum asset. `workflow_dispatch` can build only, or publish from `main` or a `v*` tag when `publish_release` is enabled and a version is supplied. Existing release tags must resolve to the checked-out commit.

Register a runner from **Settings > Actions > Runners > New self-hosted runner**, selecting Windows x64. Use a dedicated machine and account, install Git and Windows PowerShell 5.1, and preinstall the .NET 10 SDK/runtime required by the publisher. The repository's current runner is hidden and starts at user logon, so it needs an active signed-in session; configure it as a Windows service for operation across logouts (service setup requires an administrator). Set the repository variable `WINDOWS_RUNNER_LABELS_JSON` to a JSON string array when the default labels do not match your runner. For example: `["self-hosted","Windows","X64","release"]`. If unset, the workflow uses `self-hosted`, `Windows`, and `X64`.

For GitHub Enterprise Server or an on-premises network, preconfigure the runner's normal NuGet settings to use the approved internal mirror/cache and trust the enterprise CA. Keep TLS validation enabled. The workflow does not download toolchains or install applications; it runs `Launcher.Checks` and never launches the product, its apps, or installers.

The workflow manually fetches the triggering ref into a unique directory under `RUNNER_TEMP`, using a temporary Git authorization header, then removes only that run-owned checkout. It does not use a personal access token, store credentials in Git configuration, or use the interactive build workspace. The publisher's `-UseCurrentWorkspace` option is restricted to that validated Actions checkout. Protect `main` and `v*` tags, restrict who can dispatch release runs, and use a dedicated runner account because self-hosted runners execute repository code with the runner's local permissions. This workflow intentionally has no pull-request trigger.

The release script uses `GITHUB_SERVER_URL`, `GITHUB_API_URL`, and `GITHUB_TOKEN` supplied by the Actions context, so release creation and asset upload use the configured GitHub Enterprise Server or GitHub.com API. The only required token permission is repository contents write. No upload-artifact action is used, which keeps the release path compatible with GHES installations that do not support upload-artifact v4. Retain the runner's trusted TLS configuration for your enterprise server.

## Small online launcher

Each tagged release also includes `i-luncher-start.exe`, a small .NET Framework 4.8 launcher that opens before any .NET 10 runtime or app package is downloaded. Downloading this starter gets only the launcher executable. On first use of an app, it downloads the shared runtime once and that app's package; later app uses reuse the cached files. The original full offline ZIP remains available for machines that need a complete package without network access.

The online launcher downloads immutable assets from the release tag using HTTPS. Public GitHub releases need no token. For private GitHub or GHES releases, enter a user-authorized PAT in launcher settings; if saved, it is protected for the current Windows user with DPAPI. A corporate mirror can be configured for environments that do not allow direct release downloads. The launcher verifies catalog and package SHA-256 values before extraction, and the same runtime package is reused across app installs and release versions when its content hash matches.
