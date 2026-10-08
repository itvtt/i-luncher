# SwiftCopy

Windows용 C# WPF 파일 복사·이동 앱입니다. **왼쪽에서 원본을 선택하고, 오른쪽에서 대상 폴더를 정한 뒤 복사 또는 이동 버튼을 누르면 바로 실행됩니다.**

기본 빌드는 [통합 i-luncher](../README.md)에 포함되는 라이브러리입니다. 별도 EXE 개발·게시 명령에는 `-p:ILuncherIntegrated=false`를 지정합니다.

Send / Receive로 소스를 동기화할 때는 상위 i-luncher 폴더의 `Build.ps1 -App swift-copy`로 빌드하고 `Start-Launcher.cmd`로 실행하세요. 아래의 직접 빌드·실행 명령은 [로컬 작업 복사본](../README.md)에서 사용해야 원본 clone에 빌드 폴더가 생기지 않습니다.

![SwiftCopy 화면](artifacts/SwiftCopy-preview.png)

## 실행

`Start-Launcher.cmd`로 i-luncher를 열고 SwiftCopy를 선택하세요. 통합 실행 파일의 `--app swift-copy` 옵션으로도 직접 열 수 있습니다. 별도로 게시한 독립 버전은 `dist/win-x64/SwiftCopy.exe`로 실행합니다.

기본 제목 표시줄 대신 앱과 통합된 상단 바를 사용합니다. 오른쪽의 최소화·최대화/복원·닫기 버튼으로 창을 제어하며, 상단 빈 영역 드래그, 더블클릭 최대화/복원, 가장자리 크기 조절을 지원합니다.

기본 창은 980×760 DIP이며, 파란 제목 표시줄과 밝은 원본·대상 패널로 앱을 쉽게 구분할 수 있습니다. 가운데 큰 상태 카드와 굵은 진행 막대에서 대기·전송·완료 상태를 확인합니다. 버튼을 누르면 경로 확인 단계부터 즉시 상태가 바뀌며, 전송 중에는 MB/s뿐 아니라 초당 완료 파일 수·처리량·남은 시간을 표시합니다. 아래에는 동시에 처리하는 파일마다 별도 진행 막대, 파일명, 복사/검증/이동/일시정지 상태, 완료 개수를 표시합니다. 작은 창이나 작업 수가 많은 경우 목록을 스크롤할 수 있습니다. 완료는 초록색, 일시정지·취소는 주황색, 오류는 붉은색 문구로 구분하며 작업표시줄에도 진행 상태를 표시합니다. Noto Sans KR 기반 SwiftCopy Sans 글꼴과 Display/ClearType, 픽셀 정렬을 사용합니다.

글꼴은 [Noto Sans KR 원본](https://github.com/google/fonts/tree/main/ofl/notosanskr)을 Medium(500)·SemiBold(600) 정적 폰트로 변환한 SwiftCopy Sans입니다. SIL Open Font License 1.1을 `SwiftCopy.App/Assets/Fonts/OFL.txt`와 글꼴 내부 메타데이터에 포함했습니다. `tools/Build-Fonts.py`는 재생성 스크립트이며 fontTools가 필요합니다. WPF에서 내장 글꼴·한글 글리프·실제 굵기를 확인했으며 합성 굵기를 사용하지 않습니다.

- 원본 박스 바깥 오른쪽의 **찾기** 버튼 하나로 Windows 탐색기 선택창을 엽니다. Ctrl/Shift로 파일과 폴더를 함께 선택한 뒤 **추가**를 누릅니다. 폴더를 더블클릭하면 내부로 이동합니다. 확장자 제한이 없으며 드래그 앤 드롭과 탐색기에서 복사한 항목의 Ctrl+V 붙여넣기도 지원합니다.
- **대상 폴더**는 입력 박스 바깥 오른쪽의 **찾기**로 선택하거나 입력란에 직접 경로를 입력·붙여넣기 합니다. 따옴표로 감싼 경로, 환경 변수, 외장 드라이브와 UNC 네트워크 경로도 지원합니다. 찾아보기는 현재 입력한 폴더가 존재하면 그 위치에서 열립니다.
- **복사**는 원본을 유지합니다. **이동**은 완료한 항목의 위치를 바꿉니다. 버튼을 누르면 추가 확인창 없이 시작합니다.
- 같은 이름의 대상 파일은 자동으로 건너뜁니다. 이동할 때 건너뛴 원본도 유지됩니다. 선택한 폴더는 폴더 이름을 포함해 대상 안에 들어갑니다.
- 전송 중에는 **일시정지 / 계속 전송 / 취소**, 완료 후에는 **기록 보기**를 이용합니다.
- **동시 작업**에서 1·2·4·8·16개를 선택합니다. 기본값은 4이며 전송 중에는 변경할 수 없습니다. 작업 수를 늘렸을 때의 효과는 원본·대상 저장장치에 따라 다릅니다. 표시되는 각 작업은 서로 다른 파일을 처리하는 병렬 전송입니다.

## 속도와 데이터 처리

- 같은 볼륨에서 대상 폴더가 아직 없으면 폴더 전체를 Windows 이름 변경 연산으로 이동합니다. 내부 파일 개수와 용량을 미리 세지 않습니다. 이 경우 화면에 `폴더 N개 / 즉시 이동`을 표시합니다.
- 기존 폴더에 합치거나 파일을 개별 선택하면 파일별 이름 변경을 시도합니다. 다른 볼륨이면 선택한 동시 작업 수만큼 병렬로 복사합니다.
- 파일을 개별로 많이 선택할 때 모든 원본을 서로 비교하던 경로 검사를 경로별 조회로 바꿨습니다. 파일마다 반복하던 대상 폴더 생성 호출도 줄였습니다. 링크/정션 검사, 임시 파일 커밋, 원본 보호와 이동 시 검증은 유지합니다.
- 각 복사 작업은 지정된 전용 스레드에서 처리하므로 다른 앱의 스레드풀 작업에 밀려 병렬 처리가 늦게 시작되는 상황을 줄입니다.
- 폴더 탐색과 전송을 동시에 수행하고 대기열은 2,048개로 제한합니다. 30만 개 파일의 전체 목록을 메모리에 보관하지 않습니다. UI는 250ms 간격으로 갱신합니다.
- Windows `CopyFileExW`를 사용합니다. 256 MiB 이상 파일에는 버퍼 없는 I/O를 적용합니다.
- 복사는 대상 폴더의 고유한 임시 파일에 수행하고 크기 확인 후 최종 이름으로 변경합니다. 다른 볼륨으로 이동할 때는 SHA-256으로 기본 데이터 스트림을 비교한 뒤 원본 파일 핸들로 삭제합니다. 복사하는 동안 원본의 쓰기·삭제를 차단합니다.
- 취소하면 진행 중인 임시 파일을 정리하고 이미 완료한 파일은 유지합니다. 원본과 대상이 겹치는 경로는 거부합니다. 권한·잠금 등의 오류는 기록하고 다른 항목을 계속 처리합니다.
- 전송 기록: `%LOCALAPPDATA%\SwiftCopy\Logs`. 오류, 건너뜀, 재시도, 폴더 즉시 이동과 최종 집계를 기록합니다.

실제 속도는 SSD/HDD, USB 연결, 네트워크, 백신, 대상 파일 개수에 따라 달라집니다. 같은 볼륨의 폴더 전체 이름 변경 성능을 다른 드라이브 복사 성능으로 해석하면 안 됩니다.

파일 시스템 백업 도구는 아닙니다. 사용 중인 파일의 스냅샷, NTFS ACL의 완전 복제, 중단한 큰 파일의 바이트 단위 재개는 제공하지 않습니다. 재실행 시 완료한 대상 파일은 건너뜁니다. 탐색 복사에서는 링크·정션·재분석 지점을 건너뛰고 로그에 남깁니다. 폴더 전체 즉시 이동은 폴더 내부 항목을 그대로 유지합니다. 드라이브 루트 대신 내부 폴더를 선택하세요.

## 검증

검증 보고서는 `artifacts` 폴더에 있습니다. 병렬 작업 추가 후의 [대량 파일 비교 측정과 검증 결과](../artifacts/swiftcopy-performance/README.md)에는 준비 시간과 실제 복사 시간을 구분해 기록했습니다.

- 실제 300,000개 작은 파일 복사 후 **모든 출력 파일의 내용과 개수 일치** 확인.
- 같은 볼륨의 300,000개 파일 포함 폴더 이동: **24.55ms**, 이동 전후 개수 및 원본 위치 제거 확인. 이 수치는 탐색·내용 재복사 없는 폴더 이름 변경이며 사전·사후 개수 검사는 측정 밖입니다.
- 기능 검사 15개: 혼합 선택, 빈 폴더/파일, 한글/이모지, 중복 처리, 같은 볼륨 이동/합치기, 다른 볼륨 검증 이동, 경로 겹침/정션 차단, 잠긴 원본 보호, 읽기 전용/긴 경로, 일시정지/재개/취소, 300 MiB 파일 복사·SHA-256 검사, 진행 중 덮어쓰기 취소 시 기존 대상 보존, 5,000개 작은 파일 복사.
- WPF UI 검사: 복사/이동 버튼 클릭 즉시 실행, 출력 내용, 복사 원본 보존, 이동 원본 제거, 완료 상태와 버튼 복구 확인.
- 사용자 지정 상단 바: 기본 제목 표시줄 제거, 창 드래그·가장자리 크기 조절·버튼의 Windows 입력 영역 검사, 최소화·최대화·복원·닫기 버튼 동작 확인.
- 기본 창 980×760 DIP, 최소 창 800×640 DIP에서 입력·상태·작업 영역과 스크롤 가능한 병렬 작업 목록을 확인합니다. 전송 중에만 일시정지·취소 버튼을 표시합니다.
- 실제 Windows 선택창에서 파일·폴더 혼합 입력, 폴더 단독 선택, 취소, 대상 폴더 찾아보기, 따옴표가 포함된 대상 경로 직접 입력 후 복사를 검증했습니다.
- 폰트·ClearType 설정, 창의 네이티브 아이콘 핸들, ICO의 여러 해상도 프레임을 검사했습니다.

300 GB 전체 데이터 전송과 실제 USB/NAS 장비별 성능은 이 환경에서 시험하지 않았습니다.

## 개발

.NET 10 SDK와 Windows가 필요합니다. 외부 NuGet UI 라이브러리는 사용하지 않습니다.

Visual Studio에서 `SwiftCopy.sln`을 열고 `SwiftCopy.App`을 시작 프로젝트로 실행합니다. 기존 시작 설정 때문에 "클래스 라이브러리인 프로젝트는 직접 시작할 수 없습니다"가 표시되면, 솔루션 탐색기에서 `SwiftCopy.App`을 마우스 오른쪽 버튼으로 클릭해 **시작 프로젝트로 설정**한 뒤 F5를 누르세요. `SwiftCopy.Core`는 앱이 사용하는 클래스 라이브러리이므로 직접 실행할 수 없습니다.

```powershell
dotnet build SwiftCopy.sln -c Release
dotnet run --project SwiftCopy.App -c Debug -p:ILuncherIntegrated=false
dotnet run --project SwiftCopy.Checks -c Release
dotnet run --project SwiftCopy.Checks -c Release -- --scale
dotnet run --project SwiftCopy.UiChecks -c Release
dotnet publish SwiftCopy.App -c Release -r win-x64 --self-contained true -p:ILuncherIntegrated=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/win-x64
```

`SwiftCopy.Core`: 전송 엔진 / `SwiftCopy.App`: WPF 화면 / `SwiftCopy.Checks`: 실제 파일 검사 / `SwiftCopy.UiChecks`: WPF 이벤트 검사.

아이콘 원본은 `SwiftCopy.App/Assets/SwiftCopy.svg`입니다. `tools/Build-Icon.ps1`로 16/20/24/32/40/48/64/128/256px PNG와 다중 해상도 `SwiftCopy.ico`를 재생성할 수 있습니다.

검사에서 생성한 자료는 `%TEMP%\SwiftCopy-Checks-*`, `%TEMP%\SwiftCopy-UiChecks-*` 및 검사 출력 폴더의 `cross-volume-fixture-*`에 남겨 확인할 수 있습니다. 다른 볼륨 이동 검사는 TEMP와 검사 실행 파일이 다른 드라이브에 있을 때 실행합니다. 같은 드라이브이면 `SWIFTCOPY_CROSS_VOLUME_ROOT` 환경 변수에 다른 드라이브의 검사 폴더를 지정합니다. `--filter Cross-volume`으로 해당 검사만 다시 실행할 수 있습니다.

Windows API 근거: [CopyFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexw), [MoveFileExW](https://learn.microsoft.com/ko-kr/windows/win32/api/winbase/nf-winbase-movefileexw).






