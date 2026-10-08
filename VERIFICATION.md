# 수신 폴더 교체 호환성 검증

2026-09-15, Windows 11 / .NET SDK 10.0.401에서 확인했습니다. 사내 PC의 파일·로그는 사용하지 않았습니다. Send와 Receive의 제품 소스 및 실행 파일은 수정하지 않았습니다.

## 재현 결과

현재 Receive DLL의 실제 `SwapPreparedDirectory`를 호출했습니다. 새 전송본에는 `dist/bin/obj`가 없고, 기존 수신 폴더에만 해당 폴더를 만든 조건입니다.

| 기존 수신 폴더의 상태 | 관찰 결과 |
| --- | --- |
| 일반 `dist/bin/obj` | 교체 및 이전 폴더 정리 성공 |
| 읽기 전용·숨김 속성 | 성공 |
| 413자 경로 | 성공 |
| `dist` 안에 배타적으로 열린 파일 | IOException, 기존 폴더 보존 |
| `bin`의 삭제 공유 없는 디렉터리 핸들 | IOException, 기존 폴더 보존 |
| `obj`가 현재 작업 디렉터리 | IOException, 기존 폴더 보존 |
| `bin`의 실행 중인 EXE | 교체 성공, 이전 백업 정리 경고 |
| 같은 파일 잠금이 수신 폴더 밖에 존재 | 교체 성공 |

복구 검사와 실제 Sender 암호화 / Receiver 복호화 검사까지 총 10개 조건을 검증했습니다. 원본에 빌드 폴더가 있을 때와 없을 때를 각각 포장한 결과, 복호화한 파일 경로와 SHA-256이 동일했습니다. 암호문은 난수를 사용하므로 암호문 자체의 일치 여부를 비교하지 않았습니다.

세부 조건과 실행 방법은 [재현 검사 보고서](Tools/ReceiveCompatibilityChecks/REPORT.md), 기계 판독 결과는 [latest-results.json](Tools/ReceiveCompatibilityChecks/latest-results.json)에 있습니다. 이는 사내 장애의 원인을 단정하는 결과가 아니라, 사용자 증상과 양립하는 실패 경로를 실제로 확인한 결과입니다.

## 변경 및 검증

- `Build.ps1`은 원본을 `%LOCALAPPDATA%\i-luncher\Workspaces\<경로별 식별자>`로 동기화한 후, 복사본에서 기존 빌드 절차를 실행합니다.
- `Start-Launcher.cmd` / `Start-Launcher.ps1`은 그 복사본의 런처를 실행하며 작업 디렉터리도 복사본으로 지정합니다.
- 소스 동기화에서 기존 외부 `dist/bin/obj`를 보존하므로 특정 앱만 빌드해도 다른 앱의 게시 결과가 사라지지 않습니다.
- `Tools/BuildWorkspaceChecks.ps1`: 깊이별 제외, 소스 갱신·삭제, 열린 외부 출력 보존, 원본 보존, 잘못된 작업 폴더·내부 플래그 거부를 확인했습니다.
- 전체 빌드: 런처 및 DeskCal, DiskAtlas, FolderCompareWPF, SwiftCopy 게시 성공.
- 런처만 다시 빌드: 성공. 컴파일러 출력도 호출자에게 전달됩니다.
- `Start-Launcher.ps1`로 실제 외부 런처 창을 열고 실행 파일 경로를 확인한 뒤 종료했습니다. 복사본 안에서 호출한 `-PrintWorkspace`도 같은 작업 폴더를 반환했습니다.
- 실행 시에도 빌드와 같은 잠금을 사용해 게시 중인 EXE의 실행을 피합니다. 드라이브 루트 경로 정규화 검사도 통과했습니다.
- `Launcher.Checks`: 11개 통과.
- `Launcher.UiChecks`: 36개 통과. 첫 병렬 실행에서는 포커스 검사에 실패했고, 다른 프로세스 실행 검사가 끝난 뒤 단독 재실행에서 통과했습니다.
- `Launcher.UiChecks --launch-apps`: 39개 통과. 외부 작업 복사본의 FolderCompareWPF와 SwiftCopy가 실제로 열리고, 런처 종료 후에도 독립적으로 실행됨을 확인했습니다. 검사가 시작한 프로세스만 종료했습니다.
- 원본에 있던 빌드 파일 2,860개의 크기와 수정 시간이 전체 빌드 이후에도 동일했습니다.
- 수정된 i-luncher를 기존 Send / Receive 통합 검사로 전송했을 때 파일별 내용 검증까지 성공했습니다.
- 설치된 Send UI 및 backend EXE의 SHA-256은 각각 현재 `bin/publish` 결과와 일치했습니다. 다른 PC의 설치본 버전은 확인하지 않았습니다.

## 사용 범위

수신 후 원본 i-luncher 폴더에서 `Build.ps1`을 실행하고 `Start-Launcher.cmd`로 시작합니다. 로컬 작업 복사본은 다음 동기화 때 원본으로 갱신되므로 소스 수정은 원본에 보관합니다.

원본 clone에서 직접 Visual Studio 또는 `dotnet build/run`을 실행하면 빌드 폴더가 다시 생깁니다. 해당 작업은 로컬 복사본에서 실행해야 합니다. 이전 clone의 실행본·열린 IDE·터미널은 이 변경으로 강제 종료되지 않습니다. 다른 프로세스나 보안 프로그램이 수신 폴더를 잠그는 경우까지 모든 IOException을 없애는 변경은 아닙니다.
