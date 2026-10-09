# 런처 공지 게시

`Notices/notices.json`만 수정해 `main`에 커밋·푸시하면 전용 Actions가 검증 후 게시합니다. 런처 앱을 다시 빌드할 필요는 없습니다. 공지 기능이 없는 기존 런처는 한 번 업데이트해야 합니다.

공개 주소: https://itvtt.github.io/i-luncher-notices/notices.json

소스 저장소는 비공개로 유지하며, 검증된 공지 파일만 공지 전용 공개 저장소 `itvtt/i-luncher-notices`로 보냅니다. 해당 저장소의 GitHub Pages가 공지를 게시합니다.

파일 형식은 다음과 같습니다. 실제 공지가 없는 초기 파일은 `notices: []`입니다.

```json
{
  "schemaVersion": 1,
  "notices": [
    {
      "id": "example-notice",
      "revision": 1,
      "title": "공지 제목",
      "body": "공지 본문\n여러 줄도 가능합니다.",
      "publishedAt": "2026-10-09T00:00:00Z",
      "important": false
    }
  ]
}
```

- 새 공지에는 고유한 영문 소문자 ID를 사용합니다. 같은 ID의 내용을 수정하고 다시 알리려면 `revision`을 올립니다.
- `publishedAt`은 유효한 UTC 날짜이며 반드시 `Z`로 끝나야 합니다. 모든 필드는 필수이고 추가 필드나 중복 필드는 허용하지 않습니다.
- ID는 영문·숫자로 시작하고 영문·숫자·`.`·`_`·`-`만 사용하며 최대 80자입니다. 제목은 120자, 본문은 12,000자, 공지는 100개, UTF-8 파일은 256 KiB 이내입니다. `revision`은 1~2,147,483,647의 정수입니다.
- 게시 상태는 **Actions → Publish launcher notices**에서 확인합니다. 잘못된 파일은 배포하지 않습니다. 필요하면 `main`을 선택해 **Run workflow**로 다시 실행합니다.
- 런처를 열 때와 실행 중 30분마다 새 공지를 확인합니다. 공지 창이나 런처의 **새로고침**으로 바로 확인할 수도 있습니다. GitHub Pages 캐시 때문에 잠시 늦게 반영될 수 있습니다.
- `important`를 `true`로 설정하면 읽기 전까지 앱 목록 위에 표시됩니다. 공지 본문을 열면 읽음 처리되고, 같은 ID와 `revision`은 다시 새 공지로 표시되지 않습니다. 읽은 공지도 **공지** 버튼에서 볼 수 있습니다.
- 미래의 `publishedAt`으로 설정하면 해당 시각 이후 조회할 때 표시됩니다. 인터넷 연결이 끊겨도 마지막으로 받은 공지는 유지됩니다.

로컬 검증: `python Tools/CI/Publish-Notices.py Notices/notices.json`

공지 전용 변경은 Windows 배포 빌드를 실행하지 않습니다. 공지 워크플로는 재실행 시에도 최신 `main`을 읽고, 검증한 `notices.json` 하나만 공지 전용 저장소에 반영합니다.

최초 연결에는 공지 전용 저장소의 쓰기 권한이 있는 SSH 배포 키와 소스 저장소의 `NOTICE_PUBLISH_KEY` Actions 시크릿을 사용합니다. 공지 전용 저장소의 Pages Source는 **GitHub Actions**입니다. 평소 공지를 수정할 때는 이 설정을 바꿀 필요가 없습니다.
