# i-luncher

GitHub에 프로젝트별로 릴리스된 앱을 한곳에서 설치·업데이트·실행하는 런처입니다.

- 앱은 **GitHub 저장소 주소**로 추가합니다. 런처가 그 저장소의 **최신 릴리스 태그를 버전으로** 보여 줍니다.
- 카드를 누르면 설치되지 않은 앱은 설치하고, **설치된 버전보다 높은 릴리스가 있으면 자동으로 업데이트한 뒤** 실행합니다.
- 설치된 앱은 런처 없이 **따로 실행해도 스스로 업데이트**합니다 (각 앱의 자체 업데이터: [AppKit](AppKit/README.md), DeskCal·Terminal Notes는 자체 구현).
- 앱 목록은 **API 서버**(ApiServer 프로젝트, 예: `http://<서버 IP>:2003`)가 관리합니다. 런처는 누구나 목록을 읽고, 추가·수정·삭제는 서버 관리자 비밀번호로 저장합니다. API 서버가 없으면 i-luncher 저장소의 [`catalog/apps.json`](catalog/apps.json)을 GitHub API로 고치는 방식도 쓸 수 있습니다.
- 앱 저장소는 비공개라도 됩니다. API 서버의 **GitHub 중계**가 서버 PC의 GitHub 자격 증명으로 릴리스를 대신 받아 주므로 사용자 PC와 앱에 GitHub 토큰을 넣지 않습니다.
- i-luncher 자신도 이 저장소의 **Actions 러너가 릴리스**하고, 설치된 런처는 시작할 때 **스스로 업데이트**합니다.

github.com과 GitHub Enterprise Server(`https://<호스트>/api/v3`) 모두 같은 방식으로 동작합니다.

```text
GitHub
├─ team/i-luncher            ← 이 저장소: 런처 소스 + catalog/apps.json(앱 목록) + 공지.txt
│    └─ Releases: i-luncher-Setup.exe, i-luncher-win-x64.zip
├─ team/DeskCal              ← 앱마다 자기 저장소와 Actions 러너
│    └─ Releases: DeskCal-win-x64.zip (+ DeskCal-Setup.exe)
├─ team/SwiftCopy …
└─ team/ATOZ (Velopack)      ← 설치 프로그램 방식도 지원

사용자 PC (%LOCALAPPDATA%\Programs)
├─ i-luncher\                ← i-luncher-Setup.exe 로 설치, 스스로 업데이트
├─ DeskCal\                  ← 런처가 설치, 앱이 스스로도 업데이트
└─ SwiftCopy\ …
```

## 사용

1. 릴리스의 **i-luncher-Setup.exe**를 실행합니다. 관리자 권한 없이 `%LOCALAPPDATA%\Programs\i-luncher`에 설치되고 시작 메뉴·바탕화면 바로 가기가 생깁니다. PC에 .NET 10 Desktop Runtime이 없으면 설치 폴더에 함께 넣습니다.
2. 런처를 열면 앱 카드에 **설치된 버전과 최신 릴리스 버전**이 보입니다 (`v1.1.3 → v1.1.4 업데이트`, `v1.1.4 · 최신`, `v2.0.0 · 미설치`, `릴리스 없음`, `확인 실패`). 버전에 마우스를 올리면 설치 위치·릴리스 날짜·확인 실패 이유가 나옵니다.
3. 카드를 누르면:
   - **미설치** → 버전·파일·설치 위치를 보여 주고 설치 여부를 묻습니다. 설치 후 실행합니다.
   - **설치됨, 새 버전 있음** → 업데이트한 뒤 실행합니다. 앱이 실행 중이면 종료하고 업데이트할지 묻습니다 (아니면 지금 버전을 엶). 업데이트가 실패해도 이전 버전은 그대로 남아 실행됩니다.
   - **설치됨, 최신** → 바로 실행합니다. (최근 2분 안에 확인하지 않았으면 실행 전에 한 번 더 확인)
4. 카드 우클릭: 실행 · 업데이트 · 설치/다시 설치 · 제거 · 편집 · 목록에서 삭제 · 저장소 열기 · 릴리스 노트 · 설치 폴더 열기.
5. 새 버전이 있는 앱이 있으면 목록 위 **모두 업데이트**로 한꺼번에 업데이트합니다. 실행 중인 앱은 한 번만 묻고 닫았다가 업데이트 뒤 다시 실행합니다 (취소하면 그 앱만 건너뜀).
6. 왼쪽에서 전체 앱 / 설치된 앱 / **업데이트** / 분류로 거르고, 위 검색창(`Ctrl+F`)으로 찾습니다. `F5`는 새로고침입니다. 30분마다 목록과 버전을 다시 확인합니다.

설치한 앱은 Windows **설정 → 앱**에도 나오며 거기서 제거할 수도 있습니다. 런처를 제거해도 런처로 설치한 앱은 남습니다.

### 즐겨찾기

왼쪽 **즐겨찾기**에 런처 앱과 이 PC 에 설치된 일반 프로그램을 모아 두고 누르면 바로 실행합니다.

- 런처 앱: 카드 우클릭 → 즐겨찾기에 추가 / 빼기 (이름 오른쪽 ★).
- 일반 프로그램: **프로그램 추가** → 설치된 프로그램(시작 메뉴) 목록에서 고르거나 찾아보기. 실행 파일·바로 가기·폴더를 창에 끌어 놓아도 추가됩니다. 우클릭: 실행 · 편집(이름·경로·실행 인수) · 파일 위치 열기 · 삭제. 파일이 없어진 카드는 흐리게 보입니다.
- 이 PC 에만 저장합니다: `%LOCALAPPDATA%\i-luncher\favorites.json`.

### 설정

**API 서버** 주소(비우면 빌드 설정 `API_SERVER`), **관리자 비밀번호**(앱 목록을 바꿀 때만, DPAPI로 암호화), **i-luncher 자동 업데이트**. 앱 목록은 API 서버의 목록을 씁니다.
마스터 계정(`MASTER_USERS`)에는 **마스터만 보기**가 더 있어, 체크한 앱은 마스터에게만 보입니다 (저장할 때 관리자 비밀번호 필요).

### 토큰

github.com 저장소는 API 서버의 GitHub 중계로 받으므로 토큰이 필요 없습니다. 그 밖의 비공개·internal 저장소는 빌드 설정 `.env`의 `LAUNCHER_TOKEN`(읽기 전용, contents: read)을 런처에 넣으며, 런처 저장소와 같은 호스트에만 보냅니다.

## 앱 목록 관리 (추가 · 수정 · 삭제)

**앱 추가**를 누르고 저장소 주소를 넣은 뒤 **불러오기**를 누르면 저장소 이름·설명과 최신 릴리스를 읽어 칸을 채웁니다. 저장하면 `catalog/apps.json`에 `i-luncher: <앱> 추가` 커밋이 생깁니다. 그 사이 다른 사람이 목록을 바꿨으면 최신 목록에 다시 적용해 저장합니다 (덮어쓰지 않음). 수정·삭제도 같습니다. 이 커밋은 런처 릴리스를 만들지 않습니다 (워크플로 `paths-ignore`).

받는 주소: `https://github.example.com/team/App`, `…/App.git`, `…/App/releases/tag/v1.0.0`, `git@github.example.com:team/App.git`, `team/App`(기본 호스트) 등.

`catalog/apps.json`은 GitHub에서 직접 고쳐도 됩니다. 필수는 `id`, `name`, `repository`이고 나머지는 비우면 자동입니다.

```json
{
  "schemaVersion": 1,
  "apps": [
    { "id": "deskcal", "name": "DeskCal", "repository": "https://github.example.com/team/DeskCal",
      "category": "일정", "description": "바탕화면 달력", "installName": "DeskCal" },
    { "id": "atoz", "name": "ATOZ", "repository": "https://github.example.com/team/status",
      "kind": "setup", "installName": "ATOZ" }
  ]
}
```

| 필드 | 뜻 (비우면) |
|---|---|
| `kind` | `zip`(릴리스 zip을 런처가 설치) 또는 `setup`(릴리스의 설치 프로그램 실행). 비우면 zip이 있으면 zip, Velopack 릴리스나 설치 프로그램만 있으면 setup |
| `asset` | 설치할 zip 이름, `*` 가능 (`win-x64`가 든 zip → 앱 이름으로 시작하는 zip → 하나뿐인 zip) |
| `setupAsset`, `setupArguments` | 설치 프로그램 이름과 인수 (`*Setup*.exe`/`.msi`, 인수 `/S`, Velopack은 `--silent`) |
| `installName` | 설치 폴더 `%LOCALAPPDATA%\Programs\<이름>`과 Windows "앱" 목록 키 이름 (`name`). **앱의 자체 업데이터·설치 프로그램과 같아야** 따로 실행해도 스스로 업데이트합니다. 앱 추가 창이 zip 이름(`DeskCal-win-x64.zip` → `DeskCal`)에서 채웁니다 |
| `executable`, `arguments` | 실행 파일(설치 폴더 기준)과 인수 (설치 이름과 같은 exe → 하나뿐인 exe, `current\` 아래도 찾음) |
| `icon` | 카드 아이콘: 저장소 안 경로(`Assets/App.png`) 또는 https 주소 (아래 '아이콘') |
| `includePrerelease` | 프리릴리스도 최신으로 볼지 (false) |
| `order`, `category`, `description` | 표시 순서, 분류, 설명 |

API 서버도 목록 저장소도 없는 빌드는 이 PC 목록 `%LOCALAPPDATA%\i-luncher\catalog.json`을 씁니다.

## API 서버

저장소 `ApiServer`의 `server\relay.py`가 이 PC의 모든 API를 맡습니다 (예전 Terminal Notes 중계 서버, 포트 2003 그대로).

| 주소 | 쓰는 곳 |
|---|---|
| `/api/reports`, `/api/skills`, `/api/announcements` | Terminal Notes (앱 키) |
| `/launcher/catalog`, `/launcher/notices` | i-luncher 앱 목록·공지 (읽기 누구나, 저장은 관리자 비밀번호) |
| `/api/v3/repos/<소유자>/<저장소>/…` | GitHub 중계: 런처와 각 앱이 비공개 저장소의 릴리스·아이콘을 토큰 없이 받음 (읽기만, 토큰 계정·앱 목록의 저장소만) |
| `/`, `/apps`, `/status` … | 관리 페이지 (관리자 비밀번호) |

**ApiServer.exe**(관리 프로그램)는 다른 앱처럼 i-luncher로 설치·업데이트하며, 새 버전이면 시작할 때 서버(`%LOCALAPPDATA%\TerminalNotesRelay\relay.py`)를 바꾸고 SETTINGS(비밀값)는 그대로 옮긴 뒤 다시 시작합니다. 데이터는 `C:\ProgramData\TerminalNotesRelayData`에 따로 있습니다.

런처는 빌드 설정 `API_SERVER`(또는 설정 창)로 서버를 알고, 각 앱은 빌드할 때 `UPDATE_GITHUB_API=http://<서버 IP>:2003/api/v3`로 같은 중계를 씁니다.

## 아이콘

카드 아이콘은 설치하기 전에도 앱 저장소에서 불러옵니다.

1. 카탈로그의 `icon`에 지정한 파일
2. 저장소 `.csproj`의 `<ApplicationIcon>` — 실행 파일 아이콘의 원본이라 가장 정확합니다. 테스트·설치 프로그램 프로젝트는 보지 않습니다.
3. 설치된 실행 파일의 아이콘
4. 저장소에서 이름으로 찾은 `.ico`/`.png` (Assets·Resources 아래, 이름에 icon·logo·앱 이름)
5. 아무것도 없으면 이름으로 정해지는 색 타일과 머리글자

받은 아이콘은 `%LOCALAPPDATA%\i-luncher\icons`에 두고 7일마다(못 찾았으면 하루마다) 다시 확인합니다. 저장소 파일 목록·프로젝트 파일·아이콘을 읽으므로 비공개 저장소는 읽기 토큰이 필요합니다.

카드는 창 폭에 맞춰 열 수와 너비를 정합니다. 맨 위는 이름(오른쪽 ★ = 즐겨찾기)과 설명, 맨 아래는 왼쪽 카테고리, 오른쪽 버전(확인 실패·릴리스 없음 = 노랑, 자세한 내용은 툴팁)과 동작 아이콘(업데이트 = 초록 ↑, 받기 = 파랑 ↓)입니다. 진행 중에는 아래에 진행 막대가 나옵니다.
카드를 누르면 받기·업데이트·실행 중 그때 할 일을 합니다.

## 버전

- 앱의 최신 버전 = 그 저장소의 **최신 정식 릴리스 태그** (`v1.2.3`, `1.2.3.4`, `app-v1.2.3-beta.1` 모두 읽음). 비교는 숫자 순서, 같은 번호면 정식이 프리릴리스보다 높고, `+빌드정보`는 무시합니다.
- 설치된 버전 = 런처가 설치한 실행 파일이 그대로면 **설치한 릴리스 버전**, 앱이 스스로 업데이트해 실행 파일이 바뀌었으면 **실행 파일의 버전**, 그것도 없으면 Windows "앱" 목록의 `DisplayVersion`. 그래서 실행 파일 버전이 잘못 찍힌 앱도 같은 업데이트를 반복하지 않습니다 (툴팁에 차이를 보여 줌).
- 높은 릴리스가 있을 때만 업데이트합니다. 같거나 낮으면, 또는 버전을 알 수 없으면 업데이트하지 않습니다.
- 릴리스 워크플로는 **마지막 릴리스의 다음 패치 번호**를 고르고 `-p:Version`으로 넘깁니다. 프로젝트 파일의 `<Version>`은 바닥값이라 큰 변경은 거기를 올리면 됩니다 (예: `1.1.0`). 실행 파일 버전·태그·Windows 앱 목록 버전이 모두 같아집니다.
- 예전 i-luncher의 바이너리 지문 기반 앱 버전(`AppVersions.json`, 루트 `Directory.Build.targets`)은 없앴습니다. `Projects\` 아래 앱도 이제 자기 프로젝트의 버전 그대로 빌드됩니다.

## 배포 (GitHub)

### i-luncher 저장소

1. 이 폴더를 GitHub 저장소(비공개도 됨)로 올립니다. 앱들은 각자 저장소(`<소유자>/<앱>`)로 따로 관리합니다.
2. **Settings → Actions → Runners**에 Windows x64 self-hosted 러너를 등록합니다 (레이블 `self-hosted`, `windows`, .NET 10 SDK 필요).
3. 비공개·internal 저장소면 **Settings → Secrets → Actions**에 `ILUNCHER_ENV`를 만들고 [`.env.sample`](.env.sample)을 참고해 최소 `LAUNCHER_TOKEN=<읽기 전용 토큰>`을 넣습니다. 러너 PC에 `.env`를 두고 러너 환경 변수 `ILUNCHER_ENV_PATH`로 알려 줘도 됩니다. `LAUNCHER_REPOSITORY`는 비워 두면 그 저장소가 됩니다.
4. `main`에 푸시하면 [release.yml](.github/workflows/release.yml)이 확인(`Launcher.Checks`) → 빌드 → `vX.Y.Z` 릴리스(`i-luncher-Setup.exe`, `i-luncher-win-x64.zip`)를 만듭니다. 초안으로 파일을 모두 올린 뒤 공개하므로 파일이 빠진 릴리스가 최신으로 보이지 않습니다.
5. 사용자에게 `i-luncher-Setup.exe`를 한 번 나눠 주면 이후는 런처가 스스로 업데이트합니다 (설정에서 끌 수 있음).

### 앱 저장소 규약

런처가 설치·업데이트할 수 있는 릴리스:

- 태그 `vX.Y.Z`, 파일 `<설치이름>-win-x64.zip` (실행 파일과 의존 파일이 든 폴더, 최상위 폴더 하나로 감싸도 됨). 설치 프로그램 방식이면 `*Setup*.exe`/`.msi`.
- 앱이 스스로 업데이트하려면: 설치 폴더 `%LOCALAPPDATA%\Programs\<설치이름>`과 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<설치이름>`의 `InstallLocation`으로 "설치된 사본"을 알아보고, 같은 저장소의 최신 릴리스를 확인합니다. 런처는 설치할 때 이 키를 만들므로 런처로 설치한 앱도, 앱의 설치 프로그램으로 설치한 앱도 같은 방식으로 업데이트됩니다.

새 앱 저장소는 [AppKit](AppKit/README.md)을 넣으면 위 규약·자체 업데이트·Actions 릴리스가 한 번에 갖춰집니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File AppKit\Install-AppKit.ps1 -Project <앱 저장소>\MyApp\MyApp.csproj -RepositoryRoot <앱 저장소>
```

| 앱 | 릴리스 · 자체 업데이트 |
|---|---|
| DeskCal, Terminal Notes | 기존 자체 구현 (`build\Publish-Release.ps1`, `UpdateService.cs`) — 규약과 같음 |
| FolderCompareWPF, SwiftCopy, DiskAtlas | AppKit 적용 (`.github\workflows\release.yml`, `AppKit\`) |
| ATOZ | Velopack 자체 업데이트. 런처에서는 설치 프로그램 방식 (`kind: setup`) |
| file_shared | .NET Framework 4.8 + Windows 서비스라 AppKit 미적용. 릴리스 zip이 있으면 런처로 설치·실행은 가능 |

### 공지

앱 목록 저장소의 `공지.txt`를 런처가 API로 읽습니다 (Pages·별도 워크플로 없음). 첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 `---`만 있는 줄입니다. 제목이 같으면 같은 공지이고 본문을 고치면 읽은 사람에게도 다시 알립니다. 읽지 않은 공지는 목록 위에 띠로 보입니다.

## 개발

```powershell
dotnet build i-luncher.sln -m:1
dotnet run --project Launcher.Checks          # 핵심 동작 확인 (가짜 GitHub·임시 폴더만 사용)
dotnet run --project Launcher                 # 개발 실행 (.env 없으면 이 PC 목록만, 자체 업데이트 없음)
powershell -NoProfile -ExecutionPolicy Bypass -File build\Publish-Release.ps1 -Version 1.0.0   # out\ 에 릴리스 파일
```

`ILUNCHER_DATA_DIR` 환경 변수로 설정·목록·캐시 폴더(기본 `%LOCALAPPDATA%\i-luncher`)를 바꿔 시험할 수 있습니다. `Launcher.Checks -- --ui-snapshot <데이터 폴더> <png>`는 실제 창을 띄워 카드 상태를 출력합니다.

| 폴더 | 내용 |
|---|---|
| `Launcher\` | 런처 (WPF, .NET 10). `Services\GitHubClient`·`CatalogStore`·`AppInstaller`·`AppManager`·`ReleaseVersion` |
| `Launcher.Checks\` | 확인 프로그램 (NuGet 없음) |
| `installer\` | i-luncher-Setup.exe (.NET Framework 4.8, 런타임 동봉) |
| `AppKit\` | 앱용 자체 업데이터·빌드 설정·릴리스 템플릿 (런처도 같은 업데이터로 업데이트) |
| `build\` | 릴리스 스크립트 (버전 고르기, 빌드, GitHub Release 만들기) |
| `catalog\apps.json`, `공지.txt` | 런처가 API로 읽고 고치는 데이터 |

각 앱과 API 서버의 소스는 이 저장소가 아니라 앱마다 따로 있는 저장소(`DeskCal`, `TerminalNotes`, `ApiServer` …)에 있습니다.

예전 통합 빌드(앱 DLL을 런처에 포함), 소스 자동 검색·빌드, 온라인 시작 프로그램(Bootstrapper), 바이너리 지문 버전 체계는 GitHub 릴리스 방식으로 바뀌어 제거했습니다. 이전 파일은 git 기록에 있습니다.
