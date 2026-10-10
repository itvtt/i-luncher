# i-luncher

Compare, SwiftCopy, DiskAtlas, DeskCal을 함께 빌드하는 통합 WPF 프로그램입니다. 실행 파일은 `i-luncher.exe` 하나이며 네 도구는 함께 배포되는 DLL에 포함됩니다.

Compare·SwiftCopy·DeskCal은 실행 버튼을 누르면 런처 프로세스 안에서 창을 바로 엽니다. 처음 클릭할 때 별도 빌드, SDK 확인, EXE 압축 해제 또는 소스 복사를 하지 않습니다. 이미 열린 도구를 다시 누르면 기존 창을 앞으로 가져옵니다. 런처 창을 닫아도 열린 도구는 계속 사용할 수 있습니다.

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

런처는 앱 스토어 형태의 라이브러리 화면을 제공합니다. 왼쪽에서 **전체 앱**, **설치된 앱**, 앱 분류를 선택하고 상단 검색창에서 이름·분류·설명을 검색할 수 있습니다. 검색과 분류 필터는 함께 적용되며, 결과가 없으면 필터를 초기화할 수 있습니다. `Ctrl+F`는 검색창으로 이동하고 `Esc`는 검색어를 지웁니다. 새로 고침해도 검색어와 선택한 필터가 유지됩니다.

설치된 앱은 카드의 아이콘·제목 영역을 클릭하면 실행합니다. 미설치 앱을 클릭하면 설치 여부를 묻고, 동의하면 필요한 파일을 준비해 개별 설치와 시작 메뉴 바로가기를 등록합니다. 설치 직후 자동 실행하지는 않습니다. 설치 후에는 카드나 시작 메뉴에서 해당 앱만 실행할 수 있습니다. DiskAtlas의 관리자 권한 확인은 유지됩니다.

카드 안에는 별도의 설치·제거 버튼을 두지 않습니다. 설치된 카드에서 마우스 오른쪽 버튼을 누르거나 키보드 메뉴 키/`Shift+F10`을 사용하면 **제거** 메뉴가 열립니다. 제거는 해당 앱의 설치 등록과 시작 메뉴 바로가기를 지웁니다. 다운로드한 앱 파일, 공용 실행 환경, 사용자 데이터는 유지하며, 다시 실행하려면 설치를 등록해야 합니다.

같은 우클릭 메뉴의 **완전 삭제**는 설치 등록과 바로가기뿐 아니라 런처가 관리하는 해당 앱의 설치 폴더와 캐시도 삭제합니다. 설치 폴더 안에 직접 저장한 파일도 삭제되므로 확인 전에 필요한 파일을 옮겨 주세요. 다른 앱이 사용하는 공용 파일, 설치 폴더 밖의 문서·설정, 원본 배포 패키지는 유지합니다. 실행 중이거나 잠긴 파일은 앱을 종료한 뒤 다시 시도해야 합니다.

기본 도구의 실행 파일과 의존 파일은 같은 버전의 공용 설치본 한 벌을 공유합니다. 배포본의 추가 앱도 같은 설치 묶음과 `runtime/`을 사용합니다. 같은 버전을 다시 설치하면 검증된 설치본을 재사용하며, 처음 필요한 설치 공간과 새 앱 고유 파일의 용량은 발생합니다. 설치본은 원본 소스나 압축을 푼 배포 폴더와 별도로 보관합니다.

소스 프로젝트도 카드를 클릭해 설치할 수 있습니다. 필요한 빌드 결과를 준비한 뒤 실행에 필요한 파일만 설치합니다. 개발 중 추가 .NET 앱은 호환되는 설치된 런타임을 사용하므로, 다른 PC에 전달할 때는 공용 런타임이 포함된 배포 ZIP을 사용하세요.

온라인 시작 프로그램은 기존 통합 WPF 런처에서 개별 설치한 앱도 인식합니다. 기존 설치 기록, 실행 파일, 시작 메뉴 바로가기가 일치하면 설치된 앱으로 표시하고 실행 시 기존 설치본을 재사용합니다. 각 카드는 둥근 테두리 안에 왼쪽 앱 아이콘과 오른쪽 이름·설명·버전을 배치하며, 기본 창에는 세 열로 표시됩니다. 미설치 앱은 아이콘 색상을 유지하면서 살짝 흐리게 표시하고 작업 중에는 진행 상황을 표시합니다.

## 개발 빌드

기본 제공 도구와 추가 서비스 프로젝트의 소스는 `Projects/` 아래에 모여 있습니다.

```text
Projects/
├─ DeskCal/
├─ DiskAtlas/
├─ FolderCompareWPF/
├─ SwiftCopy/
└─ TerminalNotes/
```

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

Each tagged release also includes `i-luncher-start.exe`, a small .NET Framework 4.8 launcher that opens before any .NET 10 runtime or app package is downloaded. Downloading this starter gets only the launcher executable. Confirming an app's first installation downloads the shared runtime once and that app's package; later installations reuse verified cached files. The original full offline ZIP remains available for machines that need a complete package without network access.

The online launcher downloads immutable assets from the release tag using HTTPS. Public GitHub releases need no token. For private GitHub or GHES releases, enter a user-authorized PAT in launcher settings; if saved, it is protected for the current Windows user with DPAPI. A corporate mirror can be configured for environments that do not allow direct release downloads. The launcher verifies catalog and package SHA-256 values before extraction, and the same runtime package is reused across app installs and release versions when its content hash matches.

### App versions in online catalogs

The catalog `version` identifies the launcher release. Each app's `version` comes from its `launcher.app.json` when declared; otherwise the publisher evaluates that app project's MSBuild `Version` property with the same Release and `win-x64` publish settings. The catalog records `versionSource` as `manifest` or `project`, and the package is built with that same app version. SDK default versions are preserved as evaluated, so apps can share a version until their own version metadata changes. Set an explicit app version when you want app releases to advance independently from launcher releases. An update is offered only when both app versions are known and the available version is newer; package/runtime hash changes, legacy installation format, and build metadata alone do not imply an app update. Package hashes continue to verify downloaded and installed content. Older catalogs without an app version display an unknown version unless their exact app ID and package SHA-256 match a verified compatibility entry. The five v0.2.17 main DLLs report ProductVersion `1.0.0+7892841c7f27577fd4d2c773a2bed0482e07913e` and FileVersion `1.0.0.0`; their catalog-facing SemVer is therefore shown as `1.0.0` rather than inventing distinct app numbers.

## 앱 버전과 업데이트

설치 버전과 배포 버전을 모두 확인할 수 있고 배포 버전이 더 높을 때만 업데이트를 표시합니다. 버전이 같거나 낮은 경우, 버전을 확인할 수 없는 경우에는 업데이트로 표시하지 않습니다. 기존 설치 방식이나 패키지·공용 런타임 해시가 달라졌다는 이유만으로 표시하지 않으며, 배포할 앱을 변경했다면 그 앱의 버전도 올려야 합니다.

카드의 설명 아래에 앱 버전을 표시합니다. 새 버전이 있으면 설치된 버전과 새 버전을 함께 표시하고, 카드 우클릭 메뉴에 **업데이트**를 제공합니다. 버전 정보를 기록하지 않았던 기존 설치는 검증된 설치 실행 파일이나 같은 이름의 관리 DLL에서 ProductVersion/FileVersion을 읽으며, 이를 읽을 수 없으면 알 수 없는 버전으로 표시합니다. 정확한 앱 ID와 패키지 SHA-256이 일치하는 검증된 호환성 항목만 구버전 카탈로그의 앱 버전을 보완합니다. v0.2.17의 다섯 앱 DLL은 불변 카탈로그와 해시를 확인한 뒤 ProductVersion `1.0.0+7892841c7f27577fd4d2c773a2bed0482e07913e`, FileVersion `1.0.0.0`으로 확인했으므로 카탈로그 버전은 모두 1.0.0이며, 서로 다른 번호를 임의로 만들지 않습니다. 설치 기록의 콘텐츠 해시는 화면의 버전 번호로 사용하지 않습니다.

최신 릴리스 확인은 첫 화면을 표시한 뒤 백그라운드에서 진행합니다. 확인에 실패해도 기존 설치 앱을 실행할 수 있습니다. 카드를 눌러 실행하는 동작은 설치된 버전을 사용하며, 업데이트는 우클릭 메뉴에서 명시적으로 선택해야 합니다. 새 패키지와 공용 런타임을 검증한 뒤 설치 기록을 전환하고, 실패하면 기존 설치 기록과 파일을 유지합니다. 이전 패키지 캐시와 사용자 데이터는 자동 삭제하지 않습니다.

GitHub Actions의 Windows self-hosted runner에서 새 `v*` 태그를 릴리스하면 앱별 패키지, 버전이 포함된 온라인 카탈로그, 시작 프로그램을 함께 게시합니다. 앱 버전은 앱 매니페스트의 `version` 또는 해당 프로젝트에서 평가한 MSBuild `Version`을 사용하고, 카탈로그의 전역 릴리스 버전과 분리합니다. 기존 카탈로그에 앱 버전이 없으면 알 수 없는 버전으로 표시합니다. 클라이언트는 설정된 저장소의 최신 정식 릴리스를 확인합니다. GHES 배포는 Actions가 전달하는 서버 URL, API URL, 저장소 이름을 사용합니다.

배포 카탈로그에는 기존 앱 ID를 유지해야 합니다. 설치된 앱을 빠뜨린 카탈로그는 기존 앱의 실행을 보호하기 위해 적용하지 않고 마지막으로 검증된 카탈로그를 유지합니다.

통합 WPF 배포본에는 `i-luncher-updates.json` 설정을 함께 포함하며, 인증 토큰은 포함하지 않습니다. 온라인 업데이트를 적용한 앱은 검증된 개별 앱 실행 파일과 공용 런타임을 사용합니다. 작은 시작 프로그램과 통합 WPF 런처는 같은 사용자 캐시와 설치 기록을 공유하므로 동일한 앱 패키지를 별도로 복사하지 않습니다.

## file_shared 파일 공유 서비스

`file_shared`는 런처에서 실행할 수 있는 HTTP 파일 공유 서비스와 업로드 UI입니다. 처음 실행하면 기존 프로그램 동작에 따라 관리자 권한을 요청해 `FileShareService` Windows 서비스를 설치하고 자동 시작으로 등록한 뒤 업로드 창을 엽니다. 서버는 TCP 5000 포트를 사용하며 업로드 파일과 서비스 실행 파일을 `C:\tem` 아래에 저장합니다.

실행에는 Windows .NET Framework 4.8이 필요합니다. 개발 빌드에는 .NET Framework 4.8 타깃팅 팩이 포함된 Visual Studio Build Tools의 MSBuild와 NuGet을 사용할 수 있어야 합니다.
