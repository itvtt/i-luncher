# i-api (API 서버)

`i-api.exe` 하나가 i-luncher 와 앱들이 쓰는 API 를 맡습니다 (C#, .NET 10 실행 환경만 있으면 됨. 2.23 까지는 Python `relay.py`).

- **i-luncher (마스터)**: 서버 목록 · 스크린샷, 관리자 메뉴 (API · 앱 목록 · 릴리스 · 러너 · 공지 · 문제 보고 · 스킬)
- **앱 사용 기록**: 앱이 보내는 사용 기록 (마스터 계정만), 재무성과(ImPerformance)가 읽음
- **Terminal Notes**: 문제 보고 · 스킬 · 플러그인 · 공지
- **GitHub 중계** (예전 빌드용), **EQ Status 설비 상태** (설정했을 때만)

## 서버 PC에서

보통은 서버 PC 에서 i-luncher(마스터 계정)의 **관리자 메뉴**를 열면 이 폴더의 파일이 `%LOCALAPPDATA%\i-api` 에 들어가고, 로그인 자동 실행(시작 폴더의 `i-api.vbs`)이 등록되고, 서버가 켜집니다. 런처가 새 버전이 되면 시작할 때 서버도 바꿉니다 (예전 파일은 `backup\i-api-<버전>\`, 설정은 그대로).

예전 Python 서버(`%LOCALAPPDATA%\i-api\relay.py` 또는 `%LOCALAPPDATA%\TerminalNotesRelay\relay.py`)는 그때 **한 번 저절로 옮깁니다**: 그 서버를 멈추고, `relay.py` 맨 위 SETTINGS 를 `settings.txt` 로, 로그인 자동 실행을 `i-api.exe` 로, 러너 로그온 작업(`GitHub Runners`)도 새 서버로. `relay.py` 는 `backup\relay-<버전>.py` 로 남습니다.

손으로 둘 때: 이 폴더(런처 설치 폴더의 `server\`)를 서버 PC 에 복사하고 **`서버 시작.cmd`** (자동 실행 등록 · 방화벽 열기(Windows 확인 창 한 번) · 시작 · 응답 확인, 실행 중이면 다시 시작). 끄려면 **`서버 중지.cmd`** (멈추고 자동 실행 해제).

## 설정 (`settings.txt`, i-api.exe 옆)

처음 시작할 때 기본값과 설명으로 만들어지고, 새 버전이 더한 설정은 시작할 때 덧붙습니다 (고친 값은 그대로). 줄마다 `이름=값`, `#` 은 설명. 고친 뒤 관리자 메뉴 → 서버 → [다시 시작]. 관리자 메뉴 → 서버 → ⋯ → **설정 파일 열기**. 순서: 이 파일 < 같은 이름의 환경 변수 < `--set 이름=값`.

| 항목 | 뜻 |
| --- | --- |
| `RELAY_HOST` · `RELAY_PORT` | 받을 IP와 포트(기본 2003). IP 를 비우면 이 PC 의 모든 IP. 쓰면 이 PC 의 IP 여야 함 |
| `RELAY_BIND` | (선택) 받을 IP 를 따로 (`0.0.0.0` = 모든 IP) |
| `RELAY_ADMIN_PASSWORD` | 관리자 메뉴 · 관리 페이지 비밀번호. 비우면 누구나 고침. 10분에 10번 틀리면 10분 잠김 |
| `RELAY_INGEST_KEY` | (선택) Terminal Notes 앱 키 (앱 `.env` 와 같게) |
| `MASTER_USERS` | (선택) 마스터 계정: 공지 저장, 모든 스킬 수정 · 삭제, 플러그인 올리기 |
| `RELAY_DATA_ROOT` | 데이터 폴더 (기본 `C:\ProgramData\i-api-data`). 아래 폴더들은 이곳 기준 |
| `RELAY_DATA_DIR` · `RELAY_SKILLS_DIR` · `RELAY_PLUGINS_DIR` · `RELAY_ANNOUNCEMENTS_DIR` · `LAUNCHER_DIR` | 보고서 · 스킬 · 플러그인 · 공지 · 앱 목록 폴더 |
| `RELAY_BACKUP_DIR` · `RELAY_BACKUP_KEEP` | 자동 백업 폴더 · 보관 수 (기본 30) |
| `RELAY_REPORT_RETENTION_DAYS` · `RELAY_REPORTS_MAX_MB` | 보고서 보관 일수 · 최대 용량 ("확인 중" 보고서는 남김) |
| `RELAY_UPLOADS_PER_HOUR` | 주소당 시간당 보고 · 스킬 공유 수 |
| `RELAY_GITHUB_TOKEN` · `RELAY_GITHUB_API` | GitHub 중계 · 스킬 링크 · 릴리스 · 러너에 쓰는 토큰 (비우면 이 PC 의 git 자격 증명), GitHub Enterprise 면 `https://<호스트>/api/v3` |
| `LAUNCHER_GITHUB_REPOS` | GitHub 중계 · 릴리스 화면에 더할 저장소 (소유자/저장소, 소유자/*) |
| `EQUIPMENT_DB_*` · `EQUIPMENT_PORT` | EQ Status 설비 DB (MySQL). 비우면 설비 상태를 열지 않음. 포트 2004 |
| `RUNNERS_ROOT` · `RUNNERS_ENV_DIR` · `RUNNER_PACKAGE` | 이 PC 의 GitHub Actions 러너 (관리자 메뉴 → 러너) |
| `RELAY_WEBHOOK_URL` · `RELAY_PUBLIC_URL` · `RELAY_TLS_CERT` · `RELAY_TLS_KEY` | 알림 웹훅, 알림 링크 주소, HTTPS (PEM) |

## 데이터

보고서 · 스킬 · 공지 · 앱 목록 · 사용 기록 · API 기록 · 로그(`relay.log`) · 백업은 이 폴더가 아니라 **데이터 폴더**(`C:\ProgramData\i-api-data`)에 있어, 서버 폴더를 바꾸거나 지워도 남습니다. 서버 PC 를 옮길 때는 이 폴더를 그대로 복사합니다. 예전 위치(`C:\ProgramData\TerminalNotesRelayData` 등)의 데이터는 처음 시작할 때 옮기거나 복사해 옵니다 (원본은 지우지 않고 `DATA-MOVED.txt` 를 남김).

**자동 백업**: 스킬 · 플러그인 · 공지 · 앱 목록 · 사용 기록을 시작할 때와 하루에 한 번, 바뀐 것이 있을 때만 `backups\store-날짜-시각.zip`. 되살리기: 서버를 멈춘 뒤 `i-api.exe --restore-backup [ZIP]` (비어 있거나 없는 폴더만).

## API 관리 (관리자 메뉴 → API)

서버의 API 하나하나를 봅니다: 켜짐 · 오늘 · 30일 호출 수, 실패(400 이상), 평균 시간, 날짜별 막대, 마지막 실패. 스위치로 끈 API 는 503 으로 거절합니다 (서버를 다시 켜도 그대로, 데이터 폴더의 `api\`). 관리자 메뉴가 쓰는 API 와 상태 확인은 끄지 못합니다. 새 API 는 `CDEP\Server\Functions.cs` 에 한 줄로 등록합니다.

## 명령

```powershell
i-api.exe                         # 설정대로 실행 (창 없이)
i-api.exe --check                 # 설정 · 방화벽만 확인
i-api.exe --start | --stop        # 서버 시작.cmd · 서버 중지.cmd 와 같음
i-api.exe --restart | --halt      # 다시 시작 / 멈추기만 (관리자 메뉴가 씀)
i-api.exe --allow-firewall        # 다른 PC 가 접속하도록 포트를 엶 (규칙 i-api-2003, 예전 TerminalNotesRelay-2003 도 인정)
i-api.exe --settings-from <예전 relay.py>   # 예전 Python 서버의 SETTINGS 를 settings.txt 로
i-api.exe --backup | --restore-backup [ZIP]
i-api.exe --set RELAY_PORT=5081   # 이번 실행만 설정을 바꿈
```

다른 PC 가 접속하지 못하면 방화벽 규칙(위)을 확인합니다. 관리자 메뉴 → 서버의 "규칙 없음", `--check`, 로그에도 보입니다.

## 보안 메모

HTTP 로 받으면 관리자 비밀번호와 보고서가 암호화되지 않습니다 (밖에 열면 `RELAY_TLS_*` 또는 HTTPS 프록시). 관리 페이지는 다른 사이트의 요청(Origin 불일치)을 거부하고, 관리 API 의 바꾸는 요청은 JSON 만 받습니다. 한 번에 받는 크기는 25 MB 까지.
