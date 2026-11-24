# CDEP — API 서버 i-api (C#)

이 폴더에 API 서버를 둡니다 (폴더 이름만 예전 그대로). `i-api.csproj` 가 서버 프로그램 `i-api.exe` 이고, API 를 더할 때도 여기에 더합니다.
2.23 까지는 Python `relay.py` 였고, 3.0 부터 C# 입니다 (사용자 결정 2026-11-24: 서버 PC 에 Python 없이 돌게). 같은 데이터 폴더 · 같은 API 를 그대로 씁니다.

**API 서버는 사내 서버 PC 한 대에서 돌립니다. C-DEP 에는 올리지 않습니다** (사용자 결정 2026-11-24: 일반 사용자는 API 를 쓰지 않고, 쓰는 것은 마스터뿐).

## 무엇을 하나

- i-luncher **마스터 계정**: 서버 목록 · 스크린샷 · 공지 (`/launcher/…`), 앱 사용 기록 (`/launcher/usage`). 일반 사용자의 런처 · 앱은 이 서버를 쓰지 않습니다
- 관리 API (`/admin/api/…`, 관리자 비밀번호): i-luncher **관리자 메뉴**가 씀. **API 화면**에서 API 마다 켜고 끄고 호출 · 실패 · 걸린 시간을 봄 (`/admin/api/functions`)
- Terminal Notes: 문제 보고 · 스킬 · 플러그인 · 공지 (`/api/…`)
- GitHub 중계 (`/api/v3/…`): 예전에 빌드한 앱의 업데이트용으로만 남김
- **EQ Status 설비 상태 API는 여기서 하지 않습니다.** EQ Status는 따로 둔 설비 API 서버(`i-Projects\EQStatus\ops\server`, 포트 **2004**)를 씁니다. 이 서버의 `EQUIPMENT_DB_*` 는 비워 둡니다.

## 코드

| 폴더 | 내용 |
|---|---|
| `Program.cs` | 명령 줄 (`--start` · `--stop` · `--restart` · `--settings-from` …), 서버로 돌기 · 정리 · 깃허브 동기화 |
| `Core\` | 설정 파일(`Settings.cs`, settings.txt), 설정(`Config.cs`), 공통 도구 |
| `Http\` | 작은 HTTP 서버 (http.sys 를 쓰지 않아 관리자 권한 없이 다른 PC 의 주소로 받음), API 목록 · 켜고 끄기 · 호출 기록 (`Api.cs`) |
| `Server\Functions.cs` | **모든 API 의 목록** (id · 묶음 · 방법 · 주소 · 이름 · 설명 · 처리기). 새 API 는 여기에 한 줄 + `Handlers.cs` 에 처리기 |
| `Server\` | 요청 나누기, 처리기, 관리 페이지, 자동 실행 · 방화벽 · 멈추기, 데이터 폴더 옮기기 · 백업 |
| `Stores\` | 보고서 · 스킬 · 플러그인 · 공지 · 앱 목록 · 사용 기록 |
| `GitHub\` · `Runners\` · `Equipment\` | GitHub 중계 · 릴리스, 러너 관리, 설비 DB (MySQL) |
| `seed\` · `import_app_list.py` | 서버 목록의 원본과 넣는 도구 (아래) |

NuGet 패키지 없이 .NET 10 기본 실행 환경만 씁니다. 런처의 `ApiManager.csproj` 가 이 서버를 게시해 런처 설치 폴더의 `server\` 로 넣습니다.

## 새 API 더하기

1. **`Server\Functions.cs`** 의 목록에 한 줄: id(바꾸지 않음) · 구역 · 묶음 · 방법 · 주소 · 이름 · 설명 · 처리기.
   ```csharp
   Make("hello.read", "launcher", GroupLauncher, "GET", "/launcher/hello/{name}", "인사 읽기", "예시: 이름을 받아 인사를 돌려줌", HelloRead),
   ```
   - 구역(앞의 확인): `launcher` = `/launcher/…` (누구나, 고치기는 처리기에서 `RequireAdmin`), `app` = `/api/…` (Terminal Notes 앱 키), `admin` = `/admin/api/…` (관리자 비밀번호, POST · PUT 은 JSON 만)
   - 주소의 `{이름}` 은 조각 하나 (`request.Value("name")`), 끝의 `**` 는 나머지 모두. 방법을 여럿 받으면 `"GET|PUT"`
   - 끄면 안 되는 API 는 마지막에 `switchable: false`
2. **`Server\Handlers.cs`** 에 처리기:
   ```csharp
   private void HelloRead(Request request) => request.Json(200, new JsonObject { ["text"] = "안녕 " + request.Value("name") });
   ```
   거절은 `throw new RelayError(400, "이유")` (앱 · 관리자 메뉴가 그 글을 그대로 보여 줌). 본문은 `request.JsonObjectBody()`, 쿼리는 `request.QueryValue("from")`.
3. **`i-api.Checks\Program.cs`** 에 확인 하나 (`Check("…", () => { using var h = new Harness(); … })`) 를 더하고 `dotnet run --project i-api.Checks`.
4. **`i-api.csproj` 의 `<Version>` 을 올리고** 런처를 릴리스합니다. 서버 PC 의 런처가 켜질 때 서버를 새 버전으로 바꾸고, 새 API 는 관리자 메뉴 → **API** 에 저절로 보입니다 (켜고 끄기 · 호출 수).

## 서버 PC 에서 실행

1. 서버 PC 에서 **`i-api.exe` 를 실행하고 포트를 넣어 [실행]** 합니다 (포트는 기본값이 없어 꼭 정함). 그 PC 에서 서버가 켜지고, 로그인 자동 실행(`i-api.vbs`)으로 재부팅해도 다시 켜지며, 방화벽에 그 포트를 엽니다. 런처(마스터 계정)의 **관리자 메뉴**를 그 PC 에서 열어도 `%LOCALAPPDATA%\i-api` 에 설치하고 같은 창을 엽니다. 예전 Python 서버(`relay.py`)는 이때 저절로 옮겨집니다 (SETTINGS → `settings.txt` · 자동 실행 · 러너 로그온 작업).
2. 설정은 `%LOCALAPPDATA%\i-api\settings.txt` (관리자 메뉴 → 서버 → ⋯ → 설정 파일 열기): `RELAY_ADMIN_PASSWORD`, GitHub Enterprise 면 `RELAY_GITHUB_API=https://<Enterprise 호스트>/api/v3` · `RELAY_GITHUB_TOKEN`(읽기 전용). 고친 뒤 [다시 시작].
3. 방화벽은 [실행]할 때 엽니다 (관리자 메뉴 → 서버에 "규칙 없음"이 보이면 서버 PC 에서 `i-api.exe` 를 다시 실행하고 Windows 확인 창에서 "예". 자세한 것은 [`ApiManager\server\README.md`](../ApiManager/server/README.md)).
4. 데이터(앱 목록 · 스크린샷 · 사용 기록 · 공지 · 스킬 · 보고서 · API 기록 · 백업)는 `C:\ProgramData\i-api-data` 에 따로 있습니다. 서버 PC 를 옮길 때는 이 폴더를 새 서버 PC 에 그대로 복사합니다.
5. 런처를 새로 릴리스하면 서버 PC 의 런처가 켜질 때 서버를 새 버전으로 바꿉니다 (settings.txt 는 그대로).

서버 PC 를 정한 뒤 **쓰는 쪽의 주소**를 그 PC 로 바꿉니다 (코드는 고치지 않음):

| 쓰는 곳 | 설정 |
|---|---|
| 마스터 PC 의 i-luncher | 관리자 메뉴 → 서버 → **서버 주소**에 서버 PC 의 `IP:포트` (포트를 꼭 씀). 새로 설치하는 마스터의 기본값은 빌드 설정(프로필 `i-luncher.env`) `API_SERVER=http://<서버 PC>:2003` |
| 마스터 PC 의 관리자 메뉴 | 그 PC 프로필 `i-luncher.env` 에 `API_ADMIN_PASSWORD=<RELAY_ADMIN_PASSWORD 와 같은 값>` (서버 PC 자신은 settings.txt 에서 읽음) |
| AppKit 앱 (DeskCal, FastCopy …) | 바꾸지 않음 (사용 기록은 런처의 주소를 따라가고, 업데이트는 GitHub) |
| Terminal Notes | 그 앱 `.env` 의 중계 서버 주소 · 키 (`.env.sample` 참고) |
| EQ Status | 그대로 (설비 API 서버 주소 `EQSTATUS_API_BASE_URL`) |

확인: `Invoke-WebRequest -UseBasicParsing http://<서버 PC>:2003/health`, 그리고 마스터 계정의 런처 스토어에 서버 목록이 보이는지.

## 컨테이너 (지금은 쓰지 않음)

[`Dockerfile`](Dockerfile) · [`env.sample`](env.sample) 은 나중에 컨테이너로 돌릴 때를 위해 남겨 둡니다 (.NET 10, 포트는 RELAY_PORT 로 꼭 정함, 데이터 `/data`, 상태 `GET /health`). 그때는 설정 파일 대신 같은 이름의 환경 변수로 설정하고 (순서: settings.txt < 환경 변수 < `--set`), `/data` 를 영구 저장소에 연결합니다.

## 확인 · CI

```powershell
dotnet run --project i-api.Checks          # 저장소 맨 위에서: 서버를 임시 포트 · 임시 폴더로 띄워 확인 (이 PC 의 데이터는 건드리지 않음)
dotnet run --project CDEP\i-api.csproj -- --set RELAY_PORT=5081 --set RELAY_DATA_ROOT=D:\tmp\i-api   # 한 번만 다른 설정으로
```

[`../.github/workflows/cdep.yml`](../.github/workflows/cdep.yml): `CDEP\` · `i-api.Checks\` 가 바뀐 채로 main 에 push 하면 `i-api.Checks` 와 (docker 가 있으면) 이미지 빌드를 확인합니다. 런처 릴리스(`release.yml` → `build\Publish-Release.ps1`)도 `i-api.Checks` 가 통과해야 만들어집니다 (런처가 이 서버를 품음).

## 서버 목록 넣기 (설명 · 사진까지 그대로)

서버 목록(관리자 메뉴 → 앱 목록, 마스터 계정의 런처에만 보임)은 서버 데이터라 보내기로 가지 않습니다. 새 서버 PC 에 처음 넣을 때나 빠진 앱이 있을 때 [`seed/apps.json`](seed/apps.json)(설명 · 상세 설명 · 개발자 · 스크린샷 `seed/media`)을 그대로 넣습니다. **설명이나 사진을 새로 지어내지 않습니다.** (이 도구만 Python 3.8 이상을 씁니다. 서버 PC 가 아니어도 되고 어느 PC 에서나 서버 주소로 넣습니다.)

```powershell
$env:API_ADMIN_PASSWORD = '<RELAY_ADMIN_PASSWORD 와 같은 값>'
python import_app_list.py --server http://<서버 PC>:2003 --repository-base https://<Enterprise 호스트>/<소유자>           # 미리 보기
python import_app_list.py --server http://<서버 PC>:2003 --repository-base https://<Enterprise 호스트>/<소유자> --apply   # 저장
```

- 서버에 없는 앱은 모든 값으로 더하고, 이미 있는 앱은 **비어 있는 칸만** 채웁니다 (서버에 있던 값은 그대로).
- 기본 목록(모든 런처에 보이는 앱)은 여기가 아니라 GitHub 공지 저장소(`i-luncher-notices`)의 `basic-apps.json` · `basic-media/` 입니다 (예비 목록은 `i-luncher\basic-apps.json`).
- 앱을 서버 목록에 새로 더하면 `seed/apps.json` 과 `seed/media/<id>-1~3.png` 에도 같이 넣어 둡니다 (사진은 최소 3장).
