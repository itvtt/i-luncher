#!/usr/bin/env python3
"""서버 목록(API 서버의 앱 목록, 마스터 계정의 런처에만 보임)에 seed\\apps.json 의 앱을 넣습니다. 설명 · 상세 설명 · 스크린샷까지 그대로 (지어내지 않게).

    python import_app_list.py --server https://<API 서버 주소> --repository-base https://<Enterprise 호스트>/<소유자>
    python import_app_list.py ... --apply      실제로 저장 (없으면 무엇을 바꿀지 보여 주기만)

- 관리자 비밀번호: 환경 변수 API_ADMIN_PASSWORD (또는 RELAY_ADMIN_PASSWORD). 화면에 출력하지 않습니다.
- 서버에 없는 앱은 모든 값으로 더합니다. 이미 있는 앱(id 또는 예전 id 가 같음)은 **비어 있는 칸만** 채웁니다
  (설명 · 상세 설명 · 개발자 · 분류 · 스크린샷). 서버에 이미 있는 값은 바꾸지 않습니다 (사내 설정 우선).
- 스크린샷 파일(seed\\media)은 필요한 것만 서버에 올리고(POST /launcher/media) 그 이름을 씁니다.
표준 라이브러리만 씁니다 (Python 3.8 이상).
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILL_IF_EMPTY = ("description", "details", "author", "category")


def load_seed(path: Path) -> list:
    text = "\n".join(line for line in path.read_text(encoding="utf-8").splitlines() if not line.lstrip().startswith("//"))
    return json.loads(text)["apps"]


class Server:
    def __init__(self, base: str, password: str):
        self.base = base.rstrip("/")
        self.auth = "Basic " + base64.b64encode(("admin:" + password).encode("utf-8")).decode("ascii")

    def call(self, method: str, path: str, data: bytes | None = None, content_type: str | None = None):
        request = urllib.request.Request(self.base + path, data=data, method=method)
        if content_type:
            request.add_header("Content-Type", content_type)
        if method != "GET":
            request.add_header("Authorization", self.auth)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            detail = error.read(2000).decode("utf-8", "replace")
            raise SystemExit(f"{method} {path}: {error.code} {detail}")


def same_app(existing: dict, seed: dict) -> bool:
    ids = {str(existing.get("id", "")).lower(), *[str(i).lower() for i in existing.get("formerIds") or []]}
    return seed["id"].lower() in ids


def revised_fields(existing: dict, seed: dict) -> list:
    """원본을 고친 앱(seed 의 former = 예전 원본 값): 서버 값이 예전 원본과 똑같은 칸 = 사내에서 고치지 않은 칸"""
    former = seed.get("former") or []
    fields = [key for key in ("description", "details")
              if seed.get(key) and existing.get(key) != seed[key] and any(key in old and old[key] == existing.get(key) for old in former)]
    shots = [str(shot).lower() for shot in existing.get("screenshots") or []]
    if seed.get("screenshots") and any([str(shot).lower() for shot in old["screenshots"]] == shots for old in former if old.get("screenshots")):
        fields.append("screenshots")
    return fields


def plan(catalog: list, seed_apps: list, repository_base: str) -> list:
    """(action, app, fields) 목록: add = 새 앱, fill = 빈 칸 채우기, update = 사내에서 고치지 않은 예전 원본 값을 새 원본으로"""
    steps = []
    for seed in seed_apps:
        existing = next((app for app in catalog if same_app(app, seed)), None)
        if existing is None:
            app = {key: value for key, value in seed.items() if key != "former"}
            app["repository"] = f"{repository_base.rstrip('/')}/{seed['repository']}"
            steps.append(("add", app, sorted(app)))
            continue
        fields = [key for key in FILL_IF_EMPTY if seed.get(key) and not str(existing.get(key) or "").strip()]
        if seed.get("screenshots") and not existing.get("screenshots"):
            fields.append("screenshots")
        if fields:
            steps.append(("fill", existing, fields))
        revised = [key for key in revised_fields(existing, seed) if key not in fields]
        if revised:
            steps.append(("update", existing, revised))
    return steps


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description="서버 목록에 seed\\apps.json 의 앱 넣기")
    parser.add_argument("--server", required=True, help="API 서버 주소, 예: https://<배포 주소>")
    parser.add_argument("--repository-base", required=True, help="저장소 주소 앞부분, 예: https://<Enterprise 호스트>/<소유자>")
    parser.add_argument("--seed", default=str(HERE / "seed" / "apps.json"))
    parser.add_argument("--apply", action="store_true", help="실제로 저장")
    args = parser.parse_args(argv)
    password = os.environ.get("API_ADMIN_PASSWORD") or os.environ.get("RELAY_ADMIN_PASSWORD") or ""
    seed_path = Path(args.seed)
    seed_apps = load_seed(seed_path)
    by_id = {app["id"]: app for app in seed_apps}
    server = Server(args.server, password)
    catalog_doc = server.call("GET", "/launcher/catalog")
    catalog = catalog_doc.get("apps", [])
    steps = plan(catalog, seed_apps, args.repository_base)
    if not steps:
        print("바꿀 것 없음: 서버 목록에 이미 다 들어 있습니다.")
        return 0
    for action, app, fields in steps:
        print({"add": "더함  ", "fill": "채움  "}.get(action, "바꿈  ") + f"{app['id']}: {', '.join(fields)}")
    if not args.apply:
        print("미리 보기만 했습니다. 저장하려면 --apply 를 붙이세요.")
        return 0
    if not password:
        raise SystemExit("관리자 비밀번호가 없습니다: 환경 변수 API_ADMIN_PASSWORD 를 정하세요.")
    media = seed_path.parent / "media"
    for action, app, fields in steps:
        seed = by_id[app["id"]] if app["id"] in by_id else next(s for s in seed_apps if same_app(app, s))
        if "screenshots" in fields or action == "add":
            names = []
            for shot in seed.get("screenshots") or []:
                data = (media / shot).read_bytes()
                kind = "image/jpeg" if shot.lower().endswith((".jpg", ".jpeg")) else "image/png"
                names.append(server.call("POST", "/launcher/media", data, kind)["name"])
            app["screenshots"] = names
        if action == "add":
            catalog.append(app)
        else:
            for key in fields:
                if key != "screenshots":
                    app[key] = seed[key]
    body = json.dumps({"apps": catalog, "revision": catalog_doc.get("revision", "")}, ensure_ascii=False).encode("utf-8")
    saved = server.call("PUT", "/launcher/catalog", body, "application/json")
    print(f"저장했습니다: 서버 목록 {len(saved.get('apps', []))}개")
    return 0


if __name__ == "__main__":
    sys.exit(main())
