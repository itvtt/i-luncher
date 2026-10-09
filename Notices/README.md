# 런처 공지 게시

[GitHub에서 공지.txt 수정하기](https://github.com/itvtt/i-luncher/edit/main/%EA%B3%B5%EC%A7%80.txt)를 열고, **첫 줄에 제목**, **둘째 줄부터 본문**을 입력합니다.

```text
런처 업데이트 안내
새 기능을 추가했습니다.
자세한 내용은 공지에서 확인해 주세요.
```

**Commit changes…**에서 **main에 직접 커밋**을 선택해 저장하면 자동 게시됩니다. 날짜와 공지 버전은 자동으로 정해지며, 런처 앱을 다시 빌드할 필요는 없습니다. 게시 상태는 [Publish launcher notices](https://github.com/itvtt/i-luncher/actions/workflows/publish-notices.yml)에서 확인할 수 있습니다.

- TXT에는 현재 공지 한 개를 작성합니다. 내용을 수정해 다시 저장하면 읽은 사용자에게도 새 공지로 알립니다. 제목은 120자, 본문은 12,000자 이내로 입력합니다.
- TXT 공지는 읽기 전까지 앱 목록 위에 표시됩니다. 본문을 열면 읽음 처리되며, 읽은 공지도 **공지** 버튼에서 다시 볼 수 있습니다.
- 공지를 내리려면 `공지.txt`의 내용을 모두 비운 뒤 저장합니다. 기존 JSON으로 작성한 공지는 유지됩니다.
- 런처를 열 때와 실행 중 30분마다 확인합니다. 런처나 공지 창의 **새로고침**으로 바로 확인할 수 있으며, GitHub Pages 캐시 때문에 반영이 잠시 늦을 수 있습니다. 공지 기능이 없는 기존 런처는 한 번 업데이트해야 합니다.

기존 `Notices/notices.json` 형식도 지원합니다. TXT는 `launcher-text-notice` ID의 중요 공지 한 개로 변환되며, 날짜는 해당 파일의 마지막 커밋 시각(UTC), 버전은 파일의 커밋 횟수입니다. 다른 JSON 공지는 그대로 합쳐집니다. JSON의 같은 ID를 수정해 다시 알리려면 `revision`을 올립니다. 모든 공지를 합쳐 최대 100개, 공개 JSON은 256 KiB 이내여야 합니다.

소스 저장소는 비공개로 유지하며 검증한 JSON만 공지 전용 공개 저장소에 게시합니다. 공개 주소는 https://itvtt.github.io/i-luncher-notices/notices.json 입니다. 연결에는 `NOTICE_PUBLISH_KEY` Actions 시크릿과 공개 저장소의 GitHub Actions Pages 설정을 사용하며, 평소 공지를 수정할 때 바꿀 필요는 없습니다. 공지 전용 변경은 Windows 배포 빌드를 실행하지 않습니다.

로컬 검증: `python Tools/CI/Publish-Notices.py Notices/notices.json --text 공지.txt`
