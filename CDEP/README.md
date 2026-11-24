# CDEP — API 서버 (relay.py)

이 폴더에 API 서버를 둡니다 (폴더 이름만 예전 그대로). API 를 더할 때도 이 폴더의 `relay.py` 에 더합니다.

**API 서버는 사내 서버 PC 한 대에서 돌립니다. C-DEP 에는 올리지 않습니다** (사용자 결정 2026-11-24: 일반 사용자는 API 를 쓰지 않고, 쓰는 것은 마스터뿐).

## API 서버

i-luncher와 앱들이 쓰는 API를 `relay.py` 하나로 제공합니다 (Python 3.8 이상, 표준 라이브러리만, 설치할 패키지 없음).

- i-luncher **마스터 계정**: 서버 목록 · 스크린샷 · 공지 (`/launcher/…`), 앱 사용 기록 (`/launcher/usage`). 일반 사용자의 런처 · 앱은 이 서버를 쓰지 않습니다
- GitHub 중계 (`/api/v3/…`): 예전에 빌드한 앱의 업데이트용으로만 남김. 지금 런처 · 앱은 버전 확인 · 설치 · 업데이트를 GitHub 에 바로 묻습니다
- Terminal Notes: 문제 보고 · 스킬 스토어 · 공지 (`/api/…`)
- 관리 화면 API (`/admin/api/…`, 관리자 비밀번호): i-luncher **관리자 메뉴**가 씀
- **EQ Status 설비 상태 API는 여기서 하지 않습니다.** EQ Status는 다른 서버의 데이터를 읽는 별도 설비 API 서버(`i-Projects\EQStatus\ops\server`, 포트 **2004**)를 씁니다. 이 서버의 `EQUIPMENT_DB_*` 는 비워 둡니다 (비우면 설비 상태 포트 `EQUIPMENT_PORT` 도 열지 않음). 포트는 어디서나 API 서버 2003 · 설비 상태 2004 입니다.

**`relay.py` 는 여기 하나뿐입니다.** 런처의 관리자 메뉴가 서버 PC 에 설치할 때 이 파일을 씁니다 (`ApiManager.csproj` 가 `..\CDEP\relay.py` 를 설치 폴더의 `server\relay.py` 로 넣음).

## 서버 PC 에서 실행

1. 서버 PC 에서 런처(마스터 계정)의 **관리자 메뉴**를 엽니다. 처음 열면 그 PC 에 서버를 설치하고 켭니다 (`%LOCALAPPDATA%\TerminalNotesRelay\relay.py`, 로그인하면 다시 시작).
2. 설정은 그 `relay.py` 맨 위 SETTINGS 입니다 (관리자 메뉴 → 서버 → ⋯ → 설정 파일 열기): `RELAY_ADMIN_PASSWORD`, GitHub Enterprise 면 `RELAY_GITHUB_API=https://<Enterprise 호스트>/api/v3` · `RELAY_GITHUB_TOKEN`(읽기 전용). `EQUIPMENT_DB_*` 는 비워 둡니다. 고친 뒤 [다시 시작].
3. 방화벽에서 포트 2003 을 엽니다 (관리자 메뉴 → 서버에 "규칙 없음"이 보이면. 자세한 것은 [`ApiManager\server\README.md`](../ApiManager/server/README.md)).
4. 데이터(앱 목록 · 스크린샷 · 공지 · 스킬 · 보고서 · 백업)는 `C:\ProgramData\TerminalNotesRelayData` 에 따로 있습니다. 서버 PC 를 옮길 때는 이 폴더를 새 서버 PC 에 그대로 복사합니다.
5. 런처를 새로 릴리스하면 서버 PC 의 런처가 켜질 때 서버를 새 버전으로 바꿉니다 (SETTINGS 는 그대로 옮김).

서버 PC 를 정한 뒤 **쓰는 쪽의 주소**를 그 PC 로 바꿉니다 (코드는 고치지 않음):

| 쓰는 곳 | 설정 |
|---|---|
| 마스터 PC 의 i-luncher | 관리자 메뉴 → 서버 → **서버 주소**에 서버 PC 의 IP (IP 만 쓰면 포트 2003). 새로 설치하는 마스터의 기본값은 빌드 설정(프로필 `i-luncher.env`) `API_SERVER=http://<서버 PC>:2003` |
| 마스터 PC 의 관리자 메뉴 | 그 PC 프로필 `i-luncher.env` 에 `API_ADMIN_PASSWORD=<RELAY_ADMIN_PASSWORD 와 같은 값>` (서버 PC 자신은 SETTINGS 에서 읽음) |
| AppKit 앱 (DeskCal, FastCopy …) | 바꾸지 않음 (사용 기록은 런처의 주소를 따라가고, 업데이트는 GitHub) |
| Terminal Notes | 그 앱 `.env` 의 중계 서버 주소 · 키 (`.env.sample` 참고) |
| EQ Status | 그대로 (설비 API 서버 주소 `EQSTATUS_API_BASE_URL`) |

확인: `Invoke-WebRequest -UseBasicParsing http://<서버 PC>:2003/health`, 그리고 마스터 계정의 런처 스토어에 서버 목록이 보이는지.

## 컨테이너 (지금은 쓰지 않음)

[`Dockerfile`](Dockerfile) · [`env.sample`](env.sample) 은 나중에 컨테이너로 돌릴 때를 위해 남겨 둡니다 (python:3.12-slim, 포트 2003, 데이터 `/data`, 상태 `GET /health`). 그때는 `relay.py` 를 고치지 않고 SETTINGS 와 같은 이름의 환경 변수로 설정하고 (순서: SETTINGS < 환경 변수 < `--set`), `/data` 를 영구 저장소에 연결합니다.

## CI

[`../.github/workflows/cdep.yml`](../.github/workflows/cdep.yml): `CDEP\` 가 바뀐 채로 main 에 push 하면 `python -m unittest test_relay` 와 (docker 가 있으면) 이미지 빌드를 확인합니다. `relay.py` 가 바뀌면 런처 릴리스(`release.yml`)도 함께 돕니다 (런처가 이 파일을 품음).

## 이 PC에서 시험 (이 폴더에서)

```powershell
python -m unittest test_relay          # 시험 (실제 서버를 임시 포트 · 임시 폴더로 띄움)
python relay.py --set RELAY_PORT=5081 --set RELAY_DATA_ROOT=D:\tmp\relay   # 한 번만 다른 설정으로
```

## 서버 목록 넣기 (설명 · 사진까지 그대로)

서버 목록(관리자 메뉴 → 앱 목록, 마스터 계정의 런처에만 보임)은 서버 데이터라 보내기로 가지 않습니다. 새 서버 PC 에 처음 넣을 때나 빠진 앱이 있을 때 [`seed/apps.json`](seed/apps.json)(설명 · 상세 설명 · 개발자 · 스크린샷 `seed/media`)을 그대로 넣습니다. **설명이나 사진을 새로 지어내지 않습니다.**

```powershell
$env:API_ADMIN_PASSWORD = '<RELAY_ADMIN_PASSWORD 와 같은 값>'
python import_app_list.py --server http://<서버 PC>:2003 --repository-base https://<Enterprise 호스트>/<소유자>           # 미리 보기
python import_app_list.py --server http://<서버 PC>:2003 --repository-base https://<Enterprise 호스트>/<소유자> --apply   # 저장
```

- 서버에 없는 앱은 모든 값으로 더하고, 이미 있는 앱은 **비어 있는 칸만** 채웁니다 (서버에 있던 값은 그대로).
- 기본 목록(모든 런처에 보이는 앱)은 여기가 아니라 GitHub 공지 저장소(`i-luncher-notices`)의 `basic-apps.json` · `basic-media/` 입니다 (예비 목록은 `i-luncher\basic-apps.json`).
- 앱을 서버 목록에 새로 더하면 `seed/apps.json` 과 `seed/media/<id>-1~3.png` 에도 같이 넣어 둡니다 (사진은 최소 3장).
