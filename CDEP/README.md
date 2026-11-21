# CDEP — 회사 배포 환경(C-DEP)에서 돌리는 것

이 폴더에 C-DEP 에서 돌릴 것을 모두 둡니다. 지금은 **API 서버**(`relay.py`) 하나입니다. API 를 더할 때도 이 폴더의 `relay.py` 에 더합니다.

## API 서버

i-luncher와 앱들이 쓰는 API를 `relay.py` 하나로 제공합니다 (Python 3.8 이상, 표준 라이브러리만, 설치할 패키지 없음).

- i-luncher **마스터 계정**: 서버 목록 · 스크린샷 · 공지 (`/launcher/…`), 앱 사용 기록 (`/launcher/usage`). 일반 사용자의 런처 · 앱은 이 서버를 쓰지 않습니다
- GitHub 중계 (`/api/v3/…`): 예전에 빌드한 앱의 업데이트용으로만 남김. 지금 런처 · 앱은 버전 확인 · 설치 · 업데이트를 GitHub 에 바로 묻습니다
- Terminal Notes: 문제 보고 · 스킬 스토어 · 공지 (`/api/…`)
- 관리 화면 API (`/admin/api/…`, 관리자 비밀번호): i-luncher **관리자 메뉴**가 씀
- **EQ Status 설비 상태 API는 여기서 하지 않습니다.** EQ Status는 다른 서버의 데이터를 읽는 별도 설비 API 서버(`i-Projects\EQStatus\ops\server`)를 씁니다. 이 서버의 `EQUIPMENT_DB_*` 는 비워 둡니다.

**`relay.py` 는 여기 하나뿐입니다.** 런처의 관리자 메뉴가 Windows 서버 PC 에 설치할 때도 이 파일을 씁니다 (`ApiManager.csproj` 가 `..\CDEP\relay.py` 를 설치 폴더의 `server\relay.py` 로 넣음). Windows 서버 PC 에 두는 방식은 [`ApiManager\server\README.md`](../ApiManager/server/README.md).

## C-DEP 에서 실행

컨테이너로 돌립니다: [`Dockerfile`](Dockerfile) (이미지 빌드 위치 = 이 `CDEP` 폴더) (python:3.12-slim, `python relay.py`, 포트 2003, 데이터 `/data`).

1. **설정은 환경 변수로** — `relay.py` 맨 위 SETTINGS와 같은 이름의 환경 변수가 그 값을 대신합니다 (순서: SETTINGS < 환경 변수 < `--set`). 넣을 값과 설명은 [`env.sample`](env.sample).
   - 비밀 값으로: `RELAY_ADMIN_PASSWORD`, `RELAY_GITHUB_TOKEN`(읽기 전용), `RELAY_INGEST_KEY`
   - GitHub Enterprise: `RELAY_GITHUB_API=https://<Enterprise 호스트>/api/v3`
2. **`/data` 를 영구 저장소에 연결** — 앱 목록(`launcher\catalog.json`) · 스크린샷(`launcher\media`) · 공지 · 스킬 · 보고서 · 백업이 여기 있습니다. 연결하지 않으면 다시 배포할 때 사라집니다. 서버를 옮길 때는 예전 서버의 데이터 폴더(Windows 서버 PC는 `C:\ProgramData\TerminalNotesRelayData`)를 그대로 복사해 넣습니다.
3. **상태 확인**: `GET /health` (Dockerfile의 HEALTHCHECK 도 이것을 부름).
4. **HTTPS** 는 보통 배포 환경의 게이트웨이가 합니다. 서버가 직접 할 때만 `RELAY_TLS_CERT` · `RELAY_TLS_KEY`.
5. 컨테이너에서는 Windows 전용 기능(러너 관리 · 로그온 자동 실행 · 방화벽)을 쓰지 않습니다. 러너는 GitHub Enterprise 쪽에 따로 둡니다.

배포한 뒤 **쓰는 쪽의 주소**를 이 서버 주소로 바꿉니다 (코드는 고치지 않음):

| 쓰는 곳 | 설정 |
|---|---|
| i-luncher | 빌드 설정(프로필 `i-luncher.env`) `API_SERVER=https://<배포 주소>` |
| AppKit 앱 (DeskCal, FastCopy …) | 바꾸지 않음 (업데이트는 GitHub: `UPDATE_GITHUB_API=https://<Enterprise 호스트>/api/v3`) |
| Terminal Notes | 그 앱 `.env` 의 중계 서버 주소 · 키 (`.env.sample` 참고) |
| i-luncher 관리자 메뉴 | 관리자 PC 의 프로필 `i-luncher.env` 에 `API_ADMIN_PASSWORD=<RELAY_ADMIN_PASSWORD 와 같은 값>` (이 PC 에 relay.py 가 없을 때 이 값을 씀) |
| EQ Status | 그대로 (설비 API 서버 주소 `EQSTATUS_API_BASE_URL`) |

## CI / CD

[`../.github/workflows/cdep.yml`](../.github/workflows/cdep.yml): `CDEP\` 가 바뀐 채로 main 에 push 하면 `python -m unittest test_relay` 와 (docker 가 있으면) 이미지 빌드를 확인합니다. C-DEP 배포(CD) 단계는 회사의 C-DEP 방식에 맞게 그 workflow 에 더하거나 C-DEP 쪽에서 이 폴더를 빌드하게 합니다. `relay.py` 가 바뀌면 런처 릴리스(`release.yml`)도 함께 돕니다 (런처가 이 파일을 품음).

## 이 PC에서 시험 (이 폴더에서)

```powershell
python -m unittest test_relay          # 시험 (실제 서버를 임시 포트 · 임시 폴더로 띄움)
python relay.py --set RELAY_PORT=5081 --set RELAY_DATA_ROOT=D:\tmp\relay   # 한 번만 다른 설정으로
```

## 서버 목록 넣기 (설명 · 사진까지 그대로)

서버 목록(관리자 메뉴 → 앱 목록, 마스터 계정의 런처에만 보임)은 서버 데이터라 보내기로 가지 않습니다. 새 서버(C-DEP)에 처음 넣을 때나 빠진 앱이 있을 때 [`seed/apps.json`](seed/apps.json)(설명 · 상세 설명 · 개발자 · 스크린샷 `seed/media`)을 그대로 넣습니다. **설명이나 사진을 새로 지어내지 않습니다.**

```powershell
$env:API_ADMIN_PASSWORD = '<RELAY_ADMIN_PASSWORD 와 같은 값>'
python import_app_list.py --server https://<API 서버 주소> --repository-base https://<Enterprise 호스트>/<소유자>           # 미리 보기
python import_app_list.py --server https://<API 서버 주소> --repository-base https://<Enterprise 호스트>/<소유자> --apply   # 저장
```

- 서버에 없는 앱은 모든 값으로 더하고, 이미 있는 앱은 **비어 있는 칸만** 채웁니다 (서버에 있던 값은 그대로).
- 기본 목록(모든 런처에 보이는 앱)은 여기가 아니라 런처 안의 `i-luncher\basic-apps.json` · `basic-media\` 입니다.
- 앱을 서버 목록에 새로 더하면 `seed/apps.json` 과 `seed/media/<id>-1~3.png` 에도 같이 넣어 둡니다 (사진은 최소 3장).
