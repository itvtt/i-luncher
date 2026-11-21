# i-luncher 배포 · 운영 정리

런처와 앱을 어떻게 나눠 주고, 누가 무엇을 보고, 버전과 업데이트를 어디서 받는지 한곳에 모은 문서입니다.
GitHub(github.com)와 GitHub Enterprise 모두 같은 방식입니다. 이 문서와 다른 문서가 다르면 이 문서가 기준입니다 (2026-11-21).

## 한눈에

- **설치 파일은 하나**(`i-luncher-Setup.exe`, GitHub 릴리스)이고, 누구에게나 같은 런처가 갑니다.
- 역할은 **일반**과 **마스터** 두 가지입니다. 부서 역할은 아직 없습니다 (나중에 정함).
- **일반은 로컬 앱**입니다: API 서버에 아무것도 묻지 않고, 런처에 든 **기본 목록**만 봅니다.
- **마스터**는 기본 목록에 **서버 목록**(API 서버)을 더해 보고, 관리자 메뉴를 씁니다.
- **공지는 모두에게**: GitHub 공지 저장소(`i-luncher-notices`)의 `공지.txt` 를 런처가 바로 읽습니다.
- **버전 확인 · 설치 · 업데이트는 모두 그 앱의 GitHub 저장소에서 바로** 받습니다 (Actions 러너가 올린 릴리스). API 서버를 거치지 않습니다.
- 일반 사용자가 받는 저장소(런처 · 기본 목록 앱)는 **공개**로 둡니다. 그러면 토큰이 필요 없습니다.

```
                ┌──────────── GitHub(Enterprise) 릴리스 ────────────┐
                │  i-luncher · DeskCal · Compare · …  (Actions 러너)  │
                └───────▲──────────────────────▲─────────────────────┘
                        │ 버전 · 설치 · 업데이트  │ 앱 자체 업데이트
   일반 PC ─ i-luncher ─┘                       └─ 설치된 앱
   모든 PC ─ i-luncher ─── 공지: GitHub 공지 저장소의 공지.txt
   마스터 PC ─ i-luncher ─┬─ (위와 같음)
                          └─ API 서버(CDEP\relay.py): 서버 목록 · 관리자 메뉴 · 사용 기록
```

## 1. 역할: 일반 · 마스터

| | 일반 | 마스터 |
|---|---|---|
| 누구 | 마스터가 아닌 모든 사람 | 이 PC 의 Windows 계정이 런처 빌드 설정 `MASTER_USERS` 에 있음 |
| 스토어 | 기본 목록만 (런처 버전에 고정) + 내 앱 | 기본 목록 + 서버 목록 + 내 앱 |
| 공지 (GitHub `공지.txt`) | 있음 | 있음 |
| 관리자 메뉴 | 없음 | 있음 |
| API 서버 | **쓰지 않음** | 서버 목록 · 관리자 메뉴 · 사용 기록 |
| 앱 사용 기록 (AppKit `AppUsage`) | 모으지도 보내지도 않음 (나중에 정함) | 보냄 |

- 마스터 계정의 제목 표시줄에는 **일반 | 마스터** 전환이 있습니다. **일반**을 고르면 그 PC 가 일반 사용자와 똑같이 동작합니다 (확인용, 이 PC 에 기억).

## 공지

**공지**는 모든 런처(일반 · 마스터)가 GitHub 공지 저장소(기본 `i-luncher-notices`, 공개)의 `공지.txt` 를 바로 읽습니다 (API 서버를 거치지 않음). 첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 `---` 만 있는 줄. 고치기는 GitHub 에서 그 파일을 편집해 `main` 에 저장 (관리자 메뉴 → 공지 → i-luncher → **GitHub에서 편집** 이 그 페이지를 엶).

- 저장소는 빌드 설정 `NOTICES_REPOSITORY`(비우면 `i-luncher-notices`, 이름만이면 런처 저장소와 같은 호스트 · 소유자), 파일은 `NOTICES_PATH`(비우면 `공지.txt`).
- 제목이 같으면 같은 공지이고, 본문을 고치면 읽은 사람에게도 다시 알립니다. 읽지 않은 공지는 목록 위에 띠로 보입니다.
- 새 환경에서는 같은 이름의 **공개** 저장소를 만들고 `공지.txt` 를 넣습니다 (비워 두면 공지 없음).
- 마스터를 더하거나 빼려면 런처 빌드 설정 `MASTER_USERS` 를 고쳐 런처를 릴리스합니다. 설치 파일을 다시 나눠 주지 않습니다 (런처가 스스로 업데이트).

## 2. 앱 목록

| 목록 | 누가 보나 | 원본 · 고치는 곳 | 반영 |
|---|---|---|---|
| **기본 목록** | 모두 | `i-luncher\basic-apps.json` + 스크린샷 `i-luncher\basic-media\<id>-1~3.png` | 런처를 릴리스하면 다음 버전부터 |
| **서버 목록** | 마스터 | 관리자 메뉴 → 앱 목록 (처음 넣을 때: `CDEP\seed\apps.json` → `CDEP\import_app_list.py`) | 저장하면 바로 |
| **내 앱** | 그 PC · 그 사용자 | 런처 독의 **+ → GitHub 프로젝트…** (`%LOCALAPPDATA%\i-luncher\my-apps.json`) | 바로 |

- **모두에게 나눠 주는 앱은 기본 목록으로만** 관리합니다. 그 밖의 앱은 각 사용자가 내 앱으로 더해 씁니다.
- 지금 목록: 기본 = DeskCal · Compare · FastCopy · DiskScan · Capture · MemoPad. 서버 = Terminal Notes · EQ Status · File Shared · ImPerformance.
- `basic-apps.json` 의 `repository` 는 **저장소 이름만** 씁니다. 호스트 · 소유자는 런처 빌드 설정 `LAUNCHER_REPOSITORY` 와 같게 붙습니다.
- 스토어 내용(한 줄 설명 · 상세 설명 · 개발자 · 스크린샷 3장 이상)은 원본 파일의 값을 그대로 씁니다. 지어내지 않습니다.

### 기본 목록에 앱 더하기

1. 앱 저장소에 릴리스 workflow 가 있고 릴리스(`<설치이름>-win-x64.zip`)가 올라가는지 확인합니다.
2. 저장소가 **공개**인지 확인합니다 (일반 사용자는 토큰이 없음).
3. `basic-apps.json` 에 항목(`id`, `name`, `repository` = 저장소 이름, `category`, `description`, `installName`, `details`, `author`, `screenshots`)을 넣고 `basic-media\` 에 스크린샷 3장을 넣습니다.
4. `Launcher.Checks` 를 통과시킨 뒤 push 하면 런처가 릴리스됩니다.

### 사용자가 지운 기본 앱

- 사용자는 앱을 우클릭 → **목록에서 삭제…** 로 기본 목록 앱도 뺄 수 있습니다 (설치돼 있으면 목록에서만 빼기 / 설치된 앱도 제거).
- 뺀 앱은 `%LOCALAPPDATA%\i-luncher\removed-apps.json` 에 남습니다. **런처를 업데이트하거나 다시 설치해도 다시 나오지 않습니다.**
- 다시 보이는 때는 사용자가 독의 **+ → 삭제한 앱 다시 추가**를 누르거나, **+ → GitHub 프로젝트…** 에 같은 저장소를 넣을 때뿐입니다.
- 기본 목록에 새로 넣은 앱은 그대로 보입니다 (지운 적이 없으므로).

## 3. 릴리스와 업데이트 (GitHub 에서 바로)

1. 앱 저장소 `main` 에 push → 그 저장소의 self-hosted 러너가 빌드 → 다음 버전으로 GitHub 릴리스.
2. **런처**는 시작할 때와 새로고침할 때 각 앱 저장소의 `releases/latest` 를 GitHub 에 바로 묻고, 높은 버전이 있으면 설치 · 업데이트합니다. 런처 자신도 `LAUNCHER_REPOSITORY` 의 릴리스로 스스로 업데이트합니다.
3. **설치된 앱**은 런처 없이 실행해도 스스로 업데이트합니다 (AppKit `AppUpdater`, DeskCal · Terminal Notes 는 자체 업데이터). 앱 옆의 `update.json`(빌드할 때 `UPDATE_*` 로 만들어짐)에 적힌 GitHub API · 저장소에 바로 묻습니다.
4. EQ Status 는 Velopack 으로 업데이트합니다: `EQSTATUS_UPDATE_REPOSITORY_URL` = GitHub(Enterprise) 저장소 주소 (비공개면 `EQSTATUS_UPDATE_TOKEN`).

### 토큰

- **공개 저장소는 토큰이 필요 없습니다.** 일반 사용자가 받는 저장소는 공개로 둡니다.
- 비공개 저장소를 읽어야 하면:
  - 사용자마다: 런처 **설정 → GitHub 토큰** (읽기 권한). 이 PC · 이 Windows 계정에만 암호화해 저장되고, **설치된 앱의 자체 업데이터도 같은 토큰을 씁니다** (앱에 토큰이 없을 때).
  - 모두 같은 토큰: 런처 빌드 설정 `LAUNCHER_TOKEN`, 앱 빌드 설정 `UPDATE_TOKEN` (읽기 전용만. 실행 파일 안에 들어감).
- github.com 은 토큰 없이 쓰면 **인터넷 주소 하나당 시간당 60번**까지입니다 (같은 공유기 뒤의 PC 가 함께 씀). 자주 넘으면 설정에 토큰을 넣습니다 (시간당 5,000번). GitHub Enterprise 는 보통 이 한도를 켜 두지 않습니다.

## 4. 저장소 공개 기준

| 저장소 | 공개 |
|---|---|
| i-luncher | 공개 (런처 자체 업데이트) |
| 기본 목록 앱 | 공개 (일반 사용자가 받음) |
| 서버 목록 앱 | 마스터만 보므로 비공개여도 됨 (마스터가 런처 설정 → GitHub 토큰). 일반 사용자가 내 앱으로도 받지 못하게 하려면 비공개 |
| i-luncher-notices | 공개 (모든 런처가 공지를 읽음) |
| Receive | 런처로 나눠 주지 않음 (관리자 PC 에서 독 **+** 로 등록) |

공개하기 전에 확인합니다:
- git **기록 전체**에 토큰 · 비밀번호 · 내부 주소 · 실제 데이터가 없는지 (지금 파일만이 아니라 지난 커밋도 보입니다).
- 커밋 이메일이 공개해도 되는 주소인지.
- **workflow 트리거가 `push` · `workflow_dispatch` 뿐인지.** self-hosted 러너를 쓰는 공개 저장소에 `pull_request` 트리거를 두면, 누구나 포크해 PR 로 러너 PC 에서 코드를 실행할 수 있습니다. 넣지 않습니다.

## 5. API 서버 (`CDEP\relay.py`)

| 하는 일 | 쓰는 쪽 |
|---|---|
| 서버 목록 · 스크린샷 (`/launcher/catalog`, `/launcher/media`) | 마스터의 런처 (`/launcher/notices` 는 남아 있지만 런처는 쓰지 않음: 공지는 GitHub) |
| 관리 API (`/admin/api/…`, 관리자 비밀번호) | 관리자 메뉴 |
| 앱 사용 기록 (`/launcher/usage`) | 마스터 계정의 앱 (AppKit `AppUsage`), ImPerformance 가 읽음 |
| 문제 보고 · 스킬 · 공지 (`/api/…`) | Terminal Notes |

- **일반 사용자의 런처 · 앱은 이 서버에 아무것도 보내지 않습니다.**
- GitHub 중계(`/api/v3/…`)는 예전에 빌드한 앱을 위해 남아 있을 뿐, 지금 런처 · 앱은 쓰지 않습니다.
- EQ Status 의 설비 상태 API 는 이 서버가 아니라 따로 둔 설비 API 서버입니다.
- 배포 방법: [`CDEP\README.md`](../CDEP/README.md).

## 6. 설정 키

**런처 빌드 설정** (`i-luncher.env`, 프로필 폴더 `APPS_ENV_DIR` 또는 러너 환경 변수 `ILUNCHER_ENV_PATH`)

| 키 | 값 |
|---|---|
| `API_SERVER` | API 서버 주소 (마스터만 씀) |
| `MASTER_USERS` | 마스터 Windows 계정 (쉼표). 비우면 모두 일반 |
| `LAUNCHER_REPOSITORY` | 런처 저장소 `https://<호스트>/<소유자>/i-luncher` (기본 목록의 호스트 · 소유자도 여기서) |
| `GITHUB_HOST` | GitHub Enterprise 호스트 (짧게 쓴 `소유자/저장소` 에 붙음) |
| `NOTICES_REPOSITORY` · `NOTICES_PATH` | 공지 저장소(비우면 `i-luncher-notices`) · 파일(비우면 `공지.txt`) |
| `LAUNCHER_TOKEN` | (선택) 모두가 쓸 읽기 전용 토큰. 공개 저장소면 비움 |

**앱 빌드 설정** (`<앱>.env`, 같은 프로필 폴더 또는 러너 환경 변수 `RELEASE_ENV_PATH`)

| 키 | 값 |
|---|---|
| `UPDATE_GITHUB_API` | `https://<Enterprise 호스트>/api/v3` (github.com 이면 `https://api.github.com`). **API 서버 주소를 넣지 않습니다** |
| `UPDATE_REPOSITORY` | `<소유자>/<앱>` |
| `UPDATE_TOKEN` | 보통 비움 (공개 저장소, 또는 사용자 설정 토큰) |
| `UPDATE_INSTALL_NAME` | 설치 폴더 · "앱" 목록 이름 (`basic-apps.json` 의 `installName` 과 같게) |

**사용자 PC** (`%LOCALAPPDATA%\i-luncher`, 런처 업데이트 · 제거에도 남음)

| 파일 | 내용 |
|---|---|
| `settings.json` | 자동 업데이트, GitHub 토큰(암호화), `GeneralView`(마스터의 일반 화면) |
| `my-apps.json` | 내 앱 |
| `removed-apps.json` | 목록에서 삭제한 기본 · 서버 목록 앱 |
| `favorites.json` | 즐겨찾기 · 내 PC 프로그램 · 웹사이트 |

## 7. 처음 구성할 때 · 옮길 때

1. 런처와 기본 목록 앱 저장소가 **공개**인지 확인합니다 (4장 확인 항목 포함). 공지 저장소 `i-luncher-notices`(공개, `공지.txt`)를 만듭니다.
2. 저장소마다 self-hosted 러너를 등록하고, 앱 빌드 설정 `UPDATE_GITHUB_API` 를 GitHub(Enterprise) API 로 둡니다.
3. 런처 빌드 설정에 `API_SERVER` · `GITHUB_HOST` · `LAUNCHER_REPOSITORY` · `MASTER_USERS` 를 넣습니다.
4. API 서버를 배포하고 서버 목록을 넣습니다 (`CDEP\README.md`, `import_app_list.py`).
5. 런처와 앱을 한 번씩 릴리스합니다. **러너가 같은 PC 를 쓰면 하나씩** (빌드가 겹치면 메모리가 모자랄 수 있음). 이미 설치된 앱은 그 릴리스를 받은 뒤부터 새 업데이트 주소를 씁니다.
6. 확인:
   - 일반 계정 PC: 스토어에 기본 목록만, 관리자 메뉴 없음, `공지.txt` 의 공지가 보임, 버전이 보이고 받기 · 업데이트가 됨.
   - 마스터 PC: 제목 표시줄의 일반 | 마스터 전환, 서버 목록 · 공지 · 관리자 메뉴.
   - 기본 앱을 목록에서 삭제 → 런처를 다시 켜도 안 보임 → + → 삭제한 앱 다시 추가로 돌아옴.

## 8. 예전 방식에서 바뀐 점

- 부서용 설치 파일(`i-luncher-Team-Setup.exe`)과 그룹 파일(`store-group.txt`)은 없앴습니다. 새 설치 파일이 남은 `store-group.txt` 를 지웁니다.
- 런처 · 앱의 업데이트가 API 서버의 GitHub 중계를 거치지 않습니다. 예전 빌드의 앱은 중계로 새 버전을 한 번 받은 뒤부터 GitHub 에 바로 묻습니다.
- "부서 목록"은 **서버 목록**으로 부릅니다 (마스터만 봄).
- 런처 공지는 API 서버(관리자 메뉴에서 저장)가 아니라 GitHub 공지 저장소의 `공지.txt` 입니다. 모두에게 보입니다.
- 앱 사용 기록은 마스터 계정만 보냅니다.

## 9. 하지 말 것

- 일반 사용자 경로(일반 화면의 런처, 앱 업데이트)에 API 서버 호출을 다시 넣지 않습니다.
- 부서 역할을 만들지 않습니다 (사용자가 나중에 정함).
- 공개 저장소의 workflow 에 `pull_request` 트리거를 넣지 않습니다.
- 기본 목록 앱의 저장소를 비공개로 두지 않습니다.
- 사용자가 지운 기본 앱을 런처 업데이트로 다시 보이게 하지 않습니다.
