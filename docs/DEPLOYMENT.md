# i-luncher 배포 · 운영 정리

런처와 앱을 어떻게 나눠 주고, 누가 무엇을 보고, 버전과 업데이트를 어디서 받는지 한곳에 모은 문서입니다.
GitHub(github.com)와 GitHub Enterprise 모두 같은 방식입니다. 이 문서와 다른 문서가 다르면 이 문서가 기준입니다 (2026-11-22).

## 한눈에

- **설치 파일은 하나**(`i-luncher-Setup.exe`, GitHub 릴리스)이고, 누구에게나 같은 런처가 갑니다.
- 역할은 **일반**과 **마스터** 두 가지입니다. 부서 역할은 아직 없습니다 (나중에 정함).
- **일반은 로컬 앱**입니다: API 서버에 아무것도 묻지 않습니다. 화면은 스토어 · 왼쪽 메뉴 없는 **홈 화면**(큰 아이콘 + 아래 독)이고, 기본 앱 `GENERAL_APPS`(기본 DeskCal, 정보는 GitHub 공지 저장소의 `basic-apps.json`)과 그 PC 에 설치한 앱만 보입니다. 업데이트가 있으면 **모두 업데이트**.
- **마스터**는 기본 목록에 **서버 목록**(API 서버)을 더해 봅니다. API 서버의 관리는 런처가 아니라 서버 PC 의 **i-api 관리 창**에서 합니다.
- **공지 · 기본 목록은 모두에게**: GitHub 공지 저장소(`i-luncher-notices`)의 `공지.txt` 와 `basic-apps.json` 을 런처가 바로 읽습니다. GitHub 에서 고치면 런처 릴리스 없이 반영됩니다.
- **버전 확인 · 설치 · 업데이트는 모두 그 앱의 GitHub 저장소에서 바로** 받습니다 (Actions 러너가 올린 릴리스). API 서버를 거치지 않습니다.
- 일반 사용자가 받는 저장소(런처 · 기본 목록 앱)는 **공개**로 둡니다. 그러면 토큰이 필요 없습니다.

```
                ┌──────────── GitHub(Enterprise) 릴리스 ────────────┐
                │  i-luncher · DeskCal · Compare · …  (Actions 러너)  │
                └───────▲──────────────────────▲─────────────────────┘
                        │ 버전 · 설치 · 업데이트  │ 앱 자체 업데이트
   일반 PC ─ i-luncher ─┘                       └─ 설치된 앱
   모든 PC ─ i-luncher ─── 공지 · 기본 목록: GitHub 공지 저장소의 공지.txt · basic-apps.json
   마스터 PC ─ i-luncher ─┬─ (위와 같음)
                          └─ API 서버(i-api, CDEP): 서버 목록 · 사용 기록 (관리는 서버 PC 의 i-api 관리 창)
```

## 1. 역할: 일반 · 마스터

| | 일반 | 마스터 |
|---|---|---|
| 누구 | 마스터가 아닌 모든 사람 | 이 PC 의 Windows 계정이 런처 빌드 설정 `MASTER_USERS` 에 있음 |
| 화면 | 홈 화면 (스토어 없음): `GENERAL_APPS`(기본 DeskCal) + 설치한 앱 + 내 앱 · 내 PC 프로그램, 위쪽 분류 필터(아이콘, 내 앱 포함), 모두 업데이트 | 왼쪽 메뉴의 스토어(기본 목록 + 서버 목록) · 설치됨 · 내 앱 |
| 공지 (GitHub `공지.txt`) | 있음 | 있음 |
| API 서버 | **쓰지 않음** | 서버 목록 · 사용 기록 (설정 → API 서버) |
| 앱 사용 기록 (AppKit `AppUsage`) | 모으지도 보내지도 않음 (나중에 정함) | 보냄 |

- 마스터 계정의 제목 표시줄에는 **일반 | 마스터** 전환이 있습니다. **일반**을 고르면 새로 설치한 일반 사용자의 화면이 됩니다 (확인용, 이 PC 에 기억): 따로 둔 빈 데이터 `C:\i-luncher\general-preview` (일반으로 바꿀 때마다 비움), 이 PC 에 설치된 다른 앱은 숨김. 마스터의 데이터는 그대로.

## 공지

**공지**는 모든 런처(일반 · 마스터)가 GitHub 공지 저장소(기본 `i-luncher-notices`, 공개)의 `공지.txt` 를 바로 읽습니다 (API 서버를 거치지 않음). 첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 `---` 만 있는 줄. 고치기는 GitHub 에서 그 파일을 편집해 `main` 에 저장 (마스터의 런처 설정 → 런처 공지 → **GitHub에서 편집** 이 그 페이지를 엶).

- 저장소는 빌드 설정 `NOTICES_REPOSITORY`(비우면 `i-luncher-notices`, 이름만이면 런처 저장소와 같은 호스트 · 소유자), 파일은 `NOTICES_PATH`(비우면 `공지.txt`).
- 제목이 같으면 같은 공지이고, 본문을 고치면 읽은 사람에게도 다시 알립니다. 읽지 않은 공지는 목록 위에 띠로 보입니다.
- 새 환경에서는 같은 이름의 **공개** 저장소를 만들고 `공지.txt` 를 넣습니다 (비워 두면 공지 없음).
- 공지 저장소가 아직 없거나 닿지 않거나 GitHub 와 다르게 답해도 런처는 그대로 돌고 공지만 비어 있습니다 (기본 목록은 보관한 목록 · 런처에 든 목록). 처리하지 못한 오류가 생겨도 런처는 꺼지지 않고 창 위 오류 줄에 보이며 `%LOCALAPPDATA%\i-luncher\error.log` 에 남습니다.
- 마스터를 더하거나 빼려면 런처 빌드 설정 `MASTER_USERS` 를 고쳐 런처를 릴리스합니다. 설치 파일을 다시 나눠 주지 않습니다 (런처가 스스로 업데이트).

## 2. 앱 목록

| 목록 | 누가 보나 | 원본 · 고치는 곳 | 반영 |
|---|---|---|---|
| **기본 목록** | 모두 | GitHub 공지 저장소 `i-luncher-notices` 의 `basic-apps.json` + 스크린샷 `basic-media/<id>-1~3.png` (GitHub 에서 편집) | 런처가 다음에 시작 · 새로고침할 때 (런처 릴리스 필요 없음) |
| **서버 목록** | 마스터 | 서버 PC 의 i-api 관리 창 → 앱 목록 (`CDEP\seed\apps.json` 의 새 앱 · 원본에서 고친 설명 · 사진은 i-api 가 시작할 때 스스로 반영 (사내에서 고친 칸은 그대로), 빈 칸 채우기는 `CDEP\import_app_list.py`) | 저장하면 바로 |
| **내 앱** | 그 PC · 그 사용자 | 런처 독의 **+ → GitHub 프로젝트…** (`C:\i-luncher\my-apps.json`) | 바로 |

- **모두에게 나눠 주는 앱은 기본 목록으로만** 관리합니다. 그 밖의 앱은 각 사용자가 내 앱으로 더해 씁니다.
- 지금 목록: 기본 = DeskCal · Compare · FastCopy · DiskScan · Capture · MemoPad. 서버 = Terminal Notes · EQ Status · File Shared · ImPerformance · i-api · PARA · WIDE.
- `basic-apps.json` 의 `repository` 는 **저장소 이름만** 씁니다. 호스트 · 소유자는 런처 빌드 설정 `LAUNCHER_REPOSITORY` 와 같게 붙습니다.
- 스토어 내용(한 줄 설명 · 상세 설명 · 개발자 · 스크린샷 3장 이상)은 원본 파일의 값을 그대로 씁니다. 지어내지 않습니다.

### 기본 목록에 앱 더하기 (GitHub 에서, 런처 릴리스 없음)

1. 앱 저장소에 릴리스 workflow 가 있고 릴리스(`<설치이름>-win-x64.zip`)가 올라가는지 확인합니다.
2. 저장소가 **공개**인지 확인합니다 (일반 사용자는 토큰이 없음).
3. 공지 저장소의 `basic-media/` 에 스크린샷 3장을 올립니다 (**Add file → Upload files**).
4. 공지 저장소의 `basic-apps.json` 을 GitHub 에서 편집해 항목(`id`, `name`, `repository` = 저장소 이름, `category`, `description`, `installName`, `details`, `author`, `screenshots`, `order`)을 넣고 `main` 에 저장합니다.
5. 끝. 각 런처가 다음에 시작하거나 새로고침할 때 보입니다.

- 런처는 읽은 목록을 이 PC 에 보관(`basic-apps-cache.json`)해 GitHub 에 닿지 못하면 그것을 씁니다. 형식이 틀리면 마지막 목록을 쓰고 마스터 화면에 오류를 띄웁니다.
- 스크린샷은 파일 내용(git sha)이 바뀌면 다시 받습니다. 런처에 든 같은 그림이면 받지 않습니다.
- 공지 저장소에 `basic-apps.json` 이 없거나 처음 실행이 오프라인이면 런처에 든 예비 목록(`i-luncher\basic-apps.json` · `basic-media\`)을 씁니다. 새 환경에서는 그 두 개를 공지 저장소에 복사해 시작합니다.

### 사용자가 지운 기본 앱

- 사용자는 앱을 우클릭 → **목록에서 삭제…** 로 기본 목록 앱도 뺄 수 있습니다 (설치돼 있으면 목록에서만 빼기 / 설치된 앱도 제거).
- 뺀 앱은 `C:\i-luncher\removed-apps.json` 에 남습니다. **런처를 업데이트하거나 다시 설치해도 다시 나오지 않습니다.**
- 다시 보이는 때는 사용자가 독의 **+ → 삭제한 앱 다시 추가**를 누르거나, **+ → GitHub 프로젝트…** 에 같은 저장소를 넣을 때뿐입니다.
- 기본 목록에 새로 넣은 앱은 그대로 보입니다 (지운 적이 없으므로).

## 3. 릴리스와 업데이트 (GitHub 에서 바로)

1. 앱 저장소 `main` 에 push → 그 저장소의 self-hosted 러너가 빌드 → 다음 버전으로 GitHub 릴리스.
2. **런처**는 시작할 때와 새로고침할 때 각 앱 저장소의 `releases/latest` 를 GitHub 에 바로 묻고, 높은 버전이 있으면 설치 · 업데이트합니다. 런처 자신도 `LAUNCHER_REPOSITORY` 의 릴리스로 스스로 업데이트합니다.
3. **설치된 앱**은 런처 없이 실행해도 스스로 업데이트합니다 (AppKit `AppUpdater`, DeskCal · Terminal Notes 는 자체 업데이터). 앱 옆의 `update.json`(빌드할 때 `UPDATE_*` 로 만들어짐)에 적힌 GitHub API · 저장소에 바로 묻습니다.
4. EQ Status 는 Velopack 으로 업데이트합니다: `EQSTATUS_UPDATE_REPOSITORY_URL` = GitHub(Enterprise) 저장소 주소 (비공개면 `EQSTATUS_UPDATE_TOKEN`).

### 토큰

- **공개 저장소는 토큰이 필요 없습니다.** 일반 사용자가 받는 저장소는 공개로 둡니다.
- **마스터 PC 는 토큰을 넣지 않습니다**: 런처가 시작할 때 이 PC 의 git 로그인(저장소를 push 하는 그 계정, Git Credential Manager 등)을 창 없이 읽어 토큰으로 쓰고, 설치된 앱의 자체 업데이터도 같은 토큰을 씁니다. 설정에 직접 넣은 토큰이 있으면 그것이 먼저입니다.
- 비공개 저장소를 읽어야 하면:
  - **프로젝트마다**: 비공개 저장소는 **프로젝트마다** 토큰을 받습니다: 앱을 우클릭 → **토큰…** (GitHub 프로젝트 추가 때는 읽지 못하면 그 자리에서 물음). 저장하기 전에 그 저장소를 그 토큰으로 읽어 보고, 이 PC 의 이 Windows 계정에만 암호화해 둡니다 (`settings.json` 의 `RepositoryTokens`). 런처와 **그 앱의 자체 업데이터**가 그 프로젝트의 토큰을 씁니다. 설정 화면에 토큰 하나를 넣는 칸은 없습니다.
  - 모두 같은 토큰: 런처 빌드 설정 `LAUNCHER_TOKEN`, 앱 빌드 설정 `UPDATE_TOKEN` (읽기 전용만. 실행 파일 안에 들어감).
  - 찾는 순서: 프로젝트 토큰 → 호스트 토큰(마스터 PC 의 git 로그인) → 빌드 토큰.
- github.com 은 토큰 없이 쓰면 **인터넷 주소 하나당 시간당 60번**까지입니다 (같은 공유기 뒤의 PC 가 함께 씀). 토큰을 쓰는 프로젝트는 시간당 5,000번. GitHub Enterprise 는 보통 이 한도를 켜 두지 않습니다.

## 4. 저장소 공개 기준

| 저장소 | 공개 |
|---|---|
| i-luncher | 공개 (런처 자체 업데이트) |
| 기본 목록 앱 | 공개 (일반 사용자가 받음) |
| 서버 목록 앱 | 마스터만 보므로 비공개여도 됨 (마스터 PC 는 git 로그인을 자동으로 씀). 일반 사용자가 내 앱으로도 받지 못하게 하려면 비공개 |
| i-luncher-notices | 공개 (모든 런처가 공지 · 기본 목록을 읽음) |
| Receive | 런처로 나눠 주지 않음 (관리자 PC 에서 독 **+** 로 등록) |

공개하기 전에 확인합니다:
- git **기록 전체**에 토큰 · 비밀번호 · 내부 주소 · 실제 데이터가 없는지 (지금 파일만이 아니라 지난 커밋도 보입니다).
- 커밋 이메일이 공개해도 되는 주소인지.
- **workflow 트리거가 `push` · `workflow_dispatch` 뿐인지.** self-hosted 러너를 쓰는 공개 저장소에 `pull_request` 트리거를 두면, 누구나 포크해 PR 로 러너 PC 에서 코드를 실행할 수 있습니다. 넣지 않습니다.

## 5. API 서버 (i-api, `CDEP\i-api.csproj`)

| 하는 일 | 쓰는 쪽 |
|---|---|
| 서버 목록 · 스크린샷 (`/launcher/catalog`, `/launcher/media`) | 마스터의 런처 (`/launcher/notices` 는 남아 있지만 런처는 쓰지 않음: 공지는 GitHub) |
| 관리 API (`/admin/api/…`, 관리자 비밀번호) | i-api 관리 창 |
| 앱 사용 기록 (`/launcher/usage`) | 마스터 계정의 앱 (AppKit `AppUsage`), ImPerformance 가 읽음 |
| 스킬 · 플러그인 · 공지 (`/api/…`) | Terminal Notes |

- **서버 PC 한 대**에서 돌립니다 (C-DEP 에는 올리지 않음). 그 PC 에 릴리스의 `i-api-win-x64.zip` 을 풀고 (`%LOCALAPPDATA%\i-api`) `i-api.exe` 를 실행해 관리 창에서 포트를 정하고 [실행] 합니다 (런처에는 서버가 들어 있지 않음, 사용자 결정 2026-11-25).
- 이름은 **i-api** (C#, 3.0 부터): 서버 `%LOCALAPPDATA%\i-api\i-api.exe`, 설정 그 옆의 `settings.txt`, 데이터 `C:\ProgramData\i-api-data`, 로그인 자동 시작 `i-api.vbs`. 서버 PC 에 Python 이 필요 없습니다. 예전 Python 서버(`relay.py`, 예전 이름 `TerminalNotesRelay` 포함)는 새 i-api.exe 를 처음 실행할 때 저절로 옮겨집니다 (SETTINGS → `settings.txt`). 새 버전은 관리 창의 [업데이트] (같은 저장소의 최신 릴리스에서 zip 을 받아 바꿈).
- i-api 관리 창 → **API**: API 마다 켜고 끄기 (끈 API 는 503), 오늘 · 30일 호출, 실패, 평균 시간. 새 API 는 `CDEP\Server\Functions.cs` 에 등록합니다.
- **일반 사용자의 런처 · 앱은 이 서버에 아무것도 보내지 않습니다.**
- GitHub 중계(`/api/v3/…`)는 예전에 빌드한 앱을 위해 남아 있을 뿐, 지금 런처 · 앱은 쓰지 않습니다.
- EQ Status 의 설비 상태 API 는 이 서버가 아니라 따로 둔 설비 API 서버입니다 (포트 **2004**). 포트는 어디서나 API 서버 **2003** · 설비 상태 **2004** 입니다 (설비 DB 를 넣은 API 서버는 `EQUIPMENT_PORT` 2004 에서도 설비 상태를 내려 줌).
- **서버 주소**: 런처 빌드 설정 `API_SERVER` 가 기본이고, 마스터는 런처 설정 → **API 서버**에서 바꿉니다 (`IP:포트` 로, 포트는 서버 PC 에서 i-api.exe 로 정한 것, 비우고 연결하면 기본값, `C:\i-luncher\settings.json` 의 `ApiServer`). 그 PC 의 서버 목록 · 스크린샷 · 설치된 앱의 사용 기록이 모두 그 주소로 갑니다. 주소를 바꾸려고 런처를 다시 릴리스하지 않아도 됩니다.
- 서버 PC 에 두는 방법: [`CDEP\README.md`](../CDEP/README.md).

## 6. 설정 키

**런처 빌드 설정** (`i-luncher.env`, 프로필 폴더 `APPS_ENV_DIR` 또는 러너 환경 변수 `ILUNCHER_ENV_PATH`)

| 키 | 값 |
|---|---|
| `API_SERVER` | API 서버 기본 주소 (마스터만 씀). 마스터는 설정 → API 서버에서 바꿀 수 있음 |
| `MASTER_USERS` | 마스터 Windows 계정 (쉼표). 비우면 모두 일반 |
| `GENERAL_APPS` | 일반 화면(홈)에 늘 보이는 기본 앱 id (쉼표, `basic-apps.json` 의 id). 비우면 `deskcal` |
| `LAUNCHER_REPOSITORY` | 런처 저장소 `https://<호스트>/<소유자>/i-luncher` (기본 목록의 호스트 · 소유자도 여기서) |
| `GITHUB_HOST` | GitHub Enterprise 호스트 (짧게 쓴 `소유자/저장소` 에 붙음) |
| `NOTICES_REPOSITORY` · `NOTICES_PATH` | 공지 저장소(비우면 `i-luncher-notices`) · 파일(비우면 `공지.txt`) |
| `BASIC_APPS_PATH` | 공지 저장소 안의 기본 목록 파일 (비우면 `basic-apps.json`, 스크린샷은 그 옆 `basic-media/`) |
| `LAUNCHER_TOKEN` | (선택) 모두가 쓸 읽기 전용 토큰. 공개 저장소면 비움 |

**앱 빌드 설정** (`<앱>.env`, 같은 프로필 폴더 또는 러너 환경 변수 `RELEASE_ENV_PATH`)

| 키 | 값 |
|---|---|
| `UPDATE_GITHUB_API` | `https://<Enterprise 호스트>/api/v3` (github.com 이면 `https://api.github.com`). **API 서버 주소를 넣지 않습니다** |
| `UPDATE_REPOSITORY` | `<소유자>/<앱>` |
| `UPDATE_TOKEN` | 보통 비움 (공개 저장소, 또는 그 PC 사용자가 이 프로젝트에 넣은 토큰) |
| `UPDATE_INSTALL_NAME` | 설치 폴더 · "앱" 목록 이름 (`basic-apps.json` 의 `installName` 과 같게) |

**사용자 PC** (`C:\i-luncher` = 백업해 새 PC 에서 다시 쓸 것만, 런처 업데이트 · 제거에도 남음. 예전 `%LOCALAPPDATA%\i-luncher` 의 이 파일들은 런처가 처음 시작할 때 옮김, 그 폴더를 만들 수 없는 PC 는 예전 위치)

| 파일 | 내용 |
|---|---|
| `settings.json` | 자동 업데이트, 마스터가 바꾼 API 서버 주소 `ApiServer`, 프로젝트마다의 토큰 `RepositoryTokens`(암호화), 마스터 PC 의 git 로그인 토큰 `Tokens` · `AutoTokenHosts`, `GeneralView`(마스터의 일반 화면) |
| `my-apps.json` | 내 앱 |
| `removed-apps.json` | 목록에서 삭제한 기본 · 서버 목록 앱 |
| `favorites.json` | 즐겨찾기 · 내 PC 프로그램 · 웹사이트 |
| `custom-icons\` · `my-media\` | 고른 아이콘 · 내 앱 그림 (즐겨찾기 · 내 앱이 전체 경로로 가리킴) |

캐시는 `%LOCALAPPDATA%\i-luncher` 에 둡니다 (지워도 다시 받음): `basic-apps-cache.json` · `catalog-cache.json` (받아 둔 기본 · 서버 목록), `notice-state.json`, `icons\` · `screenshots\` (`screenshots\basic\` = 기본 목록 스크린샷) · `site-icons\` · `downloads\` · `error.log` (처리하지 못한 오류). 토큰은 PC · 계정마다 암호화라 새 PC 로 옮겨지지 않습니다 (다시 넣음).

설정 → **재설정**: **목록** = `removed-apps.json` · `basic-apps-cache.json` · `catalog-cache.json`, **전체** = 위 사용자 데이터와 캐시 모두. 런처를 다시 시작하며 지우고, 설치한 앱은 그대로 둡니다 (README "설정 · 저장 위치").

## 7. 처음 구성할 때 · 옮길 때

1. 런처와 기본 목록 앱 저장소가 **공개**인지 확인합니다 (4장 확인 항목 포함). 공지 저장소 `i-luncher-notices`(공개)를 만들고 `공지.txt`, 그리고 `i-luncher\basic-apps.json` · `basic-media\` 의 복사본을 넣습니다.
2. 저장소마다 self-hosted 러너를 등록하고, 앱 빌드 설정 `UPDATE_GITHUB_API` 를 GitHub(Enterprise) API 로 둡니다.
3. 런처 빌드 설정에 `API_SERVER` · `GITHUB_HOST` · `LAUNCHER_REPOSITORY` · `MASTER_USERS` 를 넣습니다.
4. 서버 PC 에 `i-api-win-x64.zip` 을 풀고 i-api.exe 로 API 서버를 켠 뒤 서버 목록을 넣습니다 (`CDEP\README.md`, `import_app_list.py`). 마스터 PC 는 런처 설정 → API 서버에 그 PC 의 `IP:포트` 를 넣습니다.
5. 런처와 앱을 한 번씩 릴리스합니다. **러너가 같은 PC 를 쓰면 하나씩** (빌드가 겹치면 메모리가 모자랄 수 있음). 이미 설치된 앱은 그 릴리스를 받은 뒤부터 새 업데이트 주소를 씁니다.
6. 확인:
   - 일반 계정 PC: 왼쪽 메뉴 없는 홈 화면에 DeskCal(+ 설치한 앱), `공지.txt` 의 공지가 보임, 버전이 보이고 받기 · 업데이트 · 모두 업데이트가 됨.
   - 마스터 PC: 제목 표시줄의 일반 | 마스터 전환, 서버 목록 · 공지, 설정의 API 서버.
   - 기본 앱을 목록에서 삭제 → 런처를 다시 켜도 안 보임 → + → 삭제한 앱 다시 추가로 돌아옴.

## 8. 예전 방식에서 바뀐 점

- 부서용 설치 파일(`i-luncher-Team-Setup.exe`)과 그룹 파일(`store-group.txt`)은 없앴습니다. 새 설치 파일이 남은 `store-group.txt` 를 지웁니다.
- 런처 · 앱의 업데이트가 API 서버의 GitHub 중계를 거치지 않습니다. 예전 빌드의 앱은 중계로 새 버전을 한 번 받은 뒤부터 GitHub 에 바로 묻습니다.
- "부서 목록"은 **서버 목록**으로 부릅니다 (마스터만 봄).
- 런처 공지는 API 서버가 아니라 GitHub 공지 저장소의 `공지.txt` 입니다. 모두에게 보입니다.
- 기본 목록은 런처 안(고치면 런처 릴리스)이 아니라 GitHub 공지 저장소의 `basic-apps.json` 입니다 (런처 안의 것은 예비).
- 앱 사용 기록은 마스터 계정만 보냅니다.

## 9. 하지 말 것

- 일반 사용자 경로(일반 화면의 런처, 앱 업데이트)에 API 서버 호출을 다시 넣지 않습니다.
- 부서 역할을 만들지 않습니다 (사용자가 나중에 정함).
- 공개 저장소의 workflow 에 `pull_request` 트리거를 넣지 않습니다.
- 기본 목록 앱의 저장소를 비공개로 두지 않습니다.
- 사용자가 지운 기본 앱을 런처 업데이트로 다시 보이게 하지 않습니다.
- 기본 목록을 런처 안(고칠 때마다 런처 릴리스)으로 되돌리지 않습니다. 원본은 공지 저장소의 `basic-apps.json` 입니다.
