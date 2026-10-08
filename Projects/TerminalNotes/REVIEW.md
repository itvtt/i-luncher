## 2026-10-06: + 터미널을 태스크와 분리한 독립 OpenCode로 변경

- `MainWindow.IndependentTerminals.cs::AddIndependentTerminal`, `MainWindow.xaml.cs::AddTerminalCore`, `MainWindow.Profiles.cs::CreateProfileTerminal`: + 터미널과 Ctrl+Shift+T는 현재 작업 폴더에서 독립 터미널 하나를 추가한다. 태스크 런타임·소유권·슬롯·선택·플랜 문서를 만들거나 변경하지 않는다. 설치 확인 뒤의 명시적 추가 경로도 같은 동작을 사용한다.
- `TerminalPane.cs`의 `useProjectContext` 선택 인자와 `CreateTerminalTabCore`의 독립 상태: 프로젝트 플랜 주입·자동 진행·수동 입력의 태스크 기록 처리를 연결하지 않는다. 실제 OpenCode serve/attach와 기존 네이티브 화면·입력·상태 배경색은 유지한다. 독립 세션마다 별도 키를 발급하며 중단/시작 때 해당 키와 OpenCode 세션만 복원한다.
- `SelectedPlanTerminal`, `TerminalTabs_SelectionChanged`와 문서·이름·선택 메뉴 갱신: 독립 터미널 선택과 이름 변경이 태스크 선택·플랜 초안·프로젝트 이름을 바꾸지 않는다. 독립 세션의 폴더를 태스크 목록에 자동 등록하지 않으며 태스크 없는 화면에서는 독립 터미널이 빈 태스크 영역의 폭도 사용한다.
- `RunIndependentTerminalLifecycleAsync`는 태스크 중단 저장이나 프로젝트 잠금을 사용하지 않는다. `MainWindow.ExternalTerminals.cs::ContinueTerminalInCmdAsync`는 독립 세션을 이관 대상에서 제외한다. `MainWindow.ProjectArchive.cs`는 같은 폴더를 사용하는 독립 터미널이 남아 있으면 먼저 닫도록 안내하여 프로세스나 탭을 임의로 종료하지 않는다.
- `tests/UI/IndependentTerminalSmoke.cs`: 빈 시작 화면에서 버튼 추가, 플랜/태스크/파일 비생성, 기존 태스크와 별도 표시, 초안·소유권·선택·슬롯 보존, 독립 이름 변경, 프로젝트 작업 잠금 중 중단/시작, 실제 OpenCode의 메시지 0건과 동일 세션 복원, 원래 셸 명령 실행, 아카이브/CMD 보호를 검사했다. 임시 설정·작업 폴더와 격리된 OpenCode 환경을 사용하며 실제 모델 요청은 보내지 않았다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개, UI INDEPENDENT TERMINAL SMOKE, UI COMPOSER SMOKE 통과. 마지막 아카이브/CMD 보호 추가 뒤 최종 전체 UI SMOKE도 통과했다. 기존 일반 터미널의 8개 제한과 전역 화면 번호는 유지하며 일반 태스크의 시작 경로는 독립 세션 개수와 무관하다. Luna 읽기 전용 검토에서 외부 이관·아카이브 보호를 확인했다.
- 배포 경로: `artifacts/win-x64-independent-opencode-terminals`. 실행기를 새 배포본에 연결했고 빌드/배포 DLL과 터미널 엔진 DLL의 SHA-256 일치를 확인했다. 앱 DLL: `D5A22979467803BBA833B47FEC24E5D628A6C5E242624F36E2924C3D2A0421D4`. 사용자 앱·설정·실행 중인 세션은 변경하지 않는다. Windows 10 실기기 검사나 모델 작업 실행을 검증한 결과는 아니다.

## 2026-10-06: + 터미널 추가 시 기존 화면 유지

- 원인: `MainWindow.xaml.cs::AddTerminalCore`가 같은 프로젝트의 추가 터미널을 만들 때 태스크 작업 공간을 숨겼다. 일반 터미널 배치는 태스크 소유 터미널을 제외하므로 기존 프로세스가 살아 있어도 화면에서 사라졌다. 수정 전 강화된 UI 검사로 기존 화면 숨김을 재현했다.
- `MainWindow.AdditionalTerminals.cs::UpdateAdditionalTerminalHost`, `MainWindow.Terminals.cs::ApplyTerminalLayout`: 기존 태스크 화면은 원래 호스트·슬롯에 유지하고 추가 세션 영역만 옆에 배치한다. 추가·선택 시 태스크·플랜 패널과 기존 터미널이 함께 보이며, 마지막 추가 터미널을 닫으면 원래 태스크 배치로 복원한다. 태스크의 터미널 소유권·실행 수명·고정 슬롯·초안은 변경하지 않는다.
- `ChangeTerminalLayout`, `FitTerminalLayoutToCount`, `FitTerminalLayoutAfterExplicitClose`, `SetTaskWorkspaceVisible`: 배치 변경과 모드 전환 때 추가 영역을 함께 갱신한다. 전체 태스크 종료 시 기존 축소 동작도 유지한다. 독립 Luna 구현 검토에서 차단할 문제 없음.
- `tests/UI/Program.cs`: 기본 네이티브 화면의 전용 검사와 기존 Ctrl+Shift+T/추가 버튼 검사에서 동시 표시·겹침 없음·태스크와 플랜 표시·선택 전환·동일 소유 세션·기존 출력·닫은 뒤 명령 실행과 배치 복원을 확인한다. 호환 렌더러 출력은 xterm의 자동 줄바꿈 표시를 기준으로 논리 행을 복원해 검사한다. 추가 검사와 기존 탭 전환 검사는 각각 해당 단계 직전에 출력한 표식을 검증한다. 테스트는 임시 프로젝트·설정·셸만 사용한다.
- 검증: Release 빌드 오류 0개, Documents 7개, UI NATIVE TERMINAL ADD SMOKE, UI COMPOSER SMOKE, 전체 UI SMOKE 통과. 네이티브 창 캡처에서 기존 화면과 추가 화면 두 개가 나란히 표시되는 것을 확인했다. NuGet 취약점 조회는 네트워크 제한으로 NU1900 경고가 발생했다. 실제 모델 호출이나 Windows 10 실기기 검사는 포함하지 않는다.
- 초기 호환 렌더러 검사에서 자동 줄바꿈에 의한 표식 분리와 앞선 단계의 오래된 콘솔 출력 소실이 관찰됐다. 논리 행 복원과 단계별 표식 분리 후 원래 탭 전환 검사를 그대로 통과했다. 임의 시점의 전체 과거 콘솔 기록 보존까지 검증한 결과는 아니다. 제품의 터미널 엔진·셸·브라우저 코드는 변경하지 않았다.
- 배포 경로: `artifacts/win-x64-add-terminal-preserve-visible`. 기존 사용자 앱·설정·실행 중인 터미널을 종료하거나 변경하지 않았다.

## 2026-10-06: 기본 작업 폴더가 태스크로 표시되는 문제 수정

- 원인: 실제 설정의 기본 작업 폴더 `C:\opencode`가 저장된 이름·문서 연결에도 남아 있었다. `GetKnownPickerProjects`가 해당 기록을 합치면서 태스크 폴더들을 담는 상위 폴더 자체까지 터미널 선택 메뉴에 포함했다. 설정의 관련 필드만 읽어 확인했으며 사용자 설정은 변경하지 않았다.
- `MainWindow.Terminals.cs::GetTerminalPickerProjects`: 기본 작업 폴더와 경로가 같은 항목을 두 터미널 선택 메뉴에서 제외한다. 이름 문자열로 제외하지 않으므로 실제 하위 태스크의 이름이 `opencode`여도 유지한다. 전체 카탈로그·기존 저장 기록·실행 중인 세션은 보존하며 아카이브 제외도 유지한다. 독립 Luna 검토에서 추가 문제 없음.
- `tests/UI/Program.cs::RunRegisteredProjectPickerSmoke`: 기본 폴더가 저장된 이름과 문서 연결 양쪽에 있는 경우를 재현한다. 두 메뉴에서 기본 폴더만 제외하고, 같은 이름의 실제 하위 폴더와 저장된 외부 프로젝트는 유지되는지 검증한다.
- 검증: Release 빌드 오류 0개, Documents 7개, UI COMPOSER SMOKE, UI STARTUP PROJECT PICKER SMOKE 통과. 전체 UI의 첫 실행은 기존 트레이 숨김 단계에서 시간 초과가 발생했고, 같은 구현·검사로 다시 실행하여 전체 UI SMOKE가 통과했다. NuGet 취약점 조회는 네트워크 제한으로 NU1900 경고가 발생했다. 이전 검사와 같은 격리된 설정·테스트 프로세스 전용 WebView 옵션을 사용했다.
- 배포 경로: `artifacts/win-x64-terminal-picker-workspace-root`. 기존 사용자 앱과 터미널을 종료하지 않았다.

## 2026-10-06: 터미널 선택 목록에서 아카이브 제외

- `MainWindow.Terminals.cs::GetTerminalPickerProjects`, `UpdateDispatcher`, `MainWindow.SingleWorkspace.cs::ShowSlotTaskPicker`: 터미널 상단 선택 메뉴와 빈 터미널 클릭 메뉴에 일반 태스크만 표시한다. 왼쪽 패널의 태스크·아카이브 탭 선택과 관계없이 아카이브 프로젝트를 제외한다.
- 전체 프로젝트를 수집하는 `GetKnownPickerProjects`와 `AssignProjectToSlot`은 보존한다. 아카이브 패널의 직접 배치, 실행 중인 터미널의 제목·상태 갱신과 세션 수명은 유지한다.
- `tests/UI/Program.cs`: 저장된 목록과 최초 실행 목록, 두 사이드바 탭에서의 아카이브 제외를 검증한다. 기존 아카이브 터미널의 직접 배치와 동일한 탭·화면 유지, 비동기 탐색 중 메뉴 갱신과 긴 목록 스크롤 검사도 보존했다. 독립 Luna 코드 검토에서 추가 문제 없음.
- 검증: Release 빌드 오류 0개, Documents 7개, UI COMPOSER SMOKE, UI STARTUP PROJECT PICKER SMOKE, 전체 UI SMOKE 통과. 최초 실행 메뉴의 실제 화면에서 일반 태스크 5개만 표시되는 것을 확인했다. NuGet 취약점 조회는 네트워크 제한으로 NU1900 경고가 발생했다.
- 전체 UI 검사는 이전 항목과 동일한 격리 설정·OpenCode 경로·테스트 전용 WebView 실행 옵션을 사용했다. 실제 사용자 설정이나 앱 실행 옵션은 변경하지 않았다.
- 배포 경로: `artifacts/win-x64-terminal-picker-active-only`. 사용자 앱과 기존 터미널은 종료하지 않았다.

## 2026-10-06: 각 터미널의 태스크명 표시와 직접 편집

- `MainWindow.ProjectTaskModes.cs::AttachSingleProjectTerminal`, `MainWindow.Terminals.cs::ApplyTerminalLayout`: 터미널마다 태스크명을 27 DIP 높이의 한 줄 제목으로 표시한다. 기존 분할 테두리·슬롯 드래그·본문 크기 조절과 세션 수명은 유지하며 제목 옆의 기존 관리 버튼과 슬롯 번호는 계속 숨긴다.
- `MainWindow.TitleEditing.cs::BeginTerminalTaskNameEdit`: 더블클릭·F2로 같은 위치에서 편집하며 Enter·포커스 이동으로 저장, Esc로 취소한다. 빈 이름 거부와 최대 60자는 기존 편집기를 재사용한다. 편집 중 클릭·더블클릭·상태 갱신이 초안을 덮어쓰거나 포커스를 빼앗지 않도록 보호한다.
- `UpdateDispatcher`와 `MainWindow.xaml.cs::SetTerminalTitle`: 표시할 태스크명과 터미널 세션 별칭을 구분한다. 프로젝트 이름의 기존 저장 경로로 태스크 목록·같은 프로젝트의 제목을 갱신한다. 별도로 지정한 세션 별칭, 프로젝트 경로, 플랜 연결과 실행 중인 프로세스는 유지한다. 기본 별칭이 이전 프로젝트명과 같을 때 함께 갱신하는 기존 동작은 보존한다.
- 검증: Release 빌드 오류 0개(네트워크 제한으로 NuGet 취약성 조회 NU1900 경고 5개), Documents 7개, UI COMPOSER SMOKE, 실제 ConPTY의 NativeTerminalSurface, 전체 UI SMOKE 통과. 이름 저장·취소·공백 거부·설정 저장·상태 갱신 중 초안 보존·분할별 이름 독립성과 세션 보존을 확인했다. 두 칸·네 칸 실제 화면에서 제목과 테두리도 확인했다. 독립 Luna 구현·테스트 검토를 수행했다.
- 검사 환경: 기존 전체 UI 검사는 WebView 호환 렌더러를 강제하므로 제한된 실행 환경에서 브라우저가 종료되었다. 격리된 테스트 프로세스에만 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--no-sandbox`를 지정하고, OpenCode 설정·데이터 경로와 CLI 검색 경로도 테스트에만 지정했다. 실제 사용자 설정·앱 실행 옵션은 변경하지 않았다. 테스트 셸은 PSReadLine 기록 저장을 끈다. 기존 트레이 검사의 시간 초과를 추적하기 위해 창 활성화·메시지 진단을 추가했고 최종 실행에서 숨기기·복원·종료가 통과했다. 실제 모델 작업이나 Windows 10 실기기 검사는 포함하지 않는다.
- 배포 경로: `artifacts/win-x64-terminal-task-names`. 사용자 앱과 기존 세션은 종료하지 않았다.

## 2026-10-05: 분할 터미널 사이 테두리 표시

- `MainWindow.SingleWorkspace.cs::ArrangeSingleProjectViews`: 분할 슬롯의 1 DIP 테두리가 실행 중인 프로젝트 화면에 가려지지 않도록 슬롯 바깥 여백은 1 DIP, 프로젝트 화면 여백은 2 DIP로 맞췄다. 중성 테두리를 `#536174`로 표시하며 드래그 강조를 유지한다. 단일 화면은 기존 여백으로 돌아간다.
- `MainWindow.Terminals.cs::ApplyTerminalLayout`, 세션 선택 목록 갱신: 일반 분할 터미널의 테두리·여백을 각각 1 DIP로 표시하며 선택된 터미널의 기존 강조색을 유지한다. 본문 안의 별도 제목·장식은 추가하지 않았다.
- 기존 슬롯 배치·세션·드래그 처리·작은 터미널 자동 배율 로직은 보존했다. 독립 Luna 검토에서 여백을 덮어쓰는 경로나 추가 문제를 발견하지 않았다.
- `tests/UI/Program.cs`: 기존 분할 검사에 두 칸·네 칸 화면 캡처를 추가했다. Release 빌드 경고·오류 0개, Documents 7개, 전체 UI SMOKE를 통과했다.
- 배포 경로: `artifacts/win-x64-terminal-pane-borders`. 실행 중인 사용자 앱과 터미널은 종료하지 않았다.

## 2026-10-05: 작은 터미널 화면 맞춤과 플랜 버튼 위치 조정

- `Controls/NativeTerminalSurface.cs::CreateScrollableViewport`: 작은 분할에서 최소 40×24 화면이 잘리던 문제를 수정했다. OpenCode의 논리 격자는 유지하고 표시 배율만 균일하게 줄여 전체 화면을 칸에 맞춘다. 큰 칸에서는 배율 1로 복원하며 사용 가능한 행·열을 갱신한다. 외부 스크롤 대신 기존 터미널의 휠·스크롤백·마우스 좌표 처리를 유지한다. 일반 셸과 프로세스 수명 코드는 변경하지 않았다.
- `MainWindow.ProjectInput.cs::CreateProjectInputComposer`, `MainWindow.TabDocuments.cs`: 기존 저장·실행 버튼을 ‘플랜에 추가’ 입력창 아래 오른쪽으로 옮겼다. 동일한 컨트롤과 핸들러를 사용하며 자동 진행은 기존 상단 위치를 유지한다. 문서 저장·추가·전송·충돌·초안 보호 로직은 변경하지 않았다.
- `tests/NativeTerminalSurface/Program.cs::VerifyAdaptiveViewportAsync`: 수정 전 240×160 칸에서 화면 잘림을 재현했다. 수정 후 좁거나 낮은 칸에서 양 끝 셀의 실제 렌더링, 최소 격자, 마우스 좌표 변환과 배율 복원을 검사했다. 기존 실제 ConPTY 입력·크기 변경·프로세스 격리 검사도 통과했다.
- `tests/OpenCodeTuiPane/Program.cs`: 기존 외부 스크롤 기대값을 화면 전체가 칸 안에 들어오는 조건으로 갱신했다. 실제 OpenCode 1.18.34 serve/attach를 격리된 임시 설정으로 실행해 여섯 차례 크기 변경 중 프로세스·세션·미전송 초안 보존과 프롬프트 미제출을 확인했다. 실제 작은 화면과 큰 화면을 캡처했다. 모델 작업 실행이나 Windows 10 실기기 검증은 포함하지 않는다.
- `tests/UI/Program.cs`: 저장·실행 버튼이 추가 입력창 아래 오른쪽에 하나씩 표시되고 자동 진행만 문서 위에 남는지 검증한다. 기본 크기와 최소 창 크기 캡처에서 버튼이 보이는 것을 확인했다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개, NativeTerminalSurface, OpenCodeTuiPane, UI COMPOSER SMOKE, 전체 UI SMOKE 통과. 독립 Luna 검토의 오래된 스크롤 기대값 지적을 반영했으며 추가 구현 문제는 없었다.
- 배포 경로: `artifacts/win-x64-terminal-fit-plan-actions`. 사용자 앱·실행 중인 세션·실제 프로젝트 설정은 종료하거나 변경하지 않았다.

## 2026-10-05: 터미널 클릭 메뉴를 태스크 목록으로 단순화

- `MainWindow.SingleWorkspace.cs::ShowSlotTaskPicker`: 새 프로젝트 만들기·프로젝트 폴더 열기·구분선·메뉴 제목을 제거하고 태스크 항목만 표시한다. 빈 목록에서는 선택할 태스크가 없다는 안내만 표시한다. 기존 전체 프로젝트 탐색, 초기 로딩, 스크롤, 선택 차단과 슬롯 배치 경로는 유지한다.
- `ArrangeSingleProjectViews`: 빈 터미널 안내·툴팁·접근성 이름을 태스크 선택 동작에 맞춘다. 새 프로젝트 생성 및 폴더 열기의 내부 함수는 삭제하지 않았다.
- `tests/UI/Program.cs`: 기존 메뉴 검사와 빈 칸 안내 기대값을 새 동작에 맞췄다. 기존 태스크 선택·배치·취소 시 세션 보존 검사는 유지한다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사, UI COMPOSER SMOKE, UI STARTUP PROJECT PICKER SMOKE, 전체 UI SMOKE 통과. 실제 메뉴 캡처에서 태스크 6개만 표시되며 26개 항목의 스크롤도 유지되는 것을 확인했다.
- 배포: `artifacts/win-x64-task-list-only-picker`에 게시하고 실행기·README 경로를 갱신했다. 사용자 앱과 기존 세션은 종료하지 않았다.

## 2026-10-05: 최초 실행의 프로젝트 목록 지연 및 메뉴 잘림 수정

- 기존 저장 프로젝트 병합만으로는 최초 실행 문제가 해결되지 않았다. 격리된 일반 시작 설정(`initialWorkspace` 없음)에 실제 프로젝트 폴더 6개와 저장된 이름 1개를 두고, 첫 프로젝트의 실행 기록 잠금을 유지해 재현했다. 수정 전 실제 빈 터미널 메뉴에는 저장된 프로젝트 하나만 표시되어 회귀 검사가 실패했다(`startup-slot-picker-before.png`).
- `MainWindow.ProjectTasks.cs::RefreshProjectTasksAsync`, `MainWindow.Terminals.cs::GetKnownPickerProjects`: 디렉터리 탐색 결과를 별도로 보존하고 실행 기록 로딩 전에 선택 목록에 반영한다. 기존 실행 기록 처리·태스크 행 등록·일반/아카이브 검색 범위·선택 보호는 유지한다.
- `MainWindow.SingleWorkspace.cs::ShowSlotTaskPicker`, `MainWindow.Terminals.cs::WaitForProjectDirectoryScanAsync`: 탐색 중에는 로딩 안내를 표시하고, 완료되면 이미 열린 메뉴에 전체 프로젝트를 채운다. 중첩된 갱신은 최신 탐색을 기다리며 창 종료·교체된 메뉴는 갱신하지 않는다.
- 실제 표시 검사에서 두 번째 결함도 확인했다. 전체 높이의 빈 터미널 아래에 메뉴를 배치해 메뉴 높이가 130 DIP로 제한되고, 6개 항목 중 아래쪽이 잘렸다. `MainWindow.xaml::ProjectPickerMenu`에 이 메뉴 전용 스크롤 영역을 추가하고 터미널 영역 안쪽에 배치했다. 일반 컨텍스트 메뉴 스타일은 보존한다.
- `tests/UI/Program.cs::RunStartupProjectPickerSmoke`: 일반 최초 시작, 기록 로딩 지연 중 실제 두 선택 목록, 탐색 완료 후 재클릭 없는 메뉴 갱신, 6개 항목의 실제 가시 영역, 26개 프로젝트의 스크롤 및 마지막 항목 도달을 검증한다. 목록을 열거나 스크롤해도 터미널·플랜이 만들어지지 않는다. 기존 저장 프로젝트 검사는 비동기 목록 준비를 기다리도록 보완했다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개, UI STARTUP PROJECT PICKER SMOKE, UI COMPOSER SMOKE, 전체 UI SMOKE 통과. 수정 후 6개 프로젝트가 모두 표시되는 실제 메뉴를 `startup-slot-picker.png`로 캡처했다. OpenCode 모델 요청 실행은 이 검사에 포함하지 않는다.
- 구현의 독립 Luna 검토에서 추가 문제 없음. 실제 사용자 설정·프로젝트·실행 중인 터미널은 수정하거나 종료하지 않았다.
- 배포: `artifacts/win-x64-startup-project-picker`에 게시하고 실행기·README 경로를 갱신했다. 게시된 실제 DLL로 격리된 최초 시작 검사를 다시 수행해 통과했다. RID 빌드·게시·배포 검사 DLL SHA-256: `14E7622936872BD27F5D08E58626E72798B006EBFBA36058314B360816FCDF22`.

## 2026-10-05: 터미널 선택 목록의 저장된 프로젝트 누락 보완

- `MainWindow.Terminals.cs::GetKnownPickerProjects`, `UpdateDispatcher`: 화면에 등록된 태스크 행과 열린 세션만 참조하던 목록에 저장된 플랜 연결·프로젝트 이름을 합친다. 경로를 정규화해 중복 없이 이름순으로 표시하며, 팝업 상단에 프로젝트 수와 열린 터미널 수를 각각 표시한다.
- `MainWindow.SingleWorkspace.cs::ShowSlotTaskPicker`, `AssignProjectToSlot`: 빈 터미널 칸에서도 같은 전체 목록을 사용한다. 태스크 행 등록 전의 저장된 프로젝트도 명시적으로 선택하면 기존 배치 경로를 사용한다. 행 생성은 선택 시에만 수행하며 폴더 존재·외부 실행·보관 이동·저장·실행 준비 중 선택 차단을 유지한다.
- `MainWindow.ProjectTasks.cs::RefreshProjectTasksAsync`: 검색 완료 후 터미널 목록도 즉시 갱신한다. 목록을 여는 것으로 플랜 저장이나 터미널 생성을 수행하지 않는다.
- 기존 검사는 태스크 행을 수동 등록한 뒤 목록 항목만 확인했다. `tests/UI/Program.cs::RunRegisteredProjectPickerSmoke`는 격리된 설정에서 일반·이름 지정·아카이브 프로젝트를 읽고 태스크 행 등록 전의 상태를 재현한다. 두 목록에 저장된 프로젝트가 나타나는지, 목록 열기가 플랜·터미널을 생성하지 않는지, 보관 이동 중 선택이 차단되는지 검증한다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사, UI COMPOSER SMOKE, 전체 UI SMOKE 통과. 실제 팝업에서 프로젝트 4개와 열린 터미널 1개가 표시되고, 모든 항목이 한 열로 배치되며 마지막 항목이 보이는 것을 확인했다. `project-picker-smoke.png`로 캡처했다. 대규모 목록 스크롤 부하나 실제 모델 요청 검증은 포함하지 않는다.
- 검사 이력: 새 회귀 검사의 최초 실행은 초기 작업 폴더가 만드는 기존 터미널과 숨긴 창의 팝업 표시 상태를 잘못 가정해 실패했다. 기존 터미널 스냅샷 보존으로 검사 기준을 수정하고, 실제 표시 검사는 별도의 열린 창 경로에서 검증했다. 임시 레이아웃 프로브는 격리 설정이 불완전해 검증 근거에서 제외하고 해당 프로세스를 종료했다.
- 독립 Luna 검토에서 확인한 슬롯 선택의 저장·보관 이동 중 보호 조건을 보완했다. 최종 구현 검토에 차단 문제 없음.
- 배포: `artifacts/win-x64-project-picker-catalog`에 게시하고 실행기·README 경로를 갱신했다. 빌드와 배포 DLL SHA-256 일치를 확인했다. 사용자 앱과 기존 터미널은 종료하지 않았다.

## 2026-10-05: 태스크·아카이브 탭 디자인 개선

- `MainWindow.xaml::ProjectArchiveTab`: 두 탭을 둥근 그룹 안에서 선택하는 형태로 다듬었다. 선택 탭은 은은한 파란 면·얇은 테두리·밝은 글자로 강조하고, 마우스 올림·누름·키보드 포커스·비활성 상태를 구분한다. 포커스 테두리는 별도 층으로 표시해 글자 위치가 흔들리지 않는다.
- `MainWindow.ProjectArchive.cs::CreateProjectArchiveTabs`: 공통 배경과 좌우 여백을 추가하고 태스크·아카이브 클릭 영역을 각각 56×32, 68×32 DIP로 조정했다. 기존 `new` 버튼과 같은 줄을 유지하며 최소 사이드바 폭 200 DIP 안에 배치한다. 그룹 배경의 표시 상태는 기존 탭과 함께 접히도록 연결했다.
- 기존 라디오 그룹, 탭 전환 핸들러, 아카이브 기능과 태스크·플랜 접기 동작을 보존했다. 독립 Luna 코드 검토에서 차단 문제 없음.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사, 전체 UI SMOKE와 UI COMPOSER SMOKE 통과. 실제 기본 크기·최소 창 크기 캡처에서 새 디자인의 글자와 `new` 배치를 확인했다. 실제 모델 요청 검증은 포함하지 않는다.
- 배포: `artifacts/win-x64-modern-task-tabs`에 게시하고 실행기·README 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 재실행 버튼 제거와 중단·시작 전환

- `MainWindow.ProjectTasks.cs::EnsureProjectTaskRow`, `MainWindow.ProjectTaskModes.cs`: 태스크 행과 태스크 터미널 헤더에서 재실행 버튼을 제거했다. 태스크 제목 영역을 넓히고 폴더·제목 편집·시작/중단·반복 실행 동작을 유지했다.
- `MainWindow.xaml`, `MainWindow.Terminals.cs`: 상단·단일·분할 터미널의 별도 재실행 버튼과 미사용 아이콘 템플릿을 제거했다. `MainWindow.Toolbar.cs::TerminalProcessToggle_Click`, `MainWindow.TerminalLifecycle.cs::ToggleSelectedTerminalProcessAsync`, `UpdateTerminalLifecycleButtons`: 기존 버튼 하나가 실행 중에는 `■ 중단`, 중단 후에는 `▶ 시작`으로 전환된다.
- 기존 내부 재실행 경로와 컨텍스트 메뉴 핸들러를 보존했다. 시작 시 기존 종료 대기·중복 조작 방지·소유권 확인을 사용하며 탭 위치·프로젝트·플랜·입력 초안·다른 터미널을 유지한다. 시작 중인 프로세스도 기존 조건에 따라 중단할 수 있다.
- `tests/UI/Program.cs`, `tests/TerminalLifecycle/Program.cs`: 재실행 버튼 부재, 남은 버튼 크기와 배치, 실제 네이티브 셸의 중단 → 시작 전환 및 다른 태스크·입력 초안 보존을 검사한다.
- 독립 Luna 검토에서 차단 문제 없음. Release 빌드 경고·오류 0개, Documents 7개 검사, TerminalLifecycle, UI COMPOSER SMOKE, 전체 UI SMOKE 통과. UI 캡처에서도 재실행 버튼 제거를 확인했다. 실제 모델 요청 검증은 포함하지 않는다.
- 검사 이력: 최초 composer 검사는 이전 버튼 개수 4개를 기대해 실패했다. 재실행 버튼 제거 후 실제 구성인 3개로 검사 기준을 갱신했고 최종 composer 검사가 통과했다.
- 배포: `artifacts/win-x64-start-stop-only`에 게시하고 실행기·README 경로를 갱신했다. 빌드·배포 DLL SHA-256 일치: `5CC5F66CD4122794022855CA511528D2CADCFDEFF36D8E5CA633A6A78E97FF7A`. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 태스크·플랜 동시 접기와 저장·실행 통합

- `MainWindow.ProjectTaskCategories.cs::CreateTaskCategoryEntry`: 카테고리 생성 항목을 `+새카테고리`로 표시한다. `MainWindow.ProjectTasks.cs::EnsureProjectTaskRow`: 기존 폴더 버튼을 제목 왼쪽으로 옮기고 한 줄 배치·제목 편집·폴더 열기 동작을 유지한다.
- `MainWindow.TaskSidebar.cs`, `MainWindow.xaml.cs`, `MainWindow.Toolbar.cs`: 기존 내부 배치와 터미널을 유지하며 플랜/터미널 경계의 중앙 버튼 하나로 태스크와 플랜을 함께 접고 펼친다. 기존 사이드바 폭·접힘 설정을 유지하고 플랜/터미널 폭 비율을 선택적 `TaskPlanWidth` 설정에 보존한다.
- `MainWindow.ProjectInput.cs`, `MainWindow.TabDocuments.cs`: 아래 입력창을 `플랜에 추가`로 표시하고 별도 전송 버튼을 숨긴다. 상단 저장·실행을 공통으로 사용한다. 저장은 문서와 추가 입력을 저장만 하며, 실행은 추가 입력이 있으면 기존 요청 전송 경로를, 없으면 기존 전체 플랜 실행 경로를 사용한다. 반복 저장·실패 후 재시도의 중복 추가 방지와 저장 충돌·초안 보존을 유지한다.
- 독립 검토: 저장·실행 경로와 패널 전환에서 추가 차단 문제 없음. 검토 중 확인한 폭 저장 문제는 한쪽 star 값 대신 플랜/터미널 비율을 보존하도록 보완했다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사, TabDocuments, UI COMPOSER SMOKE, 전체 UI SMOKE, PlanExecution 통과. 실제 UI 캡처에서 카테고리 문구, 왼쪽 폴더 버튼, 단일 접기 버튼과 저장·실행 배치를 확인했다. UI 회귀 검사는 공통 저장의 비전송, 반복 저장·실행 재시도의 중복 추가 방지, 폭 비율 저장·복원을 검증했다. 실제 모델 요청 검증은 포함하지 않는다.
- 검사 이력: 카테고리 문구만 수정한 초기 UI 검사는 터미널 포커스 검사에서 실패했다. 통합 후 첫 UI 검사는 접기 버튼 안내문의 언어 차이로 실패했고, 안내문을 `태스크·플랜 패널 접기/펼치기`로 수정한 최종 전체 UI 검사가 통과했다.
- 배포: `artifacts/win-x64-unified-task-plan`에 게시하고 실행기·README 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 재실행 아이콘을 세로 직사각형과 실행 삼각형으로 변경

- `MainWindow.xaml`의 `RestartActionIcon`: 채워진 세로 직사각형과 실행 삼각형을 조합한 `▮▶` 모양으로 변경했다. 태스크와 터미널의 공통 템플릿에 적용되며 버튼 크기와 동작은 유지한다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사와 전체 UI 검사 통과. 실제 UI 스크린샷에서 양쪽 버튼의 모양을 확인했다.
- 배포: `artifacts/win-x64-rerun-bar-play`에 게시하고 실행기·README 경로를 갱신했다. 빌드와 배포 DLL SHA-256 일치를 확인했으며, 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 재실행 아이콘을 |》 모양으로 변경

- `MainWindow.xaml`의 `RestartActionIcon`: 세로 막대와 오른쪽 이중 화살표를 조합한 16×16 벡터로 변경했다. 태스크와 터미널의 기존 공통 템플릿을 사용하며, 버튼 폭·클릭 처리·접근성 이름·툴팁을 유지한다.
- 검증: 최종 Release 빌드 경고·오류 0개와 전체 UI 검사 통과. Documents 7개 검사도 통과했다. 최종 UI 스크린샷에서 양쪽 아이콘과 한 줄 태스크 배치를 확인했다.
- 배포: `artifacts/win-x64-rerun-chevron`과 실행기·README 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 태스크·터미널 재실행 아이콘 변경

- `MainWindow.xaml`의 공통 `RestartActionIcon` DataTemplate: 중지 사각형과 실행 삼각형을 조합한 16×16 벡터 아이콘으로 재실행을 표시한다. 버튼 전경색과 비활성 투명도를 따르며, 각 버튼에 독립적인 시각 요소를 생성한다.
- `MainWindow.ProjectTasks.cs`의 `EnsureProjectTaskRow`와 터미널 재실행 버튼 두 곳에 같은 템플릿을 적용했다. 툴팁에 다시 실행을 명시하고 기존 클릭 처리·상태 조건·버튼 크기·접근성 이름을 유지했다.
- 검토: 독립 Luna 검토에서 템플릿 표시, 색상 바인딩, 클릭 영역, 비활성 상태, 접근성 및 기존 동작 유지에 차단 문제 없음.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사와 전체 UI 검사 통과. UI 검사 스크린샷에서 태스크 행과 터미널 상단의 아이콘 표시를 확인했다.
- 배포: `artifacts/win-x64-rerun-action-icon`과 실행기·README 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 새 카테고리 표시를 +로 변경

- `MainWindow.ProjectTaskCategories.cs`의 `CreateTaskCategoryEntry`: 트리 맨 아래 생성 버튼의 표시를 `=`에서 `+`로 변경했다. 위치와 이름 입력·저장 동작은 유지한다.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사 통과. 첫 전체 UI 검사는 터미널 우클릭 메뉴 시간 초과로 실패했으며, 코드 변경 없이 다시 실행한 전체 UI 검사는 통과했다.
- 배포: `artifacts/win-x64-category-plus`와 실행기·README 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다.

## 2026-10-05: 터미널 선택에 전체 프로젝트 표시

- `MainWindow.Terminals.cs`의 `UpdateDispatcher`: 열린 터미널만 나열하던 목록에 등록된 태스크 프로젝트를 합친다. 이름·경로순으로 정렬하며, 터미널이 없는 프로젝트는 선택 항목을 표시하고 열리면 기존 세션 항목으로 대체한다. 기존 세션 버튼과 선택·상태 갱신 경로는 유지한다.
- `MainWindow.Toolbar.cs`의 `SessionPicker_Click`: 팝업을 열기 직전에 목록을 갱신한다. 목록 갱신 자체는 디스크 검색이나 프로세스 시작을 수행하지 않는다. 미실행 프로젝트 선택은 기존 열기 경로와 외부 터미널·저장·이동 중 보호 조건을 사용한다.
- `MainWindow.SingleWorkspace.cs`의 `ShowSlotTaskPicker`: 태스크·아카이브 탭 필터를 제거해 빈 칸에서도 등록된 전체 프로젝트를 선택할 수 있게 했다. 기존 세션 재사용과 자리 이동을 유지한다.
- 검토: 독립 Luna 검토에서 세션 버튼 재사용, 프로젝트 중복 제거·정렬, 탭과 무관한 목록, 선택 보호 조건, 배치 보존에 차단 문제 없음.
- 검증: Release 빌드 경고·오류 0개, Documents 7개 검사, 전체 UI, UI composer 검사 통과. 미실행 항목 표시 시 세션 불변, 기존 세션 선택 시 pane 재사용, 실행 후 중복 항목 제거, 양쪽 탭에서 일반·아카이브 선택 및 기존 8칸 배치 보존을 확인했다. 추가 테스트의 임시 세션은 다음 검사 전에 정리한다.
- 배포: `artifacts/win-x64-all-project-picker`와 실행기 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다. 빌드·배포 DLL SHA-256 일치: `63B98528E212FBBA317035E576F70474458BC2F22E12FFA89067291413B47831`.

## 2026-10-04: 카테고리 트리 아래 생성 항목

- `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`: `new` 클릭을 새 프로젝트 대화상자로 직접 연결했다. 프로젝트·카테고리를 함께 표시하던 메뉴는 제거했다.
- `MainWindow.ProjectTaskCategories.cs`의 `CreateTaskCategoryEntry`: 카테고리 트리의 별도 `=` 항목을 클릭하면 기존 이름 입력 대화상자를 열고, 저장 시 카테고리를 생성한다. 전체 행을 클릭할 수 있으며 키보드 포커스·툴팁·접근성 이름을 제공한다.
- `MainWindow.ProjectTaskTree.cs`: 생성 항목은 카테고리 이름순 정렬에 포함하지 않고 항상 맨 아래에 둔다. 제목 편집으로 정렬이 보류되어도 새 카테고리를 생성 항목 앞에 넣는다. `MainWindow.ProjectArchive.cs`는 프로젝트가 없는 경우에도 트리를 표시해 카테고리를 생성할 수 있게 한다.
- 검토: 독립 Luna 검토에서 빈 작업 폴더의 접근, 정렬·이름 변경·삭제 후 위치, 기존 태스크 선택·드래그 경로 유지에 차단 문제 없음을 확인했다.
- 검증: Release 빌드 경고·오류 0개, Documents·UI composer·전체 UI 검사 통과. `artifacts/win-x64-category-tree-create`에 게시하고 실행기를 갱신했다. 빌드/배포 DLL SHA-256 일치: `91DE410B300854385EAB74E7F87FB9FFDDE4DBB9F796D47C40B2234BE0C17962`.

## 2026-10-04: 사용자 카테고리와 터미널 프로세스 중단·재실행

- `Services/ProjectCategoryCatalog.cs`: 작업 폴더별 카테고리와 프로젝트 분류를 저장한다. 중복·예약 이름 검증, 이름 변경·삭제, 아카이브 경로 이동을 지원하며 실제 프로젝트 폴더는 변경하지 않는다. `MainWindow.xaml.cs`의 설정 읽기·저장과 `MainWindow.ProjectArchive.cs`의 이동 처리에 연결했다.
- `MainWindow.ProjectTaskCategories.cs`, `MainWindow.ProjectTaskTree.cs`, `MainWindow.ProjectTasks.cs`: `new` 메뉴에서 프로젝트·카테고리를 생성하고, 태스크를 카테고리에 드래그하거나 우클릭 메뉴로 옮긴다. 카테고리와 태스크 표시 이름은 대소문자를 구분하지 않는 이름순으로 정렬한다. 미분류·빈 카테고리도 드롭 대상이며 상태·터미널 배정은 분류를 바꾸지 않는다. 행 재사용과 트리 갱신 중 선택 이벤트 차단으로 선택·제목 편집·플랜·입력 초안을 유지한다. `MainWindow.TitleEditing.cs`에서 편집 완료 후 정렬을 갱신한다.
- `MainWindow.xaml`, `MainWindow.Toolbar.cs`, `MainWindow.TerminalLifecycle.cs`, `TerminalPane.Lifecycle.cs`: 터미널 상단에 선택한 프로세스 중단·재실행 버튼을 추가했다. 선택한 탭/프로젝트의 중복 조작을 차단하고, 관리 태스크는 자동 실행 중단 및 시작 취소 완료를 확인한 뒤 소유 프로세스 종료를 기다린다. 재실행은 해당 탭만 교체하며 위치·프로젝트·플랜 경로·입력 초안을 유지한다. 같은 프로젝트의 별도 수동 터미널을 조작해도 기존 관리 태스크를 중단하지 않는다.
- 재실행으로 플랜·hi를 자동 제출하지 않으며 오래된 제출 식별자는 새 pane에 복사하지 않는다. 플랜 없는 준비 상태는 새 pane으로 연결하고, 명시적 작업 제출 이후 전역 자동 진행 설정을 복구한다. 시작 취소가 10초 안에 완료되지 않으면 기존 프로세스를 유지하고 재시도 안내를 표시한다.
- 검토: 독립 Luna 검토에서 수동 pane의 소유권, 자동 진행 복원, 플랜 없음 안내, 시작 취소 경쟁, 카테고리 이동 시 선택 이벤트를 점검하고 발견된 문제를 수정했다. 최종 재검토에서 차단 문제 없음.
- 검증: Release 빌드 경고·오류 0개. Documents, ProjectCategories 27개 검사, UI composer, 전체 UI, 네이티브 PowerShell 프로세스 중단·재실행 검사 통과. 실제 PID 종료·다른 pane 유지·관리 태스크 재연결·지연된 시작 취소를 확인했다. WorkflowIntegration `--empty-plan-native-only`는 모의 OpenCode API/서버로 종료 대기·재실행·빈 플랜 및 안내 기준 보존·자동 요청 없음·이후 명시적 요청 1회 접수를 확인했다. 이 모의 API 검사를 실제 모델 또는 실제 OpenCode attach 검증으로 간주하지 않는다.
- 배포: `artifacts/win-x64-task-categories-terminal-controls`에 게시하고 실행기의 두 경로를 갱신했다. 실행 중인 사용자 앱은 종료하지 않았다. 빌드/배포 DLL SHA-256 일치: `51131098940A81087AB8A0E761F9808E505C71911072AE0A221EF5E963B2C1A4`.

## 2026-10-04: 태스크 헤더 load 버튼 제거

- `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`에서 태스크·아카이브 탭 옆 `load` 버튼 생성과 연결만 제거했다. 같은 행의 `new` 버튼과 기존 프로젝트 열기 함수 및 다른 진입점은 유지한다.
- 변경 전 파일과 비교해 버튼 생성부 15줄 외에 프로젝트 실행·저장·터미널 처리 변경이 없음을 확인했다.

## 2026-10-04: 프로젝트 버튼과 플랜 없는 시작 안내

- `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`: 태스크·아카이브 탭 행에서 새로고침 버튼을 제거했다. 프로젝트 탐색 함수와 시작·설정 변경·불러오기·생성·보관 시 갱신은 유지한다. `load`는 배경과 테두리가 있는 버튼, `new`는 파란 버튼으로 표시하며 44×28 DIP 크기와 간격을 맞췄다. 기존 클릭 동작·툴팁·접근성 이름은 유지한다.
- `MainWindow.ProjectTasks.cs`의 `OpenProjectTaskWithoutPlanAsync`, `MainWindow.TabDocuments.cs`의 `RefreshTabDocumentControls`·`IsReadyProjectWaitingForPlan`: 플랜 없이 OpenCode가 준비되면 기존 플랜 상태 줄에 주황색으로 `플랜 없음 · 작업 시작 대기`를 표시한다. 같은 pane의 요청 세대를 기록해 이후 직접 요청을 보내거나 플랜을 작성하면 안내를 해제하며, 실제 실행·승인 대기·오류·미저장 상태를 우선한다. pane은 약한 참조로 보관하며 기존 자동 진행 및 요청 이력은 변경하지 않는다. 터미널 안에 안내 UI나 자동 `hi` 요청은 추가하지 않는다.
- 검토: 독립 Luna 검토에서 헤더의 최소 폭 200 DIP 안에 탭과 버튼 192 DIP가 배치되는 점, 기존 클릭 처리·목록 갱신 유지, 준비 완료 이후 안내 표시, pane 교체·수동 요청·오류 상태 처리 및 기존 작업 이력 보존을 확인했다.
- 검증: Release 빌드 경고·오류 0개, Documents·전체 UI·WorkflowIntegration `--empty-plan-native-only` 통과. 누락/빈 플랜은 요청 없이 같은 터미널을 열고, 이후 실제 플랜은 같은 터미널에 한 번만 접수됨을 모의 API로 확인했다. 화면 캡처는 이 세션에서 WPF 영역이 검게 나와 육안 검증에 사용하지 못했으며 레이아웃 확인은 UI 검사와 소스 검토에 근거한다.
- 배포: `artifacts/win-x64-project-actions`에 게시하고 `Start-TerminalNotes.cmd`를 갱신했다. 실행 중인 앱은 종료하지 않았다. 빌드/배포 DLL SHA-256 일치: `A8B4F4B01A2FCF4A72AABFF4A906185ED94A6A6F7175E9DDACE009B8AC37D8A9`.

## 2026-10-04: 터미널 전체 상태 배경과 플랜 없는 OpenCode 시작

- `Controls/NativeTerminalSurface.cs`, `TerminalPane.cs`: OpenCode 네이티브 본문과 스크롤 영역의 여백까지 진행 녹색·입력 대기 주황색·기타 회색 배경을 적용한다. 명시적인 ANSI 배경과 반전 출력에도 상태색을 유지하며 글자 대비, 반전 표시와 선택 영역을 보존한다. 일반 셸의 ANSI 배경 동작은 유지한다.
- `MainWindow.ProjectTasks.cs`, `MainWindow.ProjectTaskModes.cs`, `MainWindow.xaml.cs`: 태스크 시작·재실행에서 플랜이 없거나 공백뿐이면 같은 OpenCode 터미널을 열어 직접 입력을 기다린다. 이 경로만 플랜 파일 생성을 건너뛰며 자동 초기 요청을 보내지 않고 미저장 초안·기존 요청 상태를 보존한다. 이후 내용 있는 플랜은 기존 전송 경로를 사용한다. 준비 중 중단과 파일 읽기 오류 처리를 유지한다.
- 검토: 별도 Luna 작업자가 파일 읽기 실패와 파일 부재 구분, 준비 대기 취소, 기존 상태·초안 보존, 자동 초기 요청 경로 차단을 확인했다. 테스트의 리플렉션 호출은 변경된 선택 함수의 기본 인자를 명시하도록 맞췄다.
- 검증: Release 빌드·배포 경고/오류 0개. Documents, 전체 UI, NativeTerminalSurface 검사를 통과했다. 네이티브 픽셀 검사는 ANSI 배경·반전·글자 대비·선택과 스크롤 여백의 상태색 변경을 확인한다. 실제 OpenCode 1.18.34는 플랜 부재·빈 플랜 모두 메시지 0개로 열렸고, 미전송 초안·크기 변경·소유 프로세스 종료를 확인했다. 마지막 실제 TUI 화면에서 검정 여백이 사라진 것도 확인했다.
- WorkflowIntegration `--single-only`, `--initial-plan-only`, `--empty-plan-native-only` 통과. 누락/공백 플랜으로 시작·재실행할 때 자동 요청 없이 같은 pane을 재사용하며 파일·초안·상태를 보존한다. 이후 작성한 플랜은 같은 pane에서 정확한 요청 하나로 접수되며 기존 세션 생성 실패·응답 유실 복구도 확인했다. 이 흐름 검사는 격리된 모의 API를 사용하고 실제 모델 요청은 보내지 않았다.
- 배포: 기존 실행 중인 앱을 종료하지 않고 `artifacts/win-x64-terminal-state-background`에 새 빌드를 게시했다. `Start-TerminalNotes.cmd`가 이 경로를 사용한다. 빌드/배포 DLL SHA-256 일치: `569F8C85D92D43B7F9010FBCFCA0E8FA57D6FA232F2EFDADE2106BAD580153E7`.

## 2026-10-04: OpenCode 자체 터미널 화면과 제목 표시줄 분할 버튼

- `TerminalPane.cs`, `TerminalPane.Native.cs`, `TerminalPane.Tui.cs`: 기본 OpenCode 화면을 자체 API 대화 UI에서 실제 `opencode attach` TUI로 전환한다. 같은 pane의 `serve` 프로세스와 API 상태·정확한 요청 접수 확인은 유지하고, 자체 ANSI/VT 엔진과 WPF 화면으로 TUI를 표시한다. 기본 경로는 WebView를 초기화하지 않는다. 기존 대화 화면은 `TERMINALNOTES_OPENCODE_RENDERER=headless`로 명시적으로 선택할 수 있다.
- `TerminalPane.Startup.cs`, `TerminalPane.Automation.cs`: API로 전송한 세션을 TUI에서도 선택하고, 접수된 요청의 화면 재연결은 요청 재전송과 분리한다. 수동 TUI 입력은 API의 실제 프로젝트·루트 세션·메시지 ID·역할·원문·시각으로 확인한 후 연결한다. TUI에서 과거 세션을 열기만 한 경우에는 앱 작업의 세션을 변경하지 않는다. 앱의 중단 버튼은 확인된 앱 작업 세션을 API로 중단하며, TUI에 Ctrl+C를 덧붙이지 않는다.
- `Controls/NativeTerminalSurface.cs`, `TerminalPane.Surface.cs`: 작은 분할에서 스크롤로 접근하는 최소 40열×24행을 유지한다. 실제 검사에서 발견한 소수점 측정 때문에 23행이 되던 경우를 화면과 프로세스에 전달하기 전에 보정한다. 닫기·외부 이어가기에서는 pane이 소유한 TUI와 서버를 함께 정리한다.
- `MainWindow.xaml`: 기존 1/2/3/4/6/8 분할 버튼을 제목 표시줄의 설정·최소화·최대화 왼쪽으로 옮기고 12 DIP 간격을 둔다. 기존 클릭 처리·단축 기능과 작업표시줄 영역 보정은 유지한다. 820 DIP 미리보기에서 배치와 간격을 확인했다.
- 검토: 별도 소스 검토에서 초기 세션 선택 이벤트와 TUI 구독 시점의 경합, 수동 메시지의 정확한 세션·시각 확인을 보강했다. Windows 10 실기기에서 OpenCode 자체의 종료 문제가 해결되었다고 주장하지 않는다.
- 검증: Release 빌드와 배포는 경고·오류 0이다. Documents, OpenCodeStatus, NativeTerminalSurface, 명시적 headless 화면, 전체 UI 회귀 검사를 통과했다. 실제 설치된 OpenCode 1.18.34를 실행한 OpenCodeTuiPane 검사에서 WPF 본문·세션 선택의 OSC 제목·미전송 초안·여섯 차례 크기 변경·최소 40×24 크기·메시지 수 불변·소유 서버와 attach 프로세스 종료를 확인했다. OpenCodeTuiPane 검사는 모델 요청을 보내지 않았다.
- 추가 WorkflowIntegration 검사에서는 이전 PTY 입력 추적을 API 요청 추적으로 변경하고 프로젝트 경로·프롬프트·단 한 번의 요청·서로 다른 세션/메시지 ID를 확인하도록 했다. 검사 실행기의 `--version` 응답이 없어 실제 설치된 OpenCode가 선택될 수 있던 문제도 발견했다. 모의 버전 응답과 실제 실행 경로 확인을 추가하여 fixture의 실행기가 아니면 창/요청 전에 검사를 중단한다. 앞선 실패 fixture에는 요청 접수와 Busy/Idle 기록이 있지만 실제 모델 응답을 입증하는 자료는 없었으며, 관련 실행 프로세스가 남아 있지 않음을 확인했다.
- WorkflowIntegration `--single-only` 최종 통과: 즉시/이미 준비된 태스크 시작, 설치 확인 지연 중 시작, 터미널이 없는 프로젝트의 플랜 실행, 입력 저장 후 전송 재시도의 중복 저장 방지, 외부 플랜 변경 차단, 디스크 플랜 재읽기 및 빈/없는 플랜 거부를 확인했다. 모의 서버의 `/global/health`, 시작 함수의 현재 인수, 공통 실행 지침이 포함된 정확한 전송 원문을 검사에 반영하고 시작 대기에 90초 한도를 두었다. 독립 검토에서 기존 저장·준비 대기·세션/메시지·중복 요청 보호가 유지됨을 확인했다.
- 배포: 기존 실행을 종료하지 않고 `artifacts/win-x64-opencode-tui`에 새 실행 파일을 배포했으며 `Start-TerminalNotes.cmd`가 이 경로를 사용한다. 빌드/배포 DLL SHA-256 일치: `E45DA0B95DB921C34B524A128BE58125AA501A3F5A154E0E28F00F218737E731`.

## 2026-09-30: 이미 반영된 요구사항의 재구현 방지

- 기존 재구현 금지 지침을 보강했다. 전역 기본 템플릿, 프로젝트 실행/재개 지침, 플랜 추가 전송, 지속 개선 및 정체 재시작 요청에서 요구사항별 현재 구현·변경분·유효 검증 근거를 먼저 대조하도록 한다. plan/todo 미완료 표시만으로 다시 구현하지 않고, 이미 충족된 항목은 근거와 todo 상태만 정리한다. 실제 누락·구체 결함만 최소 수정하고 모두 충족되면 코드 변경 없이 끝낸다.
- 기존 사용자가 편집한 전역 MD와 플랜 원문은 수정하지 않았다. 코드 완료 여부를 프로그램이 자동 판정한다고 주장하지 않으며 모델에 전달하는 구체 절차를 보강한다. native todo/최종 응답 기반 자동 재개 보호는 유지한다.
- AutoResumeController 통합 95개 검증 통과. 지속 개선을 켠 채 완료 근거가 확정된 작업을 6회 추가 조회해도 Continue/Abort를 보내지 않는 회귀를 추가했다. TabDocuments 및 InputJournal의 MD 충돌/기존 사용자 지침 보존 검사도 통과했다. InputJournal의 NU1900은 네트워크 제한으로 패키지 취약성 정보를 조회하지 못한 경고다.
- 기본 배포 경로 artifacts/win-x64의 DLL은 실행 중 프로세스가 사용 중이어서 복사하지 못했다. 사용자 앱을 종료하지 않고 최신 빌드는 별도의 win-x64-update 디렉터리로 배포한다.

## 2026-09-30: 터미널 드래그 상태 해제

- 제목/카드에서 놓친 mouseup의 시작점이 다음 입력에 재사용되지 않도록 해제·영역 이탈·캡처 상실·Unloaded 때 초기화한다. 기존 슬롯 이동·취소·예외 후 원복 검사는 다시 통과했다.
- xterm의 ownerDocument mousemove/mouseup 및 드래그 스크롤 리스너가 WebView 해제 이벤트 누락으로 남을 수 있어, 버튼 없는 이동·pointerup 보완·포인터 취소/캡처 상실·창 blur·문서 숨김 때 정상 mouseup 경로로 끝낸다. 선택 영역은 지우지 않으며 늦은 복구는 새 gesture를 중단하지 않는다. 마지막 유효 마우스 위치와 버튼을 사용하고 실제 mouseup이 왔으면 중복 해제하지 않는다.
- tests/TerminalPointer/verify.mjs에서 실제 Assets의 xterm 번들을 headless Edge로 실행했다. 누락된 mouseup 뒤 선택이 계속 늘어나는 실패를 확인한 후 정상 mouseup, buttons=0 이동 복구, pointerup 보완, blur, pointercancel, lostpointercapture, 정상 드래그 연장 및 SGR TUI 마우스 입력/해제 검사를 통과했다. blur 해제가 TUI에 전달되지 않던 Event 인수/generation 비교도 검사 중 발견해 수정했다. TUI에 해제 후 가짜 드래그 입력이 남지 않는 것을 확인했다.
- headless 테스트는 로컬 번들·대체 WebView 메시지 bridge를 사용하며 실제 모델·가시 창·사용자 터미널 프로세스를 실행하지 않는다. 실행 환경의 중첩 renderer sandbox 시작 제한으로 해당 테스트용 Edge만 --no-sandbox로 실행하며 제품 설정에는 적용하지 않는다. 사용자의 물리 마우스/WebView HWND에서 발생한 원래 제스처 자체를 직접 재현한 것은 아니다.

## 2026-09-30: 프로젝트 추가 시 터미널 배치 보존과 폴더 아이콘

- 신규 프로젝트/폴더 불러오기에서 가시 빈 칸이 없으면 1번 슬롯을 교체하던 자동 배치 경로를 수정했다. 기존 가시/숨김 배치와 pane을 유지하며 새 태스크·플랜만 선택한다. 가시 빈 칸이 있으면 그 칸에만 새 프로젝트를 열고, 명시적인 드래그의 교체/교환은 유지한다. 아카이브 목록에서 신규 프로젝트를 만들어도 일반 목록으로 전환해 새 카드를 표시한다.
- 카드의 시작/중단 오른쪽에 단색 벡터 폴더 버튼을 추가했다. 선택 프로젝트와 무관하게 카드의 경로를 Explorer에 개별 인수로 전달하며, 없는 폴더/실행 오류는 상태로 표시한다. 기존 카드 높이·지속 개선 체크박스·가로 스크롤 없는 구성을 유지한다.
- 숨김 UI 회귀 통과: 가득 찬 1분할/8분할 신규 추가, 중복 생성, 기존 폴더 로드, 숨김 배치 보존, 빈 칸 자동 배치와 기존 pane 동일성, 아카이브 탭 전환, 190 DIP 최소 사이드바에서 폴더 아이콘 위치/경계 확인. 다른 프로젝트·공백/특수문자 경로의 Explorer 실행 인수를 대체 실행기로 검사했으며 실제 탐색기 창은 열지 않았다. 기존 입력·슬롯·카드·복구 회귀도 통과했고 카드 렌더링을 확인했다.

## 2026-09-30: 환경 한정 표현 제거

- 설치 진행/미연결 안내와 모델 설정 오류 메시지를 자동 설치·OpenCode 기본값 등 일반적인 표현으로 정리했다. 코드 주석, 테스트 예시, README/작업 지침/검토 기록의 환경 한정 표현도 정리했다. 실행 설정과 동작, 기록의 사실 및 검증 한계는 유지했다.
- 제품 소스와 문서 검색, 컴파일된 실행 어셈블리의 문자열 검사를 완료했다. 제3자 라이선스의 저작권자 표기는 보존했다. Release win-x64 빌드는 경고·오류 0이며 문구 변경을 위해 새로운 동작 테스트를 추가하지 않았다.
## 2026-09-30: CMD 이어가기 시작 확인과 실패 후 재시도

- CMD 스크립트가 OpenCode 실행 전에 ready를 기록하고 앱이 500ms 후 성공으로 간주하던 경합을 제거했다. 외부 OpenCode에 전용 loopback 포트를 지정하고, 동일 세션 ID·프로젝트 응답과 활성 세션 잠금을 확인한 경우에만 시작 성공을 반환한다. 시작 완료 전 종료/실패, 잘못된 세션/프로젝트는 성공 처리하지 않는다.
- 앱 pane의 종료와 외부 실행 성공을 분리했다. 실제 시작 확인 후에만 WasContinuedExternally를 설정하며, 실패하면 상태를 표시하고 이미 검증한 세션 ID로 CMD를 다시 시도한다. 내장 서버가 먼저 종료된 경우에도 성공한 native 검증으로 보존된 root session만 사용할 수 있고, 미검증 candidate를 추정하지 않는다. 프로젝트에 외부 CMD가 이미 있으면 중복 열기를 차단한다.
- 외부 시작은 총 60초 이내에서 확인하고 개별 느린 조회는 재시도한다. 실패/시간 초과/취소 시 이번에 만든 launcher의 프로세스 트리만 종료하고 종료 결과를 확인한다. 기존 외부 CMD는 종료하지 않으며 정리 실패를 성공처럼 보고하지 않는다. 인계 중 문서 저장·탭 소유권·앱 종료 보호를 유지한다.
- 검증 통과: ExternalTerminalStartup의 사전 취소 시 프로세스 미생성, 느린 정상 시작, 지연 실패, 개별 2초 조회 시간 초과 후 재시도, HTTP 401 즉시 거부, 세션/프로젝트 불일치, 총 시간 초과·취소 후 해당 helper PID 종료·세션 잠금 해제. 숨김 WPF UI에서 실제 테스트용 ConPTY 종료 후 미확정/실패/재시도/확정 상태 전환, 잘못된 세션 거부, 내장 서버 종료 뒤 검증된 ID 보존 및 미검증 ID 거부를 확인했고 기존 입력/슬롯/카드 회귀도 통과했다.
- 서비스 구현과 별도 인계 경로 검토는 Luna 작업자가 수행했다. 모의 HTTP endpoint와 숨김 helper/대기 셸을 이용했으며, 가시 CMD에서 실제 모델 작업 출력을 확인하지 않았다. 과거 ExternalTerminal 전체 suite의 가시 창·3역할·API 없는 probe 전제는 현재 싱글/실제 API 확인 경로와 달라 실행하지 않았고 새 focused 검사를 사용했다.
- Release win-x64 배포 성공, 빌드/배포 DLL SHA-256 일치: `5C1389EFD245CE53DD9AC53245806582A7788D49FBC27C317AFCEA7E2E15A89A`. 새 테스트 프로젝트 복원으로 빠진 RID 자산은 win-x64 복원 후 재배포해 해결했다. 테스트의 NU1900은 네트워크 제한에 따른 취약성 정보 조회 경고였고, 마지막 RID 복원은 NuGetAudit=false로 오프라인 캐시를 사용했다.

## 2026-09-30: 재요청·재시작 횟수 제한 해제

- 최신 사용자 지시에 따라 토큰 절약 목적의 자동 continuation 3회 제한과 사용자 작업당 프로세스 재시작 1회 제한을 제거했다. 자동 진행/지속 개선이 켜진 미완료 작업은 1분·3분·이후 최대 10분의 재요청 간격을 유지한다. 새 프로세스에서도 다시 정체가 확인되면 기존 5분 표시·15분 취소·취소 후 2분 확인 절차로 복구한다.
- 사용자 중단·종료 예약·승인/질문·실행 도구·하위 작업·플랜/요청 변경 보호와 같은 controller의 중복 재시작 차단은 유지했다. 전송 결과 또는 재시작 성공을 확인하지 못한 경우의 확인 필요 상태는 횟수 제한과 구별해 유지한다.
- 복구 prompt를 원래 지시와 분리해 재시작을 거듭해도 복구 안내문이 누적되지 않게 했다. 프로그램 전송을 터미널 키 입력처럼 여러 줄로 분해하던 경로도 수정해 사용자 요청 전체를 보존한다. 전송 이후 다른 요청이 입력됐으면 이전 원문으로 덮어쓰지 않는다.
- Luna 작업자가 정책과 정책 테스트를 수정하고 원문 보존 흐름을 별도 검토했다. OpenCodeRecovery standalone 통과(7회 반복 재시도·10분 backoff·승인/도구·미확인 응답 보호). AutoResumeController 통합 93개 assertion 통과(8회 continuation, 반복 후 승인/중단, 3개 연속 replacement session 복구·원문/동일 prompt 보존). PlanDispatch 검사에서 Enter 전·후 두 관찰 경로 모두 여러 줄 원문 보존 확인. 숨김 WPF UI 회귀 검사와 테스트용 실제 ConPTY 종료 검사 통과.
- 모의 API 및 테스트용 셸로 검증했으며 실제 모델 호출이나 가시 창 테스트는 실행하지 않았다.
- Release win-x64 배포 성공, 빌드/배포 DLL SHA-256 일치: `C0F98E944405226B13E663D1C24DA4D2981AF09C2CEF257C2DB16C62FCE556D7`. 컴파일 오류 없이 NuGet 취약성 정보 조회 실패 NU1900 경고만 남았다.

## 2026-09-30: 장시간 정체 감지와 취소·프로세스 재시작

- 싱글 카드의 녹색을 현재 터미널 활동에 연결했다. 미완료 작업이 있다는 이유만으로 idle을 녹색으로 표시하지 않으며, 정체/복구는 중성색·별도 문구, 승인/질문은 주황을 유지한다. 종료한 프로세스의 캐시된 busy 상태를 무시하고 활동 변경 시 카드도 갱신한다.
- 현재 요청의 응답·추론·도구 내용 지문을 정체 타이머에 사용하고 todo 완료/완료 도구의 진전 지문 및 재시도 예산과 분리했다. 같은 busy가 5분 지속되면 정체 의심, 15분이면 native abort, 취소 후 2분 더 동일한 busy이면 앱 소유 ConPTY 프로세스 종료를 기다린 뒤 새 싱글 터미널에서 저장된 플랜과 원래 요청을 전달한다. todo 작성 전에 멈춘 정확한 현재 사용자 요청도 복구한다.
- 재시작은 명시적 사용자 작업당 최대 1회다. 현재 파일부터 확인하도록 요청하며 기존 파일·OpenCode 기록을 삭제하지 않는다. 승인·질문·도구 실행·하위 작업·새 사용자 요청·플랜 변경·사용자 중단을 최종 재조회와 전송 직전 소유권 검사로 보호한다. 실패 시 재시작 루프를 만들지 않고 확인 필요 상태로 둔다.
- 긴 턴의 최근 50개 메시지가 전부 assistant여서 현재 요청을 잃는 문제를 수정했다. 모든 응답의 명시적 parentID와 세션이 일치할 때만 정확한 부모 사용자 메시지를 조회하고 반환 ID·세션·역할·parts를 검증한다. 모호한 이전 요청을 추정해 재전송하지 않는다.
- 검증 통과: OpenCodeRecovery 정책, OpenCodeRecoveryClient의 활동 변화·최종 abort preflight·50개 응답/부모 검증, AutoResumeController 통합 76개 assertion(무 todo 정체/취소, 취소 무응답 재시작 1회, 승인 등장·사용자 중단), OpenCodeStatus, NativeAutomationPane. 숨김 WPF UI에서 색상·종료 상태 및 기존 입력 탭/카드/분할/사이드바 회귀 검사를 통과했다. 실제 ConPTY에 테스트용 대기 셸을 띄워 소유권/최종 조회 실패 시 생존과 허용된 종료 후 프로세스 종료 대기를 확인했다.
- 클라이언트 변경과 별도 코드 검토는 Luna 작업자가 수행했다. 모의 native API 및 테스트용 셸 검증이며, 설정된 제공자/모델의 실제 정체 복구와 새 OpenCode의 실제 모델 응답까지 검증한 것은 아니다. 실행 중인 도구 또는 하위 작업·연결 불명 상태를 강제 종료하는 복구는 하지 않는다.
- Documents 회귀 검사도 통과했다. 최종 읽기 전용 검토에서 소유 프로세스 종료 완료 → 실패 pane 교체 → 새 pane의 소유권/플랜 확인 → 재전송 경로를 확인했다. Release win-x64 배포 성공 및 빌드/배포 DLL SHA-256 일치: `C49FEB7399DDD572AA13A998721FA2AD13233ED2E4AE89CCF3A9A8EE6746657C`. 컴파일 오류는 없고 네트워크 제한으로 NuGet 취약성 정보 조회 NU1900 경고가 남았다.

## 2026-09-30: 입력창의 플랜·질문 탭

- 전송 버튼 옆 드롭다운을 제거하고 플랜 문서 아래 입력창에만 플랜·질문 탭을 배치했다. 위쪽 MD 편집기는 계속 표시하며 입력 초안은 탭 전환 시 유지한다. 탭 선택만으로 전송하지 않고 기존 버튼/Ctrl+Enter를 사용한다.
- 프로젝트별 입력·선택한 탭·저장된 요청 재시도 식별자를 유지한다. 플랜 저장 후 전송과 질문의 MD 비저장 처리는 변경하지 않았다. 전송/문서 저장 중에는 두 탭을 비활성화한다.
- 숨김 WPF UI 검사 통과: 탭 전환, 공유 초안/프로젝트 전환, 문서 편집 영역 분리, 드롭다운 제거, 저장 재시도 식별자 보존, 전송 중 탭 잠금, 240 DIP 입력 영역 배치. 실제 렌더링으로 두 탭과 입력/전송 버튼을 확인했다. 기존 카드/슬롯/드래그/사이드바 검사도 통과했다. 전체 UI의 이전 메뉴 assertion도 갱신했으며 가시 창 및 모델 호출 검사는 실행하지 않았다.
- 별도 Luna 읽기 전용 검토 완료. Release win-x64 배포 성공 및 빌드/배포 DLL SHA-256 일치 확인. NuGet 취약성 조회는 네트워크 제한에 따른 NU1900 경고가 남았다.

## 2026-09-30: 단일 시작/중단 버튼 · 지속 개선 체크박스

- 카드의 시작·중단을 상태에 따라 전환하는 버튼 하나로 합치고 터미널 열기 버튼을 제거했다. 지속 개선은 카드에 체크박스 하나로 직접 표시하며, 카드/터미널의 ⚙ 팝업과 상태 줄의 중복 표시를 제거했다. 기존 종료 예약은 카드 우클릭 메뉴에 유지하고 예약 시각은 툴팁과 메뉴에 표시한다.
- 플랜 로드/저장부터 시작 취소를 허용하고 중단 저장 중에는 재클릭·재시작을 막는다. 별도 Luna 검토에서 발견한 이전 완료 표시가 재시작 준비 중에 남는 문제를 수정해 버튼·상태·진행 그룹을 일치시켰다.
- 숨김 WPF UI 회귀 검사 통과: 동일 버튼 전환과 실제 중단 핸들러의 시작 취소, 세션 보존, 중단 저장 경합 보호, 외부 CMD 이관 차단, 프로젝트별 체크박스 저장·해제·종료 예약 표시. 기존 전송 방식·고정 슬롯·드래그 복원·사이드바 접힘 검사도 통과했다. 최소 크기 카드 렌더링에서 체크박스와 버튼을 확인했다. 가시 창/실제 마우스/실제 모델 호출은 사용하지 않았다.

- Release win-x64 빌드·배포 성공, 배포 DLL과 빌드 DLL의 SHA-256 일치 확인. 네트워크 제한으로 NuGet 취약성 조회 NU1900 경고만 남았으며 컴파일 오류는 없다.

## 2026-09-30: OpenCode 설치 검사 중 16비트 호환성 오류

- Windows Application 로그의 Wow64 Emulation Layer 1109가 `npm/node_modules/opencode-ai/bin/opencode.exe`를 지목했다. TerminalNotes 배포 실행 파일은 PE32+ x64이고, 문제의 OpenCode 파일은 479바이트 postinstall 미실행 안내문이었다. npm CMD/PowerShell shim을 먼저 실행하면서 이 안내문을 Windows 실행 파일로 호출한 것이 오류 창의 원인이었다.
- 알려진 npm shim의 상대 실행 대상과 정확한 안내문 marker를 확인해 프로브 전에 건너뛴다. CMD/PS1 중 하나만 있어도 기존 native payload를 검사할 수 있다. 정상 사용자 wrapper의 우선순위와 패키지 이름/manifest 버전/실제 버전 일치 검사는 유지한다. 직접 EXE 검사는 PEReader로 헤더를 확인한 뒤에만 프로세스를 만든다.
- 외부 npm 파일, 레지스트리, 호환성 설정은 수정하지 않았다. 본체 경로는 현재 앱 프로세스의 PATH에만 반영한다.
- Installation 회귀 검사 통과: 실제 상대 경로 CMD/PS shim 제외, 단독 CMD의 정상 payload 선택, payload 없는 설치 실패, 정상 사용자 wrapper 우선, 패키지명/버전 불일치 거부, 텍스트/잘린 MZ/16비트 NE 이미지의 프로세스 시작 전 거부, 실제 CMD/PS 버전 프로브 및 취소.
- 실제 설치된 OpenCode 1.18.33의 `--version` 확인 성공, 같은 검사 구간의 새 Wow64 호환성 이벤트 0건. 제한된 실행 환경에서 기본 사용자 설정 경로 쓰기가 차단되므로 검사 프로세스의 XDG config/data/state/cache만 임시 폴더로 격리했다. 모델 요청이나 설치 스크립트 실행은 하지 않았다.

## 2026-09-30: 사이드바 접기 · 기존 터미널 이동 · 프로젝트 설정

- 태스크 사이드바 접기/펼치기를 추가했다. 접으면 목록과 구분선이 차지하는 폭을 터미널에 돌려주며 상단 펼치기 버튼은 남는다. 조절한 폭과 접힘 상태는 기존 설정에 추가 저장하고, 프로젝트 선택/목록/분할 변경 시 접힌 상태를 유지한다.
- 기존 터미널 제목의 빈 공간도 드래그를 받도록 투명 배경과 ↕ 표시를 추가했다. 숨겨질 터미널 대신 메인 창이 OLE 드래그 소유자가 되며 종료·취소·예외에서 뷰를 복원한다. 슬롯 이동은 세션 재시작 없이 자리 교환한다.
- 지속 개선과 종료 예약을 공통 상단에서 프로젝트별 ⚙ 메뉴로 옮겼다. 카드와 터미널의 ⚙는 동일한 프로젝트의 기존 체크박스/예약 동작을 공유하며, 카드 상태에는 활성 여부와 예약 시각을 표시한다. 패널을 접어도 터미널에서 설정할 수 있다.
- 숨김 WPF UI 검사 47개 통과. 실제 헤더/호스트 구성, 슬롯 이동/취소/실패 복원, 사이드바 폭 회복과 설정 저장, 두 프로젝트의 지속 개선 설정 분리/저장/예약 표시를 검증했다. OLE OS 루프는 검사 콜백으로 대체했으며 물리 마우스 드래그·표시된 설정 팝업·실제 모델은 이번 검사에 포함하지 않았다.
- 별도 Luna 작업자의 읽기 전용 검토에서 중요한 상태/세션/입력/가시성 문제는 발견되지 않았다. Release 빌드 성공. NuGet 연결 제한에 따른 NU1900 경고는 취약성 조회 실패이며 컴파일 오류는 없었다.

## 2026-09-30: 전송 메뉴와 고정 터미널 슬롯

- 플랜/질문 탭 대신 공통 입력창과 전송 방식 메뉴를 사용한다. 메뉴 선택은 전송하지 않으며 버튼/Ctrl+Enter로 선택한 방식대로 보낸다. 프로젝트별 공통 초안과 선택, 저장된 요청의 재시도 ID를 유지한다. 플랜 추가 전 저장·충돌 검사와 질문 비저장 경로는 보존한다.
- 1/2/3/4/6/8개의 고정 슬롯과 항상 표시되는 태스크 사이드바를 구성했다. 카드나 터미널 제목을 끌어 배정하며 이미 배정된 태스크는 자리 교환, 미배정 태스크는 대상 칸만 교체한다. 빈 칸의 선택 메뉴도 제공한다. ×는 슬롯만 비우고 실행 중인 프로세스와 세션은 유지한다. 축소 시 숨긴 배치와 재확대 시 위치를 보존한다.
- 카드는 이름·상태·실행/중단/열기로 간소화하고 역할/모드/시간 칸을 숨겼다. 지속 개선과 종료 예약은 선택한 열린 프로젝트 위에서 설정한다. WebView2의 네이티브 화면이 WPF 드롭 대상을 가리지 않도록 드래그 중 화면만 숨기고 finally에서 복원한다.
- 숨김 WPF UI 검증 31개 통과: 전송 메뉴/공통 초안/프로젝트 전환/재시도 식별자, 슬롯 배정·교환·교체·비우기, 분할별 드롭 대상 수, 숨겨진 배치와 TerminalPane 객체 보존, 드래그 화면 숨김/복원, 62 DIP 카드와 가로 스크롤 없음. Documents, TabDocuments, InputJournal 검증도 통과했다. 기존 전체 UI 검증의 오래된 카드 크기/가로 스크롤 기준은 갱신했으나 가시 창 전체 테스트는 재실행하지 않았다.
- 사용 가능한 최신 Luna 작업자가 데이터/세션 수명과 드래그 이벤트를 별도로 읽기 전용 검토했고 필수 수정 사항은 없었다. 물리 마우스 드래그, 실제 WebView 화면 조작, 실제 모델 호출은 이번 검증에 포함하지 않았다.
- Release win-x64 배포 성공. NuGet 연결 제한으로 취약성 조회 NU1900 경고가 있었으며 패키지 취약성 조회 성공을 주장하지 않는다. 캐시된 종속성으로 컴파일과 게시를 완료했다.

## 2026-09-28: 터미널 추가 입력 저장과 재개 기준

- OpenCode native `chat.message`에서 접수된 루트 세션의 사용자 원문을 `terminal-notes/terminal-inputs.md`에 누적한다. 한글/여러 줄/Markdown fence를 보존하고 메시지 ID로 중복을 판별한다. 자동 지휘/재개/플랜 프롬프트, synthetic/ignored 텍스트, 하위 에이전트 요청, 화면 출력과 미전송 키 입력은 추가 요청에 섞지 않는다. 파일에 한 번의 append write 후 fsync를 수행하며 실패는 native 오류와 toast로 알린다. 원본 플랜과 편집기 초안은 수정하지 않는다.
- 기존 OpenCode 설정/플러그인을 보존하며 프로세스 설정에 로컬 기록 플러그인을 추가한다. 모듈은 내용 해시 경로에 보존하므로 앱 자원 종료/업데이트 뒤에도 CMD에서 사용할 수 있다. 프로젝트의 OpenCode 설정 파일은 변경하지 않는다. 과거 세션의 입력을 소급 복사하지 않는다.
- 일반 세션 지침, MD 실행, 모든 자동화 역할, 자동 재개 요청에 현재 코드/변경분/기존 결과/todo/추가 입력을 먼저 대조하고 정상 구현을 보존해 남은 작업과 확인된 결함만 처리하도록 명시했다. 입력 기록은 완료 근거가 아니며 과거 요청을 순서대로 재실행하지 않는다. 기존 실행 ID 유지와 미확정 구현의 검토 우선 복구는 유지한다.
- Node 검사 통과: 원문/코드 블록/의도적인 같은 내용 재입력/메시지 중복/자동 요청 및 하위 세션 제외/저장 실패 표시, 독립 4개 프로세스의 큰 한글 입력 40건 동시 append 무손실.
- 실제 OpenCode 1.18.33에서 provider를 호출하지 않는 noReply API로 입력을 접수해 MD 저장과 자식 세션 제외를 확인했다. 원본 서버/임시 지침 종료 뒤 프로덕션 CMD 설정으로 같은 세션을 다시 열어 이전 대화 유지와 새 입력 저장을 확인했다. fixture: `TerminalNotes Native Journal 2e88ee13111e453dbe91505fce8e3145`. 테스트의 초기 npm 레지스트리 대기는 격리 환경의 registry를 닫힌 loopback 주소로 설정해 해소했으며 제품 환경 변수는 변경하지 않았다.
- AutoResumeController 57 assertions, TabDocuments 충돌/플랜 보존/실행 프롬프트, AutomationCommand, Terminal의 설정 병합/플러그인 중복 방지/Unicode ConPTY 환경 검사 통과. 실제 모델의 반복 구현 방지 성능까지 검증한 것은 아니다. UI 구성 변경은 없다.
- 지정된 gpt-5.6-luna가 런타임에 없어 별도 작업자 검토는 하지 않았으며 다른 모델로 대체하지 않았다. 데이터 보존/앱 종료 뒤 모듈 수명/프로젝트별 기록 경로를 직접 검토하고 native 검사로 확인했다.
## 2026-09-28: 독립 CMD 이어가기

- 멀티의 지휘/검토/구현과 싱글 터미널 제목에 CMD 버튼을 추가했다. 현재 작업에서 확인한 원본 OpenCode 세션 ID와 프로젝트 경로로 독립 cmd/OpenCode를 열며, 최근 세션 추측이나 프롬프트 재전송을 사용하지 않는다. 상위 세션 조회는 localhost와 프로젝트 경로를 검증한다.
- 플랜 초안을 기존 충돌 검사로 저장하고, 프로젝트의 앱 실행을 Stopped로 기록한 뒤 내장 ConPTY 프로세스의 종료를 기다린다. 다른 역할의 세션 ID도 먼저 보존한다. 외부 cmd는 앱의 ConPTY job과 Dispose 대상에 포함하지 않는다. 지속되는 플랜 지침과 기존 승인 모드를 전달하며 멀티 자동 순환은 중단한다.
- 세션별 프로세스 잠금으로 중복 CMD와 외부 사용 중 앱 재실행/재개/아카이브 이동을 막는다. 앱 재시작 뒤에도 파일 존재 대신 잠금 소유 여부를 확인한다. 외부 작업 중 완료색을 표시하지 않고 상태 설명은 CMD 이어가기로 제공한다.
- 새 프로젝트 선택에서 이름 갱신이 플랜 생성보다 먼저 비동기 읽기를 시작하던 순서를 고쳤다. 존재하지 않았던 파일의 snapshot으로 첫 저장을 시도하면서 발생한 잘못된 외부 변경 충돌을 해소했다.
- ExternalTerminal 검사 통과: 실행기 프로세스 종료 뒤 외부 CMD 작업 지속, 특수문자/한글 경로, 원본 플랜과 지속 지침, 같은 세션 중복 차단/잠금 해제, 실제 OpenCode 세션 조회와 경로 불일치 차단, 싱글 CMD 버튼의 저장/중단/종료 확인, 지휘/검토/구현 각각의 원본 세션 인계 및 앱 자원 종료 후 3개 외부 세션 유지. 데이터/설정/브라우저 프로필은 임시 폴더에 격리했고 실제 모델 제공자 요청은 보내지 않았다.
- 현재 제한된 환경에서 WebView 렌더러가 시작 중 실패해 전체 화면 검사는 완료하지 못했다. `TERMINALNOTES_TEST_NO_WEBVIEW=1` 검사에서는 WPF 컨트롤 이벤트와 실제 ConPTY/OpenCode 서버 및 별도 CMD를 사용하고 내장 렌더러만 제외했다. 이전의 독립 WPF 창 분리 구현은 포함하지 않는다.
- AutoResumeController 56개 검증, NativeAutomationPane, Terminal/ConPTY 회귀 검사 및 Release 빌드 통과. 지정한 gpt-5.6-luna 모델이 제공되지 않아 별도 에이전트 검토는 수행하지 못했으며 종료/저장/재개 경로를 직접 검토했다.
## 2026-09-28: 아카이브 · 목록 종료 시간 · 동작 버튼

- 메인에 태스크/아카이브 탭을 추가했다. 행 우클릭으로 프로젝트 전체를 작업 폴더의 `archive`로 이동하고, 아카이브의 복원 메뉴로 상위 작업 폴더에 되돌린다. 원문 코드·Git·플랜·실행 기록을 보존하며 이동 메타데이터로 실행 기록의 현재 프로젝트 경로만 연결한다. 아카이브 안에서도 플랜 저장·터미널 열기·기존 실행 재개를 사용할 수 있다.
- 실행·예약·승인·미전송 입력과 문서 저장 중 이동을 막는다. 플랜 초안은 기존 충돌 검사를 거쳐 저장하고, 같은 이름의 대상은 덮어쓰지 않는다. 해당 프로젝트의 유휴 세션만 닫고 새 위치에서 다시 열며 다른 프로젝트는 유지한다. 이동 중 종료를 막고 오래된 자동화 읽기가 원래 경로를 재생성하지 않도록 했다.
- 목록의 고정 시간 칸에서 기존 지속 개선 종료 시간 폼을 연다. 실행 중 설정 변경은 dispatch token/revision과 목록 화면을 유지한다. 오른쪽 버튼은 M/S, ▶/↻, ■, ↗로 통일하고 중단 기호는 붉은색으로 구분했다. 한 줄 높이·고정 위치·접근성 이름·툴팁을 유지한다.
- ProjectArchive 검사 통과: 폴더 실이동과 복원, 바이너리/Git/원장 원문 보존, 아카이브 경로의 기존 run 재개와 claim, 실행 중 복원 거부, 충돌 시 양쪽 폴더 보존, UI 초안 저장, 두 탭 분리, 아카이브 터미널과 플랜 저장, 탭 전환 시 세션 유지, 외부 MD 충돌 보존, 이름/모드 재실행 복원.
- Automation과 AutomationProgress 검사, 로컬 임시 폴더에서 실행한 전체 UI 검사 통과. 기존 제목 편집·7:3 크기 조절·아이콘 고정 위치·트레이·프로젝트 세션 보존도 확인했다. 아카이브의 자동화 재개는 격리된 원장/claim 검사이며 실제 모델 제공자 요청은 실행하지 않았다. 지정된 gpt-5.6-luna 모델이 없어 별도 에이전트 검토는 수행하지 못했다.

## 2026-09-28: 태스크 제목 편집 복원

- 단일 행 태스크의 제목 버튼에 빠져 있던 편집 진입점을 연결했다. 더블클릭·F2·우클릭 제목 변경으로 인라인 편집하며 Enter 또는 다른 곳 클릭은 저장, Esc는 취소한다. 빈 제목은 저장하지 않는다. 상세 화면의 프로젝트 제목도 더블클릭으로 편집한다.
- 기존 프로젝트 표시 이름 설정으로 저장하므로 폴더·플랜 경로·실행 세션·목록 정렬은 유지한다. 프로젝트 기본 제목을 쓰는 싱글 터미널은 함께 갱신하고 사용자가 별도로 바꾼 터미널 제목은 보존한다.
- Release 빌드와 로컬 임시 폴더의 전체 UI 검사 통과. 실제 연결된 F2·더블클릭·메뉴 이벤트, Enter·Esc·빈 제목, 포커스 이동 저장, 상태 갱신 중 입력 유지, 기존 pane/플랜 보존 및 재실행 후 표시 이름 복원을 확인했다.

## 2026-09-28: 토큰 단위 표시 통일

- 터미널 제목·선택 목록·플랜 하단·상태 모니터·실행 기록 요약의 토큰 수를 공통 K/M/B 포맷으로 표시한다. 최대 소수 두 자리이며 1,979,402는 1.98M이다. 반올림으로 1000K가 되면 1M으로 올린다. 원래 정수 집계와 저장 값은 유지한다.
- Release 빌드(경고·오류 0)와 기존 InputCapture/토큰 집계 검사를 통과했다. WorkflowIntegration의 표시 기대값도 새 형식으로 맞췄으며, 이 변경에서는 전체 자동화 통합 검사를 다시 실행하지 않았다.

## 2026-09-28: 작업 중 자동 절전 방지

- 진행·준비·예약 중인 멀티 작업과 미완료 싱글 작업을 합산해 Windows SystemRequired/ExecutionRequired 요청 하나를 유지한다. 승인 대기·역할 전환·짧은 idle·지속 개선 간격·트레이 숨김에서도 유지하며, 마지막 작업 종료와 앱 종료 시 핸들을 해제한다. 유휴 터미널만 열려 있으면 요청하지 않는다.
- 화면 켜짐이나 잠금 방지 요청은 등록하지 않으며 전원 설정을 변경하지 않는다. 수동 절전과 Windows 전원 정책을 우회하지 않는다. 복귀 이벤트에서는 남은 작업의 요청을 새로 생성한다. 등록 실패는 상태·진단에 남기고 30초 간격으로 재시도한다.
- Release 빌드·배포 및 AutomationProgress 검사 통과. SleepPrevention 검사에서 실제 Windows 요청 생성·핸들 해제, 여러 태스크 합산, 중단·완료·일시정지, 싱글의 idle·승인 대기, 복귀 이벤트와 실패 재시도를 확인했다. 배포 DLL로도 SleepPrevention 및 전체 UI 검사를 통과했다.
- UI 검사 최초 두 실행은 버전 출력이 ConPTY까지 도달했지만 WebView 화면에 반영되지 않아 시간 초과됐다. 같은 검사 파일을 로컬 임시 폴더로 복사해 기존 버전과 새 버전, 최종 배포 DLL 모두 전체 통과했다. 화면 지연의 원인을 확정하거나 수정했다고 주장하지 않는다.
- 비관리자 실행 환경에서 powercfg /requests 목록 검사는 건너뛰었으며 네이티브 API 성공과 실제 핸들 수명은 별도로 확인했다. 물리적인 장시간 잠금·절전 및 배터리 Modern Standby 실험은 수행하지 않았다. 지정된 gpt-5.6-luna 작업자 모델을 사용할 수 없어 독립 에이전트 검토는 수행하지 못했다.

## 2026-09-24: 태스크 제어 · 간결한 화면 · 실행 복구

- 상단은 플랜·상태 모니터로 정리하고 자동화 버튼을 제거했다. 태스크 행의 주 버튼은 상태에 따라 실행/이어서 실행/새 실행으로 바뀐다. 진행 중에는 일시정지·중단, 이력이 있으면 기록을 제공한다. 불필요한 설명은 툴팁과 기록 화면으로 옮겼다.
- 최종 검토 근거가 저장된 실행에만 태스크 완료 배지를 표시한다. 완료 반영은 감시 타이머만 기다리지 않고 실행 목록 갱신과 함께 수행한다. 터미널 제목 크기와 작은 활동 그림의 실제 렌더링 크기를 조정했다.
- 중단·실패 실행을 명시적으로 재개할 때 실행 ID·계획·토큰·검토 근거를 보존한다. 미확정 구현은 먼저 검토한다. 플랜 초안을 충돌 검사 후 저장하며 빈/누락 플랜과 동시 실행은 거부한다.
- 앱 시작 시 이전에 승인한 미완료 실행·예약을 자동 복구한다. 수동 중단·일시정지·단계 후 일시정지 요청은 유지한다. 실행 소유권이 사라진 Running도 토큰·revision 비교 및 원자적 상태 전환으로 재예약한다.
- 실제 OpenCode 점검에서 중간 지휘 요청에 상세 계획이 빠져 원본 plan.md에 상세 계획을 쓰라고 오판하는 경로를 확인했다. 구조화된 지휘 계획, 실제 승인 상태, 원문 보존 계약을 중간 점검에도 전달했다. 연결 복구 후 이전 연결 오류 진단도 해제한다.
- Automation, Documents, UI, PlanExecution, LayoutCompact, Startup, OpenCodeStatus, AutomationProgress와 WorkflowIntegration --projects-only 검사를 통과했다. WorkflowIntegration은 격리된 모의 native 서비스이며 실제 제공자 검증과 구분한다.
- 실제 설치 OpenCode 1.18.32 / 설정된 기본 모델(opencode/big-pickle)로 별도 계산기 프로젝트를 완료했다. 같은 실행 ID의 수동 재개와 구현 도중 격리된 테스트 호스트 종료 후 수동 명령 없는 자동 복구를 확인했다. 복구 전후 플랜 해시와 실행 ID가 같으며 부분 구현은 Reviewer/Verification으로 넘어갔다. 테스트용 앱·프로젝트만 사용했다.
- 독립 브라우저 검사에서 키보드 음수 입력 오류를 발견해 검증용 플랜에 사실 그대로 추가했다. 지휘자의 실제 INTERVENE 요청 → 정확한 구현 세션 중단 → 검토자의 반려 → 지휘자의 수정 지시 → 재구현을 확인했다. 기존 실패 기록을 보존했으며 응답·완료 결과를 주입하지 않았다. 최종 역할 요청 18건의 dispatch 중복 전송은 0건이었다.
- 수정 후 독립 실행한 npm test 30/30, 별도 Edge CDP 검사 21/21, 실제 표시된 Edge 창의 UI Automation 검사 5/5가 통과했다. OpenCode 자체 브라우저 검증도 13/13 통과했다. 화면과 데스크톱·360px 배치 스크린샷을 확인했다. 서비스 표시 질문에는 앱이 진행을 보류했고, 실제 창을 검사한 검증 에이전트의 관찰임을 명시해 해당 테스트 세션의 질문에 답했다. 사람 사용자의 확인이나 완전 무인 실행 성공으로 주장하지 않는다.
- 2026-09-24 13:18:31 KST에 같은 실행(6c251b79-f98a-413c-9ae4-d60d9b53b987)이 FinalVerification/Completed로 저장되고 태스크 행에 `✓ 완료`가 표시됐다. 실행 근거·최종 검토 근거도 비어 있지 않음을 검사했다. 작은 활동 그림의 크기와 하단 상태 문구 보정은 최신 배포 빌드에 반영했다.

실제 검증 폴더: `artifacts/real-project-20260924-030921-20876b1f7e4746cd8150c5c475a4bc66`.
핵심 증거: `app-crash-recovery.json`, `commander-control-proof.json`, `native-dispatch-proof.json`, `independent-review-feedback.json`, `independent-browser-proof/result.json`(수정 전 실패는 `before-fix.json`), `independent-unit-tests.txt`, `visible-browser-proof.json`, `question-answer-proof.json`, `completion-verification.json`, `task-completed.png`. 실제 실행 화면은 실행 중인 테스트 빌드의 캡처이며, 최종 아이콘 크기·좁은 화면 배치는 `tests/AutomationProgress/bin/Release/net10.0-windows/task-running-readable.png` 및 WorkflowIntegration UI 검사로 별도 확인했다.

## 2026-09-23: 실행 중 지속 개선 설정 · 종료 시간 · 가독성

- 진행 중 TASK 행은 역할 전환과 응답 대기에도 녹색 강조를 유지한다. 역할 그림과 막대 애니메이션에는 실제 응답·도구 변화가 필요하다. 승인·질문은 주황색, 중단·완료는 강조 해제로 구분한다.
- 실행 중 지속 개선 해제/재설정과 날짜·시간의 종료 예약을 저장한다. 현재 claim의 revision/token을 유지하여 진행 중 결과를 잃지 않으며, 최종 검토 완료 시 최신 옵션을 확인한다. 다음 주기 예약 중 해제/만료도 이전 검증 근거를 보존해 완료하고 새로운 claim을 만들지 않는다. 현재 개선을 강제 중단하는 시간 제한은 아니다.
- 작업 행을 제목/줄바꿈 가능한 제어/역할로 배치하고, 본문과 상태 13~14px·제목 17px·편집기/터미널 15px, 터미널 줄 간격 1.12·최소 대비 7을 적용했다. 어두운 체크박스와 비활성 문구도 읽기 쉽게 보정했다. 설정·새 프로젝트·제목 창의 글꼴도 정리했다.
- Automation 저장소 검사는 현재 최종 결과와 해제의 경쟁, 종료 시간 만료, 미래 종료 시간, 주기 사이 취소/만료, 일시정지 보존, 디스크 재읽기를 통과했다. AutomationProgress WPF 검사는 실제 폼에서 잘못된 시간 거부·실행 중 시간 적용·행 강조 유지·최소 폭의 배치를 통과했다. 실제 서비스 실행을 검증한 것은 아니다.
- UI 전체 검사는 실제 설치 OpenCode 1.18.32의 버전 명령, ConPTY 입출력, 창/트레이/설정/최소 크기 배치를 통과했다. `tests/AutomationProgress/bin/Release/net10.0-windows/task-running-readable.png`와 `improvement-deadline-dialog.png`에 UI 검사 화면을 남겼다.
- 확대된 터미널 글꼴로 TuiViewport의 실제 WebView2/ConPTY 스크롤·출력 위치 보존·작은 화면·ANSI 색상·alternate buffer 마우스 전달 검사를 통과했다. AutomationCommand의 PowerShell 5.1 실행 검사도 통과했다.

## 2026-09-23: 실제 활동 표시·서비스 장애 복구·지속 개선

- 오른쪽 패널 표시 버튼을 작업 계획으로 변경하고, 작업 설정을 최소화 바로 왼쪽의 제목 표시줄 톱니바퀴 아이콘으로 옮겼다. 실제 WPF UI 전체 검사에서 아이콘 위치·설정 열기·최소화·최대화/복원·트레이 숨김/복귀·최소 창 크기의 배치를 확인했다. OpenCode 버전 확인은 설치된 실제 1.18.32 실행 파일을 테스트 프로세스 PATH에 지정했다. 화면은 `tests/UI/bin/Release/net10.0-windows/caption-smoke.png`에 보존한다.
- 실제 실행에서 idle 역할의 미전송 입력 보호 때문에 다음 검토를 시작하지 못하는 현상을 발견했다. SGR 마우스 보고가 ambiguous 입력을 남기는 경로를 수정했고, 기존 문자 초안/혼합 입력 보호 검사를 통과했다. 수정 빌드 적용을 위해 검증용 앱을 재시작하고 같은 실행·프로젝트를 정상 재개 경로로 이어갔다. 이 재시작을 무인 자동 복구 성공으로 계산하지 않는다.
- 실제 OpenCode의 세 WebView 터미널에 OS 마우스 이동을 보내 각 2개 SGR 보고가 발생했으며, 모두 HasPendingInput=false·ambiguous=false를 유지했다. TASK 목록/실제 행 더블클릭 왕복도 실행을 유지했다. 증거: 아래 실행 폴더의 `real-mouse-input-verification.json`, `real-task-list.png`, `terminal-evidence`.
- 오른쪽 활동 그림은 같은 요청에 속한 지휘/검토 중간 점검도 반영한다. 오래된 요청·종료한 프로세스의 cached busy는 녹색으로 표시하지 않는다. TASK 지속 개선 체크박스도 공통 어두운 테마를 적용했다. 입력 차단 중에는 시작 정체 타이머를 누적하거나 프로세스를 교체하지 않는다. 해당 상태 분류·보호 검사는 통과했다.
- 중간 점검 지침에 관측 시점과 마지막 실제 변화의 경과 시간을 추가했다. busy 반복과 실제 진전을 구분하고, 서비스는 시작됐지만 실행 도구가 복귀하지 않는 상황도 현재 근거와 함께 판단하도록 했다.

- native retry가 매번 무진행 시각을 초기화하던 경로를 제거했다. 짧은 retry 유예는 유지하지만 응답/도구 변화가 없으면 정확한 요청 트리를 중단하고 idle 또는 소유 프로세스 종료를 확인한 뒤 인계한다.
- 연속 transport 장애(시간 초과·연결 실패·503) 3분과 실제 프로세스 종료를 복구 대상으로 추가했다. 일시적 장애·잘못된 응답/식별자·승인·질문·사용자 입력·중단 의도는 구분한다. 준비 단계 API 무응답도 같은 역할 프로세스를 계속 재사용하지 않도록 교체한다.
- 빈 assistant 객체와 busy 플래그는 실제 활동이 아니다. 역할 터미널과 TASK의 활동 색을 실제 응답/추론/도구 변화에 연결하고, TASK 행 전체 배경·테두리·활동 막대를 강조했다.
- OpenCode 1.18.32의 TUI select-session 성공은 이벤트 접수다. 초기 구독 경쟁과 화면 전환 실패를 완화하도록 정확한 소유 세션을 전송 후/실행 중 재연결하며 프롬프트 재전송과 분리했다.
- TASK의 지속 개선 옵션, 주기별 목표/검증 이력, 완료 주기와 다음 Planning 예약의 원자적 저장, 동일 계획 중복 차단을 추가했다. 목록 갱신과 모드 선택 수명을 분리해 비동기 시작 도중 선택이 유실되지 않는다.
- 서비스 클라이언트·저장소·NativeAutomationPane·AutomationProgress 검사와 WPF의 API 장애/실제 프로세스 종료 경로를 확인했다. 앞선 모의 서비스 검사는 상태 전이 확인이며 실제 모델 검증을 대체하지 않는다.
- 실제 OpenCode(1.18.32, 설치된 기본 제공자 opencode/big-pickle) 검증에서 검토자가 원본 plan.md에 상세 계획이 없다는 이유로 반려하는 반복을 발견했다. 원본 MD 보존과 프로그램이 별도 저장·전달하는 지휘 계획의 계약을 모든 역할에 명시해 수정했다. 해당 제공자의 인증 자격 증명은 이 환경에 없다.
- 실제 프로젝트 실행/화면/복구 검증 결과는 아래와 같다. 이 실행은 최종 검증/두 번째 개선 주기 전에 중단되었으며 전체 무인 주기 성공으로 보고하지 않는다.

실제 실행 폴더: `artifacts/real-project-20260923-023537-7965cb2b244e423a94edd5894a921e0b`.

- 실제 구현 OpenCode PID 19848의 경로·정확한 세션·포트를 검증한 뒤 그 프로세스만 종료했다(`injected-failure.json`). 앱은 부분 검토 → 지휘 → 새 구현 프로세스로 자동 복구했고 계산 엔진을 생성했다. 원본 프로젝트/파일을 다시 작성하거나 모델 결과를 주입하지 않았다.
- 생성된 엔진을 별도로 `npm test` 실행해 17/17을 확인했다. 모델이 생성·시작한 HTTP 서비스에서 별도 Edge CDP로 사칙연산·우선순위·소수·단항 음수·0 나눗셈·클릭·키보드·지우기/재입력 19/19 기능 검사가 통과했다. 최초 브라우저 종합 결과는 `/favicon.ico` 404 때문에 `ok=false`이며 이를 통과로 계산하지 않는다. `browser-verification/result.json`, `browser-verification/calculator-19.png`에 근거를 보존한다.
- 서버 생성 뒤 실제 Start-Process 도구 호출이 복귀하지 않는 자연 발생 정체가 관측됐다. 10분 무진행 후 프로그램이 정확한 세션을 abort했고 native 상태는 idle로 바뀌었지만 도구는 running으로 남았다. 이 경우 ActiveSessionIds가 비어 프로세스 교체가 영원히 차단되던 조건을 수정했다. 처음부터 idle+running tool인 경우에도 정확한 root abort를 한 번 시도하고 기존 유예·소유권·입력 보호·종료 확인을 적용한다. 두 경로의 회귀 검사와 승인 대기 보호는 통과했으며 수정 후 동일 실제 장애의 자동 재현은 아직 확인하지 못했다.

# 검토 및 검증 기록

## 2026-09-23 앱 재실행 시 자동화 잠금 충돌

- 앱 진입점은 중복 창을 허용하면서 사용자 스케줄러는 하나의 `.automation.lock`을 독점하여, 두 번째 창에서 지휘 실행이 차단되던 경로를 수정했다. 설정 경로별 `.instance.lock`의 OS 소유권과 현재 사용자 전용 named pipe를 사용해 기존 창을 복원한다. 숨김/최소화 복귀와 `--workspace` 전달을 지원하고, 기존 프로세스가 종료 중이면 잠금 획득도 다시 시도한다. 자동화 소유권과 실행 중 작업은 그대로 유지한다.
- 잠금파일은 삭제하지 않는다. 정상/비정상 종료 시 OS 핸들이 해제되면 남은 파일을 그대로 재사용한다. 실제 sharing violation과 접근/권한/파일 오류의 안내를 분리했다.
- AppInstance 검사는 별도 실행 프로세스의 숨김 창 복원, 기존 스케줄러 identity, 한글/공백 경로 전달, 잘린 요청 뒤 복구, 정상 종료 후 재실행, 테스트 자식 프로세스 강제 종료 후 잠금 회수, 접근 오류 구분을 통과했다. PlanExecution의 실제 잠금 충돌·소유권 재획득 후 지휘자 활성화 검사와 Startup 회귀 검사도 통과했다. 실제 CLI·모델 호출 없이 검증했다.

## 2026-09-23 프로젝트 TASK · 지휘 중간 개입 · 스크롤

- 작업 탭에 설정 폴더의 직계 프로젝트를 읽기 전용으로 열거하고, 각 행에 지휘·검토·구현 상태/활동 그림과 프로젝트별 실행·중단을 추가했다. 더블클릭/Enter의 3열 화면과 목록 복귀는 같은 pane을 유지한다. 숨겨진 측정 호스트에서 명시적으로 열린 역할도 준비되며 자유 분할 UI를 숨겼다. 역할 팀은 전역 8세션 제한에서 분리했다.
- team/stage/start/dispatch/pending/polling/시작 취소 의도를 프로젝트별 런타임으로 분리했다. 서로 다른 프로젝트의 시작·관찰은 병행한다. 프로젝트 목록 저장 쓰기를 직렬화하고 오래된 ledger 읽기가 새 revision이나 일시정지 의도를 덮지 않도록 검토했다. 프로젝트 중단과 native abort/프로세스 복구는 자기 프로젝트·요청만 대상으로 한다.
- 구현 중 검토자와 지휘자, 검토 중 지휘자가 실제 native 요청으로 읽기 전용 중간 점검한다. 첫 점검 5초, 완료 후 20초 간격이며 단계 결과와 진행 증거를 전달한다. Coordinator의 명시적인 INTERVENE만 중단 경로를 연다. 승인·질문·retry·소유권·수동 중단과 idle 확인을 우회하지 않는다. 중간 점검 완료 전에 역할 pane을 다음 단계로 재사용하지 않는다. 구현은 작은 변경 단위를 마친 즉시 검토로 돌리도록 지시를 강화했다.
- 우측의 계획·검토·지시·전환 이력·로그 버튼·완료 요약 배너를 없앴다. 최근 실제 assistant/tool 변화만 녹색 활동 그림을 움직이고, busy 반복·watchdog grace는 활동으로 보지 않는다. 승인 대기는 주황색 정지 아이콘, 그 외는 회색, 완료는 체크다. 데이터와 진단 로그는 유지한다.
- 통합 세로 스크롤바를 보이는 오른쪽 끝에 고정했다. normal 기록/작은 TUI의 잘린 영역을 연결하고 mouse reporting wheel을 OpenCode로 전달한다. 키보드 탐색, 출력 중 과거 위치, 작은 휠 델타와 버퍼 전환을 보존한다.
- Release 빌드 0 경고/오류. OpenCodeAutomationClient(명시적 지휘 개입·승인·소유권·정확한 abort 확인 포함), Documents, AutomationProgress, 전체 WorkflowIntegration, UI smoke를 확인했다. UI smoke의 버전 명령만 임시 PATH fixture로 대체하며 나머지 명령은 거부했다. 실제 모델 연결 검증과는 구분한다.
- 새 WorkflowIntegration --projects-only: 14 프로젝트 목록 조회 무쓰기, 세로 스크롤, 3 프로젝트/9 역할 pane의 동시 native 실행, 화면 왕복 identity, 한 프로젝트만 중단, 목록 상태에서 나머지 프로젝트 완료 및 실제 지휘·검토 중간 요청 → 지휘 INTERVENE → abort 확인 → 부분 검토 → 후속 구현·최종 확인을 검증했다.
- TuiViewport에서 기록·휠·Shift 탐색·출력 중 과거 위치 보존·alternate buffer의 mouse reporting 전달·작은 화면 이동을 확인하고, WebView2 캡처 픽셀로 ANSI 출력 이후에도 상태 배경이 유지되는지 확인했다. TASK 목록과 우측 활동 그림도 실제 WPF 캡처를 확인했다.
- 정체 복구 집중 검사의 native abort 및 무응답 프로세스 종료 경로가 통과했다. WorkflowIntegration의 모든 테스트 창은 모의 서비스 전용 startup hook을 사용하도록 고쳐, 테스트용 CLI 시작 실패가 로컬 실제 설치 탐지·실행으로 넘어가지 않게 했다. 모의 서비스 점검이며 실제 모델 응답의 정확성이나 실제 서비스 실행을 검증한 것은 아니다.
- PlanExecution·ProjectSelection 검사를 3역할 TASK 구조에 맞춰 갱신하고 통과했다. 실행하지 않는 탐색 전용 CLI fixture로 검사하며, 기존/legacy MD 보존, 동일 표시 이름의 경로별 분리, 팀 재사용, 일반 터미널 선택, 터미널 0개일 때 초안 저장, 앱 재시작 후 과거 작업 자동 실행 방지를 확인했다.

## 2026-09-23: 지휘자 정체 감시·중단 확인·검토를 통한 복구

- 기존 코드의 `busy` 무기한 대기, 실행 중 도구 오류의 조기 인계, native 연결 실패 15초 뒤 미확정 요청 재시도 경로를 검토했다. 정확한 native session/message와 하위 작업의 응답·추론·도구 입력/출력·상태 변화를 감시하고, 단순 상태 신호는 진행으로 세지 않는다. 응답 무진행 3분, 도구/하위 작업 무진행 10분은 활동 시 초기화되는 감시 간격이며 작업 시간·회차 한도가 아니다.
- abort 직전 상태·질문/승인·요청 식별자·소유권·수동 취소를 다시 확인한다. 하위 작업부터 요청당 한 번 중단하고 전체 트리의 안정된 idle을 확인한 뒤 인계한다. abort 응답이 유실되어도 재전송하지 않는다. 1분간 abort가 효과 없고 새 조회에서도 같은 작업이 정체되어 있으면 그 pane의 기존 ConPTY job만 비동기로 닫는다. 통신 단절만으로 프로세스를 종료하지 않는다.
- 구현 도중 중단되거나 구현의 native 최종 결과를 검증하지 못한 경우 검토자가 부분 변경을 확인하고 지휘자가 후속 작업을 배정한다. 계획 승인과 최종 검토의 완료 권한을 유지한다. 읽기 전용 역할 재시도는 기존 지시·서비스 실행 근거를 보존하고, 실제 관측한 사용량을 유지한다. 수동 일시정지·중단 및 stale token/revision 거부를 유지한다.
- 표시되지 않아 아직 준비되지 않은 역할 pane은 다음 readiness 검사에서 버리지 않는다. Loaded/ConPTY 대기는 수동 취소 토큰과 연결한다. 시작 중 승인·질문·미전송 입력은 실패로 바꾸어 교체하지 않고 보존한다. OpenCode 종료 시 PowerShell 호스트도 끝내며 화면은 남긴다. 정체 복구·조회 실패를 진행 패널과 터미널에서 회색으로 표시한다.
- 가상 시계를 사용하는 native API 검사는 장시간 정상 진행, 멈춘 busy, 멈춘 도구와 하위 작업, 진행 재개, 늦은 승인/질문, native retry, 수동 취소, 소유권 변경, 조회 오류, abort 응답 유실, 재시작 직전 요청 변조 및 사용량 보존을 확인했다. 저장소 25개 검사는 검토 → 지휘 → 후속 구현 인계, 중복 결과 거부, pause 보존과 FinalVerification 재시도 시 기존 실행 근거 보존을 포함한다.
- 실제 WPF/ConPTY와 격리된 모의 OpenCode 서버를 연결한 전체 WorkflowIntegration 및 `--supervision-only`의 native abort/프로세스 재시작 경로가 통과했다. 설정된 모델·계정은 호출하지 않았다. NativeAutomationPane, AutomationProgress, PlanExecution, Documents, ConPTY 검사를 통과했다.
- 추가 검사는 준비 전 팀 재확인의 pane identity 보존, 종료한 모의 native 서버 PID의 실제 종료, 다른 터미널 프로세스 생존을 확인했다. 작은 터미널 창이 팀 교체를 막는 경로는 원래 배치로 복원한 뒤 재개하도록 처리한다. 종료 직후 남은 busy snapshot으로 터미널이 녹색이 되는 경로도 차단했다.
- 기본 UI 검사는 이 PC의 npm OpenCode 실행 파일이 OS에서 실행되지 않아 `opencode --version` 확인에서 실패했다. 해당 버전 명령만 임시 PATH의 격리된 stub으로 대체한 UI 전체 검사는 통과했다. 이는 실제 OpenCode 연동 검증이 아니다. 전역 설치·PATH·OpenCode 설정을 변경하지 않았다.
- 지정된 작업자 모델 `gpt-5.6-luna`를 사용할 수 없어 다른 모델로 대체하지 않았다. 동일 메인 에이전트가 구현 후 소유권·취소 경합과 데이터 보존을 별도 순서로 재검토하고 회귀 검사를 수행했다. 실제 서비스와 모델 응답은 이 환경에서 검증하지 않았다.

## 2026-09-22: 지휘 계획 구조 검사·오른쪽 진행 상태·실패 진단 기록

- 사용자 실행 기록을 읽기 전용으로 확인한 결과 첫 요청은 Coordinator/Planning이었고 PlanReview와 Coordinator/Implementation으로 이어졌다. 지휘 내용과 준비 실패·단계 전환이 상시 표시되지 않아 현재 진행을 판단하기 어려웠다. 사용자 프로젝트·MD·실행 원장은 수정하지 않았다.
- 지휘 Planning 응답에 일곱 개 구조화 항목을 추가하고 native/CLI 파서와 저장소에서 모두 검사한다. 누락·빈 항목은 지휘 단계 재시도이며, 유효한 계획만 원장과 후속 claim에 보존한다. 내용의 충분함은 별도 계획 검토로 판단한다. 원본 MD는 읽기 전용으로 취급하고 보완안을 반환하도록 지시한다.
- 우측 진행 패널은 실제 활성 claim의 역할·단계를 표시한다. OpenCode 준비와 실제 busy, 승인·질문, 연결 확인 불가를 구분하며 지휘 계획·최근 검토·다음 지시·전환 이력·로그 접근을 제공한다. 기존 원본 MD 편집기와 빈 시작 화면은 유지한다.
- 실행별 diagnostics.jsonl에 준비·전송·전환·상태 감시·오류·재시도를 기록한다. 실행/dispatch/session/message 식별자, revision, 빌드 식별자, 준비 상태 플래그와 연결 실패 종류를 남긴다. 직접 입력·프롬프트·HTTP 본문은 수집하지 않고 오류 문자열의 인증 정보는 마스킹한다. 쓰기 실패는 진행 패널에 알리며 일반 종료는 접수된 기록 저장을 기다린다.
- 요청 전송 후 HTTP client와 정확한 session/message의 소유권을 즉시 단계에 넘긴다. 이후 부가 파일 저장이나 일시적 원장 조회 실패는 같은 요청의 감시를 유지하며 새 요청을 보내지 않는다. 결과 조회의 일시적 연결 실패도 경고를 기록하고 동일 세션에서 재확인한다.
- Release 빌드, Automation/AutomationCommand, native OpenCodeAutomationClient, OpenCodeStatus, Documents, 전체 UI 테스트가 통과했다. 진단 집중 검사는 동시 JSONL append, 한국어·줄바꿈, 실행 식별자, 인증 정보 마스킹, I/O 실패와 기존 데이터 보존을 확인했다. native 계약 검사는 한 줄 지휘 응답 거부와 구조화된 계획 수락을 확인했다.
- 독립 소스 검토에서 역할 pane 중단·닫기 뒤 재전송 위험을 추가로 확인해, 같은 claim을 Stopped로 보존하고 재시도하지 않도록 수정했다. 로그 중첩 연결 오류의 마스킹도 보완했다. 최종 독립 검토에서 남은 중대 소유권·원자성 결함은 발견하지 못했다.
- 최종 WorkflowIntegration과 실제 OpenCode + 격리된 로컬 제공자 검증이 통과했다. 시작 중 취소의 재시도 방지, native-session.json 저장 실패 뒤 같은 단계 유지 및 중복 POST 없음, 검토 탭 선택과 무관한 실제 지휘 단계 표시, 계획·구현·최종 실행 확인의 전환과 반복을 확인했다. 설정된 모델/계정은 호출하지 않았다.
- AutomationProgress 집중 검사는 준비·busy·승인·unknown의 상태 색, 지휘 계획 보존, 로그 오류, 820×480에서 긴 완료 배너와 MD 편집 영역 공존, 감시 타이머 해제를 확인했다. 최소 창에서 완료 배너가 편집 영역을 모두 차지하던 문제를 수정했다. PlanExecution 및 배포 백업/사용자 MD 보존 검사도 통과했다. 이 실행 환경의 WPF 비트맵 캡처는 빈 픽셀을 반환해 시각 스크린샷 검증은 수행하지 못했으며, 렌더링 성공으로 간주하지 않고 컨트롤·상태·실제 배치 크기 검증과 구분했다.

## 2026-09-22: 지휘자 지속 제어·빌드 전 검토·실제 서비스 확인·빈 시작 화면

- 지휘 계획과 인수 기준을 저장하고 `Planning → PlanReview → Implementation → Verification → Delivery → FinalVerification` 단계를 원장과 역할 요청에 전달한다. 검토자의 명시적인 계획 승인 전에는 구현을 실행하지 않는다. 계획 반려, 구현 검토, 실제 서비스 실행 실패는 지휘자에게 돌아가며 같은 지적만으로 자동 일시정지하지 않는다.
- 구현 검증의 `complete=true`는 서비스 실행 지시로 이어진다. 구현자의 실제 실행 근거와 최종 검토자의 독립적인 실행·표시 확인 근거가 모두 있어야 전체 완료된다. 서비스 실행 담당은 기존 프로세스를 확인해 재사용하고 웹은 브라우저, 데스크톱은 앱 창, CLI는 터미널에서 결과를 보여주도록 지시받는다.
- 앱 종료로 중단된 역할과 기존 이력은 복구 시 보존한다. 명시적인 재개 시 중단된 구현·서비스 실행은 먼저 검토자로 보내 이미 반영된 변경이나 실행을 재확인한다. 단계 정보가 없는 이전 원장은 재개 때 새 계획 검토를 거친다. 명시적인 새 실행으로 과거 작업을 대체할 때는 기존 역할·회차·요약·사용량 이력을 보존한다.
- 일반 재시작은 선택 프로젝트와 터미널, MD 편집기가 빈 상태로 시작한다. 저장된 프로젝트 MD·문서 연결·이전 문서 경로·작업 폴더·Auto Mode는 보존한다. 과거 Running/Scheduled 작업은 일시정지하며 사용자가 재개하기 전에는 실행하지 않는다. 명시적인 `--workspace`와 트레이 숨김/복귀는 기존 동작을 유지한다.
- 자동화 저장소 23개 검사, AutomationCommand, OpenCodeAutomationClient, Documents, Startup, ProjectSelection, 전체 UI, PlanExecution 검증이 통과했다. 실제 OpenCode와 격리된 로컬 테스트 제공자에서 계획·계획 검토의 잘못된 응답 재시도 이후 총 10개 요청으로 8단계를 완료하고 세 역할 터미널의 생존과 표시를 확인했다. 실제 모델은 호출하지 않았다.
- WorkflowIntegration 전체가 통과했다. 추가한 16단계 시나리오는 최초 계획 반려 후 재계획·재검토, 동일한 구현 검토 실패 두 번 후 지휘자의 수정 지시, 서비스 실행 근거와 최종 검토 완료를 확인한다. OpenCode 준비 전 요청 0건, 시작 중 취소, 고아 실행 이력 보존, 저장 충돌 및 MD 재읽기 검사도 통과했다.
- 실행 중 앱을 강제 종료하지 않고 기존 배포 경로를 갱신하는 `tools/Apply-CommanderUpdate.ps1`을 추가했다. 정확한 실행 파일의 종료를 기다린 뒤 배포 디렉터리를 검증하고 이전 배포를 별도 백업으로 보존한다. 격리된 배포 검사에서 새 빌드 적용·이전 파일과 사용자 MD 보존·불완전한 빌드 거부를 확인했다.

## 2026-09-22: 동일 역할 재시도와 실제 OpenCode 터미널 생존 검증

- 지휘·구현·검토 단계가 실패하거나 최종 JSON 계약을 만족하지 못하면 실행 전체를 `Failed`로 끝내지 않는다. 역할과 회차를 그대로 둔 채 1·2·4·8·16·30초(이후 30초) 간격으로 제한 없이 다시 예약한다. 재시도 횟수와 다음 시각은 프로젝트 원장에 저장되며 앱을 다시 시작해도 유지된다. 정상 결과가 저장된 뒤에만 다음 역할로 넘어간다.
- 일시정지 요청이 들어온 단계는 실패 결과를 기록한 뒤 `Paused`로 남고, 사용자가 중단한 실행은 재시도하지 않는다. 앱 종료로 중단된 단계도 재개하면 같은 역할부터 다시 확인한다.
- `prompt_async` 응답이 끊긴 경우에는 명령을 다시 전송하지 않는다. 이미 만든 OpenCode root session과 message ID를 반환해 조회 경로에서 실제 수락 여부를 확인하므로, 구현 명령이 중복 실행되는 경로를 막았다.
- OpenCode 자식 프로세스가 끝나도 ConPTY의 PowerShell 호스트와 탭은 남긴다. 활성 단계에서 native 상태 연결이 15초 이상 복구되지 않으면 진행 중으로 무한 표시하지 않고 같은 역할을 새 OpenCode pane에서 다시 시도한다. 실패 pane은 원인 화면을 보존한 뒤 재시도 직전에 교체한다.
- 설치된 OpenCode 1.18.31을 실제로 실행하고 격리된 임시 프로젝트·로컬 테스트 제공자를 연결했다. 첫 Coordinator와 첫 Reviewer 응답을 의도적으로 잘못 반환한 결과 `Coordinator → Coordinator → Implementer → Reviewer → Reviewer` 순서로 같은 역할이 먼저 재시도됐고 실행은 Completed가 됐다. 완료 1.5초 뒤에도 세 역할 탭 모두 `IsReady && IsRunning`이었으며 각 TUI에 최종 응답이 표시됐다. 같은 실제 OpenCode 검증을 최종 변경 전후 두 번 통과했다.
- Release 빌드는 경고·오류 0. Automation 저장소는 9회 연속 실패 뒤에도 같은 Coordinator가 재예약되고 30초에서 backoff가 고정되는 것, 저장소 재생성 뒤 횟수 유지, pause/stop 우선순위를 검증했다. OpenCodeAutomationClient, NativeAutomationPane, AutomationNativeLaunch, ConPTY Terminal, WorkflowIntegration도 통과했다.

## 2026-09-22: 지휘자 첫 단계 우선 실행과 역할 터미널 보존

- 실제 `C:\opencode\terminal-notes\automation-runs.json`을 읽기 전용으로 확인했다. 최근 Reviewer 실행 3개는 작업 내용이 아니라 고정 영어 `Verified criteria:`/`Verification evidence:` 표제 누락으로 Failed 처리됐고, 최신 Coordinator도 `Implementer action:`/`Reviewer verification:` 접두사 불일치로 Failed 처리됐다. 기존 코드는 이 실패 직후 역할 pane을 Dispose해 ConPTY와 OpenCode 프로세스 트리를 종료했다. 사용자 플랜과 실행 기록은 수정하지 않았다.
- 역할 결과의 JSON 구조와 비어 있지 않은 요약은 계속 검사한다. 영어 표제나 역할 접두사만 빠진 경우 Coordinator의 의미를 판별해 Reviewer 또는 Implementer 지시로 보정하고, 불명확하면 안전하게 Implementer로 보낸다. Implementer와 미완료 Reviewer의 다음 역할 접두사도 보정한다. Coordinator·Implementer의 `complete=true`는 false로 정규화해 Reviewer만 완료할 수 있게 유지한다.
- 실패 결과, 시작 예외, 중단으로 역할 pane을 즉시 Dispose하지 않는다. busy를 해제하고 오류 상태와 화면을 보존하며, 재사용할 수 없는 pane은 다음 명시적 실행에서 교체한다. 시작 대기 중 중단은 cancellation을 직접 확인해 터미널 종료에 의존하지 않고 즉시 빠져나온다.
- 사용자가 승인한 새 실행은 같은 저장소에서 즉시 Coordinator claim을 확보하고 대기열 앞에 둔다. 그 실행의 다음 역할도 우선해 다른 프로젝트의 과거 Reviewer claim이 방금 시작한 작업처럼 먼저 표시되는 경로를 막았다. 완료 기록 선택도 새 run ID로 고정한다.
- Release 빌드는 경고·오류 0. AutomationCommand와 native OpenCodeAutomationClient, Automation 저장소 검증이 통과했다. WorkflowIntegration은 접두사 없는 Coordinator·Implementer 결과와 영어 검증 표제 없는 Reviewer 완료를 사용해 C→I→R 반복 실행, C→R 직행, 시작 중단 뒤 새 Coordinator 재실행을 완료했다. 각 완료 뒤 세 역할 탭의 `IsReady`·`IsRunning` 유지와 실패 상태 표시 뒤 pane 비종료도 확인했다. 외부 모델은 호출하지 않았다.

## 2026-09-22: 프로젝트 기준 MD와 저장·실행 UI 정리

- 프로젝트 문서를 `<프로젝트>/terminal-notes/plan.md` 하나로 고정했다. 기존 프로젝트를 열 때 고정 파일이 없으면 과거 사용자 지정/legacy 플랜을 원본 보존 상태로 한 번 복사하고, 이관 대상도 없으면 빈 파일을 원자적으로 만든다. 기존 고정 파일은 덮어쓰지 않는다.
- 시작 화면에서 기존 프로젝트와 새 프로젝트를 아이콘으로 선택한다. 터미널을 모두 닫아도 선택 프로젝트와 MD 편집기 초안은 유지되며 새 터미널 또는 지휘자 실행을 계속 선택할 수 있다. Reviewer 완료 시 자동화 창에서는 해당 실행과 요약을 선택하고, 창이 닫혀 있으면 메인 화면에 완료 요약을 표시한다.
- 편집기 하단 작업 버튼은 `저장 / 실행` 두 개만 남겼다. `실행`은 항상 현재 초안을 고정 MD에 먼저 저장하고, 저장 성공과 프로젝트·경로 재검증 뒤 선택한 현재 터미널·새 터미널·지휘자 방식으로 실행한다. 저장 실패나 외부 변경 충돌 시 실행하지 않는다.
- Release 메인 빌드는 경고·오류 0. TabDocuments 37개, ProjectSelection 22개, Conversation, PlanExecution, 전체 UI, WorkflowIntegration 검증이 통과했다. UI 검증에서 두 작업 버튼만 노출되는 것과 재시작·새 세션의 고정 MD 연결을 확인했고, WorkflowIntegration에서 고정 MD를 사용하는 Coordinator→Implementer→Reviewer 및 Coordinator→Reviewer 완료 흐름과 Reviewer 결과 표시를 확인했다. 외부 모델은 호출하지 않았다.

## 2026-09-21: 지휘자 반복 실행과 역할 전환 수정

- 실제 `C:\opencode\terminal-notes\automation-runs.json`을 읽기 전용으로 확인했다. 기록된 실행에는 Reviewer 전달이 한 번도 없었고, Coordinator가 이미 존재하는 계산기와 통과한 테스트를 직접 확인한 뒤에도 Implementer에게 같은 확인을 다시 지시했다. 앞선 실행들은 토큰 한도, 엄격한 JSON 응답 형식 실패, 앱 종료로 각각 끊겨 새 실행마다 같은 앞부분을 반복했다. 사용자 프로젝트 MD와 자동화 원장은 수정하지 않았다.
- Coordinator는 첫 단계에서 파일 수정·빌드·테스트 없이 플랜의 번호 매김 인수 기준만 만든다. 작업이 필요하면 `Implementer action:`, 현재 구현 검토만 필요하면 `Reviewer verification:`으로 다음 역할을 명시한다. 후자는 Implementer를 생략하고 Reviewer로 바로 이동한다. Coordinator와 Implementer의 `complete=true`는 계약 위반이며 완료 판정은 Reviewer만 할 수 있다.
- Implementer는 직전 단계가 지정한 범위만 수행하고 변경이 없으면 불필요한 빌드·테스트를 되풀이하지 않는다. Reviewer는 검증된 기준과 근거를 명시해야 완료할 수 있다. 미완료이면 미충족 기준·실패 근거·구체적인 구현 지시를 모두 반환하며 Coordinator를 다시 실행하지 않고 Implementer로 직접 보낸다. 정규화한 동일 지시가 다음 검토에서도 반복되면 자동 순환을 멈추고 사람 확인이 필요한 Paused 상태로 남긴다.
- 각 역할은 실제 OpenCode native todo를 만들고 완료 상태를 갱신한 뒤 구조화 결과를 반환한다. native/CLI 결과 파서는 같은 역할 계약을 적용하며 설명·코드 펜스·추가 필드가 있어도 유효한 결과 객체를 찾는다. 빈 요약, 잘못된 역할 접두사, 검토 근거 누락, native 도구 오류는 성공으로 넘기지 않는다.
- 앱 종료로 결과가 저장되지 않은 경우 읽기 전용 Coordinator는 같은 단계를 다시 실행해 누락된 기준과 지시를 복구한다. Implementer는 요청이 이미 반영되었을 수 있으므로 Reviewer로 넘겨 현재 파일 상태부터 확인하고, 읽기 전용 Reviewer는 다시 검토한다. 일반 일시정지는 사용자가 멈춘 역할을 그대로 유지한다.
- Release 메인 빌드는 경고·오류 0. Automation 저장소 22개, AutomationCommand, OpenCodeAutomationClient 검증이 통과했다. WorkflowIntegration은 실제 WPF 역할 화면과 격리된 native 대역에서 `Coordinator → Implementer → Reviewer` 및 기존 구현의 `Coordinator → Reviewer` 직행을 확인했고, 직행 경로에 Implementer 요청이 없음을 검증했다. 실제 모델은 호출하지 않았다.

## 2026-09-21: 지휘자 시작 대기·종료 및 남은 실행 기록 복구

- OpenCode 전용 ConPTY는 시작 명령 종료 후 PowerShell을 남기지 않는다. 일반 내부 셸은 기존 수명을 유지한다. TerminalPane은 표시·레이아웃·WebView 준비·ConPTY 시작을 await 가능한 핸드오프로 제공하고 지휘자는 이후 native idle을 별도로 확인한다.
- 시작 대기를 실제 소유 단계로 추적해 중단할 수 있게 했다. native 전송 반환 및 세션 기록 저장 후에도 취소/현재 dispatch를 확인해 중단 뒤 늦은 응답으로 실행 상태를 되살리지 않는다.
- 통합 검증에서 중단 후 같은 MD 재실행이 `OpenCodeProjectContext` disposed 예외로 실패하는 문제를 재현했다. 프로젝트명 갱신이 화면에 남은 종료된 역할 탭까지 접근하던 경로이며, 종료된 pane의 context 갱신을 건너뛰어 새 역할 탭으로 교체할 수 있도록 수정했다.
- 미니 화면의 자동화 소유권 기반 강제 초록색/진행 중 표시를 제거했다. 색상은 실제 pane activity를 따르며 시작 준비는 회색이다.
- 독점 스케줄러가 소유하지 않는 Running 기록만 명시적 새 실행 시 정확한 dispatch token/revision으로 Paused 복구한 뒤 기존 승인 경로로 이력을 보존한다. in-flight poll, 시작, 단계, 대기열 및 예약/실행 중복 보호를 유지한다.
- 완료 트랜잭션의 다음 claim을 부가 기록/UI보다 먼저 인수한다. 부가 결과 파일 저장 실패로 다음 단계 소유권을 잃지 않으며 조회/UI 예외로 감시 타이머를 영구 중지하지 않는다.
- Release 빌드 경고·오류 0. 저장소 16개 검증, native 자동화 클라이언트 검증, 실제 ConPTY 정상/실패 명령 종료 및 내부 셸 유지 검증 통과. 별도 수명 검토에서 발견한 전송 반환/기록 저장 중 중단 경합을 수정했다.
- WorkflowIntegration 최종 통과: 등록되지 않은 프로젝트의 과거 Running 이력 보존·복구, ConPTY/TUI 준비 후 native API 미준비 동안 요청 0건, 회색 준비 상태, 실제 소유 작업 중복 차단, 준비 중 중단, 해제 후 새 승인 지휘·구현·검토 완료를 검증했다. 기존 9역할 흐름도 통과했다. 검증용 apphost 실행 및 내부 셸 fixture 가정을 바로잡은 뒤 재검증했으며 실제 모델은 호출하지 않았다.

## 2026-09-21: 사용자 요청에 따라 작업 한도 제거

- 지휘자 실행의 토큰·회차·단계 시간 한도와 자동 진행의 3회 시도·6회 재개 한도를 제거했다. 새 실행의 한도 메타데이터는 null로 저장하며 기존 JSON 숫자는 이력으로 보존하고 실행 차단에 사용하지 않는다. UI에는 최대 회차 분모를 빼고 현재 회차와 사용량만 표시한다.
- 토큰·회차 한도로 Paused였던 기존 작업도 명시적으로 재개할 수 있다. 과거 회차 한도로 Reviewer 종료 뒤 멈췄던 작업은 다음 Coordinator 회차로 이어간다. 같은 프로젝트에 Running/Scheduled가 있으면 재개를 거부해 동시 실행을 막는다. 사용자 실제 기록을 미리 변경하거나 실행 중인 세션을 재개하지는 않았다.
- 실제 완료·오류·수동 중단/일시정지, 승인·질문 대기 및 native 상태 확인은 유지한다. HTTP 연결/응답 확인, 불명확한 POST 무재전송, 자동 진행의 1/3/10분 재개 간격도 유지한다. 파일 크기나 터미널 UI 수 같은 작업 실행 외의 경계는 이번 변경 대상이 아니다.
- Release 빌드 경고·오류 0. 저장소 15개 검사, 자동 진행 정책 검사, 자동 진행 컨트롤러/클라이언트 통합 52개 검사가 통과했다. 기존 한도 값을 넘긴 단계 전환, legacy 토큰·회차 한도 기록의 재개·보존, 수동 중단 보호, 5회 실패 후 6번째 재시도와 미완료 증거가 있는 8회 재개를 확인했다.
- WorkflowIntegration은 세 한도 필드의 기본 null, 구버전 JSON 이력 보존을 검증했다. 실제 native 대역이 busy인 동안 단계 시작 시각을 이틀 전, 과거 한도를 1분·1토큰으로 바꾼 뒤 Poll을 호출해 작업·터미널·취소 토큰이 그대로 유지됨을 확인했다. 응답을 해제한 뒤 지휘·구현·검토가 181,203토큰으로 완료됐고 전체 9역할 요청 검사가 통과했다. 테스트 helper의 컴파일·리플렉션 선택을 수정한 뒤 최종 통합에 성공했다. 실제 제공자는 호출하지 않았다.
- 저장소/재개 정책의 교차 검토에서 이번 변경의 치명적인 문제는 발견하지 않았다. `artifacts/win-x64` 및 `artifacts/win-x64-md-update` 두 배포 경로를 갱신했다.

## 2026-09-21: 새 지휘자 실행도 첫 단계 뒤 토큰 한도로 멈추는 문제

- 실행 중인 화면과 최신 저장소 상태를 읽기 전용으로 확인했다. 새 실행은 지휘 단계에 성공했지만 사용량 180,867이 앱의 고정 예산 100,000을 넘으면서 `Paused / Token budget reached.`로 바뀌었고 다음 역할은 Implementer였다. 이전 수정은 과거 Paused 이력의 새 실행 차단만 해결했고 새 실행의 같은 고정 예산은 남겨두었다.
- 일반 UI에서 승인하는 새 작업의 TokenBudget은 null로 두어 앱의 고정 토큰 상한으로 단계 전환을 막지 않도록 했다. native 사용량 집계는 유지한다. 최대 5회차·단계당 30분, 기존 명시적 예산을 가진 기록과 Resume의 한도 제한은 바꾸지 않았다. 실행 중인 사용자 세션이나 저장된 실행 기록을 자동으로 재개·변경하지 않았다.
- Release 빌드 경고·오류 0. nullable 옵션, 누적 사용량, 기존 한도 보호와 새 승인 작업 분리를 별도 코드 검토로 확인했다.
- 저장소 11개 검사는 명시적 예산과 회차 제한, 새 승인 및 기록 보존을 통과했다. WorkflowIntegration은 첫 지휘 응답을 180,867토큰으로 재현하고 구현·검토 각 168토큰을 거쳐 총 181,203으로 완료되는 것을 검증했다. 기본 옵션 null, 역할별 한 번의 요청, 완료 기록, UI 누적 사용량도 확인했다. 테스트의 완료 후 NextRole 기대값을 실제 계약인 Reviewer로 바로잡은 뒤 전체 통합이 통과했다. 외부 모델은 호출하지 않았다.
- 기본 `artifacts/win-x64`와 기존 별도 `artifacts/win-x64-md-update` 배포를 모두 갱신하고 빌드 DLL과 해시가 같은지 확인했다. 실행 중인 Debug 앱은 그대로 유지했으며 새 옵션은 수정본에서 시작하는 새 작업부터 적용된다.

## 2026-09-21: 한도 도달 이력으로 기존 MD 지휘자 실행이 차단되는 문제

- 저장된 선택 프로젝트의 자동화 메타데이터를 읽기 전용으로 확인했다. 기존 작업은 `Paused`, 사유는 `Token budget reached.`였다. UI와 실제 실행 경로가 Paused를 모두 중복 실행으로 취급했으며, 한도에 도달한 작업은 Resume도 거부되어 기존 MD의 새 실행이 막혔다. 사용자 프로젝트 MD·프롬프트·자동화 기록은 수정하지 않았다.
- 사용자가 지휘자 실행을 누르면 새 승인 작업을 시작한다. 새 저장 트랜잭션은 Running/Scheduled가 있으면 변경 없이 거부하고, Paused 이력은 플랜·옵션·토큰·결과·한도 사유를 보존한 Stopped로 전환한 뒤 현재 MD로 새 Scheduled를 생성한다. 기존 Resume의 한도 제한과 자동 재개의 정책은 그대로 유지한다.
- 초기화 잠금 획득이 한 번 실패하면 영구 비활성으로 남던 별도 문제도 수정했다. 초기화 성공 후에만 준비 완료로 표시하고, 실패 시 5초 간격으로 재시도한다. OS 잠금을 제거하거나 소유권을 빼앗지 않으며 종료된 창은 초기화를 되살리지 않는다. 실행을 막는 실제 사유는 편집기 상태 줄과 툴팁에 표시한다.
- Release 빌드 경고·오류 0. 저장소 독립 테스트 11개에서 한도 도달 후 기존 Resume 거부, 명시적 새 승인, 과거 기록 보존, 진행·예약 중 변경 없는 거부, 동시 새 실행의 단일 성공을 확인했다. 저장소 전환과 초기화 변경의 별도 코드 검토에서 치명적인 문제는 발견하지 않았다.
- PlanExecution은 실제 기존 MD를 디스크에서 다시 불러온 상태의 지휘자 활성화, 한도 도달 Paused 이력, Running/Scheduled 차단과 초기화 잠금 해제 후 자동 재획득·버튼 복구를 통과했다. WorkflowIntegration도 실제 저장소에 한도 도달 Paused 상태를 만든 뒤 화면의 명시적 실행으로 기존 기록을 보존하고 지휘·구현·검토 전체 흐름을 완료했다. 격리된 native HTTP 대역을 사용했으며 외부 모델은 호출하지 않았다.
- `artifacts/win-x64`와 앞서 제공한 `artifacts/win-x64-md-update` 두 경로를 모두 새 Release로 갱신하고 빌드 DLL 해시 일치를 확인했다. 기본 `Start-TerminalNotes.cmd`로도 수정본을 실행할 수 있다.

## 2026-09-21: 기존 MD의 지휘자 실행과 화면 간소화

- 실행 중인 이전 화면을 읽기 전용 UI Automation으로 확인한 결과 `현재 터미널`이 선택되어 있었고, 실행 버튼은 터미널의 미제출 입력 때문에 비활성이었다. MD가 수정되지 않았기 때문은 아니었다. 사용자 입력이나 실행 중인 세션은 변경하지 않았다.
- 숨은 실행 방식 메뉴를 `현재 터미널 / 새 터미널 / 지휘자` 세 선택 버튼으로 바꾸고 선택 색상을 명확히 했다. 현재 터미널의 입력·상태와 무관하게 지휘자를 선택할 수 있으며, 지휘자의 기존 실행·소유권·슬롯 검사는 유지한다. 저장된 MD는 `실행`, 변경된 초안은 `저장 후 실행`으로 표시한다. 비활성 실행 버튼은 실제 차단 이유를 툴팁으로 제공한다.
- 같은 내용의 기존 MD는 외부 변경 확인 후 다시 쓰지 않는다. 읽기 전용 속성·내용·수정 시각을 보존하고 외부 수정 충돌은 계속 차단한다. 반복 제목·경로·설명·빈 토큰 안내를 줄이고 보조 정보는 툴팁으로 옮겼다. 실제 오류와 Auto Mode의 자동 승인 설명은 유지했다.
- Release 빌드 경고·오류 0. PlanExecution에서 기존 MD 및 현재 터미널의 미제출 입력·Unknown/Busy/NeedsInput 상태에서도 지휘자 선택과 실행 활성화를 검증했다. 빈 MD와 현재 터미널 입력 보호는 유지한다. TabDocuments의 변경 없는 저장·읽기 전용 파일 보존·외부 수정 충돌 검사가 통과했다.
- 전체 UI 검증이 통과했다. 세 실행 방식의 노출·기본 선택, MD 초안·프로젝트 연결·설정·트레이 복원·1~8 터미널 배치와 종료를 확인했다. 새 화면 캡처에서 선택 색상과 하단 버튼 배치를 확인했다. 이번 변경 검증은 실제 모델 요청을 보내지 않았다.
- 실행 중인 기존 배포본이 `artifacts/win-x64/TerminalNotes.dll`을 잠그고 있어 해당 위치의 갱신은 실패했다. 사용자 세션은 종료하지 않고 `artifacts/win-x64-md-update`에 새 Release 배포를 완료했다. 새 버전은 기존 앱을 트레이에서 종료한 뒤 이 폴더의 실행 파일로 시작한다. 기존 기본 런처 경로는 변경하지 않았다.

## 2026-09-21: 지휘·구현·검토를 실제 OpenCode 대화형 세션으로 전환

- 이전 변경은 셸에서 `opencode run`을 실행하는 기존 구조의 예약 처리를 보완한 것이었다. 역할 화면 자체가 OpenCode여야 한다는 요구를 충족하지 못했다. 실제 원인은 지휘·구현·검토의 최초 생성과 교체 경로 모두 `TerminalTool.PowerShell`, 빈 시작 명령을 사용한 데 있었다.
- 이제 세 역할 모두 공통 Auto Mode에 맞는 `opencode` 또는 `opencode --auto`로 시작한다. 각각 독립적인 loopback 서버, native 상태 모니터, 프로젝트 MD 지침을 갖는다. 역할 실행에는 셸 스크립트나 headless CLI 명령을 보내지 않는다.
- native API로 전용 root session을 생성하고 TUI에 선택한 뒤 정확한 message ID로 역할 프롬프트를 한 번만 제출한다. 완료는 해당 user message에 연결된 assistant의 완료 시각·finish·JSON 결과와 실제 idle/하위 세션/승인/질문 상태로 검증한다. 요청 결과가 불명확하면 재전송하지 않는다. 중단과 창 종료는 진행 중인 제출도 취소한다.
- Release 빌드 경고/오류 0. 설치된 OpenCode로 세 역할 프로세스가 실제 시작되고 각각 native idle과 독립 포트를 갖는 것을 확인했다. HTTP 대역 테스트는 요청 순서·모델 생략 및 필드명·이전 응답 배제·승인 대기·잘못된 결과·native 오류·다른 세션·불명확한 POST 무재시도를 검증했다. 자동화 저장소 8개 및 기존 자동 재개 통합 52개 검증도 통과했다.
- API 계약과 TUI 이벤트 전달 근거: [OpenCode 1.18.31 TUI worker](https://github.com/anomalyco/opencode/blob/v1.18.31/packages/opencode/src/cli/tui/worker.ts), [TUI SDK 이벤트 구독](https://github.com/anomalyco/opencode/blob/v1.18.31/packages/tui/src/context/sdk.tsx). 실제 제공자 API는 호출하지 않았다.
- 후속 검토에서 중단 저장 중 늦은 전송, 중단과 완료 저장의 경쟁, 종료된 역할 세션의 결과로 다음 단계에 진입하는 경로를 차단했다. 중단 의사는 저장을 기다리지 않고 먼저 native 전송을 취소한다. 일시적인 읽기 타임아웃·연결 오류·408/429/5xx는 다음 조회까지 대기하며 같은 프롬프트를 다시 보내지 않는다. 단계 시간 제한은 유지한다.
- `NativeAutomationPane` 13개 검증과 전체 `WorkflowIntegration`을 통과했다. 전체 통합은 native HTTP 대역으로 3회 실행·9역할 전달·실행당 504 토큰·MD 저장과 재실행·축소 창·수동 입력 보존을 검증한다. 대역은 `opencode run` 호출을 거부하므로 기존 셸 실행으로 퇴행하면 테스트가 실패한다.
- `AutomationLiveNative`는 설치된 실제 OpenCode와 loopback 가짜 모델 제공자를 연결해 앱 스케줄러의 지휘→구현→검토 1회차를 끝까지 수행했다. 역할별 모델을 지정하지 않아 기본 모델 상속을 검증했으며 정확히 세 번의 로컬 역할 요청과 세 OpenCode 화면의 `LIVE_COORDINATOR`, `LIVE_IMPLEMENTER`, `LIVE_REVIEWER` 응답 표시를 확인했다. 외부 모델 제공자는 호출하지 않았다.

## 2026-09-21: 플랜 실행 방식 분리와 자동화 시작 보완

- `저장 후 실행`의 기본 대상은 현재 선택한 OpenCode 터미널이다. 새 터미널은 같은 프로젝트에 세션 하나만 만들고, 지휘자는 명시적으로 선택한 경우에만 지휘·구현·검토 팀을 실행한다. 프로젝트 선택과 실행 방식을 분리하고 실행 대상을 표시한다.
- 새 프로젝트는 메인 작업 폴더 아래에 프로젝트 폴더와 `terminal-notes/plan.md`를 만들고 세션 하나를 연다. 기존 폴더는 덮어쓰지 않는다. 프로젝트 이름은 지휘자 실행에도 재사용한다.
- 단일 실행은 native idle 상태, 입력 초안, 프로젝트/MD 연결과 저장 내용의 일치를 확인한다. 붙여넣기와 Enter 사이에도 재검사하며, 취소된 부분 입력은 자동으로 지우거나 재전송하지 않는다.
- 자동화 스케줄러를 Loaded 우선순위로 옮기고 예약 직후 실행 확인을 큐에 넣어 Background 타이머가 입력 대기 때문에 밀릴 수 있는 경로를 제거했다. 문제가 발생한 환경의 미실행 원인을 직접 재현한 것은 아니다. 시작 준비와 명령 전달 상태를 구분하고, CLI 실패 시 제한된 길이의 실제 오류 내용을 함께 표시한다.
- OpenCode 1.18.31의 공식 `run.ts`에서 표준 입력을 읽는 경로를 확인했다: https://github.com/anomalyco/opencode/blob/v1.18.31/packages/opencode/src/cli/cmd/run.ts . 지휘자 역할은 `opencode run`으로 실행한다.
- Release 빌드 경고/오류 0. PlanExecution 17개, PlanDispatch 21개, 프로젝트 이름 검증 14개, 자동 재개 통합 52개 검증을 통과했다. UI, 입력 캡처, 문서, 가짜 CLI를 사용하는 지휘·구현·검토 전체 통합도 통과했다. 실제 모델 요청은 수행하지 않았다.

## 2026-09-21: 대화형 OpenCode 정체 감시와 자동 재개

- MD 패널에 기본 활성화된 자동 진행 체크를 추가하고 권한 Auto Mode와 별도 저장한다. 로컬 제출과 새 native user message/todo 이벤트로 정확한 root session을 고정한다. 인사·역사적 목록·다른 프로젝트나 하위 세션만으로 작업을 재개하지 않는다.
- 정책, loopback 전송, 세션 제어를 분리했다. 미완료 idle 20초, 진행 변화 없는 busy 5분 의심/15분 복구, 실제 idle 확인 후 재개, 응답 확인 시간 제한과 재개 상한을 적용한다. 진행 신호는 native 메시지·추론·도구·todo이며 화면 문구로 상태를 추정하지 않는다.
- 반복 검토에서 취소된 todo 재실행, 과거 오류가 현재 요청을 막는 문제, 긴 응답 해시 잘림, 자동 요청 ID 정렬, 중단과 재개의 예산 중복 소모, 연결 끊김이 진행으로 계산되는 문제, 최종 전송 전 상태 변경과 응답 본문 시간 제한을 점검했다. 실행 중 도구·하위 세션·승인·질문·retry·오류는 복구를 차단한다.
- 사용자 입력·중단·MD 변경·세션 소유권 변경은 예약된 작업을 무효화한다. 앱 전역 전송 직렬화와 정확한 message ID 조회로 불확실한 POST를 반복하지 않는다. 실제 모델의 자원 대기와 복구 성공률은 별도 실환경 확인이 필요하다.
- 추가 입력 검토에서 xterm의 CSI 상태·커서·포커스 응답과 OSC 색상 응답을 사용자 입력에서 구분했다. 재개 대상은 비어 있지 않은 로컬 입력 제출에만 연결하며 빈 Enter, 슬래시 명령과 과거 메시지로 새 복구 대상을 만들지 않는다. SSE 연결의 첫 HTTP 응답에도 8초 제한을 적용해 응답 없는 연결을 재시도한다.
- Release 정책·HTTP 클라이언트·제어 통합(52개 단언)·입력 캡처·문서·터미널·native 상태 검증을 수행했다. 실제 설치된 OpenCode 1.18.31의 격리된 빈 세션에서도 전체 recovery 조회 스키마와 idle 생략 규약을 검증했다. 모델 요청은 보내지 않았으며 abort/prompt_async와 시간 경과 복구는 가짜 HTTP 서버와 가상 시간으로 검증했다.
- 최종 UI 재검사에서 일반 PowerShell의 초기 프롬프트가 native 출력까지 도달했지만 25초 동안 화면 전송 대기열에 남는 현상을 재현했다. 출력 타이머는 Background에서 Loaded로 높였다. [WPF Dispatcher 소스](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/Dispatcher.cs)의 foreground 범위(Loaded~Send)를 확인해 Win32 입력 대기 때문에 출력을 무기한 미루지 않도록 했다. 32ms 주기·한 번에 한 묶음 전송·브라우저 응답 대기는 유지한다. 테스트에 native 출력·대기열·타이머·브라우저 준비 상태 진단을 추가했다.
- 출력 우선순위 조정 후 전체 UI 검증이 통과했다. 최종 Loaded 우선순위 빌드는 별도 초기 화면 검증에서 실제 PowerShell 프롬프트·프로젝트 경로·한글 입출력·OpenCode 버전 실행·자동 진행 체크의 기본값과 글자 대비를 확인했다. 자동 진행 설정의 재시작 보존, 기존 터미널·MD 유지도 전체 UI 검증에서 통과했다. 최종 win-x64 Release 배포본을 갱신했다.

## 2026-09-21: 프로젝트 선택과 대화형 OpenCode의 MD 연결

- MD 패널의 프로젝트 메뉴가 프로젝트 경로·MD 연결을 관리한다. 메인 작업 폴더와 선택 프로젝트를 따로 저장하며, 프로젝트/터미널 선택 뒤 새 터미널과 재실행도 같은 프로젝트를 따른다. 기존 해당 세션은 재사용한다.
- 대화형 OpenCode마다 앱 소유 지침 파일을 만들고 프로세스 전용 OPENCODE_CONFIG_CONTENT에 추가한다. 프로젝트 이름·폴더·MD를 전달하며 구현 전에 최신 MD를 읽도록 지시한다. 일반 질문은 MD 없이 가능하고 세션 시작만으로 작업을 실행하지 않는다. 모델이 지침을 따르는지는 실제 모델 호출로 검증하지 않았다.
- 프로젝트 MD/이름 변경은 열린 해당 프로젝트의 지침 파일에도 원자적으로 반영한다. 여러 세션 갱신 실패 시 이전 연결로 복구하고 복구 실패 세션은 입력을 막는다. 기존 AGENTS.md/opencode.json, MD 원본, 부모 프로세스 환경은 변경하지 않는다.
- 별도 검토에서 native handle/버퍼 보존 결함은 발견하지 않았다. 검토에서 발견한 백틱 경로·이름의 Markdown 경계 문제를 고쳤다. 폴더 별칭은 기존과 같이 정규화된 문자열 기준이다.
- Release 빌드 경고·오류 0. Documents, UI, Conversation, Startup, ProjectSelection, WorkflowIntegration, Terminal, OpenCodeBridge 통과. UI의 짧은 진행색 검사는 동시 다중 WebView 테스트에서 한 번 타임아웃 후 단독 재실행에서 통과했다.
- 설치된 OpenCode의 실제 /config에서 규칙과 앱 지침이 모두 포함됨을 확인했고, 살아 있는 pane의 경로·이름 재연결 및 원본 보존을 확인했다. 제공자/모델 요청은 보내지 않았다.
- 동작 근거: OpenCode 1.18.31의 instruction.ts는 매 모델 요청에 지침 파일을 다시 읽고, config.ts의 mergeConfigConcatArrays는 기존 instructions를 합친다.
  - https://raw.githubusercontent.com/anomalyco/opencode/014614d/packages/opencode/src/session/instruction.ts
  - https://raw.githubusercontent.com/anomalyco/opencode/014614d/packages/opencode/src/config/config.ts

## 2026-09-21: 프로젝트 플랜·그룹과 최소 시작 설정

- 플랜을 프로젝트 경로 기준으로 공유하고 새 세션에서도 이어 쓴다. 기존 슬롯별 연결은 마지막 유효 지정값으로 통합하며 레거시 파일은 보존한다. 자동화 팝업의 비동기 저장도 프로젝트 연결 변경을 검사한다.
- 지휘자 자동화는 프로젝트명과 플랜으로 시작한다. 표시 이름은 저장하고 그룹 식별은 프로젝트 경로로 유지한다.
- 최초 실행은 실제 OpenCode 확인/설치 동의/재확인 후 공통 폴더와 Auto Mode를 선택한다. 완료 상태를 저장하여 다음 실행에서는 반복하지 않는다. 취소한 폴더를 생성하거나 터미널을 시작하지 않는다.
- 번호별 폴더·서비스·권한 UI를 제거하고 작업 설정에는 공통 폴더와 Auto Mode만 남겼다. 설정 변경은 새 터미널부터 적용하며 기존 세션은 유지한다. OpenCodeOnly 기본값은 Y다.
- 검증: Release 빌드 경고·오류 0. Documents, TabDocuments, Conversation, Installation, Startup, WorkflowIntegration, UI, LayoutCompact 통과. 실제 사용자 설치·모델 호출은 하지 않았다.
- 별도 데이터 보존 검토에서 고위험 회귀는 발견하지 않았다. 경로 식별은 정규화된 문자열 기준이므로 junction/UNC 등 같은 물리 폴더의 별칭은 서로 다른 프로젝트로 보일 수 있다.

## 2026-09-21: 기본 폴더·설치 확인과 설치 코드 연결

- 기본 폴더를 `C:\opencode`로 변경하고 작업 폴더가 없으면 생성한다. 이전 기본값만 마이그레이션하고 다른 지정 경로와 문서는 유지한다.
- 시작 전에 실제 `opencode --version`을 비동기로 확인한다. 미설치 또는 실행 불가이면 사용자 동의를 받은 뒤 `auto-install.cs`의 `RunAsync`만 호출하고, 다시 설치 여부를 확인한 다음 터미널과 자동화를 시작한다. 별도 다운로드/설치 구현은 추가하지 않았다.
- 거절·미연결·실패 시 터미널을 시작하지 않으며 사용자가 다시 시도할 수 있다. 중복 시작과 종료 중 설치 호출을 막는다. 읽기 전용 감지는 `.exe`/`.cmd`/`.ps1`, 시간 제한·취소·PATH 갱신을 지원한다.
- 검증: Release 빌드 경고·오류 0. Startup, Installation, UI, Documents, WorkflowIntegration 통과. 임시 스크립트로 공백 경로의 CMD/PowerShell 버전 조회를 검증했고 실제 설치된 OpenCode 1.18.31도 읽기 전용 감지로 확인했다. 테스트는 설치 메서드를 실행하지 않는다.

## 2026-09-21: 실제 OpenCode 상태 연결·터미널별 MD 경로

- OpenCode마다 전용 loopback 포트를 지정하고 네이티브 SSE `session.status`와 permission/question 이벤트를 구독한다. OpenCode 경로에서는 화면 내용·물음표·침묵에 따른 상태 추정을 호출하지 않는다. 제출은 회색 요청 전달 중, busy/retry는 녹색, idle은 회색, 승인·질문 요청은 주황색이다. 상태 연결 실패는 회색 상태 확인 불가다.
- 초기 상태 및 재연결은 GET 상태/승인/질문 목록으로 동기화한다. 끊어진 연결의 오래된 상태는 폐기하며 재연결 idle은 현재 대기로만 표시하고 놓친 작업을 완료로 단정하지 않는다. 조회 실패는 연결을 다시 시도한다. 조회 중 도착한 SSE 이벤트가 오래된 응답에 덮이지 않도록 버전을 비교한다.
- OpenCode 배경 SGR만 제한적으로 처리해 실제 xterm 셀에도 상태 배경을 적용한다. 전경 RGB·인덱스 색상과 스타일, 여러 출력 청크로 나뉜 제어 시퀀스는 보존한다.
- MD 선택/새 MD로 각 터미널의 플랜 경로를 지정한다. 미지정 세션은 고유 경로를 만들고 명시 선택만 프로젝트+번호별로 기억한다. 경로 변경 시 이전 파일과 초안을 보존하며 자동화 팝업을 연 뒤 연결이 바뀌면 저장/실행을 차단한다.
- 별도 검토에서 문서 덮어쓰기 경로는 발견하지 않았다. 상태 검토에서 발견한 재연결 후 오래된 승인/요청 대기 유지, 초기 조회 실패 후 무재시도, UI에 늦게 도착한 상태 알림 문제를 수정했다. 포트는 OS에서 빈 포트를 얻은 뒤 CLI가 바인딩하므로 짧은 경쟁 구간은 남으며 연결 실패를 진행 상태로 표시하지 않는다.
- 검증: Release 빌드 경고·오류 0. Documents, TabDocuments, Conversation, WorkflowIntegration, UI, LayoutCompact, OpenCodeStatus, TuiViewport 통과. TuiViewport는 실제 PNG 픽셀의 배경색과 xterm 셀의 전경색을 검사한다. OpenCodeBridge는 설치된 OpenCode 1.18.31의 실제 TUI에서 사전 API 호출 없이 네이티브 idle 연결을 확인하고, `Hi How can I help you today?` 화면 문구가 상태를 바꾸지 않음을 검증했다. `/session/status`, `/permission/`, `/question/`, `/event`의 실제 응답도 확인했다. 실제 모델 호출은 하지 않았다. 최초 조회 전에 연결이 끊긴 경우도 새 idle 조회가 오래된 요청 대기를 해제하는 회귀 테스트를 추가했다.

## 2026-09-21: OpenCode 전용·단일 플랜·상태 판별

- 사용자 실행 도구를 OpenCode로 고정하고 다른 도구 선택·사용자 명령 입력을 제거. 레거시 설정 숫자는 역호환을 위해 유지하고 폴더·사용자 제목을 보존해 정규화.
- 터미널별 plan.md 하나를 직접 편집하며 저장 후 실행과 자동화 창이 같은 파일을 사용. 각 역할에 원본 경로와 최신 MD 재읽기 지침 전달.
- 외부 파일 변경과 팝업을 연 뒤 변경된 초안의 덮어쓰기를 차단. 초기 비동기 문서 로드와 팝업 저장 경합을 별도 검토.
- OpenCode의 빠른 응답과 제출 전부터 남은 interrupt 안내를 상태 판별에 반영. 실제 응답·안정된 입력창 복귀를 확인하며 사용자 입력 echo, 조용한 작업, 창 크기 변경은 완료 근거에서 제외.
- opencode 자동 설치 버튼은 기본값 복원 왼쪽의 노란색 버튼. 실제 설치 로직은 auto-install.cs 미연결 stub 유지.
- Release 빌드 경고·오류 0. Documents, TabDocuments, LaunchModes, TerminalActivity, AutomationCommand, Conversation, UI, LayoutCompact, WorkflowIntegration 검증 통과. 테스트 OpenCode로 팝업 실행 2회와 오른쪽 저장 후 실행 1회, 총 9개 역할 실행을 확인했고 각 실행의 504 토큰 집계·플랜 경로·최신 내용 읽기·외부 변경 충돌을 검증했다. 설정된 제공자 호출은 하지 않고 테스트 CLI로 자동화 경로를 검증한다.

최종 갱신: 2026-09-19 / Windows x64 / .NET SDK 10.0.401

## 1차: 구현별 검토

- Markdown 저장 서비스: UTF-8/UTF-16, BOM, 외부 변경, Save As 충돌, 실패 후 상태, 파일 크기 제한을 검증.
- ConPTY: 부모 출력 핸들 유입 문제 발견 및 수정. STARTF_USESTDHANDLES로 실제 출력이 pseudoconsole에 연결되도록 수정.
- 입력 쓰기를 단일 소비자 큐로 분리해 대량 붙여넣기가 WPF UI 스레드를 막지 않도록 수정.

## 2차: 통합 코드 검토

- 터미널 시작이 파일 탐색 완료를 기다리지 않도록 수정.
- 문서 읽기/저장과 미리보기 변환을 백그라운드에서 수행. 저장 중 편집을 잠가 데이터 경합 방지.
- 줄바꿈 입력 및 붙여넣기가 문서의 기존 LF/CRLF/CR 스타일을 따르도록 수정.
- 앱 종료 확인 및 비동기 저장 흐름을 재검토.
- 최초 창 크기를 기본 모니터 작업 영역에 맞게 제한.
- 미리보기 내부 data URL을 정확히 허용하고 외부 이동을 차단하도록 수정.

## 3차: 터미널·브라우저 수명 검토

- 출력 큐를 고정 문자열 조각 큐로 변경해 큰 StringBuilder의 반복 이동 비용 제거.
- xterm 렌더 완료 응답을 받아 브라우저 출력 적체를 제한.
- WebView2 실패 시 접근할 수 없는 셸이 계속 남지 않도록 해당 터미널 종료.
- AltGr와 반복 키 입력이 앱 단축키로 잘못 처리되지 않도록 수정.
- Ctrl+V / Ctrl+Shift+V / Shift+Insert 붙여넣기를 같은 확인 경로로 통합. 외부 드롭 차단.

## 실행한 테스트

- Release 빌드: 통과.
- Markdown 파일 서비스: 7개 시나리오 통과.
- 네이티브 ConPTY 통합 테스트: 한글·emoji, 공백 포함 작업 폴더, resize, 자식 환경 변수, Ctrl+C, 종료 코드, 프로세스 트리 종료 및 종료 지연 확인 통과.
- 최신 WPF UI 통합 테스트: 실제 WebView2/xterm/ConPTY 연결, 한글 왕복, 자동 1·2·3·4·6·8 배치, 오른쪽 사용자 입력 이력, 터미널별 초안, 미니 모달, 큰 제목, 우클릭, 트레이, 설정·도구 전환·종료 통과. 이전 Markdown 편집 UI 검증은 현재 사용자 입력 패널 검증으로 대체했다.
- UI 레이아웃 및 터미널 화면 캡처를 확인.
- 작업 기록·자동화 상태 테스트: 질문 비저장, 프로세스 재생성 뒤 중복 감지, 동시 예약 직렬화, 손상 원장 보수적 실패, 예약 단일 클레임, 일시정지·중단 후 늦은 완료 차단, 회차 상한, 중단 복구 8개 시나리오 통과.
- 입력 캡처·토큰 집계 테스트: 커서 편집·한글·여러 줄 붙여넣기, Ctrl+C 취소, 히스토리/TUI 모호 입력 생략, 알려진 토큰 이벤트 중복 제거 통과.
- 자동화 명령 테스트(실행기 전환 전): 실제 모델 호출 대신 가짜 `codex` CLI로 PowerShell 5.1 UTF-8, 공백·따옴표·한글 경로, stdin 전달, JSON 스키마 결과, JSONL 토큰 합산, Ctrl+C 완료 마커 검증 통과.
- OpenCode 실행기의 `AutomationCommand`, `InputCapture`, 최신 전체 UI와 `WorkflowIntegration`이 통과했다. WorkflowIntegration은 테스트용 OpenCode의 3역할 전송·504 토큰·직접 입력 기록 및 모달을 연 상태의 후속 단계 진행을 검증했다. 설정된 OpenCode 제공자나 실제 Codex 모델 호출은 수행하지 않았다.

테스트는 자체 임시 파일과 자체 프로세스만 사용한다. UI 테스트는 별도 설정 경로를 주입한다.

## 남아 있는 제한

- 오른쪽 타임라인은 선택 project+slot에서 사용자가 제출한 최근 200개 비자동화 입력과 현재 pane의 메모리 질문만 표시한다. 상태·결과와 전체 기록은 화면 이력에 섞지 않으며 필요하면 `terminal-notes` 폴더에서 확인한다.
- 창 X는 트레이에 숨겨 세션·입력 초안·메모리 질문을 유지한다. 명시적 프로그램 종료는 세션 확인 후 프로세스 트리를 정리한다. 완전 종료·재시작 후 프로세스/출력과 메모리 질문은 복원하지 않는다.
- 전체 Codex TUI의 마우스·포커스·IME 조합은 모두 자동화하지 않았다. DeX/RDP 한영 전환은 실제 접속 환경에서 별도 확인해야 한다.
- UTF-8과 BOM 있는 UTF-16만 지원하며 CP949·UTF-32는 지원하지 않는다.
- 미리보기는 실행 가능한 HTML, 외부 이미지 및 링크 이동을 허용하지 않는다.
- 초기 창 배치는 기본 모니터 작업 영역 기준이다.
- 메인 화면은 820×480, 설정은 720×480 DIP까지 배치를 검증했다. 그보다 작은 작업 영역과 실제 DeX DPI 조합은 별도 확인이 필요하다.
- 대량 출력이 호스트 큐 제한을 초과하면 화면 출력 일부를 생략하며 안내를 표시한다.
- 파일 충돌 검사는 일반 외부 수정을 방지한다. 외부 프로그램이 동시에 파일 이름을 원자적으로 교체하는 극단적인 경합까지 완전한 파일 잠금을 제공하지는 않는다.

## 트레이 세션 유지 변경 검토

- 창 X/Alt+F4와 명시 종료를 분리하고, 복원 시 초기화를 중복 실행하지 않도록 했다.
- 터미널 제목·우상단 터미널 선택 목록·트레이 메뉴에서 전체 닫기를 제공한다. 전체 닫기는 문서를 변경하지 않는다. 터미널 본문(WebView)의 오른쪽 마우스 메뉴는 xterm 복사·붙여넣기·전체 선택·선택 해제·화면 지우기만 제공한다.
- 독립 검토에서 발견한 종료 중 숨김 경합, 개별 자원 해제 실패의 전파, Windows 종료 분기를 보완했다.
- UI 통합 검증: 숨긴 동안 명령 실행, 동일 세션 복원, 미저장 문서 유지, 실제 브라우저 contextmenu 이벤트, 전체 세션 종료 및 새 세션 생성 통과.
- 트레이 물리적 마우스 조작과 실제 Windows 로그오프는 자동화하지 않는다. 수명 관리와 시스템 종료 처리 메서드를 UI 테스트에서 호출한다.

## 폴더 트리 · 분할 디스패처 · 화면 개편

- 프로젝트 Markdown 목록을 폴더 트리로 변경. 검색 시 조상 폴더를 펼치고 동일 스캔의 확장/선택 상태를 보존한다.
- 1/2/4/8 벡터 아이콘으로 배치를 변경한다. 분할 시 문서 패널까지 전체 폭을 사용하며, 1칸 복귀 시 이전 패널 상태를 복원한다.
- 2/4/8 선택 시 부족한 터미널을 생성한다. 기존 3개 + 새 5개 = 8개, 작은 분할 선택 시 전체 세션 유지 시나리오를 실제 UI에서 검증했다.
- 상태 배경은 진행 녹색, 질문·선택 요구 주황색, 그 외 동작 없음 회색의 세 가지로 단순화했다. 완료·오류·한도는 레이블로 구분하며 조용함을 실행 종료나 입력 대기로 단정하지 않는다.
- 독립 검토로 native focus/DOM focus 경합, 비치명적 오류 상태 오염, 출력 ACK 전송 실패 후 정지를 수정했다. 화면 tail 관측은 200ms로 합쳐 대량 출력 부하를 제한한다.
- UI 통합 테스트 통과: 폴더 검색·Enter 열기, 기존 저장/미리보기, 8개 동시 ConPTY, 추가 생성과 인스턴스 유지, 분할 클릭 명령 대상, 출력/프롬프트 상태, 페이지 전환, 분할 전체 닫기, 트레이 유지, 시스템 종료 처리.
- 상태 분류 독립 테스트 통과. Release 빌드 경고/오류 없음.
- 1480×920 및 1024×700 실제 앱 PrintWindow 캡처를 확인했다. 명시적 대비, 메뉴·트리 선택, 자동 줄바꿈 세션 버튼, 분할 아이콘 및 카드 닫기를 적용했다.
- 전체 Codex TUI 상태 판별과 DeX 물리 키보드는 완전 자동 검증 범위가 아니다. UI 자동 생성 테스트는 외부 모델 호출 없이 일반 PowerShell로 실행한다.

## 통합 설정 · 실행 프로필 · 상태 배경

- 상단 설정 모달에서 기본 작업 폴더와 1~8번의 제목/Codex/Claude Code/PowerShell/OpenCode/사용자 명령/개별 폴더를 저장한다. 저장만으로 세션을 실행하거나 기존 문서를 교체하지 않는다.
- 번호별 폴더가 비었으면 기본 폴더, 상대 경로면 기본 폴더 기준. 기본 폴더는 모달 저장 전에 절대 경로 변환/존재 검증한다. 번호별 폴더는 생성 전에 확인한다.
- 커스텀 명령은 child-only UTF-8 Base64 환경 값으로 전달하고 고정 부트스트랩에서 실행한다. 사용자 명령과 작업 폴더를 프로세스 인수로 직접 조합하지 않는다.
- native 테스트: 실제 외부 AI 실행 없이 Codex/Claude/OpenCode 명령 스텁, 인수·공백 경로·한글·오류 후 셸 재사용 통과.
- UI 테스트: 설정 모달 저장, 실행 즉시 발생하지 않음, 프리셋 매핑, 사용자 명령 실제 실행, 개별 폴더/기본 폴더 fallback, +셸 override, 저장 설정 재시작 복원 통과.
- 상태 변화는 각 카드의 제목/상태 및 WPF/WebView/xterm 본문 배경에 동기화한다. 현재 배경 규칙은 진행 녹색·선택 요구 주황색·그 외 회색이다.
- 모달 중 슬롯 변경 재진입을 확인하고 새로운 슬롯/폴더로 잘못 실행되지 않게 막았다. 프로필 제목의 UTF-16 surrogate 경계도 보완했다.
- Release 빌드 경고/오류 0. ConPTY, 활동 분류, 전체 UI 통합 검증 PASS. 기존 사용자의 실행 중 앱/세션은 테스트에서 종료하지 않았다.

## 우상단 명령 통합 · 권한 모드 · 반복 UX 검토

- 메인 명령을 우상단 한 줄로 통합하고 전체 세션 목록은 선택 메뉴로 이동했다. 분할 카드의 별도 닫기 버튼은 더보기로 옮겨 제목·상태와 터미널 공간을 확보했다. 문서 생성/열기/미리보기/경로 복사와 종료 관련 기능도 더보기에서 접근한다.
- 설정은 왼쪽 슬롯 선택, 오른쪽 편집으로 변경했다. 저장/취소/복원은 우상단에 두고 작은 창에서는 폼과 슬롯 목록을 스크롤한다.
- Auto/YOLO/Bypass는 기본 해제, 상호 배타, 도구별 지원 여부에 따라 활성화한다. 도구 변경/잘못된 저장 값은 Standard로 정규화하며, 기존 세션에는 적용하지 않는다.
- 사전 UX 검토, 실제 화면 캡처, 구현 독립 검토를 거쳤다. 설정 Run.Text의 TwoWay 읽기 전용 바인딩 충돌, 어두운 문서 트리 제목, 고배율 화면 최소 크기, 빈 세션 선택기의 이전 툴팁, 시작 직후 중단 버튼 갱신을 수정했다.
- Release UI 전체 통합 PASS: 8개 ConPTY 세션과 트레이 보존, 문서 저장, 분할 페이지 전환, 1024×700 및 820×480 명령 바 경계, 더보기 기능 접근, 세션 선택기, 720×480 설정 폼, 실제 체크박스 선택/초기화, 모드 저장/명령 매핑/재시작 복원.
- 모드 조합 및 JSON 호환 독립 테스트 PASS. native 통합은 전용 스텁 PATH에서 각 CLI의 지원 플래그와 미지원→Standard 전달을 검증했다. 실제 외부 AI 작업은 실행하지 않았다. 기존 문서 보존 및 활동 분류 테스트도 PASS.

## 더보기 제거 · 즉시 생성 · 제목 더블클릭

- 더보기 버튼과 팝업 구현을 제거했다. 문서 생성/열기/미리보기는 우상단 버튼으로 접근한다. 세션 관리는 터미널 제목·우상단 터미널 선택 목록·트레이 메뉴에 유지하고, 터미널 본문 우클릭은 xterm 조작 전용으로 분리한다. 저장 단축키는 유지한다.
- +터미널 및 Ctrl+Shift+T는 저장 프로필과 기본 제목으로 즉시 생성한다. 이름 입력 모달은 이름 변경 시에만 연다.
- 단일 보기와 분할 카드의 제목 더블클릭을 이름 변경에 연결했다. 선택한 카드의 세션/출력은 유지한다.
- Release UI 전체 통합 PASS: 즉시 생성과 기본 제목, 단일/분할 제목 더블클릭, 올바른 세션 유지, 더보기 없음, 820×480 및 1024×700 버튼 배치, 기존 트레이/문서/권한 모드 검증.

## 터미널별 중단 · 닫기 · 에이전트 전환

- 단일/분할 헤더에 에이전트 선택, 중단(■), 닫기(×)를 배치했다. 공통 중단 버튼은 제거했다. 에이전트 메뉴에서 해당 슬롯의 설정을 바로 열 수 있다.
- 에이전트 전환은 확인 후 대상 탭만 같은 슬롯에 새 세션으로 교체한다. 현재 작업 폴더와 사용자 제목은 보존하고 다른 터미널은 재시작하지 않는다. 새 도구가 지원하지 않는 권한 모드는 Standard로 정규화한다.
- Release UI 전체 통합 PASS: 실제 대기 명령 중단 후 셸 재사용, 다른 세션 유지, Custom→PowerShell 전환 시 대상 프로세스만 종료/생성, 슬롯·폴더·제목·도구 저장, 개별 닫기 대상 검증, 기존 저장/권한/트레이/8분할 검증.
- 820×480 실제 캡처에서 모든 카드의 도구/중단/닫기와 제목·상태가 잘리지 않는 것을 확인했다. 외부 AI 실행 없이 일반 셸로 통합 검증했다.
- 독립 수명 검토에서 타세션 종료/누수 blocker 없음. 추가로 기본 도구명과 같은 제목도 보존하고, 저장 실패 상태가 성공 문구로 덮이지 않도록 순서를 수정했으며 슬롯 프로필 정렬을 유지했다.

## 상태 오탐 수정 · 상태 색상 분리 · 문서 내부 명령

- raw ConPTY 바이트 수신 대신 ANSI가 적용된 렌더링 화면 내용을 비교한다. 같은 텍스트 재그리기, 커서/색상 제어, 리사이즈는 진행 출력으로 새로 계산하지 않는다. xterm의 커서 위치/장치 응답을 사용자 명령과 구분한다.
- 일반 프롬프트와 동작 없음은 회색, 질문·승인·선택 요구 NeedsInput은 주황색, 실제 화면 변화와 진행 안내 Active는 녹색으로 표시한다. 완료·오류·사용량 한도는 회색 배경의 레이블로 구분한다. 진행 안내 없는 조용한 계산과 새 CLI 오류 문구까지 완전한 의미 판별을 보장하지 않는다.
- 문서 저장/새 문서/열기/미리보기를 NotesPanel 내부 DocumentCommandBar로 옮겼다. 문서 패널 숨김과 함께 숨겨지고 저장/열기 작업 중 버튼 비활성화를 유지한다.
- 결정적 상태 테스트 PASS: 동일 재그리기 및 리사이즈 오탐 방지, 자동 커서 응답, 질문/승인, busy 표시, 영문/한글 한도 오류와 정상 토큰 통계 구분, idle 복귀.
- 이전 WebView/xterm UI 통합에서 진행·idle·질문·한도 분류와 동일 ANSI 재그리기 시 idle 유지를 확인했다. 이후 배경 표시는 녹색·주황색·회색의 세 가지 규칙으로 교체했다.

## 초기 실행 대기 오탐 수정 (2026-09-15)

- 초기 배너, 입력 예시 갱신, 명령 제출 전 타이핑을 작업 진행으로 세지 않는다. 제출된 작업 또는 실제 busy 신호가 있어야 화면 변화가 녹색 진행 상태가 된다.
- 입력창의 커서 앞 프롬프트를 별도로 전달해 커서 뒤에 제안 문구가 있어도 대기 상태로 분류한다. 단축키 도움말만 있는 esc 안내는 시작 시 busy 신호로 취급하지 않는다.
- 명령 완료 프롬프트에서 제출 상태를 해제한다. 사용자 지정 시작 명령과 자동 재개 작업의 명시적 진행 표시는 계속 지원한다. 질문/승인/토큰 한도는 초기 실행에서도 별도로 표시한다.
- 결정적 상태 테스트 및 전체 Release UI 통합 PASS. 실제 xterm 배너/제안 프롬프트 fixture에서 녹색 전환이 0회임을 확인했고, 기존 진행/질문/한도/중단/세션 교체/문서 보존 검증도 통과했다.

## 작업표시줄에 가려지지 않는 창 크기 (2026-10-04)

- `MainWindow.WindowBounds.cs`의 `ApplyInitialWindowBounds`에서 현재 창이 위치한 모니터의 작업 영역을 픽셀에서 WPF DIP로 변환해 저장된 크기를 제한하고 가운데 배치한다. 화면이 최소 창 크기보다 작으면 해당 화면에 맞춰 최소 크기도 조정한다. `MainWindow.xaml.cs` 생성자와 `Window_Loaded`에 초기화·창 메시지 연결·종료 시 해제를 추가했다.
- 최대화 시 Windows의 외곽 프레임이 작업 영역 바깥으로 확장되는 현상을 실제 창 좌표로 확인했다. `WindowBoundsHook`의 최대화 크기 처리와 `QueueMaximizedWindowBoundsCorrection`의 내용 여백 보정으로 제목 표시줄부터 하단까지 작업 영역 안에 배치한다. 여백은 실제 클라이언트 화면 좌표와 작업 영역의 차이로 계산하며, 원래 크기로 복원하면 기존 여백도 복원한다. 위치·DPI·작업 영역 변화 후 다시 계산하고 누적하거나 터미널을 재생성하지 않는다.
- `tests/UI/Program.cs`에 실제 창/모니터/내용의 화면 좌표 검사를 추가했다. 집중 WORKAREA 검사는 저장된 10000×10000 크기의 자동 축소, 최대화 버튼과 직접 상태 전환, 1000×700 일반 창 복원 및 여백 복원을 확인한다. 전체 UI 검사는 최대화·복원 후 기존 터미널이 계속 실행되는지도 확인한다.
- Documents 7개, 집중 WORKAREA, 전체 Release UI 검사 통과. 1920×1080 모니터에서 작업 영역과 최대화 내용 영역은 모두 `[0,0,1920,1032]`였고, 일반 창은 `[220,56,1700,976]`로 정확히 복원됐다. Windows 외곽 프레임 좌표는 내용 영역과 구분해 기록한다. 실제 다중 모니터·혼합 DPI 환경은 이번 실행 검사에 포함하지 않았다. 별도 Luna 검토에서 추가 문제는 없었다.
- 최종 Release 빌드(경고·오류 0)와 win-x64 배포 완료. 실행기는 `artifacts/win-x64-workarea`를 사용하며 빌드/배포 DLL SHA-256이 일치한다: `FDBC35948E40B424D6DF6944D829C762B0D388D836A75327384DCF33F7DF4CD7`.

## 프로젝트 도구·플랜 버튼 정렬과 터미널 본문 표시 (2026-10-04)

- 최종 사용자 지시에 맞춰 태스크·아카이브 오른쪽 같은 행에 새로고침 아이콘, `load`, `new`를 배치했다. `MainWindow.ProjectTasks.cs`의 InitializeProjectTasks에서 기존 동작과 접근성 정보를 유지하고 표시만 변경했다. `MainWindow.TaskSidebar.cs`와 설정 기본값은 최소 200 / 기본 240 / 최대 380 DIP를 공유해 글자 버튼이 잘리지 않게 한다.
- `MainWindow.TabDocuments.cs`의 플랜 도구 모음은 자동 진행을 왼쪽, 저장·실행 버튼을 오른쪽 끝에 정렬한다. 문서 저장·실행·전송 및 활성화 상태 처리는 유지한다.
- `MainWindow.ProjectTasks.cs` / `MainWindow.ProjectTaskModes.cs`의 태스크 터미널과 `MainWindow.Terminals.cs` / `MainWindow.xaml`의 일반 터미널에서 개별 제목·상태·조작 줄을 접고 장식 테두리·여백을 없앴다. 기존 컨트롤/이벤트/세션은 보존하며 상태 갱신으로 장식이 다시 나타나지 않게 한다. 공통 터미널 선택·추가 헤더는 유지한다. 태스크 카드 드래그는 유지하고 터미널 본문을 이동 핸들로 사용하지 않는다.
- 재실행·중단·CMD 이어가기는 각 세션 선택 항목의 기존 우클릭 메뉴로 접근한다. CMD 메뉴는 정확한 TabItem과 기존 활성화 조건을 사용해 과거 역할별 세션도 선택할 수 있다. 태스크 우클릭에는 CMD 이어가기와 칸 비우기를 제공하며, 칸 비우기는 배치만 해제하고 실행 세션을 유지한다. 별도 Luna 검토에서 발견한 역할별 CMD 접근 누락을 보완했고 재검토에서 추가 문제는 없었다.
- Release 빌드(경고·오류 0), Documents 7개, 전체 UI 및 집중 COMPOSER 검사 통과. 수정한 UI 검사의 숨김 창 가시성 및 중첩 헤더 접근을 실제 트리에 맞췄다. 820 DIP 최소 창/200 DIP 태스크 패널과 1480 DIP 화면 렌더링에서 최종 버튼 문구·정렬·터미널 본문 표시를 확인했다. 사용자 앱이나 실제 모델 요청, 실제 CMD 이관은 이번 검증에서 실행하지 않았다.
- 실행기를 `artifacts/win-x64-clean-terminal`에 연결했다. 빌드/배포 DLL SHA-256 일치: `952E9F997293D92171869E495E3482CEB6821787FCB2441D9A0C13912C387CFC`.

## 패널 테두리 중앙의 접기·펼치기 버튼 (2026-10-04)

- 태스크와 플랜의 접기 버튼을 각 패널 오른쪽 경계의 세로 중앙으로 옮겼다. `MainWindow.xaml`의 FocusButton은 터미널 헤더에서 빠지고 NotesSplitter와 같은 열에 배치한다. `MainWindow.ProjectTasks.cs`의 InitializeProjectTasks와 SetTaskWorkspaceVisible은 두 경계 버튼을 구성하고 플랜 버튼을 해당 패널과 함께 이동한다.
- 경계 열은 18 DIP, 버튼은 18×40 DIP로 유지하며, 위아래의 실제 GridSplitter는 계속 인접 열의 너비를 조절한다. `MainWindow.TaskSidebar.cs`의 ApplyTaskSidebarState는 접힌 태스크 폭을 0으로 줄이되 펼치기 버튼을 남긴다. `MainWindow.xaml.cs`의 ApplyNotesVisibility도 플랜 경계 버튼을 보존한다. 기존 너비 저장·복원, 단축키, 툴팁, 문서·세션 동작을 유지한다.
- 렌더링 확인에서 발견한 이전 태스크 상단 버튼 중복을 `MainWindow.ProjectArchive.cs`의 CreateProjectArchiveTabs에서 제거했다. 생성 지점을 하나로 유지하며, 기존 UI 검사에 두 버튼이 실제 구분선과 같은 부모/열에서 세로 중앙에 있는지 확인하는 조건을 추가했다. 별도 Luna 검토 완료.
- Release 빌드(경고·오류 0), Documents 7개, 전체 UI 및 집중 COMPOSER 검사 통과. 접기/펼치기·너비 복원·접힌 플랜의 화면 모드 전환·초안/세션 보존을 확인했다. 820 DIP 최소 창과 1480 DIP 렌더링으로 중앙 버튼 및 상단 중복 제거를 확인했다. 마지막 화살표 크기·획 통일은 렌더링과 배포 빌드로 확인했다. 실제 사용자 앱이나 모델 요청은 사용하지 않았다.
- 실행기를 `artifacts/win-x64-edge-toggles`에 연결했다. 빌드/배포 DLL SHA-256 일치: `5CEC50441199ABF5B7CFB4F7680E28C417BBDED21861B268655C332F681CF35F`.

## 터미널 영역 오른쪽 배치 (2026-10-04)

- 화면을 왼쪽 태스크, 가운데 플랜, 오른쪽 터미널 순서로 변경했다. `MainWindow.xaml`은 일반 터미널과 플랜의 열 순서를 변경하고, `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`와 `SetTaskWorkspaceVisible`은 태스크 사이드바 다음에 플랜·터미널을 배치한다. 기존 NotesPanel·NotesSplitter·터미널 헤더를 재사용하며 세션과 문서 객체를 교체하지 않는다.
- 터미널 선택은 터미널 헤더 왼쪽, 새 터미널은 오른쪽을 유지한다. 플랜 접기 화살표는 플랜과 맞닿은 터미널 헤더 왼쪽으로 옮기고 방향을 맞췄다. `MainWindow.xaml.cs`의 `ToggleNotes`·`ApplyNotesVisibility`와 `MainWindow.ProjectTasks.cs`의 `SyncTaskPlanWidths`·`SetTaskPlanWidthsFromWorkspace`에서 접힘 상태와 두 열의 너비를 함께 보존한다.
- 별도 Luna 검토에서 발견한 접힌 플랜의 화면 모드 전환 시 너비 비율 변경을 수정했다. 두 GridLength 값을 그대로 옮기며 일반 터미널 모드의 구분선 이벤트가 태스크 모드의 이전 너비를 덮어쓰지 않도록 했다. 재검토에서 추가 문제는 없었다.
- Release 빌드 및 win-x64 배포 성공. Documents 검사 7개, 전체 UI 및 집중 COMPOSER 검사 통과. 실제 구분선 조절·접기 복원·접힌 상태의 화면 전환·초안·세션 보존을 검증했다. 820 DIP 최소 창/190 DIP 사이드바와 1480 DIP 화면을 렌더링해 배치를 확인했다. 사용자 앱과 실제 모델 호출은 사용하지 않았다.
- `artifacts/win-x64-terminal-right`에 배포하고 실행기를 연결했다. 배포 DLL과 빌드 결과의 SHA-256이 일치한다: `7C145619A162384203F6EC5380B15688E35B00731402099033C67B977982DDC3`.

## 패널 상단 컨트롤 한 행 배치 (2026-10-04)

- `MainWindow.ProjectArchive.cs`의 `CreateProjectArchiveTabs`와 `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`에서 새로고침·프로젝트 열기·새 프로젝트를 태스크·아카이브와 같은 행으로 이동했다. 190 DIP 최소 너비에서도 탭·아이콘·접기 화살표가 한 행에 들어간다. `MainWindow.TaskSidebar.cs`의 `UpdateTaskSidebarChrome`은 패널을 접으면 해당 아이콘도 함께 숨긴다.
- `MainWindow.xaml`의 `TerminalAreaHeader`에 터미널 선택을 왼쪽, 새 터미널과 플랜 접기·펼치기 화살표를 오른쪽에 배치했다. `MainWindow.ProjectTasks.cs`의 `SetTaskWorkspaceVisible`은 이 헤더 하나를 실제 표시 중인 터미널 영역으로 이동한다. 터미널·프로젝트 세션과 기존 이벤트는 유지한다.
- `MainWindow.Toolbar.cs`의 `UpdateToolbarState`에서 플랜 패널 상태에 따라 화살표 방향·툴팁·접근성 이름을 갱신한다. 기존 `ToggleNotes` 처리와 Ctrl+Shift+M 단축키를 사용한다.
- Release 빌드(경고·오류 0), Documents 7개 검사, 전체 UI 검사와 집중 UI 검사를 통과했다. 820 DIP 창/190 DIP 사이드바와 1480 DIP 창/기본 사이드바의 실제 WPF 캡처에서 배치와 잘림 여부를 확인했다. 미리보기 도우미에 사이드바 너비 옵션을 추가하고 기존 플랜 글자 버튼 검사를 벡터 아이콘·접근성 검사로 맞췄다. WorkflowIntegration의 같은 단언도 수정하고 해당 프로젝트의 빌드를 확인했으며, 전체 워크플로 테스트는 실행하지 않았다.
- `artifacts/win-x64-inline-controls`에 배포하고 실행기를 연결했다. 배포 DLL과 빌드 결과의 SHA-256이 일치한다: `4CFFB2CD7976C8F45148C55D18619AFC4C0301ED6E327A7EFE628C5D516B5987`.

## 태스크·아카이브 탭 구분과 도구 모음 정리 (2026-10-04)

- `MainWindow.ProjectArchive.cs`의 `CreateProjectArchiveTabs`와 `MainWindow.xaml`의 전용 `ProjectArchiveTab` 스타일에서 동일 너비의 탭, 둥근 상단 모서리, 선택 배경·테두리·파란 밑줄을 적용한다. 기존 라디오 버튼의 선택·키보드 동작과 필터 처리를 유지한다.
- `MainWindow.ProjectTasks.cs`의 `InitializeProjectTasks`에서 새로고침·프로젝트 열기·새 프로젝트를 탭 아래 한 줄의 아이콘 도구 모음으로 모은다. 기존 처리 함수와 접근성 이름, 사이드바 접기 동작을 유지한다.
- Release 빌드, Documents 7개 검사, 전체 UI 검사와 집중 UI 검사를 통과했다. 기존 검사가 190 DIP 사이드바와 40 DIP 태스크 행, 탭 전환·패널 접기·상태·제목 편집·드래그 동작을 확인한다. 독립 코드 검토에서도 회귀를 발견하지 않았다.
- 미리보기 캡처는 `Window` 대신 실제 WPF 콘텐츠를 렌더링하도록 테스트 도우미만 수정했다. 820 DIP 창의 캡처에서 탭 표시와 세 아이콘의 한 줄 배치를 직접 확인했다.
- `artifacts/win-x64-task-tabs`에 배포하고 실행기를 연결했다. 배포 DLL과 빌드 결과의 SHA-256이 일치한다: `9C1E69D243BC3464F6B1EFC5BD859BF7E0A7DED54225F675F699CD1B247B5287`.

## 태스크 한 줄 배치와 프로젝트 버튼 이동 (2026-10-03)

- 태스크 제목·재실행·시작/중단·프로젝트 폴더·반복 실행 체크박스를 한 행에 배치한다. 제목은 한 줄 말줄임과 툴팁을 사용하며, 기존 상태 판정·동작·제목 편집·드래그·예약 메뉴를 유지한다.
- 프로젝트 열기·새 프로젝트 아이콘은 오른쪽 플랜 패널에서 왼쪽 태스크 패널 상단으로 이동한다. 오른쪽 초기 안내 화면의 중복 버튼도 정리하고 왼쪽에서 시작하도록 안내한다. 기존 프로젝트 선택·생성 처리와 접근성 설명을 유지하며, 사이드바를 접으면 도구 버튼도 함께 숨긴다.
- 실행 중인 앱을 유지하며 `artifacts/win-x64-task-rows`에 배포하고 실행기를 연결한다.
- Release 빌드(경고·오류 0), Documents 7개 검사, UI 집중 검사와 최종 전체 UI 검사를 통과했다. 40 DIP 행의 제목·버튼·체크박스 정렬, 190 DIP 사이드바의 가로 스크롤 없는 배치, 제목 편집·드래그·상태·반복 실행 동작을 기존 회귀 검사로 확인했다. 최종 win-x64 배포 DLL과 빌드 결과의 SHA-256이 일치한다: `56012A9F00AB073BB10213D1653581FE79C45C8950068233FB29E9BDC3C44D21`.

## 태스크 상태 아이콘과 우측 패널 버튼 축소 (2026-10-03)

- 태스크 제목 아래의 상태 문구는 화면에서 숨기고 시작·중단 버튼에 현재 상태 아이콘을 표시한다. 기존 상태 판정과 클릭·활성화 조건을 사용하고 상세 상태는 툴팁과 접근성 도움말에 제공한다. 상태 갱신 때 같은 아이콘·활동 표시기를 재사용해 애니메이션을 계속 유지한다.
- 우측 패널의 프로젝트 열기·새 프로젝트는 32 DIP 아이콘 버튼으로 바꾸고 툴팁·접근성 이름·기존 이벤트를 유지한다. 저장·실행은 작은 글자 버튼으로 줄여 오른쪽에 정렬한다. 저장 후 실행과 충돌·비활성화 처리는 변경하지 않는다.
- 실행 중인 앱을 유지하며 `artifacts/win-x64-compact-actions`에 배포하고 실행기를 연결한다.
- Release 빌드·win-x64 배포와 Documents 7개 검사, 전체 UI 검사를 통과했다. 최종 상태 표시 수정 후 `COMPOSER_ONLY` 집중 검사도 통과했다. 저장·실행 동작과 최소 창 크기 관련 기존 검사를 유지했다. 배포 DLL의 SHA-256은 빌드 결과와 동일하다: `79DA49C32C8E737ED1189D86E555B5E1B5CD080717E98C6D9AED66825B026E3A`.
- 상태 표시 우선순위를 완료·오류·중단·입력 대기와 일치시키고, 수락된 요청의 상태 확인 중에는 중립 대기 아이콘을 표시한다. 숨겨진 테스트 창에서는 활동 표시기의 활성 상태를 확인하되 애니메이션은 정지하는 기존 동작을 유지한다. 집중 검사의 네이티브 liveness fixture는 현재 headless host 구조에 맞추고, 테스트 전용 숨김 대기 프로세스를 생성·정리하며 모델 요청을 보내지 않는다.
- 이 환경의 PrintWindow·RenderTargetBitmap 캡처는 검은 화면으로 나와 육안 확인 근거에 포함하지 않았다. 레이아웃은 기존 UI 검사와 소스 검토로 확인했다.

## 상단 메뉴와 태스크 목록 정리 (2026-10-03)

- `MainWindow.xaml`의 상태 모니터 버튼을 제거하고 상단 `CommandBar`를 오른쪽으로 정렬했다. 기존 내부 상태 모니터 처리와 나머지 메뉴 동작은 유지한다. 제거한 버튼을 요구하던 기존 테스트의 단언을 현재 UI에 맞췄다.
- 태스크·아카이브 탭 아래의 반복 목록 제목을 숨기고 트리 묶음을 기존 폴더 아이콘과 개수로 표시한다. 툴팁과 접근성 이름에는 현재 탭 이름·개수를 제공한다. 같은 트리 노드의 순서·선택·접기 상태를 유지한다.
- 목록 제목의 호스트만 목록 모드에서 숨기며, 프로젝트를 열면 다시 표시한다. 제목 편집기의 포커스 이탈 저장은 기존 경로를 사용한다. 독립 검토에서 편집 중 이름을 취소하던 초기 변경을 제거하고 보존 동작을 확인했다.
- 실행 중인 기존 앱과 세션을 유지하기 위해 새 배포 경로 `artifacts/win-x64-menu-ui`를 사용하며 `Start-TerminalNotes.cmd`가 이 배포본을 연다.
- Release 빌드(경고·오류 0), `Documents`, 전체 `UI`, `ProjectArchive` 검증을 통과했다. 실제 렌더링 이미지에서 메뉴 오른쪽 정렬·상태 모니터 버튼 제거·폴더 아이콘 표시를 확인했다.

## 공통 터미널 엔진과 네이티브 화면 (2026-10-03)

- `TerminalEngine`에 OS와 UI에 의존하지 않는 .NET 8 ANSI/VT 해석·화면 버퍼·입력 인코딩을 추가했다. UTF-8 분할 수신, 제어 문자열 길이 제한, DEC 문자·탭·스크롤 영역, 대체 화면, 커서·크기 변경과 스크롤백 순서의 회귀 검증을 포함한다. 완전한 VT 구현이나 전체 문단 재줄바꿈을 주장하지 않는다.
- 일반 셸은 `Controls/NativeTerminalSurface.cs`의 WPF 화면을 기본으로 사용한다. 기존 입력 보호·기록·전송 확인 경로를 공유하고, 기존 OpenCode API 대화 화면은 보존한다. 레거시 xterm 경로는 환경변수로 선택하며 해당 내부를 검사하는 테스트가 명시적으로 선택한다.
- `TerminalTransport`에 바이트 기반 공통 프로세스 API, Windows ConPTY, Linux/macOS용 네이티브 PTY helper를 추가했다. Windows 실행 파일을 자식 환경의 PATH에서 명시적으로 해석한다. 입력 실패 통지, 순차 쓰기, 소유 프로세스 종료 처리를 검토했다. Unix helper는 이 Windows 호스트에서 컴파일·실행하지 못했으므로 해당 플랫폼의 동작 검증은 남아 있다.
- `TerminalDesktop`은 같은 엔진을 사용하는 Avalonia 독립 앱이다. WebView를 사용하지 않으며 선택·복사·붙여넣기, 새 터미널 창, IME 입력 경로, 뷰포트만 그리는 화면과 입력/출력 수명 처리를 구현했다. 엔진 접근은 공통 잠금으로 직렬화한다.
- 기본 PowerShell 입력·출력 인코딩을 UTF-8로 맞췄다. 기존 셸 프로필·사용자 명령 전달 조건은 유지했다. 기존 `Terminal` 테스트와 `Documents` 테스트, 전체 레거시 `UI`, 실제 OpenCode API `HeadlessPane`, Release 빌드는 통과했다. WPF 네이티브 화면은 한글·일본어·emoji, 실제 Enter 경로, 중복 없는 전송, 크기 변경, 전체 선택·복사, 두 세션의 독립 수명을 검증했다.
- 실제 OpenCode TUI는 별도 임시 홈·설정·작업 폴더에서 실행했다. 모델 요청을 제출하지 않은 초안을 입력하고 여섯 차례 크기를 바꿔 프로세스와 초안 보존을 확인했다. 독립 앱은 실제 TextInput/Enter, Unicode 출력, 20회 크기 변경, 별도 창과 종료 소유권 검증을 통과했다. 이 결과는 Windows 10 실기기 검증이나 Linux/macOS 실행 검증을 대체하지 않는다.

## 작업 기록 · 입력 모드 · 소형 대시보드 (2026-09-19)

- 선택 터미널별 입력 모드는 질문을 기본값으로 두고 플랜·빌드·검토·명령을 추가했다. 질문은 앱의 Markdown·JSON·해시에 넣지 않으며, 질문 외 입력은 프로젝트 `terminal-notes` 아래 사람용 Markdown과 기계 원장에 원문·모드·슬롯·시각·상태를 저장한다.
- 같은 프로젝트와 모드의 정규화된 동일 원문을 앱에서 다시 제출하면 전송 전에 차단한다. 수동 재실행 체크는 사용자의 명시적 우회이며 자동화는 중복을 강제로 우회하지 않는다.
- raw 터미널 입력은 확실한 제출 문장만 복원한다. 셸 히스토리 탐색이나 알 수 없는 TUI 모드가 섞이면 기록을 생략하며, 직접 입력된 명령은 관찰 전에 이미 셸로 전송되므로 사전 차단을 보장하지 않는다.
- 작업 원장과 자동화 상태는 프로젝트별 잠금과 임시 파일 교체로 저장한다. 손상된 JSON은 덮어쓰지 않고 실패하며, 외부 변경 충돌을 확인한다. 질문 경로는 디렉터리 생성과 정규화보다 먼저 반환한다.
- 1·2·3·4·6·8 분할을 지원한다. 소형 대시보드가 선택한 기존 `TerminalPane`을 모달로 옮겼다가 닫을 때 원래 부모에 복원하므로 셸이나 WebView를 새로 만들지 않는다.

## 승인 플랜 3역할 자동화 · 예약 — Codex 실행기 이력 (2026-09-19, 현재는 대체됨)

- 승인된 플랜과 모델·회차·토큰·시간 제한을 실행별 고정 스냅샷으로 저장한다. 기본 모델은 지휘 `gpt-6-astra`, 구현·검토 `gpt-5.6-sol`이고 각 단계는 `model_reasoning_effort="high"`로 실행한다.
- 지휘 → 구현 → 검토를 한 프로젝트에서 한 단계씩 실행한다. 검토의 구조화된 `complete=true`만 전체 완료로 인정하며, 미완료 검토는 회차 상한 안에서 다음 지휘 단계로 돌아간다.
- 단계는 전용 터미널에서 `codex exec --json --output-schema`로 실행한다. 프롬프트와 스키마는 파일로 전달하고, 셸 문자열에 사용자 원문을 보간하지 않는다. 구현 근거는 [OpenAI 공식 비대화형 모드 문서](https://developers.openai.com/ko-KR/docs/non-interactive-mode)와 일치한다.
- 예약은 현지 `DateTimeOffset`으로 저장한다. 앱 또는 트레이 프로세스가 살아 있어야 시간에 맞춰 실행되며, 종료 중 지난 예약은 다음 시작 때 한 번만 클레임한다. 실행 중 종료된 토큰은 시작 시 일시정지로 회수하고 사용자가 기록을 확인해 재개하도록 한다.
- 일시정지는 실행 중 단계 결과를 받은 뒤 후속 클레임을 만들지 않는다. 중단은 디스패치 토큰을 무효화해 늦은 완료가 상태를 바꾸지 못하게 한다. 후속 클레임은 단계 완료 저장 직후 큐에 넣어 저널 상태 갱신 실패로 잃지 않게 했다.
- 최대 회차와 토큰 예산은 단계 경계에서 확인하고, 시간 제한은 실행 중인 전용 터미널에 적용한다. 회차·토큰 한도 도달은 완료로 가장하지 않고 일시정지 사유를 남긴다.
- 여러 앱 인스턴스가 같은 예약을 실행하지 않도록 사용자 설정 위치의 스케줄러 잠금을 보유한 인스턴스만 복구·클레임한다. 일시적으로 사용할 수 없는 프로젝트 경로도 검색 목록에서 삭제하지 않는다.
- `Automation`, `InputCapture`, `AutomationCommand` 테스트는 통과했다. `AutomationCommand`는 가짜 CLI만 사용했으며 실제 OpenAI 모델/API 호출은 수행하지 않았다. 신규 UI 및 `WorkflowIntegration` 검증은 보류 상태다.

## OpenCode 기본 실행 · 설정된 Codex 모델 연결 · 제목 확대 (2026-09-19, 현재)

- 최신 사용자 운영 환경에 맞춰 새 기본 터미널 도구를 OpenCode로 바꿨다. 설정이 없는 경우 `Z:\codex`에서 OpenCode를 시작한다. 과거 기본 Codex·Claude 프로필은 설정 버전으로 한 번만 OpenCode에 옮기고, 이후 사용자가 직접 바꾼 도구 선택은 보존한다.
- 3역할 자동화는 별도 `codex` CLI 프로세스를 만들지 않는다. 고정된 프롬프트 파일을 stdin으로 전달해 `opencode run --format json --auto`를 실행하고, OpenCode JSON 이벤트의 마지막 `text` 부분을 구조화된 결과로 검사한다. `step_finish` 토큰은 중복 ID를 제거해 합산한다.
- 역할 모델 입력의 초기값은 모두 비어 있다. 빈 값이면 `--model`을 전달하지 않아 OpenCode 설정의 기본 모델을 상속한다. 역할별 재정의가 있을 때만 `provider/model`을 `--model`에 전달하며, Terminal Notes는 특정 모델이나 reasoning variant를 강제로 지정하지 않는다.
- 이전 실행에 남은 `gpt-6-astra` 같은 제공자 없는 모델명은 임의의 모델로 변환하지 않는다. `PrepareAsync`가 CLI 시작 전에 형식을 거부하고 실행을 Failed로 기록하며, 모델을 비우거나 정확한 `provider/model`로 새 플랜을 승인하라는 안내를 남긴다.
- 의도한 경로는 **Terminal Notes → OpenCode 클라이언트 → OpenCode에 설정된 Codex 모델**이다. 이를 위해 OpenCode의 OpenAI 제공자 자격 증명과 원하는 Codex 모델이 먼저 구성되어 있어야 한다. 빈 모델 입력만으로 임의 환경에서 Codex 모델이 자동 선택된다고 주장하지 않는다. 근거: [OpenCode CLI](https://dev.opencode.ai/docs/cli/), [모델 선택 우선순위](https://dev.opencode.ai/docs/models/), [OpenAI 제공자 설정](https://dev.opencode.ai/docs/providers/#openai).
- 터미널 제목은 이전 크기의 약 5배로 확대했다. 단일 화면과 소형 터미널 제목은 65 DIP, 분할 카드 제목은 60 DIP다. 기존 `TerminalPane` 재배치와 세션 수명은 그대로 유지한다.
- 작업 입력과 이력은 오른쪽 대화 패널에 항상 유지한다. 터미널 분할 수가 바뀌어도 자동으로 접지 않는다.
- 작업 입력 분류, 질문 비저장, 프로젝트 원장, 중복 차단, 승인 플랜 스냅샷, 예약 단일 클레임, 일시정지·중단 상태 전이는 기존 동작을 유지한다.
- 가짜 OpenCode CLI 기반 `AutomationCommand` 검증 PASS. 정확한 인수·작업 폴더·UTF-8 stdin, 빈 모델의 `--model` 생략, 기존 bare 모델의 명시적 실패, JSON 이벤트·오류, Ctrl+C 완료 마커를 확인했다.
- `InputCapture` 및 전체 `WorkflowIntegration` PASS. 기본 OpenCode 시작과 기존 프로필의 1회 이전, 사용자 제목·폴더·지원 권한 보존, 직접 입력의 한글·프로그램 전송 경계, 질문 비저장·플랜 Markdown·직접 빌드 기록을 확인했다. 두 실행에서 각각 3역할·504토큰·완료 마커 3개, CLI 총 6회와 역할별 누적 336토큰을 검증했다.
- 모달로 관찰하는 동안 준비된 기존 팀에 다음 단계 명령을 전달하도록 수정했다. 이 경로에서는 팀 생성·선택·화면 재배치를 건너뛰고 전송 전 대상의 생존과 역할 매핑을 재확인한다. 독립 검토와 모달을 계속 연 두 번째 실행의 전체 완료·동일 pane 유지 검증 PASS.
- `LayoutCompact` 및 전체 Release `UI` PASS. 60/65 DIP 제목, 최소 820×480의 4·8분할, 소형 대시보드·모달 복귀, 문서 보존, 트레이, 개별 중단·전환·닫기·종료를 확인했다. 기본 권한을 Codex로 가정하던 이전 테스트는 OpenCode 지원 항목을 먼저 검증하도록 갱신했다.

## 미니 그리드 · 응답 완료 상태 (크기·제목 변경 검증 완료)

- 소형 창은 현재 메인의 1·2·3·4·6·8 행·열과 페이지를 반영한 사각형 타일 그리드다. 8개 기준 크기는 기존 220×100 DIP에서 가로·세로 각각 2배인 440×200 DIP로 변경했으며, 항상 위 무테 창과 세션 타일 identity를 유지한다. 배치별 플로팅 창 크기는 1칸 240×128, 2칸 280×128, 3칸 344×128, 4칸 280×200, 6칸 344×200, 8칸 440×200 DIP다. 작은 드래그 헤더에 ↗ 전체 화면 복귀와 × 닫기, 현재 페이지 밖 세션의 이전·다음을 제공한다.
- 타일에는 번호와 전체 제목을 표시한다. 제목은 최대 2줄로 줄바꿈하고 더 길면 말줄임과 툴팁으로 전체 이름을 제공한다. 진행 녹색·질문/선택 요구 주황색·끝남 회색 상태색은 유지한다. 상태·토큰 세부 정보는 툴팁 또는 클릭한 터미널 모달에서 제공한다. 메인 단일 화면·터미널 모달 65 DIP, 분할 카드 60 DIP 제목은 그대로 유지한다.
- 타일 클릭은 기존 `TerminalPane`을 대화형 모달로 옮긴다. 모달을 닫으면 같은 세션을 미니 그리드로 돌려보내며 새 셸이나 WebView를 만들지 않는다.
- 터미널 추가 시 세션 수를 1→1칸, 2→2칸, 3→3칸, 4→4칸, 5/6→6칸, 7/8→8칸으로 매핑해 메인·미니 배치를 함께 자동 맞춤한다. 자동 맞춤은 새 세션을 만들지 않아 5개일 때 6칸 중 한 칸을 비워 둔다. 사용자가 분할 버튼을 직접 선택한 경우에만 기존 부족 세션 자동 채움을 실행한다.
- 상태 배경은 진행 녹색, 질문·선택 요구 주황색, 그 외 회색으로 구분한다. 완료·오류·사용량 한도는 레이블로만 구분한다.
- `HasCompletedWork`는 자동화 단계의 확정 결과 또는 사용자 제출 뒤 명확한 PowerShell 프롬프트 복귀 근거가 있을 때만 설정한다. 무출력·침묵은 완료 근거가 아니며, **응답 완료**는 응답이 끝났다는 뜻일 뿐 작업 성공을 보증하지 않는다. 완료 타일 툴팁에도 이 한계를 표시한다.
- 이전 구현의 `LayoutCompact`, `WorkflowIntegration`, 전체 Release UI 검증은 통과했다. 이번 440×200 DIP 크기 확대와 타일 전체 제목 표시도 Release LayoutCompact 검증을 통과했다. 전체 제목의 접두어 보존·두 줄 표시, 1칸 240×128 크기, 같은 pane 모달 복원, 세 가지 실제 상태 배경과 페이지 전환을 확인했다.
- 초기 구현 당시 없었던 OpenCode CLI는 아래 실제 설치 검증에서 보완했다. 설정된 제공자 계정이나 Codex 모델에 실제 요청을 보내지 않았으므로 스텁 실행 검증과 실제 모델 연동은 구분한다.
- 현재 OpenCode 기본 매핑의 `LaunchModes` 검증과 Markdown 문서 서비스 7개 시나리오 PASS. Release 통합 빌드는 경고·오류 0이다.

## 오른쪽 터미널 사용자 입력 이력 패널 (검증 완료)

- 사용자에게 보이던 Markdown 편집기·문서 트리·미리보기를 숨기고 기존 `WorkbenchHost`를 기본 340 DIP 오른쪽 패널로 옮겼다. 빌드 호환에 필요한 기존 문서 컨트롤은 `LegacyDocuments`에 숨겨 두며, **기록 폴더**는 선택 프로젝트의 `terminal-notes` 또는 아직 기록이 없을 때 프로젝트 폴더를 Explorer로 연다.
- 상단에 질문·플랜·빌드·검토·명령을 배치하고, 가운데는 선택 project+slot에서 사용자가 제출한 최근 200개 입력을 채팅 말풍선으로, 하단은 터미널별 초안·전송·중복 수동 재전송으로 구성했다. 분할 수가 바뀌어도 패널과 입력을 자동으로 접지 않는다.
- 질문은 `ConditionalWeakTable<TerminalPane, ...>`의 현재 프로세스 메모리에만 보관한다. 앱 입력과 복원 가능한 직접 터미널 질문 모두 MD·JSON·해시를 만들지 않으며, 앱 종료 또는 pane 수명 종료 뒤 복원하지 않는다.
- 플랜·빌드·검토·명령은 기존 `ProjectWorkJournal`을 읽되 `IsAutomation` 항목을 제외한다. 말풍선은 사용자가 제출한 원문·분류·입력 시각만 표시하며 `Detail`, 상태·결과, 자동화 합성 명령과 일반 xterm 출력은 표시하지 않는다.
- 선택 변경과 명시적 제출·결과 갱신에서만 기록을 다시 읽고 120ms coalescing, cancellation, generation guard로 늦은 로드가 다른 터미널 타임라인을 덮지 않게 했다. 자동화 결과 저장 뒤 호출할 `RefreshConversationHistory()` 계약을 제공한다.
- 진행 녹색, 질문·선택 요구 주황색, 그 외 회색을 분할 카드·미니 타일·실제 xterm 배경에서 선명하게 구분한다. 완료·오류·한도는 레이블만 바꾼다.
- 작업 제출 또는 명시적 busy 뒤에는 녹색을 유지한다. 중간 quiet·무출력만으로 회색을 깜빡이지 않으며 실제 질문·선택 요구에서 주황색, 확정 완료·명확한 프롬프트 복귀·프로세스 종료에서 회색으로 전환한다.
- 최신 사용자 입력 전용 필터를 포함한 전용 Release WPF 집중 검증과 전체 UI 통합 검증이 통과했다. 선택 project+slot 필터, `Detail`·자동화 합성 항목 숨김, composer·직접 질문의 파일/원장 비저장, 터미널별 초안과 외부 상태 상세 미표시를 확인했다.
- TerminalContext의 실제 WPF/xterm 24개 검증이 통과했다. 녹색 `#00853F`, 주황색 `#FF8A00`, 회색 `#252D3A`와 4.5 최소 대비, 실제 선택·복사·붙여넣기·화면 지우기, 종료 중 수동 입력 차단 및 접수된 프로그램 전송 완료를 확인했다.
- TerminalActivity는 10분 무출력, 동일 화면·ANSI 재그리기, 크기 변경, 일반 TUI 입력 행에서 진행 상태를 유지한다. 질문 대기, 응답, Ctrl+C 뒤 명확한 PowerShell 복귀, 사용자 지정 시작 명령의 완료와 확정된 자동화 결과를 검증했다.
- 종료 처리 중 새 터미널·작업 입력을 막고 접수된 전송 및 저장을 기다린다. 종료 취소는 입력을 복구하며, 에이전트 교체는 입력 분류·초안·메모리 질문을 넘겨준다. 종료 시 진행 중인 이력 로드를 취소한다.
- 미니 타일은 전용 버튼 템플릿을 사용하여 마우스 hover·클릭·키보드 포커스가 상태 배경을 덮어쓰지 않는다. 실제 렌더된 타일 배경과 녹색·주황색·회색 상태값이 일치하는지 확인한다.
- 미니 타일의 녹색 선택 테두리를 없애고 선택·포커스는 대비색 테두리로 표시한다. 상태색은 사각형 내부 전체를 채운다. Release `LayoutCompact`에서 세 가지 상태의 실제 렌더링 픽셀과 각 타일의 키보드 포커스 전환, 회색 타일의 중립 선택 테두리, 기존 크기·제목·페이지·모달 복원을 검증했다.

## 실제 OpenCode 설치 확인 (2026-09-19)

- 설치 전 PATH 검색 및 npm 전역 목록에서 OpenCode가 없음을 확인했다. 기존 npm 전역 설치 폴더는 현재 프로세스와 사용자 PATH에 이미 있었다.
- 공식 npm 패키지 `opencode-ai@1.18.31`을 사용자 전역 경로에 설치했다. npm이 막은 해당 패키지의 postinstall은 `npm rebuild --global opencode-ai --allow-scripts=opencode-ai`로 한 번 실행했다. 전역 허용 정책이나 PowerShell 실행 정책은 바꾸지 않았다.
- `opencode --version`, 새 Windows PowerShell 5.1에서의 같은 명령, `opencode run --help`의 `--format`, `--model`, `--auto` 지원을 확인했다.
- 실제 제품의 `ConPtySession` 소스를 사용하는 임시 설치 검증에서 `OPENCODE_NATIVE_OK 1.18.31` 결과를 확인했다. 기존 사용자 터미널을 종료하거나 모델에 요청을 보내지 않았다.
- 설치 직후 `opencode auth list`는 등록된 자격 증명 0개였다. 사용할 제공자 계정 연결은 별도 단계이며 실제 모델 요청은 아직 검증하지 않았다.

## 화면 디자인 정돈 (2026-09-19)

- 전체 창을 짙은 회색 바탕과 중립 강조색으로 통일하고 상단 로고·분할 선택·기능 버튼의 간격과 모서리를 정리했다. 입력창·스크롤바·수동 재전송 체크박스도 같은 어두운 테마를 사용한다.
- 440×200 DIP 소형 창의 번호·두 줄 제목을 왼쪽 정렬하고 카드 간격, 열기 표시, 둥근 외곽과 헤더를 정돈했다. 녹색·주황색·회색 전체 배경, 동일 터미널의 모달 복원, 65/60 DIP 큰 제목을 유지한다. 모달의 입력 분류는 별도 탭 줄에 둔다.
- 오른쪽 이력은 입력 원문과 분류·시각을 담은 카드로 표시하고, 전송 버튼은 32 DIP 높이로 정렬했다. 질문의 메모리 전용 처리와 터미널별 초안·분류 보존 로직은 그대로다.
- Windows 11에서는 기존 DWM 설정을 재사용해 기본 창과 터미널 모달 제목 표시줄도 어둡게 맞춘다. 기본 창 조작은 Windows가 처리한다. 속성은 [Microsoft DWM 문서](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)를 확인했다.
- Release 전체 UI 검증과 Conversation 검증을 통과했다. 포함된 CLI 실행 검증은 OpenCode 버전 확인으로 변경했다. 새 스크롤바의 페이지 이동·핸들 드래그, 질문의 파일 비저장, 사용자 입력 전용 이력과 초안 보존을 확인했으며 독립 소스 검토에서 새 회귀 문제를 찾지 못했다.
- 최종 테마의 `LayoutCompact` 검증도 통과했다. 820×480에서 터미널 본문 66 DIP와 입력·전송 버튼을 확보하고, 440×200 미니 타일의 전체 상태색·키보드 포커스·두 줄 제목·페이지 전환·동일 세션 모달 복원을 실제 렌더링으로 확인했다.

## OpenCode 작은 분할 Bun 크래시 (2026-09-21)

- 설치된 OpenCode 1.18.31 / 내장 Bun 1.3.14를 실제 ConPTY에서 실행해 18×3, 18×12, 28×12에서 `panic(main thread): Segmentation fault`, 종료 코드 3을 재현했다. 단일 프로세스에서도 발생하므로 8개 동시 실행 자체를 원인으로 단정하지 않는다. 버전 출력만으로는 이 화면 초기화 오류를 검증할 수 없다.
- OpenCode 프로필의 xterm과 ConPTY 화면 크기를 최소 40열×24행으로 유지한다. 카드가 더 작으면 외부 뷰포트의 스크롤바·휠·Shift+휠로 나머지 화면에 접근한다. 네이티브 세션만 크게 설정하지 않고 xterm 크기도 일치시킨다. 일반 셸의 크기 맞춤은 유지한다.
- `TuiViewport` 회귀 테스트는 작은 창에서 시작, 확대·축소, 네이티브/xterm 행·열 일치, 스크롤바와 휠 입력, 셸 입력·출력 및 비보호 일반 셸 동작을 검증한다. 전체 `UI`, `Terminal`, `Documents` 검증도 통과했다.
- `artifacts/opencode-tui-check`는 좁은 네이티브 화면 재현용이며 `artifacts/opencode-ui-check`는 실제 8개 OpenCode의 화면을 검사한다. 임시 XDG 폴더와 설정을 사용하고 모델 요청은 보내지 않는다. 테스트용 세션만 정리하고 원본 사용자 세션은 조작하지 않는다.
- 최종 실제 8세션 검증은 1480×920 → 820×480 → 1480×920에서 모두 40×24를 유지하고 8개 전부 `Ask anything` 입력 화면에 도달했다. 로그는 `%TEMP%\TN-f5ba33c7`에 남겼다. 빈 XDG 환경의 선행 실행에서는 1개가 크래시 없이 초기화에 머물렀으며, 같은 테스트 환경 재실행에서는 8개 모두 41초 안에 준비됐다. 이 초기화 지연의 근본 원인까지 해결했다고 주장하지 않는다.




