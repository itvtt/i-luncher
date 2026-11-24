# i-api (API 서버)

`relay.py` 하나가 i-luncher 와 앱들이 쓰는 API 를 한 서비스로 맡습니다 (예전 이름 Terminal Notes 중계 서버). Python 3.8 이상만 있으면 되고 설치할 패키지는 없습니다.

- **i-luncher (마스터)**: 서버 목록 · 스크린샷, 관리자 메뉴 (앱 목록 · 릴리스 · 러너 · 공지 · 문제 보고 · 스킬)
- **앱 사용 기록**: 모든 앱이 보내는 사용 기록 (마스터 계정만), 재무성과(ImPerformance)가 읽음
- **Terminal Notes**: 문제 보내기 · 스킬 스토어 · 공지

## 서버 PC에서 실행

보통은 서버 PC 에서 i-luncher 의 **관리자 메뉴**를 열면 이 폴더의 파일이 `%LOCALAPPDATA%\i-api` 에 들어가고 서버가 켜집니다. 아래는 런처 없이 손으로 둘 때입니다.

1. 이 폴더(런처 설치 폴더의 `server\`: `relay.py`, `서버 시작.cmd`, `서버 중지.cmd`, 이 설명)를 서버 PC 에 복사합니다 (`%LOCALAPPDATA%\i-api` 를 권장). 서버는 `.env`를 쓰지 않습니다: 설정은 `relay.py` 맨 위 **SETTINGS**에서 고칩니다(아무것도 안 고치면 이 PC의 모든 IP, 포트 2003, 키·비밀번호 없이 받습니다). 서버 PC에는 Python 3.8 이상만 있으면 됩니다.
2. **`서버 시작.cmd`**를 실행합니다. 로그인 때 창 없이 자동 실행되게 등록하고(시작 폴더의 `i-api.vbs`), 방화벽에 포트를 열고(Windows 확인 창에서 "예" 한 번), 지금 바로 시작한 뒤 응답을 확인해 주소를 보여 줍니다. 이미 실행 중이면 다시 시작합니다. 새 `relay.py`로 바꿀 때는 먼저 `python 새relay.py --settings-from 예전relay.py`로 예전 설정을 옮긴 뒤 바꿔 넣고 이것을 실행합니다.
3. 끄려면 **`서버 중지.cmd`**: 이 폴더의 서버를 멈추고 자동 실행을 해제합니다(다른 폴더의 서버 자동 실행은 그대로).

보고서·스킬·공지·앱 목록·사용 기록·로그·백업은 이 폴더가 아니라 **데이터 폴더**(`C:\ProgramData\i-api-data`, 이 PC의 모든 계정 공용, 설정 `RELAY_DATA_ROOT`로 변경)에 쌓입니다. 데이터 폴더는 이 폴더(`i-api`)와 이름도 위치도 달라서, 이 폴더를 새로 복사하거나 지우고 다시 만들어도, 다른 위치로 옮겨도, 서버를 다른 Windows 계정이나 다른 Python(Microsoft Store Python은 `%LOCALAPPDATA%`를 버전마다 따로 둡니다)으로 돌려도 스토어는 그대로입니다. 2.12–2.22 의 데이터 폴더 `C:\ProgramData\TerminalNotesRelayData` 는 2.23 이 처음 시작할 때 이름만 바꿔 옮깁니다(`RELAY_DATA_ROOT` 를 따로 정했으면 그대로). 그보다 예전 위치에 데이터가 남아 있으면 시작할 때 데이터 폴더로 복사만 합니다: 2.10–2.11의 기본 위치 `%LOCALAPPDATA%\TerminalNotesRelay`, `--settings-from`으로 준 예전 relay.py의 폴더, 2.10 이전처럼 relay.py 옆. 데이터 폴더에 이미 내용이 있으면 건드리지 않고, 원본은 지우지 않으며 그 자리에 `DATA-MOVED.txt`(옮겨 간 곳)를 남깁니다. 데이터 폴더가 relay.py 폴더 안에 있으면 `--check`·로그·상태 페이지에 "주의"로 알립니다.

**자동 백업**: 스킬·플러그인·공지를 시작할 때와 하루에 한 번, 바뀐 것이 있을 때만 `backups\store-날짜-시각.zip`으로 남깁니다(`RELAY_BACKUP_KEEP`개 보관, 기본 30, 빈 스토어는 백업하지 않아 좋은 백업을 밀어내지 않음). 데이터 폴더째 지워질 때를 대비하려면 `RELAY_BACKUP_DIR`에 다른 드라이브의 절대 경로를 씁니다. 지금 바로 백업은 `python relay.py --backup`, 되살리기는 서버를 멈춘 뒤 `python relay.py --restore-backup`(가장 최근 백업) 또는 `--restore-backup <ZIP>`: 비어 있거나 없는 폴더만 되살리고 내용이 있는 폴더는 그대로 둡니다. **다른 서버 PC로 옮길 때**는 백업 ZIP 하나를 새 서버로 복사해 `--restore-backup <ZIP>`을 실행하면 됩니다(앱의 `RELAY_HOST`가 바뀌면 그 서버의 스토어를 봅니다). 같은 일을 명령으로 하려면 `python relay.py --start` / `--stop`.

## 실행

```powershell
python relay\relay.py                  # relay.py 맨 위 SETTINGS대로 실행
python relay\relay.py --set RELAY_PORT=5081   # 이번 실행만 설정을 바꿈(여러 번 쓸 수 있음, 자동 실행에도 그대로 들어감)
python relay\relay.py --settings-from D:\예전\relay.py   # 예전 relay.py의 SETTINGS를 이 파일로 옮김
python relay\relay.py --check          # 설정·방화벽만 확인(비밀값은 보이지 않음)
python relay\relay.py --allow-firewall # 다른 PC가 접속하도록 방화벽에 포트를 엶(관리자 확인 창 한 번)
python relay\relay.py --install-startup  # Windows 로그인 때 창 없이 자동 실행 + 방화벽 열기
python relay\relay.py --uninstall-startup
```

서버는 SETTINGS의 `RELAY_HOST`(이 PC의 IP)와 `RELAY_PORT`(기본 2003)로 받습니다. 앱은 앱 `.env`의 같은 `RELAY_HOST`로 접속합니다. `RELAY_HOST`가 이 PC의 IP가 아니면 시작하지 않고 이 PC의 IP를 `relay.log`와 `--check`에 알려 줍니다. `--port`, `--bind`, `--set`으로 이번 실행만 바꿀 수 있습니다. 로그는 `relay.log`(1 MB씩 5개 보관)에 남습니다. 앱이 1분마다 묻는 공지 확인은 로그에 남기지 않고, PC마다 처음 접속한 때만 `새 PC 접속`으로 남깁니다(접속한 PC는 상태 페이지에서 봅니다).

## 설정 (`relay.py` 맨 위 SETTINGS)

중계 서버는 `.env`도 환경 변수도 읽지 않습니다. `relay.py`를 메모장으로 열어 맨 위 `SETTINGS`(설정 시작 ~ 설정 끝)의 값만 고칩니다. 서버 PC에 복사한 `relay.py`에 실제 값(비밀번호 등)을 쓰고, 저장소의 `relay.py`에는 쓰지 않습니다. 이번 실행만 다르게 하려면 `--set 키=값`을 씁니다.

| 항목 | 뜻 |
| --- | --- |
| `RELAY_HOST` · `RELAY_PORT` | (선택) 받을 IP와 포트(기본 2003). IP를 비우면 이 PC의 모든 IP로 받습니다. 쓰면 이 PC의 IP여야 합니다 |
| `EQUIPMENT_PORT` | 설비 상태(`EQUIPMENT_DB_*` 를 채웠을 때)를 이 포트에서도 내려 줍니다 (기본 **2004**, 사내 설비 서버와 같은 포트, 이 포트는 설비 상태와 `/health` 만). `RELAY_PORT` 에서도 계속 받습니다. 0 = 끔. 다른 PC 가 받으려면 방화벽에 이 포트도 엽니다 (`--allow-firewall` 이 함께 엶) |
| `RELAY_BIND` | (선택) 받을 IP를 따로 정할 때. `0.0.0.0`이면 이 PC의 모든 IP |
| `RELAY_INGEST_KEY` | (선택) 앱 키. 비우면 IP만 맞으면 받고, 쓰면 앱 .env의 RELAY_INGEST_KEY와 같아야 합니다(다르면 "키가 맞지 않습니다") |
| `RELAY_ADMIN_PASSWORD` | (선택) 관리 페이지 비밀번호(사용자 이름은 아무거나). 비우면 비밀번호 없이 열립니다. 10분에 10번 틀리면 10분 잠깁니다 |
| `RELAY_DATA_ROOT` | 데이터 폴더(기본 `C:\ProgramData\i-api-data`, 2.12–2.22 는 `C:\ProgramData\TerminalNotesRelayData`, 2.11까지는 `%LOCALAPPDATA%\TerminalNotesRelay`). 아래 폴더의 상대 경로가 이곳 기준입니다. relay.py 폴더 안은 피합니다 |
| `RELAY_BACKUP_DIR` · `RELAY_BACKUP_KEEP` | 스킬·플러그인·공지 자동 백업 폴더(기본 `backups`, 다른 드라이브의 절대 경로 권장)와 보관 수(기본 30) |
| `RELAY_DATA_DIR` · `RELAY_SKILLS_DIR` · `RELAY_PLUGINS_DIR` · `RELAY_ANNOUNCEMENTS_DIR` | 보고서·스킬·플러그인·공지 저장 폴더(기본 `reports`·`skills`·`plugins`·`announcements`) |
| `RELAY_REPORT_RETENTION_DAYS` · `RELAY_REPORTS_MAX_MB` | 보고서 보관 일수(0이면 무제한)와 최대 용량. 넘으면 오래된 것부터 지우고 "확인 중" 보고서는 남깁니다 |
| `RELAY_UPLOADS_PER_HOUR` | 주소(PC)당 시간당 보고·스킬 공유 수(기본 60) |
| `RELAY_WEBHOOK_URL` · `RELAY_PUBLIC_URL` | 새 보고·스킬·공지 알림 웹훅(Slack·Teams·Mattermost·Discord)과 알림 링크 주소 |
| `RELAY_TLS_CERT` · `RELAY_TLS_KEY` | 둘 다 쓰면 HTTPS로 받습니다(PEM) |
| `MASTER_USERS` | (선택) 마스터 계정(앱 .env의 `MASTER_USERS`와 같은 값, 앱 기본 `test_01,test_07,test_08`): 공지 저장, 작성자와 상관없이 모든 스킬 수정(새 버전)·삭제. 작성자는 자기 스킬을 늘 수정·삭제합니다. 쓰면 서버도 다시 확인하고, 비우면 앱의 판단을 믿습니다 |
| `RELAY_GITHUB_TOKEN` · `RELAY_GITHUB_SYNC_MINUTES` | 깃허브 링크로 공유한 스킬을 읽을 토큰(선택, 비공개·내부 저장소용, 읽기 권한만)과 다시 확인하는 간격(기본 10분). GitHub Enterprise 링크는 `<주소>/api/v3`로 읽습니다 |
| `RUNNERS_ROOT` | (선택) 이 PC의 GitHub Actions 러너 폴더(러너마다 하위 폴더). 쓰면 i-luncher 관리자 메뉴의 **러너** 화면이 켜집니다 (아래 "러너") |
| `RUNNERS_ENV_DIR` · `RUNNER_PACKAGE` | 새 러너의 `.env`가 가리킬 빌드 설정 폴더(비우면 환경 변수 `APPS_ENV_DIR`, 없으면 `RUNNERS_ROOT\env`)와 새 러너 파일 `actions-runner-win-x64-*.zip`(비우면 다른 러너의 파일을 하드 링크로 씀) |

앱은 게시할 때 앱 `.env`(저장소 맨 위, 앱 설정은 이것 하나)의 `RELAY_HOST`·`RELAY_PORT`·`RELAY_INGEST_KEY`만 읽어 `report-relay.json`을 만듭니다. 관리자 비밀번호는 앱에 들어가지 않습니다. 서버를 옮기면 SETTINGS와 앱 `.env`의 `RELAY_HOST`를 바꾸고 앱을 다시 게시하세요.

## 관리 페이지

서버 PC에서는 **API 서버** 프로그램(WPF) 창에서 아래 일을 모두 합니다: 서버 상태·접속한 PC·설정, i-luncher 앱 목록과 런처 공지, Terminal Notes 공지, 문제 보고, 스킬. 비밀번호는 설정(`RELAY_ADMIN_PASSWORD`)에서 읽어 따로 묻지 않습니다. 이 창은 `/admin/api/*`(2.14부터, 관리자 비밀번호, 바꾸는 요청은 JSON만)와 `/launcher/*`를 씁니다.

다른 PC에서는 브라우저로 `http://서버주소:2003/`을 열고 관리자 비밀번호를 입력합니다(아이디는 아무거나).

### 앱 페이지 (스크린샷·상세 설명)

i-luncher 스토어 줄을 누르면 열리는 앱 페이지의 상세 설명과 스크린샷은 i-luncher 관리자 메뉴 → **앱 목록**에서 넣습니다 (앱 목록의 `details`, `screenshots`).
스크린샷은 [+]로 PNG·JPG를 고르거나 이미지를 복사해 앱 칸에서 Ctrl+V 하면 바로 서버에 올라가고(`POST /launcher/media`, 관리자 비밀번호, 8 MB까지, 이름 = 내용의 SHA-256 앞 16자리), [저장]하면 앱 페이지에 보입니다. 앞으로·빼기로 순서를 바꿉니다(12장까지). 런처는 `GET /launcher/media/<이름>`으로 받아 보관합니다. 어느 앱도 쓰지 않는 이미지는 앱 목록을 저장할 때 하루 지난 것부터 지웁니다.

### 러너

`RUNNERS_ROOT`를 쓰면 i-luncher 관리자 메뉴의 **러너** 화면에서 이 PC의 Actions 러너를 관리합니다 (바꾸는 요청은 서버 PC에서 온 것만 받음).

- **목록**: 러너마다 상태(실행 중·작업 중·멈춤·꺼짐)와 GitHub 상태(온라인·오프라인), 저장소, 폴더, `.env`.
- **추가(+)**: 저장소를 고르면 폴더·러너 이름·`.env`(그 저장소 `release.yml`이 읽는 `*_ENV_PATH` 변수 = `<설정 폴더>\<저장소>.env`)가 채워집니다. [등록]하면 서버가 등록 토큰을 받아 `config.cmd`로 등록하고 시작합니다 (토큰에 저장소 관리 권한 필요).
- **편집**: `.env`를 고쳐 저장하면 쉬고 있는 러너는 다시 시작해 적용합니다.
- **시작·멈추기·다시 시작**: 러너는 숨긴 콘솔에서 돌고, 멈추기는 Ctrl+Break로 GitHub 세션을 닫고 끝냅니다. 멈춘 러너는 꺼짐으로 남아 로그온 때도 시작하지 않습니다. 작업 중이면 빌드가 취소된다고 묻습니다.
- **삭제**: 멈추고 GitHub 등록을 지우고 폴더를 삭제합니다.
- **로그온 작업 하나**: 예약 작업 `GitHub Runners`(`relay.py --start-runners`)가 로그온 30초 뒤 켜 둔 러너를 모두 시작하고 끝납니다. 예전 러너별 작업(`GitHub runner <이름>`)이 있으면 위쪽 [정리]로 이 작업 하나로 바꿉니다 (예전 작업 XML은 `RUNNERS_ROOT\.backup\tasks`에 남고, 실행 중인 러너는 그대로).

- **문제 보고**: 상태(새 보고·확인 중·해결)와 메모를 남기고, 검색·삭제하며, 로그 ZIP을 받습니다.
- **스킬**: 스킬마다 버전 기록(올린 시각·작성자·바뀐 점·원문)을 보고, 예전 버전을 새 최신 버전으로 되돌리거나 스토어에서 지웁니다(30일 동안 `skills\.trash`에 남음).
- **공지**: 모든 사용자가 보는 공지를 고치고 예전 공지를 봅니다(50개 보관).
- **상태**: 서버를 켠 뒤 접속한 PC(주소·앱 버전·마지막 접속·최신 공지를 받았는지·쓴 기능, 키가 틀려 거절된 PC 포함), 방화벽 허용 여부, 보고·스킬 수, 보고서 용량, 설정 요약.

## 앱이 쓰는 API (앱의 키 `X-TerminalNotes-Key` 필요)

| 요청 | 뜻 |
| --- | --- |
| `POST /api/reports` | 보고서(`report`)와 로그 ZIP(`logs`) multipart |
| `GET /api/skills` | 최신 버전 목록(version·versionCount·downloads·note 포함) |
| `GET /api/skills/<이름>` | 최신 버전 내용 |
| `GET /api/skills/<이름>/versions` · `/versions/<n>` | 버전 기록 · 그 버전 내용(`?apply=1`이면 받은 수를 셉니다) |
| `POST /api/skills` | 공유 또는 새 버전(같은 이름은 처음 올린 Windows 계정만, 내용이 같으면 새 버전을 만들지 않음). `version`을 주면 그 번호로 저장(지금 최신보다 커야 함), 없으면 다음 번호. `origin`은 사용자가 적는 출처(유저 ID나 링크, 300자까지): 값이 바뀌어도 새 버전을 만들지 않고, 새 버전에 이어지며, `""`이면 지움(`source`는 따라가는 깃허브 파일로 별개) |
| `PUT /api/skills/<이름>/private` | 공개·비공개(`{"private": true}`). 작성자만, 새 버전 없음 |
| `POST /api/skills/withdrawn` | `names` 중 이 계정이 더 쓸 수 없는 스킬(다른 사람의 비공개)만 돌려줌. 앱이 적용한 사본을 지우는 근거. 삭제된 스킬은 들어가지 않음 |
| `GET /api/plugins` · `GET /api/plugins/<이름>` | 플러그인 목록(최신 버전) · 코드(`?install=1`이면 설치 수를 셉니다) |
| `POST /api/plugins/source` | 깃허브(Enterprise 포함) `.ts`·`.js` 파일 링크의 코드를 읽어 돌려줌(올리기 창 미리 보기) |
| `POST /api/plugins` | 플러그인 올리기·새 버전(`MASTER_USERS`가 있으면 마스터 계정만, 남의 것은 `edit`와 마스터만; 코드가 같으면 새 버전 없음). 코드는 `.ts`·`.js` 하나, 30만 자까지. `source`(깃허브 파일 링크)를 주면 그 파일을 따라가고(스킬과 같은 `RELAY_GITHUB_SYNC_MINUTES` 주기로 바뀌면 "깃허브 변경 반영" 새 버전), `""`이면 따라가기를 멈춤 |
| `DELETE /api/plugins/<이름>` | 작성자나 마스터 계정만(30일 `.trash`) |
| `GET /api/announcements` | 공지(ETag가 같으면 304). `X-TerminalNotes-Seen-By`: 지금 이 공지를 받은 PC 수 |
| `PUT /api/announcements` | 공지 저장(작성 계정만, 다른 곳에서 먼저 바뀌었으면 409) |

`GET /health`는 키 없이 `ok`를 돌려줍니다. 예전 .NET 중계 서버가 저장한 보고서·스킬 폴더를 그대로 읽으며, 버전이 없던 스킬은 v1이 됩니다.

## 다른 PC에서 쓰기

Windows 방화벽은 프로그램마다 받기를 허용합니다. `python.exe`를 허용했어도 로그인 자동 실행이 쓰는 `pythonw.exe`나 예전 .NET 중계 서버의 허용은 해당되지 않아, 방화벽을 열지 않으면 **서버 PC 자신만 접속되고 다른 PC는 막힙니다**(공지·스킬·문제 보내기가 다른 PC에서 안 됨). 서버 PC에서 한 번 실행하세요. 관리자 확인 창에서 "예"를 누르면 포트(기본 2003)를 모든 네트워크에서 엽니다(규칙 이름 `i-api-2003`, 2.22 까지 만든 `TerminalNotesRelay-2003` 도 그대로 인정).

```powershell
python relay\relay.py --allow-firewall
# 관리자 PowerShell에서 직접: netsh advfirewall firewall add rule name=i-api-2003 dir=in action=allow protocol=TCP localport=2003 profile=any
```

그룹 정책이 로컬 방화벽 규칙을 막으면 PC 관리자에게 TCP 2003 받기 허용을 요청하세요. 시작할 때 규칙이 없으면 `relay.log`에 경고가 남고, `--check`와 상태 페이지에도 보입니다.

### 공지가 다른 PC에 안 보일 때

1. 상태 페이지의 **접속한 PC**에 그 PC가 있는지 봅니다. 없으면 그 PC가 서버에 닿지 못합니다: 방화벽(위), 앱의 서버 주소(앱 폴더의 `report-relay.json`), 네트워크를 확인합니다. "키 틀림"이면 앱과 서버의 `RELAY_INGEST_KEY`가 다릅니다.
2. 앱 버전이 1.0.518 이상이어야 공유 공지를 읽습니다(그 전 앱은 자기 PC의 공지.md만 봅니다).
3. 공지 창 위쪽 한 줄이 상태를 알려 줍니다: 작성 계정에게 "지금 이 공지를 받은 PC n대", 연결 실패·이전 서버·중계 서버 없는 빌드는 ⚠와 이유.

## 시험

```powershell
cd relay; python -B -m unittest test_relay
```

## 보안 메모

- HTTP로 받을 때는 관리자 비밀번호와 보고서가 암호화되지 않습니다. 인터넷에 열면 `RELAY_TLS_CERT`·`RELAY_TLS_KEY`로 HTTPS를 쓰거나 HTTPS 리버스 프록시 뒤에 두세요.
- `RELAY_INGEST_KEY`는 앱 안에 들어 있어 완전한 비밀은 아닙니다. 마구 보내는 것을 막는 용도이며 주소당 시간당 건수를 제한합니다. 공지는 서버가 작성 계정을 다시 확인하지만 계정 이름도 앱이 보내는 값이므로, 공지가 엉뚱하게 바뀌면 관리 페이지에서 예전 공지를 열어 그 내용으로 다시 저장하세요.
- 관리 페이지는 다른 사이트에서 보낸 요청(Origin 불일치)을 거부하고, 페이지에 스크립트를 쓰지 않습니다(CSP). 한 번에 받는 크기는 25 MB까지입니다.
