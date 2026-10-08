# 공통 터미널 엔진

TerminalNotes의 ANSI/VT 해석과 화면 버퍼를 OS에 의존하지 않는 C# 라이브러리로 분리했습니다. OpenCode 자체 TUI와 일반 셸을 WPF 화면으로 직접 그리며, 별도의 Avalonia 앱도 같은 엔진을 사용합니다. 두 터미널 화면은 WebView나 xterm.js를 사용하지 않습니다. 기존 API 대화 화면과 레거시 터미널은 명시적인 호환 모드로 유지합니다.

## 구성

| 경로 | 역할 |
| --- | --- |
| `TerminalEngine/` | UTF-8 스트림 해석, ANSI/VT, 셀·커서·스크롤백, 키 입력 인코딩. .NET 8 라이브러리 |
| `Controls/NativeTerminalSurface.cs` | TerminalNotes의 WPF 화면, 선택·복사·붙여넣기·입력 |
| `TerminalTransport/` | 독립 앱의 공통 프로세스 인터페이스, Windows ConPTY, Linux/macOS PTY 보조 프로세스 |
| `TerminalDesktop/` | Windows·Linux·macOS용 Avalonia 독립 앱. 브라우저 프로세스를 사용하지 않음 |

Windows에서 프로세스와 연결하려면 OS의 ConPTY API가 필요합니다. Linux와 macOS에서는 네이티브 `forkpty`/`exec` 보조 프로세스를 사용합니다. 자체 엔진은 터미널의 해석·입력·화면 처리를 담당하며 OS의 프로세스·콘솔 기능을 대체하지 않습니다. 기존 TerminalNotes의 프로세스 수명·작업 연동은 `Services/ConPtySession.cs`를 계속 사용합니다.

## 실행

TerminalNotes는 기존 `Start-TerminalNotes.cmd`로 실행합니다. OpenCode와 일반 셸 화면에 새 엔진이 기본 적용됩니다. OpenCode는 앱이 소유한 `serve` 서버에 `attach`로 자체 TUI를 연결합니다. 플랜 전송·상태·요청 접수 확인은 같은 서버의 API를 사용합니다. TerminalNotes의 별도 대화 화면을 표시하지 않습니다.

태스크의 시작·재실행은 플랜이 없거나 비어 있어도 OpenCode를 열고 직접 입력을 기다립니다. 이때 자동 요청은 보내지 않습니다. OpenCode 본문 전체의 배경은 진행 중 녹색, 승인·질문·선택 대기 주황색, 그 외 회색으로 표시합니다. TUI가 지정한 배경색에도 상태색을 적용하되 글자 대비와 선택 표시는 유지합니다. 일반 셸은 기존 ANSI 배경색을 사용합니다.

독립 터미널은 배포 후 `Start-Standalone-Terminal.cmd`로 실행합니다. `+ 새 터미널` 또는 `Ctrl+Shift+T`는 같은 실행 폴더에서 독립된 프로세스와 새 창을 만듭니다. 직접 실행 인자는 다음과 같습니다.

```powershell
dotnet run --project .\TerminalDesktop\TerminalNotes.TerminalDesktop.csproj -c Release
dotnet run --project .\TerminalDesktop\TerminalNotes.TerminalDesktop.csproj -c Release -- --cwd C:\work --shell cmd.exe
dotnet run --project .\TerminalDesktop\TerminalNotes.TerminalDesktop.csproj -c Release -- --cwd C:\work --shell opencode.exe -- --help
```

`--shell`에는 실행 파일, 그 뒤의 `--`에는 해당 프로그램의 인자를 전달합니다. 경로와 인자는 셸 명령 문자열로 결합하지 않습니다. 기본 셸은 Windows PowerShell, Unix의 `$SHELL`(없으면 `/bin/sh`)입니다.

OpenCode의 작은 분할 화면은 최소 40×24 논리 격자를 유지하면서 화면 전체가 칸 안에 들어오도록 균일하게 축소합니다. 크기를 넓히면 표시 배율이 1로 돌아오고 사용 가능한 행·열 수가 늘어납니다. 터미널 안의 마우스 입력과 스크롤백 조작은 유지합니다.

`Ctrl+Shift+C/V`는 복사/붙여넣기, `Shift+PageUp/PageDown`과 휠은 스크롤백 탐색입니다. 마우스를 사용하는 TUI에서는 `Shift`를 누른 채 드래그하면 텍스트를 선택합니다. 기본 `Ctrl+C`는 실행 중인 프로그램으로 전달합니다.

## 지원 범위

| 환경 | 구현 상태와 검증 범위 |
| --- | --- |
| Windows 10 / Windows 11 | ConPTY 연결 구현. 현 개발 호스트에서 실제 프로세스 검증. Windows 10 실기 검증은 별도 필요 |
| Linux | 공통 엔진·Avalonia 호스트·PTY helper 구현. 현 호스트에 Linux/WSL이 없어 실행 검증 미완료 |
| macOS | 공통 엔진·Avalonia 호스트·PTY helper 구현. 실행 검증 미완료 |
| 모바일·웹·ConPTY 이전 Windows | 이 배포의 지원 대상이 아님 |

ConPTY API 자체의 최소 버전은 Windows 10 1809(build 17763)입니다. 이것이 독립 앱 전체의 지원 하한을 뜻하지는 않습니다. Avalonia 12는 Windows 10 22H2를 Tier 2로 분류하며, .NET 런타임과 그래픽 라이브러리의 요구사항도 충족해야 합니다. Linux 독립 앱은 현재 기본 X11 경로를 사용합니다. 자세한 조건은 [Avalonia 플랫폼 문서](https://docs.avaloniaui.net/docs/supported-platforms)를 참고하세요.

ANSI 색상(16/256/truecolor), 커서 이동, 지우기·삽입·삭제, 스크롤 영역, 대체 화면, 키보드 모드, bracketed paste, 마우스 모드, 화면 질의 응답을 처리합니다. 한글·일반적인 전각 문자·결합 문자를 처리하지만 모든 emoji grapheme 및 모든 터미널 확장 규격과의 일치를 보장하지 않습니다. 크기를 바꿀 때 화면을 보존·조정하지만 스크롤백 문단 전체를 재줄바꿈하지 않습니다. Sixel/Kitty 이미지, OSC 52 원격 클립보드 등은 지원하지 않습니다. 무시하는 제어 문자열에는 길이 제한을 둡니다.

모든 CLI와 모든 환경에서의 완전한 호환을 주장하지 않습니다. 특히 OpenCode의 Windows 10 종료 문제가 프로그램 자체 또는 ConPTY에서 발생한다면 새 화면 엔진만으로 해결되었다고 판단할 수 없습니다. 작업 서버와 TUI 프로세스는 분리되지만, 실제 TUI 연결에는 Windows ConPTY를 사용합니다.

## 빌드와 검증

```powershell
dotnet build .\TerminalNotes.csproj -c Release
dotnet run --project .\tests\TerminalEngine\TerminalNotes.TerminalEngine.Tests.csproj -c Release
dotnet run --project .\tests\TerminalTransport\TerminalNotes.TerminalTransportTests.csproj -c Release
dotnet run --project .\tests\NativeTerminalSurface\TerminalNotes.NativeTerminalSurfaceTests.csproj -c Release
dotnet publish .\TerminalDesktop\TerminalNotes.TerminalDesktop.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\terminal-desktop-win-x64
```

독립 앱은 .NET 8 런타임, 기존 TerminalNotes는 .NET 10 Desktop Runtime을 사용합니다. 네이티브 터미널 화면에는 WebView2가 필요하지 않습니다. 명시적으로 선택하는 기존 API 대화 화면과 레거시 터미널은 WebView2 Runtime을 사용합니다.

Linux/macOS에서는 C 컴파일러로 [PTY helper](TerminalTransport/README.md)를 먼저 빌드한 뒤 독립 앱을 빌드합니다. `terminalnotes-pty`를 앱 옆에 두고 실행 권한을 유지해야 합니다. 배포 대상과 같은 OS·아키텍처에서 helper를 빌드해야 하며 Windows에서 만든 파일로 대체할 수 없습니다. Avalonia의 플랫폼 런타임 의존성도 필요합니다.

```sh
sh TerminalDesktop/build-unix.sh
dotnet run --project tests/TerminalEngine/TerminalNotes.TerminalEngine.Tests.csproj -c Release
dotnet run --project tests/TerminalTransport/TerminalNotes.TerminalTransportTests.csproj -c Release
dotnet run --project TerminalDesktop/TerminalNotes.TerminalDesktop.csproj -c Release --no-build
```

기존 `tests/UI`, `tests/TuiViewport`, `tests/TerminalContext`는 xterm.js 내부를 검사하므로 자체적으로 `TERMINALNOTES_TERMINAL_RENDERER=webview`를 설정합니다. 이 변수는 일반 셸의 레거시 화면에 적용합니다. OpenCode의 기존 API 대화 화면을 비교하려면 별도로 `TERMINALNOTES_OPENCODE_RENDERER=headless`를 설정합니다. 변수를 없애면 기본 네이티브 화면을 사용합니다. 두 호환 모드의 검증 결과와 실제 OpenCode TUI 검증을 구분합니다.

2026-10-04에는 `tests/OpenCodeTuiPane`으로 WPF 앱의 실제 OpenCode 1.18.34 `serve` + `attach` 연결을 검사했습니다. 플랜 파일이 없는 경우와 빈 경우를 각각 실행하여 메시지 제출 없이 열리는지 확인했습니다. WebView 미초기화, API로 선택한 세션의 TUI 제목, 미전송 입력, 최소 40×24의 작은 분할과 여섯 차례 크기 변경, 두 소유 프로세스의 종료도 확인했습니다. 별도 임시 홈·설정·작업 폴더에서 모델 요청 없이 검사했으며, Windows 10 실기기 검증을 대신하지 않습니다.

```powershell
$env:TERMINALNOTES_TEST_OPENCODE = '설치된 OpenCode 실행 파일의 절대 경로'
dotnet run --project .\tests\OpenCodeTuiPane\TerminalNotes.OpenCodeTuiPaneTests.csproj -c Release
```

2026-10-03 개발 호스트에서 엔진·Windows 전송 계층·WPF 네이티브 화면·기존 UI·OpenCode API 대화 화면 검증을 통과했습니다. 실제 OpenCode TUI에도 요청을 제출하지 않은 초안을 입력하고 여섯 차례 크기를 변경하여 프로세스와 초안이 유지되는지 확인했습니다. 설치된 OpenCode 실행 파일을 지정하여 같은 검증을 실행할 수 있습니다.

```powershell
dotnet run --project .\tests\TerminalTui\TerminalNotes.TerminalTuiTests.csproj -c Release -- C:\path\to\opencode.exe
```

이 검증은 별도의 임시 홈·설정·작업 폴더를 사용하며 모델 요청을 보내지 않습니다. 독립 앱의 `--smoke` 모드는 실제 입력·출력, 20회 크기 변경, 별도 창 생성과 프로세스 수명을 확인합니다. 이 결과를 Windows 10 실기기 또는 Linux/macOS에서의 실행 결과로 간주하지 않습니다.

구현 참고: [Microsoft ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session), [VT 규격](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html), [Linux PTY](https://man7.org/linux/man-pages/man7/pty.7.html), [Avalonia 화면 그리기](https://docs.avaloniaui.net/docs/custom-controls/custom-drawn-controls), [입력과 IME](https://docs.avaloniaui.net/docs/input-interaction/text-input).
