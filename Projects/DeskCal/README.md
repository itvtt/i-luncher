# DeskCal

WPF 달력·일정 도구입니다. 기본 빌드는 i-luncher에 포함되는 라이브러리이며, 별도 EXE가 필요하면 `DeskCal.csproj`를 `-p:ILuncherIntegrated=false`로 빌드합니다. 기존 `Z:\codex\DeskCal` 원본 프로젝트는 유지됩니다.

런처 카드에서 **실행** 또는 아이콘을 누르면 같은 프로세스에서 달력 창을 엽니다. 통합 배포본은 제품 전체를 한 번 설치합니다. 달력 창을 닫으면 공휴일 요청을 취소하고 데이터베이스 파일 잠금을 해제합니다.

Send / Receive로 소스를 동기화할 때는 아래의 상위 `Build.ps1`을 사용하세요. 빌드·실행 파일은 [로컬 작업 복사본](../README.md)에 생성됩니다. IDE에서 개발할 때도 원본 clone 대신 작업 복사본의 솔루션을 열어야 원본에 `bin/obj`가 생기지 않습니다.

```powershell
# i-luncher 폴더에서 DeskCal 변경을 포함한 통합 프로그램 게시
.\Build.ps1 -App deskcal
```

일정은 `%LOCALAPPDATA%\DeskCal\deskcal.db`에 저장됩니다. 첫 실행 때 이 파일이 없고 기존 `C:\Deskcal\deskcal.db`가 있으면 SQLite 백업으로 복사합니다. 기존 파일은 덮어쓰거나 삭제하지 않습니다. 이전에 실패하면 안내 후 종료하며 빈 데이터베이스로 대체하지 않습니다. 앱 업데이트와 데이터 저장 위치는 분리됩니다.

개발·검증 시 `DESKCAL_DATA_DIR` 환경 변수에 별도 폴더를 지정하면 그 폴더만 사용하고 기존 데이터 가져오기는 생략합니다. 격리된 데이터 폴더에서 게시 EXE의 WPF 시작과 데이터베이스 생성을 확인했습니다. 원본 프로젝트의 nullable 경고는 유지돼 있습니다.
