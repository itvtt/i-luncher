#!/usr/bin/env python3
"""API 서버(예전 이름 Terminal Notes 중계 서버) — 이 PC의 앱들이 쓰는 API를 한 곳에서 제공합니다.
Terminal Notes 문제 보고 · 스킬 스토어 · 공지, i-luncher 앱 목록 · 공지, 비공개 GitHub 저장소의 릴리스 중계.

표준 라이브러리만 씁니다(Python 3.8 이상, pip 설치 없음). 설정은 모두 이 파일 맨 위 SETTINGS에서 고칩니다(.env를 쓰지 않음).

    python relay.py                     SETTINGS대로 실행
    python relay.py --set RELAY_PORT=5081 --set RELAY_BIND=127.0.0.1   이번 실행만 SETTINGS 값을 바꿈
    python relay.py --settings-from D:\\relay\\relay.py   예전 relay.py의 SETTINGS를 이 파일로 옮김(새 버전으로 바꿀 때)
    python relay.py --check             설정·방화벽만 확인하고 끝냄(비밀값은 표시하지 않음)
    python relay.py --backup            스킬·플러그인·공지를 지금 백업(ZIP, 데이터 폴더의 backups)
    python relay.py --restore-backup [ZIP]  서버를 멈춘 뒤 비어 있거나 없는 스킬·플러그인·공지 폴더를 백업에서 되살림

데이터(보고서·스킬·공지·로그·백업)는 relay.py 폴더가 아니라 C:\\ProgramData\\TerminalNotesRelayData(SETTINGS의 RELAY_DATA_ROOT)에 둡니다.
relay.py 폴더를 새로 복사하거나 지워도, 실행 계정이나 Python이 바뀌어도 스토어는 그대로이며, 예전 위치에 남은 데이터는 시작할 때 복사해 옵니다.
    python relay.py --allow-firewall    다른 PC가 접속하도록 Windows 방화벽에 포트를 엶(관리자 확인 창)
    python relay.py --install-startup   Windows 로그인 때 창 없이 자동 실행(방화벽도 엶)
    python relay.py --uninstall-startup 자동 실행 해제

앱이 쓰는 API(앱의 키 X-TerminalNotes-Key 필요)
    POST /api/reports              문제 보고(report 글 + logs ZIP)
    GET  /api/skills               공유된 스킬 목록
    GET  /api/skills/<이름>         스킬 내용(SKILL.md)
    POST /api/skills               스킬 공유(같은 이름은 처음 올린 사람만 새 버전, version으로 번호를 정할 수 있음)
    GET  /api/announcements        공지(ETag로 바뀐 경우에만 내려받음)
    PUT  /api/announcements        공지 저장(공지 작성 계정만, 다른 사람이 먼저 고쳤으면 거절)

i-luncher가 쓰는 API(키 없이 읽고, 고칠 때만 관리자 비밀번호 — Basic 인증)
    GET  /launcher/catalog         앱 목록 {schemaVersion, apps, revision}
    PUT  /launcher/catalog         앱 목록 저장 {apps, revision} (다른 곳에서 먼저 고쳤으면 409)
    GET  /launcher/notices         런처 공지 {text, revision}
    PUT  /launcher/notices         런처 공지 저장 {text, revision}
    GET  /launcher/media/<이름>     앱 페이지 스크린샷(PNG·JPG, 이름 = 내용의 SHA-256 앞 16자리). 앱 목록의 screenshots가 이 이름을 씀
    POST /launcher/media           이미지 올리기(본문 = PNG·JPG, 8 MB까지) → {name}. 어느 앱도 쓰지 않는 이미지는 하루 뒤 지움

EQ Status가 쓰는 API(앱에 내장된 키 X-EQStatus-API-Key, SETTINGS의 EQUIPMENT_API_KEY를 쓸 때만 확인)
    GET  /api/v1/equipment-status  설비 상태 {items: [{line, sdwt, process, equipmentId, status, hours, downTime, comment}], updatedAt}
                                   SETTINGS의 EQUIPMENT_DB_*(MySQL)를 EQUIPMENT_REFRESH_SECONDS마다 읽어 두고 ETag로 바뀐 경우에만 내려 줌

GitHub 중계(GitHub REST API v3와 같은 주소, 읽기만): 앱·런처가 토큰 없이 비공개 저장소의 릴리스를 확인하고 받게 함.
이 서버가 RELAY_GITHUB_TOKEN(비우면 이 PC의 git 자격 증명)으로 github.com에 대신 물어보고, 응답 속 주소를 이 서버로 바꿔 돌려줍니다.
    GET  /api/v3/repos/<소유자>/<저장소>[/releases/latest|/releases|/releases/tags/<태그>|/releases/assets/<id>|/contents/<경로>|/git/trees/<ref>]
    열어 주는 저장소: LAUNCHER_GITHUB_REPOS, 런처 앱 목록에 있는 저장소, 토큰 계정의 저장소

릴리스(관리자 비밀번호, API 서버 프로그램의 릴리스 화면): 앱 목록과 LAUNCHER_GITHUB_REPOS(소유자/저장소)의 저장소마다
마지막 릴리스, 다음 버전(마지막 릴리스 + 1, 자리마다 0~9, 프로젝트 파일 <Version>이 바닥값), 최근 릴리스 실행을 보여 주고,
고른 버전으로 저장소의 .github/workflows/release.yml을 실행합니다(workflow_dispatch, 입력 version).
    GET  /admin/api/releases       {releases: [{repository, name, latest, next, chooseVersion, run, error}]}
    POST /admin/api/releases       {repository, version} → 202 (토큰에 workflow 권한 필요)

문제 보고 분석(관리자 비밀번호): Terminal Notes가 문제를 찾으면 스스로 보내는 자동 보고("[Terminal Notes 자동 문제 보고]")와 직접 보낸 보고를
최근 며칠 단위로 모아 셉니다: 자동·직접, 문제 종류(문제: 줄), 문제 이벤트(문제 이벤트: 줄), 앱 버전, PC, 날짜별.
    GET  /admin/api/reports/analysis?days=7

GitHub 러너(관리자 비밀번호, 바꾸기는 서버 PC에서만, SETTINGS의 RUNNERS_ROOT): 이 PC의 Actions 러너(폴더마다 하나)를
API Manager의 러너 화면에서 추가·편집(.env)·시작·중지·삭제합니다. 로그온 작업 하나(GitHub Runners)가 켜 둔 러너를 시작합니다.
    GET    /admin/api/runners          {available, task, runners: [{id, name, repository, on, running, busy, github, env}], repositories}
    GET    /admin/api/runners/template?repository=<소유자>/<저장소>   새 러너의 폴더·이름·.env
    POST   /admin/api/runners          {repository, folder, name, env} → GitHub에 등록하고 시작
    PUT    /admin/api/runners/<폴더>    {env} → .env 저장(쉬는 중이면 다시 시작)
    POST   /admin/api/runners/<폴더>/start|stop|restart  {force}   멈춘 러너는 꺼짐(로그온 때도 시작하지 않음)
    DELETE /admin/api/runners/<폴더>[?force=1]   멈추고 GitHub에서 지우고 폴더 삭제
    POST   /admin/api/runners/task     로그온 작업 하나로 정리(예전 러너별 작업은 XML을 남기고 지움)
    python relay.py --start-runners    켜 둔 러너를 시작하고 끝냄(로그온 작업이 씀)

관리 페이지(관리자 비밀번호): /  /reports  /skills  /announcements  /apps(i-luncher 앱 목록·공지)  /status(접속한 PC 목록·방화벽)
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import hashlib
import hmac
import html
import http.server
import ipaddress
import json
import logging
import logging.handlers
import os
import queue
import re
import shutil
import socket
import socketserver
import ssl
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

# ── 설정 시작 ── 이 서버의 설정은 여기서만 고칩니다(.env를 쓰지 않습니다). 비운 값("")은 쓰지 않거나 기본값입니다.
# 새 relay.py로 바꿀 때는 `python relay.py --settings-from <예전 relay.py>`로 이 부분을 옮깁니다.
SETTINGS = {
    "RELAY_HOST": "",                  # 이 서버 PC의 IP(앱 .env의 RELAY_HOST와 같게). 비우면 이 PC의 모든 IP에서 받습니다.
    "RELAY_PORT": 2003,
    "RELAY_BIND": "",                  # (선택) 받을 주소를 따로 정할 때. 0.0.0.0 = 이 PC의 모든 IP
    "RELAY_INGEST_KEY": "",            # (선택) 앱 키. 쓰면 앱 .env의 RELAY_INGEST_KEY와 같아야 받습니다.
    "RELAY_ADMIN_PASSWORD": "",        # (선택) 관리 페이지 비밀번호. 비우면 누구나 관리 페이지를 엽니다.
    "MASTER_USERS": "",                # (선택) 마스터 계정(쉼표로, 앱 .env의 MASTER_USERS와 같게): 공지 작성, 모든 스킬 수정·삭제.
                                       #        비우면 앱이 정한 계정을 믿습니다. 작성자는 늘 자기 스킬을 고치고 지웁니다.
    "RELAY_GITHUB_TOKEN": "",          # (선택) 비공개·내부 GitHub 저장소의 스킬 링크와 GitHub 중계(/api/v3)에 쓰는 토큰.
                                       #        비우면 GitHub 중계는 이 PC의 git 자격 증명(git credential)을 씁니다
    "RELAY_GITHUB_SYNC_MINUTES": 10,   # 깃허브 링크 스킬을 다시 읽는 간격(분)
    "LAUNCHER_GITHUB_REPOS": "",       # (선택) GitHub 중계로 열어 줄 저장소(쉼표, 소유자/저장소 또는 소유자/*).
                                       #        비우면 런처 앱 목록에 있는 저장소와 토큰 계정의 저장소만
    "LAUNCHER_DIR": "launcher",        # i-luncher 앱 목록·공지 폴더(RELAY_DATA_ROOT 기준)
    "RELAY_DATA_ROOT": "",             # 보고서·스킬·공지를 둘 폴더. 비우면 C:\\ProgramData\\TerminalNotesRelayData(이 PC의 모든 계정 공용)
                                       # relay.py 폴더(TerminalNotesRelay)와 다른 곳이라 그 폴더를 새로 복사하거나 지워도, 계정이나 Python이 바뀌어도 남습니다
    "RELAY_DATA_DIR": "reports",       # 폴더·파일은 RELAY_DATA_ROOT 기준 상대 경로 또는 절대 경로
    "RELAY_SKILLS_DIR": "skills",
    "RELAY_PLUGINS_DIR": "plugins",
    "RELAY_ANNOUNCEMENTS_DIR": "announcements",
    "RELAY_BACKUP_DIR": "backups",     # 스킬·플러그인·공지 자동 백업(ZIP). 다른 드라이브의 절대 경로를 쓰면 데이터 폴더가 지워져도 남습니다
    "RELAY_BACKUP_KEEP": 30,           # 남길 백업 수(시작할 때와 하루에 한 번, 바뀐 것이 있을 때만 만듦)
    "RELAY_LOG_FILE": "relay.log",
    "RELAY_REPORT_RETENTION_DAYS": 90,
    "RELAY_REPORTS_MAX_MB": 2048,
    "RELAY_UPLOADS_PER_HOUR": 60,      # 주소당 시간당 보고·스킬 공유 수
    "RELAY_WEBHOOK_URL": "",           # (선택) 새 보고·스킬·공지 알림 웹훅
    "RELAY_PUBLIC_URL": "",            # (선택) 알림 링크의 주소
    "RELAY_TLS_CERT": "",              # (선택) HTTPS 인증서·키 파일
    "RELAY_TLS_KEY": "",
    "EQUIPMENT_DB_HOST": "",           # (선택) EQ Status 설비 상태 DB(MySQL 8) 주소. 비우면 /api/v1/equipment-status를 열지 않습니다
    "EQUIPMENT_DB_PORT": 3306,
    "EQUIPMENT_DB_USER": "",           #        읽기 권한만 있는 계정을 쓰세요
    "EQUIPMENT_DB_PASSWORD": "",
    "EQUIPMENT_DB_NAME": "",           #        데이터베이스 이름
    "EQUIPMENT_STATUS_TABLE": "",      #        설비 상태 표(열: line, prc, eqpid, status, Hr, Down_time, view, comment, sdwt)
    "EQUIPMENT_API_KEY": "",           # (선택) EQ Status 앱에 내장된 키(X-EQStatus-API-Key). 비우면 키 없이 받습니다
    "EQUIPMENT_REFRESH_SECONDS": 10,   # 설비 상태 DB를 다시 읽는 간격(초)
    "RUNNERS_ROOT": "",                # (선택) 이 PC의 GitHub Actions 러너 폴더(러너마다 하위 폴더). 쓰면 API Manager의 러너 화면에서
                                       #        추가·편집·시작·중지·삭제하고, 로그온 작업 하나(GitHub Runners)가 켜 둔 러너를 시작합니다
    "RUNNERS_ENV_DIR": "",             #        새 러너가 빌드에 쓰는 설정 파일(<저장소>.env) 폴더. 비우면 환경 변수 APPS_ENV_DIR, 없으면 RUNNERS_ROOT\\env
    "RUNNER_PACKAGE": "",              #        새 러너 파일(actions-runner-win-x64-*.zip). 비우면 RUNNERS_ROOT의 다른 러너 파일을 씁니다
}
# ── 설정 끝 ──

VERSION = "2.19.0"
SCRIPT_DIR = Path(__file__).resolve().parent
MAX_BODY = 25 * 1024 * 1024
REPORT_MAX_CHARS = 200_000
SKILL_BODY_MAX = 100_000
SKILL_VERSIONS_KEEP = 100
ANNOUNCEMENT_MAX_BYTES = 262_144
ANNOUNCEMENT_MAX_LINES = 2048
ANNOUNCEMENT_HISTORY_KEEP = 50
REPORT_ID = re.compile(r"^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$")
SKILL_SOURCE_MAX = 1_000_000  # a linked GitHub file, bytes
SKILL_NAME = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
STAMP = re.compile(r"^[0-9]{8}-[0-9]{6}-[0-9]{6}(?:-[A-F0-9]{8})?$")
VERSION_NUMBER = re.compile(r"^[1-9][0-9]{0,5}$")
STATUSES = {"new": "새 보고", "checking": "확인 중", "resolved": "해결"}
STARTUP_SCRIPT = "Terminal Notes Relay.vbs"
OLD_STARTUP_SHORTCUT = "Terminal Notes Relay.lnk"  # the earlier .NET relay's launcher
CLIENTS_KEEP = 2000
APP_AGENT = re.compile(r"TerminalNotes/([0-9][0-9.]*)")
FEATURES = {"reports": "문제 보고", "skills": "스킬", "announcements": "공지", "badkey": "키 틀림(거절)", "launcher": "i-luncher", "github": "GitHub 중계",
            "equipment": "설비 상태"}

log = logging.getLogger("relay")


# ── 설정 ────────────────────────────────────────────────────────────────────────────────────────────

SETTINGS_START = "# ── 설정 시작 ──"
SETTINGS_END = "# ── 설정 끝 ──"


def settings_block(text: str) -> str:
    start, end = text.find(SETTINGS_START), text.find(SETTINGS_END)
    if start < 0 or end < start:
        raise SystemExit("SETTINGS 부분(설정 시작 ~ 설정 끝)을 찾지 못했습니다.")
    return text[start:end + len(SETTINGS_END)]


def copy_settings(source: Path) -> None:
    """Puts an earlier relay.py's SETTINGS into this file (updating relay.py keeps the server's own values)."""
    own = Path(__file__).resolve()
    if source.resolve() == own:
        raise SystemExit("다른 relay.py를 고르세요(지금 이 파일입니다).")
    old = settings_block(source.read_text(encoding="utf-8-sig"))
    text = own.read_text(encoding="utf-8")
    old, added = with_new_settings(old, settings_block(text))
    new = text.replace(settings_block(text), old, 1)
    if added:
        print(f"새 설정 {len(added)}개를 기본값으로 덧붙였습니다: {', '.join(added)}")
    temporary = own.with_name(own.name + ".tmp")
    temporary.write_text(new, encoding="utf-8", newline="")
    os.replace(temporary, own)
    print(f"{source}의 설정을 이 relay.py에 옮겼습니다.")
    # The earlier relay may have kept its store beside itself (before 2.10) or in an older default folder: copy it into the
    # data folder those settings give now, so the new relay starts with the same skills.
    data_keys = ("RELAY_DATA_ROOT", "RELAY_SKILLS_DIR", "RELAY_PLUGINS_DIR", "RELAY_ANNOUNCEMENTS_DIR", "RELAY_DATA_DIR",
                 "RELAY_BACKUP_DIR", "RELAY_LOG_FILE")
    values = settings_values(old)
    config = Config(parse_args([part for key in data_keys if values.get(key, "").strip()
                                for part in ("--set", f"{key}={values[key].strip()}")]))
    # The older default folders are copied when the relay starts; here only the earlier relay.py's own folder.
    for line in adopt_old_data(config, [source.resolve().parent], include_defaults=False):
        print(line)


def with_new_settings(old: str, new: str) -> tuple:
    """The earlier SETTINGS block plus the keys this relay.py added since (their lines and comments, default values), so the
    server's file shows every setting there is. Returns (block, added keys); the block is unchanged when nothing is missing."""
    have = settings_values(old)
    lines = new.splitlines()
    added, keys = [], []
    for index, line in enumerate(lines):
        match = re.match(r'\s*"([A-Z0-9_]+)"\s*:', line)
        if not match or match.group(1) in have:
            continue
        keys.append(match.group(1))
        added.append(line)
        for follow in lines[index + 1:]:
            if not follow.strip().startswith("#"):
                break
            added.append(follow)
    if not added:
        return old, []
    end = old.rfind("}")
    head = old[:end].rstrip()
    newline = "\r\n" if "\r\n" in old else "\n"
    if not head.endswith((",", "{")) and not head.splitlines()[-1].lstrip().startswith("#"):
        head += ","
    merged = head + newline + newline.join(added) + newline + old[end:]
    return (merged, keys) if set(keys) <= set(settings_values(merged)) else (old, [])


def listen_address(host: str) -> str:
    """Where the relay listens without RELAY_BIND: RELAY_HOST itself when it is an IPv4 address (localhost: 127.0.0.1),
    else every address of this PC."""
    if host.lower() == "localhost":
        return "127.0.0.1"
    return host if is_ipv4(host) else "0.0.0.0"


def is_ipv4(value: str) -> bool:
    try:
        return ipaddress.ip_address(value).version == 4
    except ValueError:
        return False


def is_local_address(address: str) -> bool:
    """Whether this PC can listen on the address: one of its own IPs."""
    try:
        with socket.socket(socket.AF_INET6 if ":" in address else socket.AF_INET) as probe:
            probe.bind((address, 0))
        return True
    except OSError:
        return False


def local_addresses() -> list:
    """This PC's IPv4 addresses other than loopback, for messages."""
    try:
        found = {info[4][0] for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)}
    except OSError:
        return []
    return sorted(address for address in found if not address.startswith("127."))


def account_list(value: str) -> list:
    return [user.rsplit("\\", 1)[-1].strip() for user in re.split(r"[,; ]+", value or "") if user.strip()]


DATA_FOLDER = "TerminalNotesRelayData"
OLD_DATA_FOLDER = "TerminalNotesRelay"  # 2.10–2.11 default, the same name as the relay's own folder
STORE_FOLDERS = ("skills", "plugins", "announcements")
MOVED_NOTE = "DATA-MOVED.txt"


def default_data_root() -> Path:
    """One folder for the whole PC, apart from wherever relay.py is copied (that folder is named TerminalNotesRelay).
    %LOCALAPPDATA% was the default before 2.12: it differs per Windows account, Microsoft Store Python keeps it per Python
    version, and the home server had relay.py itself in it, so replacing that folder took the store along."""
    if os.name == "nt" and (os.environ.get("ProgramData") or os.environ.get("ALLUSERSPROFILE")):
        return Path(os.environ.get("ProgramData") or os.environ["ALLUSERSPROFILE"]) / DATA_FOLDER
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / DATA_FOLDER
    return Path.home() / ".local" / "share" / DATA_FOLDER


def legacy_data_roots() -> list:
    """Default data folders of relays 2.10–2.11, whose store a newer relay copies forward."""
    roots = []
    if os.environ.get("LOCALAPPDATA"):
        roots.append(Path(os.environ["LOCALAPPDATA"]) / OLD_DATA_FOLDER)
    roots.append(Path.home() / ".local" / "share" / OLD_DATA_FOLDER)
    return roots


def settings_values(block: str) -> dict:
    """The SETTINGS dict of a relay.py's settings block, without running that file."""
    import ast
    start = block.find("{")
    end = block.rfind("}")
    try:
        values = ast.literal_eval(block[start:end + 1]) if 0 <= start < end else {}
    except (ValueError, SyntaxError):
        return {}
    return {str(key): str(value) for key, value in values.items()} if isinstance(values, dict) else {}


class Config:
    """SETTINGS at the top of this file, then --set KEY=VALUE for this run (tests, a one-off port), then the other options."""

    def __init__(self, args: argparse.Namespace):
        values = {key: str(value) for key, value in SETTINGS.items()}
        # Kept for the start at login, which runs this relay with the same --set values.
        self.set_args = list(args.set or [])
        self.overrides = len(self.set_args)
        for item in self.set_args:
            key, separator, value = item.partition("=")
            if not separator or not key.strip():
                raise SystemExit(f"--set은 키=값으로 씁니다: {item}")
            values[key.strip()] = value.strip()

        def text(key: str, default: str = "") -> str:
            value = values.get(key, "").strip()
            return value if value else default

        def number(key: str, default: int) -> int:
            try:
                return int(text(key, str(default)))
            except ValueError:
                raise SystemExit(f"relay.py 설정의 {key} 값은 숫자여야 합니다.")

        # What the relay keeps stays out of the folder relay.py is copied to: a new copy of that folder, or a build runner
        # cleaning its workspace, must not take the store with it.
        self.data_root = Path(text("RELAY_DATA_ROOT") or default_data_root()).expanduser().resolve()

        def directory(key: str, default: str, option: str | None) -> Path:
            value = Path(option or text(key, default)).expanduser()
            return value if value.is_absolute() else (self.data_root / value).resolve()

        self.host = text("RELAY_HOST")
        self.port = args.port or number("RELAY_PORT", 2003)
        # The service's IP is RELAY_HOST, the address apps are built with: the relay listens there. RELAY_BIND overrides it
        # (0.0.0.0: every address of this PC).
        self.bind = args.bind or text("RELAY_BIND") or listen_address(self.host)
        self.bind_source = "--bind" if args.bind else "RELAY_BIND" if text("RELAY_BIND") else "RELAY_HOST"
        self.ingest_key = text("RELAY_INGEST_KEY")
        self.admin_password = text("RELAY_ADMIN_PASSWORD")
        self.reports_dir = directory("RELAY_DATA_DIR", "reports", args.data_dir)
        self.skills_dir = directory("RELAY_SKILLS_DIR", "skills", args.skills_dir)
        self.plugins_dir = directory("RELAY_PLUGINS_DIR", "plugins", None)
        self.announcements_dir = directory("RELAY_ANNOUNCEMENTS_DIR", "announcements", None)
        self.backup_dir = directory("RELAY_BACKUP_DIR", "backups", None)
        self.backup_keep = max(1, number("RELAY_BACKUP_KEEP", 30))
        # Per client address and hour, reports and skill shares counted apart. Offices behind one NAT or proxy share an
        # address, so this stays generous.
        self.uploads_per_hour = number("RELAY_UPLOADS_PER_HOUR", 60)
        self.retention_days = number("RELAY_REPORT_RETENTION_DAYS", 90)
        self.reports_max_mb = number("RELAY_REPORTS_MAX_MB", 2048)
        self.webhook_url = text("RELAY_WEBHOOK_URL")
        self.public_url = text("RELAY_PUBLIC_URL").rstrip("/") or f"http://{self.host or 'localhost'}:{self.port}"
        self.tls_cert = text("RELAY_TLS_CERT")
        self.tls_key = text("RELAY_TLS_KEY")
        self.log_file = directory("RELAY_LOG_FILE", "relay.log", None)
        # The same list the app is built with; the relay checks it again on every announcement save.
        # Without the line the app decides who writes announcements (it is built with its own list).
        self.editors = account_list(values.get("MASTER_USERS", ""))
        # Accounts that may change and delete any skill (an author always may their own); without the line the app decides
        # (it is built with its own list).
        self.skill_admins = account_list(values.get("MASTER_USERS", ""))
        # Skills linked to a GitHub (Enterprise) file follow it: read again every so many minutes. A token is needed only for
        # private or internal repositories.
        self.github_token = text("RELAY_GITHUB_TOKEN")
        self.github_sync_minutes = max(1, number("RELAY_GITHUB_SYNC_MINUTES", 10))
        # Seconds instead, when set (tests; a busy store may want it short).
        self.github_sync_seconds = number("RELAY_GITHUB_SYNC_SECONDS", 0) or self.github_sync_minutes * 60
        # i-luncher: its app list and notices, and the GitHub relay that lets apps read private releases without a token.
        self.launcher_dir = directory("LAUNCHER_DIR", "launcher", None)
        self.github_proxy_repos = [item.strip().lower() for item in text("LAUNCHER_GITHUB_REPOS").split(",") if item.strip()]
        # Without RELAY_GITHUB_TOKEN the relay asks this PC's git credential helper (the account that pushes the repos).
        self.github_git_credential = text("RELAY_GITHUB_GIT_CREDENTIAL", "1") != "0"
        # Where the relay asks GitHub (tests point it at a fake server). GitHub Enterprise: https://<호스트>/api/v3, and then the
        # app list's repositories on that host are relayed and released too, with this PC's git credential for that host.
        self.github_api = text("RELAY_GITHUB_API", "https://api.github.com").rstrip("/")
        api_host = urllib.parse.urlsplit(self.github_api).netloc.lower()
        self.github_web_host = "github.com" if api_host in ("api.github.com", "") else api_host
        self.github_hosts = tuple(dict.fromkeys(GITHUB_HOSTS + (self.github_web_host,)))
        # EQ Status: its equipment table, read straight from the MySQL DB (the relay's own client, no pip install).
        self.equipment_db_host = text("EQUIPMENT_DB_HOST")
        self.equipment_db_port = number("EQUIPMENT_DB_PORT", 3306)
        self.equipment_db_user = text("EQUIPMENT_DB_USER")
        self.equipment_db_password = values.get("EQUIPMENT_DB_PASSWORD", "")
        self.equipment_db_name = text("EQUIPMENT_DB_NAME")
        self.equipment_table = text("EQUIPMENT_STATUS_TABLE")
        self.equipment_api_key = text("EQUIPMENT_API_KEY")
        self.equipment_refresh_seconds = max(1, number("EQUIPMENT_REFRESH_SECONDS", 10))
        # GitHub Actions runners of this PC: one folder each under RUNNERS_ROOT, managed from the API Manager's 러너 page.
        self.runners_root = Path(text("RUNNERS_ROOT")).expanduser() if text("RUNNERS_ROOT") else None
        env_dir = text("RUNNERS_ENV_DIR") or os.environ.get("APPS_ENV_DIR", "").strip()
        self.runners_env_dir = Path(env_dir).expanduser() if env_dir else self.runners_root / "env" if self.runners_root else None
        self.runner_package = Path(text("RUNNER_PACKAGE")).expanduser() if text("RUNNER_PACKAGE") else None

    def validate(self) -> None:
        # Nothing is required: without a key the relay takes any app that reaches its IP, without a password its pages are open,
        # and without RELAY_HOST it listens on every IP of this PC.
        if bool(self.tls_cert) != bool(self.tls_key):
            raise SystemExit("HTTPS를 쓰려면 RELAY_TLS_CERT와 RELAY_TLS_KEY를 함께 설정하세요.")
        if self.bind != "0.0.0.0" and not is_local_address(self.bind):
            here = ", ".join(local_addresses()) or "확인 못함"
            raise SystemExit(f"{self.bind_source}의 {self.bind}은(는) 이 PC의 IP가 아닙니다(이 PC: {here}). " + (
                "relay.py 맨 위 SETTINGS의 RELAY_HOST를 이 PC의 IP로 고치고 앱 .env도 같게 해 다시 게시하세요. 이 PC의 모든 IP에서 받으려면 "
                "RELAY_BIND를 0.0.0.0으로 씁니다."
                if self.bind_source == "RELAY_HOST" else "이 PC의 IP나 0.0.0.0(모든 IP)을 쓰세요."))

    def data_beside_code(self) -> bool:
        """Whether the store sits in relay.py's own folder (or below it), where a new copy of that folder replaces it."""
        script = SCRIPT_DIR.resolve()
        return any(folder == script or script in folder.parents
                   for folder in (self.data_root, self.skills_dir, self.plugins_dir, self.announcements_dir))

    def address_text(self) -> str:
        """The service's IP (RELAY_HOST, which apps are built with) and where this relay listens."""
        here = ", ".join(local_addresses()) or "확인 못함"
        listening = ("이 IP로 받습니다" if self.bind == self.host else "이 PC의 모든 IP에서 받습니다" if self.bind == "0.0.0.0"
                     else f"받는 주소 {self.bind}")
        if not self.host:
            return f"서버 IP: RELAY_HOST가 비어 있습니다 — 앱 .env의 RELAY_HOST에 이 PC의 IP({here})를 쓰세요 · {listening}"
        line = f"서버 IP: {'https' if self.tls_cert else 'http'}://{self.host}:{self.port} (RELAY_HOST) · {listening}"
        if is_ipv4(self.host) and not is_local_address(self.host):
            line += f" — {self.host}은(는) 이 PC의 IP가 아닙니다(이 PC: {here}). 앱은 이 주소로 접속합니다."
        return line

    def summary(self) -> list:
        return [
            f"설정: relay.py 맨 위 SETTINGS ({Path(__file__).resolve()})" + (f" · 이번 실행 --set {self.overrides}개" if self.overrides else ""),
            self.address_text(),
            f"앱 키: {'설정됨(앱의 RELAY_INGEST_KEY와 같아야 받음)' if self.ingest_key else '없음(IP로 들어온 앱 요청을 모두 받음)'}",
            f"관리 페이지: {'비밀번호 있음' if self.admin_password else '비밀번호 없음(누구나 열 수 있음)'}",
            f"데이터 폴더: {self.data_root}  " + (f"— 주의: relay.py 폴더({SCRIPT_DIR}) 안이라 그 폴더를 바꾸거나 지우면 스킬·공지가 함께 사라집니다. "
                                               "SETTINGS의 RELAY_DATA_ROOT를 비우거나 다른 폴더로 쓰세요." if self.data_beside_code()
                                               else "(relay.py 폴더를 바꾸거나 지워도 남습니다)"),
            f"자동 백업: {self.backup_dir}  (스킬·플러그인·공지 ZIP, 시작할 때와 하루에 한 번 바뀐 것이 있으면, {self.backup_keep}개 보관 · "
            f"복원: python relay.py --restore-backup)",
            f"보고서 폴더: {self.reports_dir}  (보관 {self.retention_days or '무제한'}일, 최대 {self.reports_max_mb} MB)",
            f"올리기 제한: 주소당 시간당 보고 {self.uploads_per_hour}건 · 스킬 공유 {self.uploads_per_hour}건",
            f"스킬 폴더: {self.skills_dir}",
            f"플러그인 폴더: {self.plugins_dir}  (올리기: 마스터 계정)",
            f"공지 폴더: {self.announcements_dir}",
            f"마스터 계정(공지 작성·모든 스킬 수정·삭제): {', '.join(self.editors) or '앱에서 정함'}",
            f"깃허브 연결 스킬: {self.github_sync_seconds // 60 or self.github_sync_seconds}{'분' if self.github_sync_seconds >= 60 else '초'}마다 확인"
            f" · 토큰 {'있음' if self.github_token else '없음(공개 저장소만)'}",
            f"i-luncher 앱 목록·공지: {self.launcher_dir}  (읽기 /launcher/catalog, 고치기는 관리자 비밀번호)",
            f"GitHub 중계(/api/v3): 토큰 " + ("RELAY_GITHUB_TOKEN" if self.github_token else "이 PC의 git 자격 증명" if self.github_git_credential
                                            else "없음(공개 저장소만)") + f" · 저장소 {', '.join(self.github_proxy_repos) or '앱 목록과 토큰 계정의 저장소'}",
            "EQ Status 설비 상태(/api/v1/equipment-status): " + (
                f"{self.equipment_db_host}:{self.equipment_db_port}/{self.equipment_db_name} · 표 {self.equipment_table or '(없음)'} · "
                f"{self.equipment_refresh_seconds}초마다 읽음 · 앱 키 {'확인' if self.equipment_api_key else '없음'}"
                if self.equipment_db_host else "꺼짐(SETTINGS의 EQUIPMENT_DB_HOST가 비어 있음)"),
            "GitHub 러너: " + (f"{self.runners_root} · 설정 파일 {self.runners_env_dir} · 로그온 작업 {RUNNERS_TASK}" if self.runners_root
                               else "꺼짐(SETTINGS의 RUNNERS_ROOT가 비어 있음)"),
            f"알림 웹훅: {'설정됨 · 링크 ' + self.public_url if self.webhook_url else '없음'}",
            f"로그: {self.log_file}",
        ]


# ── 공통 도구 ─────────────────────────────────────────────────────────────────────────────────────────

def now() -> datetime:
    return datetime.now(timezone.utc)


def iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).isoformat()


def parse_time(value, unknown: datetime | None = None) -> datetime:
    """ISO 8601 from either relay. The .NET relay wrote 'Z' and up to seven fraction digits without trailing zeros, and
    Python before 3.11 reads only 3 or 6. An unreadable value gives `unknown`, else the epoch."""
    unknown = unknown if unknown is not None else datetime.fromtimestamp(0, timezone.utc)
    if not isinstance(value, str) or not value:
        return unknown
    text = value.replace("Z", "+00:00")
    text = re.sub(r"\.(\d+)", lambda found: "." + found.group(1)[:6].ljust(6, "0"), text, count=1)
    try:
        moment = datetime.fromisoformat(text)
    except ValueError:
        return unknown
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def id_time(report_id: str) -> datetime:
    """The UTC time both relays put at the start of a report id; now when it is no real date."""
    try:
        return datetime.strptime(report_id[:15], "%Y%m%d-%H%M%S").replace(tzinfo=timezone.utc)
    except ValueError:
        return now()


def local_text(moment: datetime) -> str:
    return moment.astimezone().strftime("%Y-%m-%d %H:%M")


def stamp() -> str:
    return now().strftime("%Y%m%d-%H%M%S-%f")


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def secret_equals(supplied: str, expected: str) -> bool:
    return hmac.compare_digest(hashlib.sha256(supplied.encode("utf-8")).digest(), hashlib.sha256(expected.encode("utf-8")).digest())


def one_line(value) -> str:
    return re.sub(r"\s+", " ", value if isinstance(value, str) else "").strip()


def yaml_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def as_int(value, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def skill_body(markdown: str, title: str) -> str:
    """The instructions the author wrote: SKILL.md without the frontmatter and the '# title' line the relay added."""
    text = markdown.replace("\r\n", "\n")
    if text.startswith("---\n"):
        end = text.find("\n---\n", 4)
        if end >= 0:
            text = text[end + 5:]
    text = text.lstrip("\n")
    heading = "# " + title + "\n"
    if title and text.startswith(heading):
        text = text[len(heading):].lstrip("\n")
    return text.rstrip("\n")


def viewer_name(value) -> str:
    """A Windows account as the app sends it (percent-encoded; DOMAIN\\name → name), lowercased; empty when none was sent."""
    return urllib.parse.unquote(value or "").rsplit("\\", 1)[-1].strip().lower() if isinstance(value, str) else ""


def compose_skill(name: str, title: str, description: str, body: str) -> bytes:
    return f"---\nname: {name}\ndescription: {yaml_string(description)}\n---\n\n# {title}\n\n{body}\n".encode("utf-8")


def frontmatter_value(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] == '"':
        try:
            return json.loads(value)
        except ValueError:
            return value[1:-1]
    if len(value) >= 2 and value[0] == value[-1] == "'":
        return value[1:-1].replace("''", "'")
    return value


def split_skill(text: str) -> tuple:
    """A SKILL.md or plain instructions read as the app reads them: the frontmatter's name and description, the first
    '# heading', and the instructions without the frontmatter."""
    lines = text.replace("\r\n", "\n").lstrip("\ufeff").split("\n")
    name = description = None
    if len(lines) > 2 and lines[0].strip() == "---":
        close = next((index for index in range(1, len(lines)) if lines[index].strip() == "---"), None)
        if close:
            for line in lines[1:close]:
                key, separator, value = line.partition(":")
                if separator and key.strip() in ("name", "description"):
                    value = frontmatter_value(value) or None
                    if key.strip() == "name":
                        name = value
                    else:
                        description = value
            lines = lines[close + 1:]
    body = re.sub(r"\A(?:[ \t]*\n)+", "", "\n".join(lines)).rstrip()
    first = body.split("\n", 1)[0].strip()
    return name, description, first[2:].strip() if first.startswith("# ") else None, body


def body_for(body: str, title: str) -> str:
    """The instructions to keep: the relay writes the '# title' line itself, so a copy of it at the top goes."""
    first = body.split("\n", 1)[0]
    return re.sub(r"\A(?:[ \t]*\n)+", "", body[len(first):]) if first.strip() == "# " + title.strip() else body


def github_request(link: str, token: str = "", plugin: bool = False) -> urllib.request.Request:
    """The API request for the instructions a GitHub or GitHub Enterprise link points at: a file (blob or raw
    link), a folder (tree link: its SKILL.md) or a repository (its SKILL.md on the default branch). github.com is asked at
    api.github.com, an Enterprise server at <host>/api/v3. A plugin's link is its .ts or .js file itself."""
    parsed = urllib.parse.urlsplit((link or "").strip())
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise RelayError(400, "깃허브 링크(https://…)를 넣으세요.")
    host = parsed.netloc
    parts = [urllib.parse.unquote(part) for part in parsed.path.split("/") if part]
    ref = None
    if host.lower() == "raw.githubusercontent.com" or host.lower().startswith("raw."):
        # raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>, or raw.<enterprise host>/... with subdomain isolation
        if len(parts) < 4:
            raise RelayError(400, "깃허브 파일 링크가 아닙니다.")
        owner, repo, ref, path = parts[0], parts[1], parts[2], parts[3:]
        host = "github.com" if host.lower() == "raw.githubusercontent.com" else host[4:]
    elif parts[:1] == ["raw"] and len(parts) >= 5:
        # <enterprise host>/raw/<owner>/<repo>/<ref>/<path> without subdomain isolation
        owner, repo, ref, path = parts[1], parts[2], parts[3], parts[4:]
    elif len(parts) >= 2:
        owner, repo, rest = parts[0], parts[1], parts[2:]
        if len(rest) >= 2 and rest[0] in ("blob", "tree", "raw"):
            ref, path = rest[1], rest[2:]
        elif not rest:
            path = []
        else:
            raise RelayError(400, "깃허브 파일·폴더·저장소 링크가 아닙니다.")
    else:
        raise RelayError(400, "깃허브 파일·폴더·저장소 링크가 아닙니다.")
    repo = repo[:-4] if repo.endswith(".git") else repo
    path = "/".join(path)
    if plugin:
        if not path.lower().endswith(PLUGIN_EXTENSIONS):
            raise RelayError(400, "플러그인 파일(.ts 또는 .js)의 깃허브 링크를 넣으세요.")
    elif not path.lower().endswith((".md", ".markdown", ".txt")):
        path = (path + "/" if path else "") + "SKILL.md"
    api = "https://api.github.com" if host.lower() in ("github.com", "www.github.com") else f"{parsed.scheme}://{host}/api/v3"
    quote = urllib.parse.quote
    url = f"{api}/repos/{quote(owner, safe='')}/{quote(repo, safe='')}/contents/{quote(path)}" + (f"?ref={quote(ref, safe='')}" if ref else "")
    headers = {"Accept": "application/vnd.github.raw", "User-Agent": f"TerminalNotesRelay/{VERSION}"}
    if token:
        headers["Authorization"] = "token " + token
    return urllib.request.Request(url, headers=headers)


def fetch_github(link: str, token: str = "", plugin: bool = False) -> str:
    """The text of the file a GitHub link points at."""
    request = github_request(link, token, plugin)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            data = response.read(SKILL_SOURCE_MAX + 1)
    except urllib.error.HTTPError as error:
        reason = {401: "토큰이 없거나 맞지 않습니다(relay.py 설정의 RELAY_GITHUB_TOKEN)", 403: "접근이 막혔습니다(토큰 권한 확인)",
                  404: "파일을 찾지 못했습니다(링크·브랜치, 비공개 저장소면 RELAY_GITHUB_TOKEN 확인)"}.get(error.code, f"응답 {error.code}")
        raise RelayError(400, f"깃허브에서 읽지 못했습니다: {reason}.")
    except (urllib.error.URLError, OSError, ValueError) as error:
        raise RelayError(400, f"깃허브에 연결하지 못했습니다: {getattr(error, 'reason', error)}.")
    if len(data) > SKILL_SOURCE_MAX:
        raise RelayError(400, "깃허브 파일이 너무 큽니다(1 MB까지).")
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise RelayError(400, "깃허브 파일이 UTF-8 텍스트가 아닙니다.")
    if not text.strip():
        raise RelayError(400, "깃허브 파일이 비어 있습니다.")
    return text


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise


def write_json(path: Path, value) -> None:
    atomic_write(path, json.dumps(value, ensure_ascii=False, indent=1).encode("utf-8"))


def read_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None


def field(meta: dict, name: str, default=None):
    """Meta files from the .NET relay use PascalCase; accept camelCase too."""
    if not isinstance(meta, dict):
        return default
    for key in (name, name[:1].lower() + name[1:]):
        if key in meta:
            return meta[key]
    return default


def folder_size(path: Path) -> int:
    total = 0
    for root, _, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total


def parse_multipart(content_type: str, body: bytes) -> tuple:
    """multipart/form-data → ({name: text}, {name: (filename, bytes)}); enough for the app's report upload."""
    match = re.search(r'boundary="?([^";]+)"?', content_type, re.IGNORECASE)
    if not match:
        raise ValueError("boundary가 없습니다.")
    delimiter = b"--" + match.group(1).encode("latin-1")
    fields, files = {}, {}
    for part in body.split(delimiter)[1:]:
        if part.startswith(b"--"):
            break
        if part.startswith(b"\r\n"):
            part = part[2:]
        head, separator, content = part.partition(b"\r\n\r\n")
        if not separator:
            continue
        if content.endswith(b"\r\n"):
            content = content[:-2]
        headers = {}
        for line in head.decode("utf-8", "replace").split("\r\n"):
            if ":" in line:
                key, value = line.split(":", 1)
                headers[key.strip().lower()] = value.strip()
        disposition = headers.get("content-disposition", "")
        name = re.search(r'(?:^|;)\s*name="?([^";]+)"?', disposition)
        if not name:
            continue
        filename = re.search(r'filename="?([^";]+)"?', disposition)
        if filename:
            files[name.group(1)] = (filename.group(1), content)
        else:
            charset = re.search(r"charset=([\w-]+)", headers.get("content-type", ""))
            fields[name.group(1)] = content.decode(charset.group(1) if charset else "utf-8", "replace")
    return fields, files


class RelayError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


class RateLimiter:
    """Sliding window per (bucket, client address)."""

    def __init__(self):
        self._events = {}
        self._lock = threading.Lock()

    def allow(self, bucket: str, address: str, limit: int, window: float) -> bool:
        key, moment = (bucket, address), time.monotonic()
        with self._lock:
            events = self._events.setdefault(key, deque())
            while events and moment - events[0] > window:
                events.popleft()
            if len(events) >= limit:
                return False
            events.append(moment)
            return True

    def count(self, bucket: str, address: str, window: float) -> int:
        moment = time.monotonic()
        with self._lock:
            events = self._events.get((bucket, address), deque())
            return sum(1 for event in events if moment - event <= window)


class Notifier:
    """Posts short notices to RELAY_WEBHOOK_URL (Slack·Teams·Mattermost 'text', Discord 'content') off the request thread."""

    def __init__(self, url: str):
        self._url = url
        self._queue = queue.Queue(maxsize=100)
        if url:
            threading.Thread(target=self._run, name="webhook", daemon=True).start()

    def send(self, message: str) -> None:
        if not self._url:
            return
        try:
            self._queue.put_nowait(message)
        except queue.Full:
            log.warning("webhook queue full; notice dropped")

    def _run(self) -> None:
        while True:
            message = self._queue.get()
            data = json.dumps({"text": message, "content": message}, ensure_ascii=False).encode("utf-8")
            request = urllib.request.Request(self._url, data=data, headers={"Content-Type": "application/json"}, method="POST")
            try:
                with urllib.request.urlopen(request, timeout=10):
                    pass
            except Exception as error:  # a broken webhook must never affect the service
                log.warning("webhook failed: %s", type(error).__name__)


class Clients:
    """The PCs that reached the API since the relay started (memory only): which PCs can actually connect, their app
    version, and which announcement each one last received. A PC missing here cannot reach the relay (firewall, address)."""

    def __init__(self):
        self._seen = {}
        self._lock = threading.Lock()

    def touch(self, address: str, agent: str, feature: str) -> None:
        match = APP_AGENT.search(agent or "")
        version = match.group(1) if match else ""
        with self._lock:
            entry = self._seen.get(address)
            first = entry is None
            if first:
                if len(self._seen) >= CLIENTS_KEEP:
                    del self._seen[min(self._seen, key=lambda key: self._seen[key]["last"])]
                entry = self._seen[address] = {"address": address, "first": now(), "features": set(), "revision": None, "requests": 0}
            entry["last"] = now()
            entry["requests"] += 1
            if version or first:
                entry["version"] = version
            if feature in FEATURES:
                entry["features"].add(feature)
        if first:
            log.info("새 PC 접속: %s (앱 %s)", address, version or "1.0.518 이하")

    def received(self, address: str, revision: str) -> None:
        with self._lock:
            if address in self._seen:
                self._seen[address]["revision"] = revision

    def having(self, revision: str) -> int:
        """How many PCs last received this announcement."""
        with self._lock:
            return sum(1 for entry in self._seen.values() if entry["revision"] == revision)

    def all(self) -> list:
        with self._lock:
            entries = [dict(entry, features=set(entry["features"])) for entry in self._seen.values()]
        return sorted(entries, key=lambda entry: entry["last"], reverse=True)


# ── 문제 보고 ─────────────────────────────────────────────────────────────────────────────────────────

class Reports:
    def __init__(self, directory: Path):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def folder(self, report_id: str) -> Path:
        if not REPORT_ID.match(report_id or ""):
            raise RelayError(404, "보고서를 찾지 못했습니다.")
        path = self.directory / report_id
        if not (path / "report.txt").is_file():
            raise RelayError(404, "보고서를 찾지 못했습니다.")
        return path

    def add(self, report: str, logs: bytes | None, address: str) -> dict:
        report_id = now().strftime("%Y%m%d-%H%M%S") + "-" + os.urandom(4).hex()
        folder = self.directory / report_id
        folder.mkdir(parents=True)
        atomic_write(folder / "report.txt", report.encode("utf-8"))
        if logs:
            atomic_write(folder / "logs.zip", logs)
        meta = {"Id": report_id, "ReceivedAt": iso(now()), "RemoteAddress": address, "AppVersion": line_after(report, "앱 버전:"),
                "Summary": summary(report), "HasLogs": bool(logs), "Status": "new", "Note": "", "UpdatedAt": iso(now())}
        write_json(folder / "meta.json", meta)
        return meta

    def meta(self, report_id: str) -> dict:
        folder = self.folder(report_id)
        meta = read_json(folder / "meta.json") or {}
        # The report text is read only when its meta lacks the version or summary (long lists stay fast).
        report_text = "" if field(meta, "AppVersion") and field(meta, "Summary") else \
            (folder / "report.txt").read_text(encoding="utf-8", errors="replace")
        return {
            "id": report_id,
            # Housekeeping deletes by this time: without a readable meta.json (one is written just after the report text)
            # the id dates the report, never 1970.
            "receivedAt": parse_time(field(meta, "ReceivedAt"), id_time(report_id)),
            "remoteAddress": field(meta, "RemoteAddress") or "",
            "appVersion": field(meta, "AppVersion") or line_after(report_text, "앱 버전:"),
            "summary": field(meta, "Summary") or summary(report_text),
            "hasLogs": (folder / "logs.zip").is_file(),
            "status": field(meta, "Status") if field(meta, "Status") in STATUSES else "new",
            "note": field(meta, "Note") or "",
            "raw": meta,
        }

    def all(self) -> list:
        items = []
        for path in self.directory.iterdir() if self.directory.is_dir() else []:
            if REPORT_ID.match(path.name) and (path / "report.txt").is_file():
                try:
                    items.append(self.meta(path.name))
                except (OSError, RelayError):
                    pass
        return sorted(items, key=lambda item: item["receivedAt"], reverse=True)

    def text(self, report_id: str) -> str:
        return (self.folder(report_id) / "report.txt").read_text(encoding="utf-8", errors="replace")

    def update(self, report_id: str, status: str | None, note: str | None) -> None:
        with self._lock:
            folder = self.folder(report_id)
            meta = read_json(folder / "meta.json") or {}
            if status is not None:
                if status not in STATUSES:
                    raise RelayError(400, "상태가 올바르지 않습니다.")
                meta["Status"] = status
            if note is not None:
                meta["Note"] = note[:4000]
            meta["UpdatedAt"] = iso(now())
            write_json(folder / "meta.json", meta)

    def delete(self, report_id: str) -> None:
        with self._lock:
            shutil.rmtree(self.folder(report_id), ignore_errors=True)

    def housekeep(self, retention_days: int, max_mb: int) -> int:
        """Removes reports past retention, then the oldest until under the size cap. Reports being checked stay."""
        removed = 0
        reports = self.all()
        if retention_days > 0:
            limit = now() - timedelta(days=retention_days)
            for item in reports:
                if item["receivedAt"] < limit and item["status"] != "checking":
                    self.delete(item["id"])
                    removed += 1
        if max_mb > 0:
            budget = max_mb * 1024 * 1024
            remaining = [item for item in self.all() if item["status"] != "checking"]
            total = folder_size(self.directory)
            for item in sorted(remaining, key=lambda entry: entry["receivedAt"]):
                if total <= budget:
                    break
                size = folder_size(self.directory / item["id"])
                self.delete(item["id"])
                total -= size
                removed += 1
        return removed


def line_after(report: str, prefix: str) -> str:
    for line in report.split("\n"):
        line = line.strip()
        if line.startswith(prefix):
            return line[len(prefix):].strip()
    return ""


AUTO_REPORT_TITLE = "[Terminal Notes 자동 문제 보고]"
REPORT_PROBLEM_COUNT = re.compile(r"^\s*(.+?)\s+(\d{1,6})\s*$")
REPORT_EVENT_COUNT = re.compile(r"^\s*([A-Za-z0-9_.\-]{1,80})\s*[×x]\s*(\d{1,6})\s*$")


def report_counts(report: str, prefix: str, pattern: re.Pattern, separator: str) -> dict:
    """{name: count} from one line such as "문제: 비정상 종료 1 · 오류 3" or "문제 이벤트: terminal_lifecycle_fault ×2, …"."""
    counts: dict = {}
    for part in line_after(report, prefix).split(separator):
        found = pattern.match(part)
        if found:
            counts[found.group(1).strip()] = counts.get(found.group(1).strip(), 0) + int(found.group(2))
    return counts


def reports_analysis(reports: "Reports", days: int) -> dict:
    """What the reports of the last `days` days say together, for the program's 문제 보고 page: automatic and manual, the problems
    the automatic reports counted (Terminal Notes writes "문제:" and "문제 이벤트:" lines), app versions, PCs and reports per day."""
    since = now() - timedelta(days=days)
    items = [item for item in reports.all() if item["receivedAt"] >= since]
    problems: dict = {}
    events: dict = {}
    versions: dict = {}
    addresses: dict = {}
    per_day: dict = {}
    automatic = 0
    for item in items:
        try:
            text = reports.text(item["id"])
        except RelayError:
            text = ""
        if text.startswith(AUTO_REPORT_TITLE):
            automatic += 1
        for target, counts in ((problems, report_counts(text, "문제:", REPORT_PROBLEM_COUNT, "·")),
                               (events, report_counts(text, "문제 이벤트:", REPORT_EVENT_COUNT, ","))):
            for name, count in counts.items():
                target[name] = target.get(name, 0) + count
        for target, key in ((versions, item["appVersion"] or "버전 미상"), (addresses, item["remoteAddress"] or "주소 미상"),
                            (per_day, item["receivedAt"].astimezone().strftime("%Y-%m-%d"))):
            target[key] = target.get(key, 0) + 1

    def top(counts: dict, limit: int) -> list:
        return [{"name": name, "count": count} for name, count in sorted(counts.items(), key=lambda pair: (-pair[1], pair[0]))[:limit]]

    return {"days": days, "total": len(items), "automatic": automatic, "manual": len(items) - automatic,
            "new": sum(1 for item in items if item["status"] == "new"), "problems": top(problems, 10), "events": top(events, 10),
            "versions": top(versions, 6), "addresses": top(addresses, 6),
            "perDay": [{"day": day, "count": per_day[day]} for day in sorted(per_day)]}


def summary(report: str) -> str:
    lines = [line.strip() for line in report.split("\n")]
    try:
        text = lines[lines.index("[어떤 문제가 있었나요]") + 1]
    except (ValueError, IndexError):
        text = ""
    return "(설명 없음)" if not text else text[:80] + "…" if len(text) > 80 else text


def excerpt(markdown: str, limit: int = 120) -> str:
    """A skill's first line of instructions, for the store's cards: its frontmatter, headings, code fences, comments and rules
    left out, without list marks or emphasis."""
    text = markdown.replace("\r\n", "\n")
    if text.startswith("---\n"):
        end = text.find("\n---", 4)
        if end >= 0:
            text = text[end + 4:]
    for line in text.split("\n"):
        line = line.strip()
        if not line or line.startswith(("#", "```", "<!--", ">")) or set(line) <= set("-*_=|:+ "):
            continue
        line = re.sub(r"^([-*+]|\d+[.)])\s+", "", line)
        line = re.sub(r"[*_`]+", "", line).strip()
        if line:
            return line if len(line) <= limit else line[:limit - 1].rstrip() + "…"
    return ""


# ── 스킬 스토어 ───────────────────────────────────────────────────────────────────────────────────────

class Skills:
    def __init__(self, directory: Path):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def path(self, name: str) -> Path:
        if not (0 < len(name or "") <= 64 and SKILL_NAME.match(name)):
            raise RelayError(404, "스킬을 찾지 못했습니다.")
        return self.directory / name

    def read(self, name: str) -> dict | None:
        """The latest version's card: what the store list shows."""
        folder = self.path(name)
        meta = read_json(folder / "meta.json")
        if not meta or not (folder / "SKILL.md").is_file():
            return None
        numbers = self._numbers(folder)
        return {
            "name": name, "title": field(meta, "Title") or name, "description": field(meta, "Description") or "",
            "author": field(meta, "Author") or "", "createdAt": field(meta, "CreatedAt") or None, "updatedAt": field(meta, "UpdatedAt") or None,
            "sha256": field(meta, "Sha256") or "", "downloads": as_int(field(meta, "Downloads")),
            "version": as_int(field(meta, "Version"), numbers[-1] if numbers else 1), "versionCount": len(numbers) or 1,
            "note": field(meta, "Note") or "", "private": meta.get("Private") is True, "source": field(meta, "Source") or "",
            "category": field(meta, "Category") or "", "origin": field(meta, "Origin") or "",
            "sourceError": field(meta, "SourceError") or "",
        }

    @staticmethod
    def visible(skill: dict, viewer: str | None) -> bool:
        """A private skill is only for its author; None is the relay's own admin pages, which see every skill."""
        return viewer is None or not skill.get("private") or viewer_name(skill.get("author")) == viewer_name(viewer)

    def all(self, viewer: str | None = None) -> list:
        items = []
        for folder in self.directory.iterdir() if self.directory.is_dir() else []:
            if folder.is_dir() and len(folder.name) <= 64 and SKILL_NAME.match(folder.name):
                skill = self.read(folder.name)
                if skill and self.visible(skill, viewer):
                    try:
                        skill["summary"] = excerpt((folder / "SKILL.md").read_text(encoding="utf-8", errors="replace"))
                    except OSError:
                        skill["summary"] = ""
                    items.append(skill)
        return sorted(items, key=lambda skill: skill["title"])

    def _existing(self, name: str, viewer: str | None = None) -> Path:
        skill = self.read(name)
        if not skill or not self.visible(skill, viewer):
            raise RelayError(404, "스킬을 찾지 못했습니다.")
        return self.path(name)

    @staticmethod
    def _numbers(folder: Path) -> list:
        directory = folder / "versions"
        if not directory.is_dir():
            return []
        return sorted(int(item.name) for item in directory.iterdir() if VERSION_NUMBER.match(item.name) and (item / "SKILL.md").is_file())

    def _migrate(self, folder: Path) -> None:
        """A skill shared through the earlier relay has no versions yet: its current content becomes version 1."""
        if self._numbers(folder) or not (folder / "SKILL.md").is_file():
            return
        meta = read_json(folder / "meta.json") or {}
        self._write_version(folder, 1, (folder / "SKILL.md").read_bytes(), field(meta, "Title") or folder.name,
                            field(meta, "Description") or "", field(meta, "Author") or "", "",
                            field(meta, "UpdatedAt") or field(meta, "CreatedAt") or iso(now()))
        meta["Version"] = 1
        write_json(folder / "meta.json", meta)

    @staticmethod
    def _write_version(folder: Path, number: int, data: bytes, title: str, description: str, author: str, note: str, moment: str) -> None:
        target = folder / "versions" / str(number)
        atomic_write(target / "SKILL.md", data)
        write_json(target / "meta.json", {"Version": number, "Title": title, "Description": description, "Author": author,
                                          "PublishedAt": moment, "Sha256": sha256_hex(data), "Note": note})

    def _set_latest(self, folder: Path, number: int, data: bytes, title: str, description: str, author: str, note: str, moment: str,
                    extra: dict | None = None) -> None:
        # SKILL.md and meta.json at the top stay the latest version, the layout the earlier relay and app read. Being private
        # and the GitHub link carry over from the version before unless this one says otherwise.
        old = read_json(folder / "meta.json") or {}
        atomic_write(folder / "SKILL.md", data)
        meta = {"Name": folder.name, "Title": title, "Description": description, "Author": author,
                "CreatedAt": field(old, "CreatedAt") or moment, "UpdatedAt": moment, "Sha256": sha256_hex(data),
                "Downloads": as_int(field(old, "Downloads")), "Version": number, "Note": note}
        meta.update({key: old[key] for key in ("Private", "Source", "SourceCheckedAt", "SourceError", "Category", "Origin") if key in old})
        meta.update(extra or {})
        write_json(folder / "meta.json", meta)
        for old_number in self._numbers(folder)[:-SKILL_VERSIONS_KEEP]:
            shutil.rmtree(folder / "versions" / str(old_number), ignore_errors=True)

    @staticmethod
    def _version_info(number: int, meta: dict) -> dict:
        return {"version": number, "title": field(meta, "Title") or "", "description": field(meta, "Description") or "",
                "author": field(meta, "Author") or "", "publishedAt": field(meta, "PublishedAt") or None,
                "sha256": field(meta, "Sha256") or "", "note": field(meta, "Note") or ""}

    def versions(self, name: str, viewer: str | None = None) -> list:
        """Every kept version, newest first."""
        with self._lock:
            folder = self._existing(name, viewer)
            self._migrate(folder)
            return [self._version_info(number, read_json(folder / "versions" / str(number) / "meta.json") or {})
                    for number in reversed(self._numbers(folder))]

    def version(self, name: str, number: int | None = None, count_download: bool = False, viewer: str | None = None) -> dict:
        """One version (the latest when no number is given) with its SKILL.md and the instructions body the author wrote."""
        with self._lock:
            folder = self._existing(name, viewer)
            self._migrate(folder)
            numbers = self._numbers(folder)
            if number is None:
                number = numbers[-1]
            if number not in numbers:
                raise RelayError(404, "그 버전을 찾지 못했습니다.")
            source = folder / "versions" / str(number)
            content = (source / "SKILL.md").read_text(encoding="utf-8")
            meta = read_json(folder / "meta.json") or {}
            downloads = as_int(field(meta, "Downloads"))
            if count_download:
                downloads += 1
                meta["Downloads"] = downloads
                write_json(folder / "meta.json", meta)
            info = dict(self.read(name) or {})
            info.update(self._version_info(number, read_json(source / "meta.json") or {}))
            info.update({"latestVersion": numbers[-1], "downloads": downloads, "content": content,
                         "body": skill_body(content, info["title"])})
            return info

    @staticmethod
    def may_manage(skill: dict, viewer: str, admins: list) -> bool:
        """Its author, or a master account (MASTER_USERS; empty: the app decides), may change or delete a skill."""
        return viewer_name(skill.get("author")) == viewer or not admins or viewer in admins

    def set_private(self, name: str, private: bool, viewer: str) -> None:
        """Its author alone shows a skill to everyone or keeps it private, at any time and with no new version."""
        with self._lock:
            skill = self.read(name)
            if not skill or not self.visible(skill, viewer):
                raise RelayError(404, "스킬을 찾지 못했습니다.")
            if viewer_name(skill["author"]) != viewer:
                raise RelayError(403, "공개 범위는 작성자만 바꿀 수 있습니다.")
            meta = read_json(self.path(name) / "meta.json") or {}
            meta["Private"] = private
            write_json(self.path(name) / "meta.json", meta)

    def withdrawn(self, names: list, viewer: str) -> list:
        """Of these names, the skills this account may no longer use: private to someone else. The apps take them off their PCs.
        A name that no longer exists is not one: deleting a skill leaves what PCs applied."""
        found = []
        for name in names:
            if isinstance(name, str) and 0 < len(name) <= 64 and SKILL_NAME.match(name):
                skill = self.read(name)
                if skill and not self.visible(skill, viewer):
                    found.append(name)
        return found

    def publish(self, upload, viewer: str = "", admins: list | None = None) -> dict:
        """Shares a skill, or a new version of it: the Windows account that first shared a name adds versions, and a store
        admin may too when it says it edits ("edit"); the skill stays its author's."""
        if not isinstance(upload, dict):
            raise RelayError(400, "형식이 올바르지 않습니다.")
        name = (upload.get("name") or "").strip() if isinstance(upload.get("name"), str) else ""
        title, description, author = one_line(upload.get("title")), one_line(upload.get("description")), one_line(upload.get("author"))
        note = one_line(upload.get("note"))
        body = (upload.get("body") if isinstance(upload.get("body"), str) else "").replace("\r\n", "\n").strip()
        # The author may number a new version; it must be above the latest. Without one it is the next number.
        wanted = upload.get("version")
        if wanted is None or wanted == "":
            wanted = None
        elif isinstance(wanted, int) and not isinstance(wanted, bool) and 0 < wanted <= 999999:
            pass
        elif isinstance(wanted, str) and VERSION_NUMBER.match(wanted.strip()):
            wanted = int(wanted)
        else:
            raise RelayError(400, "버전은 1 이상의 숫자로 씁니다.")
        if not (0 < len(name) <= 64 and SKILL_NAME.match(name)):
            raise RelayError(400, "영문 이름은 소문자·숫자·하이픈(-)으로 64자까지 씁니다.")
        if not 0 < len(title) <= 80:
            raise RelayError(400, "제목은 80자까지 씁니다.")
        if not 0 < len(description) <= 500:
            raise RelayError(400, "설명은 500자까지 씁니다.")
        if not 0 < len(body) <= SKILL_BODY_MAX:
            raise RelayError(400, "내용이 비었거나 너무 깁니다.")
        if not 0 < len(author) <= 64:
            raise RelayError(400, "작성자를 확인할 수 없습니다.")
        if len(note) > 200:
            raise RelayError(400, "바뀐 점은 200자까지 씁니다.")
        # Only its author sees a private skill. A GitHub link makes the skill follow that file (sync_source).
        private = upload.get("private") if isinstance(upload.get("private"), bool) else None
        source = upload.get("source").strip() if isinstance(upload.get("source"), str) else None
        # A category the author picks or makes up when sharing; the store groups and filters by it.
        category = one_line(upload.get("category")) if isinstance(upload.get("category"), str) else None
        if category is not None and len(category) > 30:
            raise RelayError(400, "카테고리는 30자까지 씁니다.")
        # Where it came from, as the author wrote it: a user id or a link. Not the file it follows (source), and "" removes it.
        origin = one_line(upload.get("origin")) if isinstance(upload.get("origin"), str) else None
        if origin is not None and len(origin) > 300:
            raise RelayError(400, "출처는 300자까지 씁니다.")
        if source:
            github_request(source)
        data = compose_skill(name, title, description, body)
        digest = sha256_hex(data)
        with self._lock:
            folder = self.path(name)
            existing = self.read(name)
            owner = author
            if existing and existing["author"].lower() != author.lower():
                if upload.get("edit") is not True:
                    raise RelayError(409, "같은 영문 이름의 스킬이 이미 있습니다. 다른 이름을 쓰세요.")
                if not self.visible(existing, viewer) or not self.may_manage(existing, viewer, admins or []):
                    raise RelayError(403, "다른 사람의 스킬은 수정할 수 없습니다.")
                if private is not None and private != existing["private"]:
                    raise RelayError(403, "공개 범위는 작성자만 바꿀 수 있습니다.")
                # The version is the admin's; the skill stays its author's.
                owner = existing["author"]
            extra = {key: value for key, value in (("Private", private), ("Source", source), ("Category", category), ("Origin", origin))
                     if value is not None}
            if existing:
                self._migrate(folder)
                if existing["sha256"].upper() == digest:
                    if extra:
                        meta = read_json(folder / "meta.json") or {}
                        meta.update(extra)
                        write_json(folder / "meta.json", meta)
                    return {"name": name, "title": title, "author": owner, "sha256": digest, "version": existing["version"],
                            "changed": False, "updated": True}
            numbers = self._numbers(folder)
            latest = numbers[-1] if numbers else 0
            if wanted is not None and wanted <= latest:
                raise RelayError(400, f"버전은 지금 버전(v{latest})보다 큰 숫자로 씁니다.")
            number = wanted if wanted is not None else latest + 1
            moment = iso(now())
            self._write_version(folder, number, data, title, description, author, note, moment)
            self._set_latest(folder, number, data, title, description, owner, note, moment, extra)
        return {"name": name, "title": title, "author": author, "sha256": digest, "version": number, "changed": True, "updated": bool(existing)}

    def linked(self) -> list:
        """The names of the skills that follow a GitHub file."""
        names = []
        for folder in self.directory.iterdir() if self.directory.is_dir() else []:
            if folder.is_dir() and len(folder.name) <= 64 and SKILL_NAME.match(folder.name) and field(read_json(folder / "meta.json") or {}, "Source"):
                names.append(folder.name)
        return sorted(names)

    def sync_source(self, name: str, token: str = "") -> int | None:
        """Reads a linked skill's GitHub file again: a change becomes a new version by its author, noted as such. The new
        version's number, or None when nothing changed or the file could not be read (kept in SourceError)."""
        link = field(read_json(self.path(name) / "meta.json") or {}, "Source")
        if not link:
            return None
        try:
            text, problem = fetch_github(link, token), ""
        except RelayError as error:
            text, problem = None, error.message
        with self._lock:
            folder = self.path(name)
            meta = read_json(folder / "meta.json") or {}
            if field(meta, "Source") != link or not (folder / "SKILL.md").is_file():
                return None
            moment = iso(now())
            meta.update({"SourceCheckedAt": moment, "SourceError": problem})
            title, author = field(meta, "Title") or name, field(meta, "Author") or ""
            if text is not None:
                _, description, _, body = split_skill(text)
                description = one_line(description) or field(meta, "Description") or title
                body = body_for(body, title).strip()
                if not 0 < len(body) <= SKILL_BODY_MAX or len(description) > 500:
                    meta["SourceError"] = "깃허브 파일이 비었거나 너무 깁니다."
                else:
                    data = compose_skill(name, title, description, body)
                    if sha256_hex(data) != (field(meta, "Sha256") or "").upper():
                        self._migrate(folder)
                        numbers = self._numbers(folder)
                        number, note = (numbers[-1] if numbers else 0) + 1, "깃허브 변경 반영"
                        self._write_version(folder, number, data, title, description, author, note, moment)
                        self._set_latest(folder, number, data, title, description, author, note, moment, {"SourceCheckedAt": moment, "SourceError": ""})
                        return number
            write_json(folder / "meta.json", meta)
            return None

    def restore(self, name: str, number: int) -> int:
        """Makes an earlier version the latest again, as a new version, so apps on any version see it as an update."""
        with self._lock:
            folder = self._existing(name)
            self._migrate(folder)
            numbers = self._numbers(folder)
            if number not in numbers:
                raise RelayError(404, "그 버전을 찾지 못했습니다.")
            source = folder / "versions" / str(number)
            data = (source / "SKILL.md").read_bytes()
            current = self.read(name)
            if current["sha256"].upper() == sha256_hex(data):
                return current["version"]
            meta = read_json(source / "meta.json") or {}
            title, description = field(meta, "Title") or current["title"], field(meta, "Description") or current["description"]
            new, moment, note = numbers[-1] + 1, iso(now()), f"v{number}로 되돌림(관리자)"
            self._write_version(folder, new, data, title, description, current["author"], note, moment)
            self._set_latest(folder, new, data, title, description, current["author"], note, moment)
            return new

    def delete(self, name: str) -> None:
        """Moves the skill to .trash (kept 30 days), so a mistaken delete can be undone from the folder."""
        with self._lock:
            folder = self.path(name)
            if folder.is_dir():
                trash = self.directory / ".trash"
                trash.mkdir(exist_ok=True)
                shutil.move(str(folder), str(trash / f"{name}-{stamp()}"))

    def housekeep(self) -> None:
        trash = self.directory / ".trash"
        if trash.is_dir():
            limit = time.time() - 30 * 86400
            for item in trash.iterdir():
                try:
                    if item.stat().st_mtime < limit:
                        shutil.rmtree(item, ignore_errors=True)
                except OSError:
                    pass


# ── 플러그인 ───────────────────────────────────────────────────────────────────────────────────────────

PLUGIN_CODE_MAX = 300_000
PLUGIN_EXTENSIONS = (".ts", ".js")


class Plugins:
    """OpenCode plugins: one .ts or .js file each, which the app installs into OpenCode's plugins folder and OpenCode runs as code.
    Only master accounts share them (checked here when MASTER_USERS is set); every version is kept, the latest is served."""

    def __init__(self, directory: Path):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def path(self, name: str) -> Path:
        if not (0 < len(name or "") <= 64 and SKILL_NAME.match(name)):
            raise RelayError(404, "플러그인을 찾지 못했습니다.")
        return self.directory / name

    @staticmethod
    def _code_file(folder: Path) -> Path | None:
        return next((folder / ("plugin" + ext) for ext in PLUGIN_EXTENSIONS if (folder / ("plugin" + ext)).is_file()), None)

    def read(self, name: str) -> dict | None:
        folder = self.path(name)
        meta = read_json(folder / "meta.json")
        code = self._code_file(folder)
        if not meta or code is None:
            return None
        return {
            "name": name, "title": field(meta, "Title") or name, "description": field(meta, "Description") or "",
            "author": field(meta, "Author") or "", "createdAt": field(meta, "CreatedAt") or None, "updatedAt": field(meta, "UpdatedAt") or None,
            "sha256": field(meta, "Sha256") or "", "downloads": as_int(field(meta, "Downloads")), "version": as_int(field(meta, "Version"), 1),
            "note": field(meta, "Note") or "", "extension": code.suffix,
            "source": field(meta, "Source") or "", "sourceError": field(meta, "SourceError") or "",
        }

    def all(self) -> list:
        items = []
        for folder in self.directory.iterdir() if self.directory.is_dir() else []:
            if folder.is_dir() and len(folder.name) <= 64 and SKILL_NAME.match(folder.name):
                plugin = self.read(folder.name)
                if plugin:
                    items.append(plugin)
        return sorted(items, key=lambda plugin: plugin["title"])

    def get(self, name: str, count_download: bool = False) -> dict:
        with self._lock:
            info = self.read(name)
            if not info:
                raise RelayError(404, "플러그인을 찾지 못했습니다.")
            folder = self.path(name)
            if count_download:
                meta = read_json(folder / "meta.json") or {}
                meta["Downloads"] = as_int(field(meta, "Downloads")) + 1
                write_json(folder / "meta.json", meta)
                info["downloads"] = meta["Downloads"]
            info["code"] = self._code_file(folder).read_text(encoding="utf-8")
            return info

    def publish(self, upload, viewer: str = "", admins: list | None = None) -> dict:
        """Shares a plugin, or a new version of one: masters only when MASTER_USERS is set; a new version by its author or a master
        (the plugin stays its author's)."""
        if not isinstance(upload, dict):
            raise RelayError(400, "형식이 올바르지 않습니다.")
        admins = admins or []
        if admins and viewer not in admins:
            raise RelayError(403, "플러그인은 마스터 계정만 올립니다.")
        name = (upload.get("name") or "").strip() if isinstance(upload.get("name"), str) else ""
        title, description, author = one_line(upload.get("title")), one_line(upload.get("description")), one_line(upload.get("author"))
        note = one_line(upload.get("note"))
        extension = upload.get("extension") if upload.get("extension") in PLUGIN_EXTENSIONS else ".ts"
        code = (upload.get("code") if isinstance(upload.get("code"), str) else "").replace("\r\n", "\n")
        if not (0 < len(name) <= 64 and SKILL_NAME.match(name)):
            raise RelayError(400, "영문 이름은 소문자·숫자·하이픈(-)으로 64자까지 씁니다.")
        if not 0 < len(title) <= 80:
            raise RelayError(400, "제목은 80자까지 씁니다.")
        if len(description) > 500:
            raise RelayError(400, "설명은 500자까지 씁니다.")
        if not code.strip() or len(code) > PLUGIN_CODE_MAX:
            raise RelayError(400, "코드가 비었거나 너무 깁니다.")
        if not 0 < len(author) <= 64:
            raise RelayError(400, "작성자를 확인할 수 없습니다.")
        if len(note) > 200:
            raise RelayError(400, "바뀐 점은 200자까지 씁니다.")
        # A GitHub link makes the plugin follow that file (sync_source); "" stops it, nothing keeps what it had.
        source = upload.get("source").strip() if isinstance(upload.get("source"), str) else None
        if source:
            github_request(source, plugin=True)
            extension = Path(urllib.parse.urlsplit(source).path).suffix.lower()
        data = code.encode("utf-8")
        digest = sha256_hex(data)
        with self._lock:
            folder = self.path(name)
            existing = self.read(name)
            owner = author
            if existing and existing["author"].lower() != author.lower():
                if upload.get("edit") is not True:
                    raise RelayError(409, "같은 영문 이름의 플러그인이 이미 있습니다. 다른 이름을 쓰세요.")
                if not (viewer_name(existing["author"]) == viewer or not admins or viewer in admins):
                    raise RelayError(403, "다른 사람의 플러그인은 수정할 수 없습니다.")
                owner = existing["author"]
            if existing and existing["sha256"].upper() == digest and existing["extension"] == extension:
                if source is not None and source != existing["source"]:
                    meta = read_json(folder / "meta.json") or {}
                    meta.update({"Source": source, "SourceError": ""})
                    write_json(folder / "meta.json", meta)
                return {"name": name, "title": title, "author": owner, "version": existing["version"], "changed": False, "updated": True}
            numbers = sorted(int(item.name) for item in (folder / "versions").iterdir() if VERSION_NUMBER.match(item.name)) \
                if (folder / "versions").is_dir() else []
            number = (numbers[-1] if numbers else 0) + 1
            moment = iso(now())
            target = folder / "versions" / str(number)
            atomic_write(target / ("plugin" + extension), data)
            write_json(target / "meta.json", {"Version": number, "Title": title, "Description": description, "Author": author,
                                              "PublishedAt": moment, "Sha256": digest, "Note": note})
            old = read_json(folder / "meta.json") or {}
            for ext in PLUGIN_EXTENSIONS:
                if ext != extension and (folder / ("plugin" + ext)).is_file():
                    (folder / ("plugin" + ext)).unlink()
            atomic_write(folder / ("plugin" + extension), data)
            meta = {"Name": name, "Title": title, "Description": description, "Author": owner,
                    "CreatedAt": field(old, "CreatedAt") or moment, "UpdatedAt": moment, "Sha256": digest,
                    "Downloads": as_int(field(old, "Downloads")), "Version": number, "Note": note}
            meta.update({key: old[key] for key in ("Source", "SourceCheckedAt", "SourceError") if key in old})
            if source is not None:
                meta.update({"Source": source, "SourceError": ""})
            write_json(folder / "meta.json", meta)
        return {"name": name, "title": title, "author": author, "version": number, "changed": True, "updated": bool(existing)}

    def linked(self) -> list:
        """The names of the plugins that follow a GitHub file."""
        return sorted(folder.name for folder in (self.directory.iterdir() if self.directory.is_dir() else [])
                      if folder.is_dir() and len(folder.name) <= 64 and SKILL_NAME.match(folder.name)
                      and field(read_json(folder / "meta.json") or {}, "Source"))

    def sync_source(self, name: str, token: str = "") -> int | None:
        """Reads a linked plugin's GitHub file again: a change becomes a new version by its author ("깃허브 변경 반영"). The new
        version's number, or None when nothing changed or the file could not be read (kept in SourceError)."""
        meta = read_json(self.path(name) / "meta.json") or {}
        link = field(meta, "Source")
        if not link:
            return None
        try:
            text, problem = fetch_github(link, token, plugin=True), ""
        except RelayError as error:
            text, problem = None, error.message
        if text is not None and len(text) > PLUGIN_CODE_MAX:
            text, problem = None, "깃허브 파일이 너무 깁니다."
        if text is not None:
            title, author = field(meta, "Title") or name, field(meta, "Author") or ""
            current = self.read(name)
            code = text.replace("\r\n", "\n")
            extension = Path(urllib.parse.urlsplit(link).path).suffix.lower()
            if current and (current["sha256"].upper() != sha256_hex(code.encode("utf-8")) or current["extension"] != extension):
                result = self.publish({"name": name, "title": title, "description": field(meta, "Description") or "", "code": code,
                                       "extension": extension, "author": author, "note": "깃허브 변경 반영", "source": link})
                with self._lock:
                    fresh = read_json(self.path(name) / "meta.json") or {}
                    fresh.update({"SourceCheckedAt": iso(now()), "SourceError": ""})
                    write_json(self.path(name) / "meta.json", fresh)
                return result["version"] if result["changed"] else None
        with self._lock:
            fresh = read_json(self.path(name) / "meta.json") or {}
            if field(fresh, "Source") == link:
                fresh.update({"SourceCheckedAt": iso(now()), "SourceError": problem})
                write_json(self.path(name) / "meta.json", fresh)
        return None

    def delete(self, name: str) -> None:
        """Moves the plugin to .trash (kept 30 days)."""
        with self._lock:
            folder = self.path(name)
            if folder.is_dir():
                trash = self.directory / ".trash"
                trash.mkdir(exist_ok=True)
                shutil.move(str(folder), str(trash / f"{name}-{stamp()}"))

    housekeep = Skills.housekeep

# ── 공지 ─────────────────────────────────────────────────────────────────────────────────────────────

class Announcements:
    """One shared Markdown board. Revision = SHA-256 of its UTF-8 bytes, the same rule the app uses."""

    def __init__(self, directory: Path, editors: list):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self.editors = [user.lower() for user in editors]
        self._lock = threading.Lock()

    @property
    def current_path(self) -> Path:
        return self.directory / "current.md"

    def read(self) -> dict:
        meta = read_json(self.directory / "meta.json") or {}
        if not self.current_path.is_file():
            return {"text": "", "revision": "missing", "exists": False, "updatedAt": "", "updatedBy": ""}
        data = self.current_path.read_bytes()
        return {"text": data.decode("utf-8", "replace"), "revision": sha256_hex(data), "exists": True,
                "updatedAt": field(meta, "UpdatedAt") or "", "updatedBy": field(meta, "UpdatedBy") or ""}

    def is_editor(self, author: str) -> bool:
        # No list on the relay: the app already lets only its own editors save.
        return not self.editors or author.rsplit("\\", 1)[-1].strip().lower() in self.editors

    def save(self, text, expected_revision, expected_exists, author: str, admin: bool = False) -> dict:
        if not isinstance(text, str):
            raise RelayError(400, "공지 내용이 없습니다.")
        if not admin and not self.is_editor(author):
            raise RelayError(403, "이 Windows 계정은 공지를 작성할 수 없습니다.")
        data = text.encode("utf-8")
        if len(data) > ANNOUNCEMENT_MAX_BYTES:
            raise RelayError(400, "공지는 UTF-8 기준 256KB까지 저장할 수 있습니다.")
        if text.count("\n") + 1 > ANNOUNCEMENT_MAX_LINES:
            raise RelayError(400, "공지는 2,048줄까지 표시하고 저장할 수 있습니다.")
        with self._lock:
            current = self.read()
            if current["exists"] != bool(expected_exists) or (current["exists"] and current["revision"] != expected_revision):
                raise RelayError(409, "공지가 다른 곳에서 변경되었습니다. 내용을 복사한 뒤 창을 다시 열어 주세요.")
            if current["exists"]:
                history = self.directory / "history"
                history.mkdir(exist_ok=True)
                shutil.copy2(self.current_path, history / f"{stamp()}-{current['revision'][:8]}.md")
                for old in sorted(history.iterdir(), key=lambda path: path.name)[:-ANNOUNCEMENT_HISTORY_KEEP]:
                    try:
                        old.unlink()
                    except OSError:
                        pass
            atomic_write(self.current_path, data)
            write_json(self.directory / "meta.json", {"UpdatedAt": iso(now()), "UpdatedBy": author})
            return self.read()

    def history(self) -> list:
        directory = self.directory / "history"
        if not directory.is_dir():
            return []
        return [path.stem for path in sorted(directory.glob("*.md"), key=lambda path: path.name, reverse=True)]

    def version_text(self, version: str) -> str:
        if not STAMP.match(version or ""):
            raise RelayError(404, "그 버전을 찾지 못했습니다.")
        path = self.directory / "history" / f"{version}.md"
        if not path.is_file():
            raise RelayError(404, "그 버전을 찾지 못했습니다.")
        return path.read_text(encoding="utf-8", errors="replace")


# ── i-luncher ────────────────────────────────────────────────────────────────────────────────────────

LAUNCHER_CATALOG_MAX = 1_048_576
LAUNCHER_HISTORY_KEEP = 50
LAUNCHER_APP_ID = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")
# Screenshots of the launcher's app pages: PNG or JPG kept under launcher\media, named by their content (SHA-256, 16 hex) so
# a name never changes meaning and the launcher caches it for good. The app list names them per app (screenshots).
LAUNCHER_MEDIA_NAME = re.compile(r"^[a-f0-9]{16}\.(png|jpg)$")
LAUNCHER_MEDIA_MAX = 8 * 1024 * 1024
LAUNCHER_MEDIA_TYPES = {"png": "image/png", "jpg": "image/jpeg"}
LAUNCHER_MEDIA_KEEP_UNUSED = 86400  # seconds an image no app names is kept (uploaded before the app list is saved)
LAUNCHER_SCREENSHOTS_MAX = 12
LAUNCHER_DETAILS_MAX = 20_000
GITHUB_HOSTS = ("github.com", "www.github.com")


class LauncherStore:
    """i-luncher's shared app list (catalog.json, the same JSON the launcher keeps in a repository) and its notices.
    Revision = SHA-256 of the stored bytes: a save names the revision it started from, so two admins never overwrite
    each other."""

    def __init__(self, directory: Path, hosts: tuple = GITHUB_HOSTS):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self.hosts = hosts
        self._lock = threading.Lock()
        self._repositories: set | None = None

    def _read(self, name: str) -> tuple:
        path = self.directory / name
        if not path.is_file():
            return b"", ""
        data = path.read_bytes()
        return data, sha256_hex(data)

    def _save(self, name: str, data: bytes, expected_revision: str) -> str:
        with self._lock:
            current, revision = self._read(name)
            if (expected_revision or "") != revision:
                raise RelayError(409, "다른 곳에서 먼저 바꿨습니다. 새로 고친 뒤 다시 저장하세요.")
            if current:
                history = self.directory / "history"
                history.mkdir(exist_ok=True)
                (history / f"{stamp()}-{name}").write_bytes(current)
                for old in sorted(history.iterdir(), key=lambda path: path.name)[:-LAUNCHER_HISTORY_KEEP * 2]:
                    try:
                        old.unlink()
                    except OSError:
                        pass
            atomic_write(self.directory / name, data)
            self._repositories = None
            return sha256_hex(data)

    def catalog(self) -> dict:
        data, revision = self._read("catalog.json")
        apps = []
        if data:
            try:
                value = json.loads(data.decode("utf-8-sig"))
                apps = value.get("apps", []) if isinstance(value, dict) else value if isinstance(value, list) else []
            except (ValueError, UnicodeDecodeError):
                log.warning("launcher catalog.json is not JSON; serving an empty list")
        return {"schemaVersion": 1, "apps": [app for app in apps if isinstance(app, dict)], "revision": revision}

    @staticmethod
    def validate(apps) -> list:
        if not isinstance(apps, list):
            raise RelayError(400, "apps는 목록이어야 합니다.")
        seen = set()
        for app in apps:
            if not isinstance(app, dict):
                raise RelayError(400, "앱마다 JSON 객체여야 합니다.")
            app_id, name, repository = (str(app.get(key) or "").strip() for key in ("id", "name", "repository"))
            if not LAUNCHER_APP_ID.match(app_id):
                raise RelayError(400, f"앱 id가 올바르지 않습니다: {app_id or '(비어 있음)'}")
            if app_id in seen:
                raise RelayError(400, f"같은 앱 id가 두 번 있습니다: {app_id}")
            seen.add(app_id)
            if not name or len(name) > 100:
                raise RelayError(400, f"{app_id}: 이름이 비었거나 너무 깁니다.")
            if not repository or len(repository) > 512:
                raise RelayError(400, f"{app_id}: 저장소 주소가 비었거나 너무 깁니다.")
            details = app.get("details")
            if details is not None and (not isinstance(details, str) or len(details) > LAUNCHER_DETAILS_MAX):
                raise RelayError(400, f"{app_id}: 상세 설명은 {LAUNCHER_DETAILS_MAX}자까지 씁니다.")
            shots = app.get("screenshots")
            if shots is not None and (not isinstance(shots, list) or len(shots) > LAUNCHER_SCREENSHOTS_MAX or not all(
                    isinstance(shot, str) and (LAUNCHER_MEDIA_NAME.match(shot) or re.match(r"^https?://\S{1,500}$", shot)) for shot in shots)):
                raise RelayError(400, f"{app_id}: 스크린샷은 {LAUNCHER_SCREENSHOTS_MAX}장까지, 올린 이미지 이름이나 http(s) 주소로 씁니다.")
        return apps

    def save_catalog(self, apps, expected_revision: str) -> dict:
        data = (json.dumps({"schemaVersion": 1, "apps": self.validate(apps)}, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        if len(data) > LAUNCHER_CATALOG_MAX:
            raise RelayError(400, "앱 목록이 너무 큽니다(1 MB까지).")
        self._save("catalog.json", data, expected_revision)
        self.clean_media()
        return self.catalog()

    # Screenshots ------------------------------------------------------------------------------------------

    @property
    def media_directory(self) -> Path:
        return self.directory / "media"

    def media_file(self, name: str) -> Path:
        path = self.media_directory / name
        if not LAUNCHER_MEDIA_NAME.match(name) or not path.is_file():
            raise RelayError(404, "이미지를 찾지 못했습니다.")
        return path

    def add_media(self, data: bytes) -> str:
        """Keeps an uploaded PNG or JPG; its name is the first 16 hex of its SHA-256 (the same image twice is one file)."""
        if not data:
            raise RelayError(400, "이미지가 비어 있습니다.")
        if len(data) > LAUNCHER_MEDIA_MAX:
            raise RelayError(413, "이미지는 8 MB까지 올릴 수 있습니다.")
        kind = "png" if data.startswith(b"\x89PNG\r\n\x1a\n") else "jpg" if data.startswith(b"\xff\xd8\xff") else ""
        if not kind:
            raise RelayError(415, "PNG나 JPG 이미지만 올릴 수 있습니다.")
        name = f"{sha256_hex(data)[:16].lower()}.{kind}"
        path = self.media_directory / name
        if path.is_file():
            os.utime(path)  # counts as new: kept until an app list names it
        else:
            atomic_write(path, data)
        return name

    def clean_media(self) -> int:
        """Deletes images no app names any more, once they are a day old (an upload waits for its app list save)."""
        named = {shot for app in self.catalog()["apps"] for shot in (app.get("screenshots") or []) if isinstance(shot, str)}
        removed = 0
        try:
            files = [path for path in self.media_directory.iterdir() if path.is_file()]
        except OSError:
            return 0
        for path in files:
            try:
                if path.name not in named and time.time() - path.stat().st_mtime > LAUNCHER_MEDIA_KEEP_UNUSED:
                    path.unlink()
                    removed += 1
            except OSError:
                pass
        return removed

    def notices(self) -> dict:
        data, revision = self._read("notices.txt")
        return {"text": data.decode("utf-8-sig", "replace"), "revision": revision}

    def save_notices(self, text, expected_revision: str) -> dict:
        if not isinstance(text, str):
            raise RelayError(400, "공지 내용이 없습니다.")
        data = text.replace("\r\n", "\n").encode("utf-8")
        if len(data) > ANNOUNCEMENT_MAX_BYTES:
            raise RelayError(400, "공지는 UTF-8 기준 256KB까지 저장할 수 있습니다.")
        self._save("notices.txt", data, expected_revision)
        return self.notices()

    def repositories(self) -> set:
        """owner/name (lower case) of the app list's repositories on GitHub (github.com, or the GitHub Enterprise host of
        RELAY_GITHUB_API): the GitHub relay serves these."""
        if self._repositories is None:
            found = set()
            for app in self.catalog()["apps"]:
                parsed = urllib.parse.urlsplit(str(app.get("repository") or "").strip())
                parts = [part for part in parsed.path.split("/") if part]
                if parsed.netloc.lower() in self.hosts and len(parts) >= 2:
                    name = parts[1][:-4] if parts[1].lower().endswith(".git") else parts[1]
                    found.add(f"{parts[0]}/{name}".lower())
            self._repositories = found
        return self._repositories


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class GitHubProxy:
    """Reads from GitHub for apps that have no token: release lists, release files, repository files and trees, only for
    allowed repositories and only GET. JSON answers name this relay instead of api.github.com, so a launcher or app that
    follows an asset's url comes back here. Release files are streamed; GitHub's redirect to its file storage is followed
    without the token (the storage refuses a second authorization)."""

    ACCEPTS = ("application/vnd.github+json", "application/json", "application/octet-stream",
               "application/vnd.github.v3.raw", "application/vnd.github.raw", "application/vnd.github.raw+json")
    QUERY_KEYS = ("ref", "recursive", "per_page", "page")
    JSON_TTL = 60
    FILE_TTL = 300
    JSON_MAX = 32 * 1024 * 1024

    def __init__(self, config: Config, launcher: LauncherStore):
        self.config = config
        self.launcher = launcher
        self._lock = threading.Lock()
        self._cache: dict = {}
        self._token: tuple = ("", 0.0)
        self._login: tuple = ("", 0.0)

    # Token and the account behind it ------------------------------------------------------------------

    def token(self) -> str:
        if self.config.github_token:
            return self.config.github_token
        if not self.config.github_git_credential:
            return ""
        with self._lock:
            value, until = self._token
            if time.monotonic() < until:
                return value
        value = git_credential_token(self.config.github_web_host)
        with self._lock:
            # Kept an hour; a missing credential is asked again after five minutes.
            self._token = (value, time.monotonic() + (3600 if value else 300))
        return value

    def login(self) -> str:
        token = self.token()
        if not token:
            return ""
        with self._lock:
            value, until = self._login
            if time.monotonic() < until:
                return value
        value = ""
        try:
            request = urllib.request.Request(self.config.github_api + "/user", headers=self._headers("application/vnd.github+json", token))
            with urllib.request.urlopen(request, timeout=15) as response:
                value = str(json.loads(response.read().decode("utf-8")).get("login") or "").lower()
        except (urllib.error.URLError, OSError, ValueError) as error:
            log.warning("GitHub account behind the relay token not known: %s", getattr(error, "reason", error))
        with self._lock:
            self._login = (value, time.monotonic() + (3600 if value else 300))
        return value

    def _headers(self, accept: str, token: str) -> dict:
        headers = {"Accept": accept, "User-Agent": f"ApiServer/{VERSION}", "X-GitHub-Api-Version": "2022-11-28"}
        if token:
            headers["Authorization"] = "Bearer " + token
        return headers

    def allowed(self, owner: str, name: str) -> bool:
        full = f"{owner}/{name}".lower()
        for pattern in self.config.github_proxy_repos:
            if pattern == full or pattern.endswith("/*") and pattern[:-2] == owner.lower():
                return True
        if full in self.launcher.repositories():
            return True
        login = self.login()
        return bool(login) and login == owner.lower()

    # Requests -----------------------------------------------------------------------------------------

    @staticmethod
    def route(parts: list) -> tuple:
        """owner, name and the API path for an allowed shape of /api/v3/repos/<owner>/<name>/..."""
        if len(parts) < 3 or parts[0] != "repos":
            raise RelayError(404, "GitHub 중계는 저장소(/api/v3/repos/...)만 읽습니다.")
        owner, name, tail = parts[1], parts[2], parts[3:]
        if not re.match(r"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", owner) or not re.match(r"^[A-Za-z0-9._-]{1,100}$", name) or name in (".", ".."):
            raise RelayError(404, "저장소 이름이 올바르지 않습니다.")
        shapes = (tail == [] or tail == ["releases"] or tail == ["releases", "latest"]
                  or len(tail) == 3 and tail[:2] == ["releases", "tags"]
                  or len(tail) == 3 and tail[:2] == ["releases", "assets"] and tail[2].isdigit()
                  or len(tail) >= 2 and tail[0] == "contents" and ".." not in tail
                  or len(tail) == 3 and tail[:2] == ["git", "trees"])
        if not shapes:
            raise RelayError(404, "GitHub 중계가 읽지 않는 주소입니다.")
        quote = urllib.parse.quote
        path = "/repos/" + "/".join(quote(part, safe="") for part in [owner, name] + tail)
        return owner, name, path

    def request(self, handler: "Handler", parts: list) -> None:
        owner, name, path = self.route(parts)
        if not self.allowed(owner, name):
            raise RelayError(404, f"{owner}/{name}은(는) 이 서버의 GitHub 중계에 열려 있지 않습니다(LAUNCHER_GITHUB_REPOS 또는 런처 앱 목록).")
        query = urllib.parse.urlencode([(key, value) for key in self.QUERY_KEYS for value in handler.query.get(key, [])[:1]])
        accept = (handler.headers.get("Accept") or "").split(",")[0].strip().lower()
        accept = accept if accept in self.ACCEPTS else "application/vnd.github+json"
        url = self.config.github_api + path + ("?" + query if query else "")
        if accept == "application/octet-stream" or "raw" in accept:
            return self._stream(handler, url, accept)
        base = handler.public_base() + "/api/v3"
        status, body = self._json(url, accept, ttl=self.FILE_TTL if "/contents/" in path or "/git/trees/" in path else self.JSON_TTL)
        # The same answer GitHub gave, with its API address replaced by this relay's (asset urls, repository urls).
        text = body.decode("utf-8", "replace").replace(self.config.github_api + "/", base + "/")
        handler._send(status, text.encode("utf-8"), "application/json; charset=utf-8")

    def _json(self, url: str, accept: str, ttl: int) -> tuple:
        key = (url, accept)
        with self._lock:
            hit = self._cache.get(key)
            if hit and time.monotonic() < hit[0]:
                return hit[1], hit[2]
        request = urllib.request.Request(url, headers=self._headers(accept, self.token()))
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                status, body = response.status, response.read(self.JSON_MAX + 1)
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read(65536)
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise RelayError(502, f"GitHub에 연결하지 못했습니다: {getattr(error, 'reason', error)}")
        if len(body) > self.JSON_MAX:
            raise RelayError(502, "GitHub 응답이 너무 큽니다.")
        if status == 200:
            with self._lock:
                if len(self._cache) > 500:
                    self._cache.clear()
                self._cache[key] = (time.monotonic() + ttl, status, body)
        return status, body

    def _stream(self, handler: "Handler", url: str, accept: str) -> None:
        token = self.token()
        opener = urllib.request.build_opener(_NoRedirect)
        try:
            try:
                response = opener.open(urllib.request.Request(url, headers=self._headers(accept, token)), timeout=60)
            except urllib.error.HTTPError as error:
                if error.code not in (301, 302, 303, 307, 308) or not error.headers.get("Location"):
                    return handler._send(error.code, error.read(65536), "application/json; charset=utf-8")
                # GitHub's file storage: a signed address that must not get the token.
                location = urllib.parse.urljoin(url, error.headers["Location"])
                response = urllib.request.urlopen(urllib.request.Request(location, headers={"User-Agent": f"ApiServer/{VERSION}"}), timeout=60)
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise RelayError(502, f"GitHub에서 파일을 받지 못했습니다: {getattr(error, 'reason', error)}")
        with response:
            length = response.headers.get("Content-Length")
            handler._status = response.status
            handler.send_response(response.status)
            handler.send_header("Content-Type", response.headers.get("Content-Type") or "application/octet-stream")
            if length:
                handler.send_header("Content-Length", length)
            else:
                handler.close_connection = True
            handler.send_header("Cache-Control", "no-store")
            handler.end_headers()
            while True:
                chunk = response.read(65536)
                if not chunk:
                    break
                handler.wfile.write(chunk)

    def forget(self, prefix: str) -> None:
        """Drops the cached answers under a URL prefix (a repository after its release was started)."""
        with self._lock:
            for key in [key for key in self._cache if key[0].startswith(prefix)]:
                del self._cache[key]


# ── 릴리스 ───────────────────────────────────────────────────────────────────────────────────────────

RELEASE_WORKFLOW = "release.yml"
RELEASE_VERSION_TEXT = re.compile(r"^(0|[1-9][0-9]{0,4})\.[0-9]\.[0-9]$")
RELEASE_ACTIVE = ("requested", "queued", "pending", "waiting", "in_progress")
RELEASE_REQUEST_SHOWN = 180  # seconds a started release shows as requested until GitHub lists its run


def release_version(text) -> tuple | None:
    """(major, minor, patch) in a tag or version such as v1.2.3, or None."""
    match = re.search(r"(\d+)\.(\d+)\.(\d+)", str(text or ""))
    return tuple(int(part) for part in match.groups()) if match else None


def next_release_version(latest: tuple | None, floor: tuple | None) -> str:
    """What the release workflows pick by themselves: the last release + 1 with every part 0-9 (1.0.9 -> 1.1.0,
    1.9.9 -> 2.0.0); the project file's <Version> is the floor (never backwards); 1.0.0 without either."""
    floor = floor or (1, 0, 0)
    if latest and latest >= floor:
        major, minor, patch = latest[0], latest[1], latest[2] + 1
        if patch > 9:
            minor, patch = minor + 1, 0
        if minor > 9:
            major, minor, patch = major + 1, 0, 0
        return f"{major}.{minor}.{patch}"
    return ".".join(str(part) for part in floor)


class Releases:
    """The API 서버 program's release page. Projects: the app list's GitHub repositories, then the owner/name entries of
    LAUNCHER_GITHUB_REPOS (the launcher itself, for example). For each: the last release, the next version and the
    latest run of .github/workflows/release.yml. A release runs that workflow (workflow_dispatch) with the chosen version
    as its input; the workflow builds and publishes as for a push, and refuses a version that is not above the last release."""

    def __init__(self, config: Config, launcher: LauncherStore, github: GitHubProxy):
        self.config = config
        self.launcher = launcher
        self.github = github
        self._lock = threading.Lock()
        self._requested: dict = {}  # repository (lower case) -> (version, when)

    def projects(self) -> list:
        """[(owner/name, display name)] in the launcher's order."""
        found, seen = [], set()
        apps = self.launcher.catalog()["apps"]
        ordered = sorted(enumerate(apps), key=lambda item: (as_int(item[1].get("order"), 1 << 30), item[0]))
        for _, app in ordered:
            parsed = urllib.parse.urlsplit(str(app.get("repository") or "").strip())
            parts = [part for part in parsed.path.split("/") if part]
            if parsed.netloc.lower() not in self.config.github_hosts or len(parts) < 2:
                continue
            name = parts[1][:-4] if parts[1].lower().endswith(".git") else parts[1]
            full = f"{parts[0]}/{name}"
            if full.lower() not in seen:
                seen.add(full.lower())
                found.append((full, str(app.get("name") or name)))
        for pattern in self.config.github_proxy_repos:
            if pattern.count("/") == 1 and not pattern.endswith("/*") and pattern not in seen:
                seen.add(pattern)
                found.append((pattern, pattern.split("/", 1)[1]))
        return found

    @staticmethod
    def _base(full: str) -> str:
        owner, _, name = full.partition("/")
        if not re.match(r"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", owner) or not re.match(r"^[A-Za-z0-9._-]{1,100}$", name) or name in (".", ".."):
            raise RelayError(400, f"저장소 이름이 올바르지 않습니다: {full}")
        return "/repos/" + urllib.parse.quote(owner, safe="") + "/" + urllib.parse.quote(name, safe="")

    def _get(self, path: str, ttl: int, raw: bool = False) -> tuple:
        status, body = self.github._json(self.config.github_api + path, "application/vnd.github.raw" if raw else "application/vnd.github+json", ttl)
        if raw:
            return status, body.decode("utf-8-sig", "replace") if status == 200 else ""
        try:
            return status, json.loads(body.decode("utf-8")) if body else None
        except ValueError:
            return status, None

    @staticmethod
    def _problem(status: int, answer) -> str:
        message = answer.get("message") if isinstance(answer, dict) else ""
        return f"GitHub 오류 {status}" + (f": {message}" if message else "")

    def info(self, full: str, name: str) -> dict:
        entry = {"repository": full, "name": name, "latest": "", "latestTag": "", "latestAt": "", "floor": "", "next": "",
                 "chooseVersion": False, "run": None, "error": ""}
        try:
            base = self._base(full)
            status, release = self._get(base + "/releases/latest", ttl=10)
            latest = None
            if status == 200 and isinstance(release, dict):
                entry["latestTag"] = str(release.get("tag_name") or "")
                entry["latestAt"] = str(release.get("published_at") or release.get("created_at") or "")
                latest = release_version(entry["latestTag"])
                entry["latest"] = ".".join(map(str, latest)) if latest else ""
            elif status != 404:
                raise RelayError(502, self._problem(status, release))
            status, workflow = self._get(f"{base}/contents/.github/workflows/{RELEASE_WORKFLOW}", ttl=60, raw=True)
            if status != 200:
                raise RelayError(404, f"릴리스 워크플로(.github/workflows/{RELEASE_WORKFLOW})가 없습니다." if status == 404 else f"GitHub 오류 {status}")
            entry["chooseVersion"] = "inputs.version" in workflow
            # The project file the workflow builds: its <Version> is the floor, as in the workflow's own version step.
            floor = None
            project = re.search(r"[A-Za-z0-9_.\\/-]+\.csproj", workflow)
            if project:
                path = "/".join(urllib.parse.quote(part, safe="") for part in project.group(0).replace("\\", "/").split("/") if part)
                status, text = self._get(f"{base}/contents/{path}", ttl=60, raw=True)
                version = re.search(r"<Version>\s*(\d+)\.(\d+)(?:\.(\d+))?", text) if status == 200 else None
                if version:
                    floor = (int(version.group(1)), int(version.group(2)), int(version.group(3) or 0))
                    entry["floor"] = ".".join(map(str, floor))
            entry["next"] = next_release_version(latest, floor)
            status, runs = self._get(f"{base}/actions/workflows/{RELEASE_WORKFLOW}/runs?per_page=1", ttl=0)
            if status == 200 and isinstance(runs, dict) and runs.get("workflow_runs"):
                run = runs["workflow_runs"][0]
                entry["run"] = {"status": run.get("status") or "", "conclusion": run.get("conclusion") or "", "event": run.get("event") or "",
                                "createdAt": run.get("created_at") or "", "url": run.get("html_url") or "", "number": run.get("run_number")}
        except RelayError as error:
            entry["error"] = error.message
        # Started here but not listed by GitHub yet (it takes a few seconds): shown as requested.
        with self._lock:
            requested = self._requested.get(full.lower())
        if requested and (now() - requested[1]).total_seconds() < RELEASE_REQUEST_SHOWN:
            run = entry["run"]
            if not run or parse_time(run.get("createdAt"), datetime.min.replace(tzinfo=timezone.utc)) < requested[1] - timedelta(seconds=5):
                entry["run"] = {"status": "requested", "conclusion": "", "event": "workflow_dispatch", "createdAt": iso(requested[1]),
                                "url": "", "number": None, "version": requested[0]}
            elif run.get("event") == "workflow_dispatch":
                run["version"] = requested[0]
        return entry

    def list(self) -> list:
        projects = self.projects()
        if not projects:
            return []
        with ThreadPoolExecutor(max_workers=min(8, len(projects))) as pool:
            return list(pool.map(lambda item: self.info(*item), projects))

    def release(self, repository, version) -> dict:
        if not isinstance(repository, str) or not isinstance(version, str):
            raise RelayError(400, "저장소와 버전이 필요합니다.")
        version = version.strip()
        version = version[1:] if version[:1] in ("v", "V") else version
        known = {full.lower(): (full, name) for full, name in self.projects()}.get(repository.strip().lower())
        if not known:
            raise RelayError(404, "릴리스 목록에 없는 저장소입니다(앱 목록 또는 LAUNCHER_GITHUB_REPOS).")
        full, name = known
        if not RELEASE_VERSION_TEXT.match(version):
            raise RelayError(400, "버전은 1.2.3 형식으로 쓰세요 (가운데·끝 자리는 0~9).")
        token = self.github.token()
        if not token:
            raise RelayError(503, "GitHub 토큰이 없습니다(RELAY_GITHUB_TOKEN 또는 이 PC의 git 자격 증명).")
        base = self._base(full)
        with self._lock:
            requested = self._requested.get(full.lower())
            if requested and (now() - requested[1]).total_seconds() < 60:
                raise RelayError(409, f"{name}은(는) 방금 v{requested[0]} 릴리스를 시작했습니다.")
        self.github.forget(self.config.github_api + base)
        info = self.info(full, name)
        if info["error"]:
            raise RelayError(409, f"{name}: {info['error']}")
        if info["run"] and info["run"]["status"] in RELEASE_ACTIVE:
            raise RelayError(409, f"{name}의 릴리스가 이미 진행 중입니다. 끝난 뒤에 다시 하세요.")
        latest = release_version(info["latest"])
        if latest and release_version(version) <= latest:
            raise RelayError(409, f"v{version}은(는) 마지막 릴리스 {info['latestTag']}보다 커야 합니다.")
        if not info["chooseVersion"] and version != info["next"]:
            raise RelayError(409, f"{name}의 릴리스 워크플로는 버전을 받지 않아 다음 버전 {info['next']}만 릴리스할 수 있습니다.")
        for tag in (f"v{version}", version):
            status, _ = self._get(f"{base}/git/ref/tags/{urllib.parse.quote(tag, safe='')}", ttl=0)
            if status == 200:
                raise RelayError(409, f"{tag} 태그가 이미 있습니다.")
        status, repo = self._get(base, ttl=300)
        branch = repo.get("default_branch") if status == 200 and isinstance(repo, dict) else ""
        if not branch:
            raise RelayError(502, self._problem(status, repo))
        payload = {"ref": branch}
        if info["chooseVersion"]:
            payload["inputs"] = {"version": version}
        url = f"{self.config.github_api}{base}/actions/workflows/{RELEASE_WORKFLOW}/dispatches"
        headers = dict(self.github._headers("application/vnd.github+json", token), **{"Content-Type": "application/json"})
        request = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                response.read()
        except urllib.error.HTTPError as error:
            try:
                answer = json.loads(error.read(65536).decode("utf-8"))
            except ValueError:
                answer = None
            hint = " 토큰에 workflow 권한이 있는지 확인하세요." if error.code in (401, 403, 404) else ""
            raise RelayError(502, f"GitHub이 릴리스 실행을 거절했습니다. {self._problem(error.code, answer)}{hint}")
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise RelayError(502, f"GitHub에 연결하지 못했습니다: {getattr(error, 'reason', error)}")
        with self._lock:
            self._requested[full.lower()] = (version, now())
        self.github.forget(self.config.github_api + base)
        log.info("release %s v%s started from the admin window (%s)", full, version, branch)
        return {"repository": full, "name": name, "version": version, "ref": branch}


def git_credential_token(host: str) -> str:
    """The token this PC's git credential helper keeps for host (the account that pushes the repositories). Never shown."""
    environment = dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="never")
    try:
        result = subprocess.run(["git", "credential", "fill"], input=f"protocol=https\nhost={host}\n\n", capture_output=True,
                                text=True, timeout=20, env=environment,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.SubprocessError) as error:
        log.warning("git credential for %s not available: %s", host, error)
        return ""
    for line in result.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):].strip()
    return ""


# ── GitHub Actions 러너 ─────────────────────────────────────────────────────────────────────────────

RUNNERS_TASK = "GitHub Runners"
# Earlier setups started each runner with a logon task of its own; the runner page puts RUNNERS_TASK in their place.
LEGACY_RUNNER_TASK = re.compile(r"^(GitHub runner .+|i-luncher build runner)$", re.IGNORECASE)
RUNNER_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
RUNNER_ENV_LINE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
RUNNER_STATE = "runners.json"
RUNNER_ENV_MAX = 16_384
RUNNER_GITHUB_TTL = 30
RUNNER_SKIP = {"run-helper.cmd", "update.finished", RUNNER_STATE}  # a runner's own files, not part of a new runner's copy
RUNNER_SESSION_LEFT = ("session for this runner already exists", "session conflict")
CREATE_BREAKAWAY_FROM_JOB = 0x01000000
# Run by a short helper process: attaching to another console is for a whole process, and the relay serves requests meanwhile.
# Ctrl+Break (unlike Ctrl+C) reaches a process even when it was started to ignore Ctrl+C; the runner answers both by closing
# its GitHub session and exiting.
CTRL_BREAK_HELPER = (
    "import ctypes, sys, time\n"
    "kernel = ctypes.WinDLL('kernel32')\n"
    "keep = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_uint)(lambda event: 1)\n"
    "kernel.FreeConsole()\n"
    "if not kernel.AttachConsole(int(sys.argv[1])):\n"
    "    sys.exit(2)\n"
    "kernel.SetConsoleCtrlHandler(keep, 1)\n"
    "sent = kernel.GenerateConsoleCtrlEvent(1, 0)\n"
    "time.sleep(0.5)\n"
    "kernel.FreeConsole()\n"
    "sys.exit(0 if sent else 3)\n")

_KERNEL = None


def _kernel():
    global _KERNEL
    if _KERNEL is None:
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        _KERNEL = kernel
    return _KERNEL


def windows_processes() -> list:
    """[(pid, parent pid, program file name)] of this PC's processes; empty off Windows."""
    if os.name != "nt":
        return []
    import ctypes
    from ctypes import wintypes

    class Entry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                    ("szExeFile", ctypes.c_wchar * 260)]

    kernel = _kernel()
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
    if not snapshot or snapshot == ctypes.c_void_p(-1).value:
        return []
    found = []
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(Entry)
        more = kernel.Process32FirstW(wintypes.HANDLE(snapshot), ctypes.byref(entry))
        while more:
            found.append((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile))
            more = kernel.Process32NextW(wintypes.HANDLE(snapshot), ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return found


def process_image(pid: int) -> str:
    """The program file a process runs (junctions resolved); "" when Windows does not tell."""
    if os.name != "nt":
        return ""
    import ctypes
    from ctypes import wintypes
    kernel = _kernel()
    handle = kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ""
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(32768)
        return buffer.value if kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)) else ""
    finally:
        kernel.CloseHandle(handle)


def kill_process(pid: int) -> bool:
    if os.name != "nt":
        return False
    kernel = _kernel()
    handle = kernel.OpenProcess(0x0001, False, pid)  # PROCESS_TERMINATE
    if not handle:
        return False
    try:
        return bool(kernel.TerminateProcess(handle, 1))
    finally:
        kernel.CloseHandle(handle)


def send_ctrl_break(pid: int) -> bool:
    """Ctrl+Break to the (hidden) console a process runs in."""
    try:
        return subprocess.run([sys.executable, "-I", "-c", CTRL_BREAK_HELPER, str(pid)], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL, timeout=30, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def user_environment() -> dict:
    """This account's environment as a new logon gets it (variables set since the relay started count), for the runners;
    the relay's own when Windows cannot build it."""
    if os.name != "nt":
        return dict(os.environ)
    import ctypes
    from ctypes import wintypes
    userenv = ctypes.WinDLL("userenv", use_last_error=True)
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    userenv.CreateEnvironmentBlock.argtypes = [ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.BOOL]
    userenv.DestroyEnvironmentBlock.argtypes = [ctypes.c_void_p]
    kernel = _kernel()
    token = wintypes.HANDLE()
    if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 0x0008, ctypes.byref(token)):  # TOKEN_QUERY
        return dict(os.environ)
    block = ctypes.c_void_p()
    try:
        if not userenv.CreateEnvironmentBlock(ctypes.byref(block), token, False):
            return dict(os.environ)
        environment, offset = {}, 0
        try:
            while True:
                entry = ctypes.wstring_at(block.value + offset)
                if not entry:
                    break
                offset += (len(entry) + 1) * 2
                key, separator, value = entry.partition("=")
                if separator and key:
                    environment[key] = value
        finally:
            userenv.DestroyEnvironmentBlock(block)
        return environment if environment else dict(os.environ)
    finally:
        kernel.CloseHandle(token)


def console_text(data: bytes) -> str:
    """Output of a Windows console program (schtasks, config.cmd): UTF-8, else the console's or Windows' code page."""
    for encoding in ("utf-8", "oem", "mbcs"):
        try:
            return data.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return data.decode("utf-8", "replace")


def scheduled_tasks() -> list:
    """This PC's Task Scheduler tasks by full name (\\GitHub Runners); empty off Windows or when schtasks fails."""
    if os.name != "nt":
        return []
    import csv
    import io
    try:
        result = subprocess.run(["schtasks", "/Query", "/FO", "CSV", "/NH"], stdin=subprocess.DEVNULL, capture_output=True, timeout=60,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.SubprocessError):
        return []
    names = []
    for row in csv.reader(io.StringIO(console_text(result.stdout))):
        if row and row[0].startswith("\\") and row[0] not in names:
            names.append(row[0])
    return names


def task_definition(name: str) -> str:
    """A task's XML ("" when it does not exist)."""
    if os.name != "nt":
        return ""
    try:
        result = subprocess.run(["schtasks", "/Query", "/TN", name, "/XML"], stdin=subprocess.DEVNULL, capture_output=True, timeout=60,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.SubprocessError):
        return ""
    return console_text(result.stdout) if result.returncode == 0 else ""


def runners_task_xml(command: str, arguments: str, folder: str, user: str) -> str:
    """The one logon task: starts the runners that are on, shortly after this account logs on, and ends."""
    esc = html.escape
    return f"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{esc("켜 둔 GitHub Actions 러너를 시작합니다 (i-luncher API Manager의 러너 화면에서 관리)")}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{esc(user)}</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{esc(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{esc(command)}</Command>
      <Arguments>{esc(arguments)}</Arguments>
      <WorkingDirectory>{esc(folder)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"""


def register_task(name: str, xml: str) -> str:
    """Creates or replaces a task of this account (no administrator rights needed); "" or why it failed."""
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "task.xml"
        path.write_text(xml, encoding="utf-16")
        try:
            result = subprocess.run(["schtasks", "/Create", "/TN", name, "/XML", str(path), "/F"], stdin=subprocess.DEVNULL, capture_output=True,
                                    timeout=60, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except (OSError, subprocess.SubprocessError) as error:
            return str(error)
    return "" if result.returncode == 0 else console_text(result.stdout + result.stderr).strip()[:300] or f"종료 코드 {result.returncode}"


def xml_value(xml: str, tag: str) -> str:
    match = re.search(f"<{tag}>(.*?)</{tag}>", xml, re.DOTALL)
    return html.unescape(match.group(1)).strip() if match else ""


def runner_version(folder: Path) -> str:
    """The runner's version: its bin is a junction to bin.<version> once the runner has updated itself."""
    try:
        target = os.readlink(folder / "bin")
    except (OSError, ValueError, NotImplementedError):
        return ""
    match = re.search(r"bin\.(\d+\.\d+\.\d+)$", target.rstrip("\\/"))
    return match.group(1) if match else ""


def is_link(path: Path) -> bool:
    """A symbolic link or a directory junction (its target is not inside it)."""
    try:
        info = os.lstat(path)
    except OSError:
        return False
    return path.is_symlink() or bool(getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


def remove_tree(folder: Path) -> None:
    """Deletes a runner folder; its junctions (bin, externals) go as links, never through to what they point at."""
    def retry(function, path, _):
        os.chmod(path, stat.S_IWRITE)
        function(path)

    for root, folders, _ in os.walk(folder):
        for name in list(folders):
            path = Path(root) / name
            if is_link(path):
                os.rmdir(path)
                folders.remove(name)
    shutil.rmtree(folder, onerror=retry)


def link_tree(source: Path, target: Path) -> int:
    """Copies a folder as hard links (no extra disk space; a file is copied where linking fails). Returns the file count."""
    count = 0
    for root, folders, files in os.walk(source):
        here = target / Path(root).relative_to(source)
        here.mkdir(parents=True, exist_ok=True)
        for name in files:
            try:
                os.link(Path(root) / name, here / name)
            except OSError:
                shutil.copy2(Path(root) / name, here / name)
            count += 1
    return count


class ProcessTable:
    """One look at this PC's processes; program files are asked once each, when needed."""

    def __init__(self):
        self.entries = {pid: (parent, name) for pid, parent, name in windows_processes()}
        self._paths: dict = {}

    def name(self, pid: int) -> str:
        return self.entries.get(pid, (0, ""))[1]

    def parent(self, pid: int) -> int:
        return self.entries.get(pid, (0, ""))[0]

    def path(self, pid: int) -> str:
        if pid not in self._paths:
            self._paths[pid] = process_image(pid)
        return self._paths[pid]

    def under(self, folder: Path) -> list:
        """[(pid, program name)] whose program file is in the folder: a runner's listener, its worker, the node it runs steps with."""
        prefixes = {str(folder).rstrip("\\/").lower() + os.sep}
        try:
            prefixes.add(str(folder.resolve()).rstrip("\\/").lower() + os.sep)
        except OSError:
            pass
        return [(pid, name) for pid, (_, name) in self.entries.items() if pid and self.path(pid).lower().startswith(tuple(prefixes))]

    def descendants(self, roots: set) -> set:
        found, queue = set(), list(roots)
        while queue:
            pid = queue.pop()
            for child, (parent, _) in self.entries.items():
                if parent == pid and child not in found and child not in roots:
                    found.add(child)
                    queue.append(child)
        return found


class Runners:
    """This PC's GitHub Actions self-hosted runners, one folder each under RUNNERS_ROOT (the runner's own layout: .runner, bin,
    externals, run.cmd, and its .env of variables for the jobs). The API Manager's 러너 page lists them, adds one (registers
    it with its repository), edits its .env, starts, stops and removes them. One logon task (RUNNERS_TASK) starts every runner
    that is on; a runner stopped here stays off until it is started here again.

    A runner runs hidden in a console of its own. Stopping sends that console Ctrl+Break: the runner closes its GitHub session
    and exits. A runner that is killed instead leaves its session on GitHub for about two minutes, and a start within that time
    ends at once ("A session for this runner already exists")."""

    def __init__(self, config: Config, github: "GitHubProxy | None" = None, releases: "Releases | None" = None):
        self.config = config
        self.github = github
        self.releases = releases
        self.root = config.runners_root
        self._lock = threading.Lock()
        self._tasks: tuple = (None, 0.0)

    # Where and what ------------------------------------------------------------------------------------

    def available(self) -> str:
        """Why the runner page cannot work on this PC, or "" when it can."""
        if os.name != "nt":
            return "러너 관리는 Windows에서만 합니다."
        if not self.root:
            return "러너 폴더가 정해지지 않았습니다 (relay.py SETTINGS의 RUNNERS_ROOT)."
        if not self.root.is_dir():
            return f"러너 폴더가 없습니다: {self.root}"
        return ""

    def _require(self) -> None:
        problem = self.available()
        if problem:
            raise RelayError(503, problem)

    @staticmethod
    def _is_runner(path: Path) -> bool:
        return path.is_dir() and bool(RUNNER_ID.match(path.name)) and ((path / ".runner").is_file() or (path / "config.cmd").is_file())

    def folders(self) -> list:
        try:
            return sorted((path for path in self.root.iterdir() if self._is_runner(path)), key=lambda path: path.name.lower())
        except OSError:
            return []

    def folder(self, runner_id) -> Path:
        self._require()
        path = self.root / runner_id if isinstance(runner_id, str) and RUNNER_ID.match(runner_id) else None
        if path is None or not self._is_runner(path):
            raise RelayError(404, f"러너를 찾지 못했습니다: {runner_id}")
        return path

    def _off(self) -> set:
        value = read_json(self.root / RUNNER_STATE)
        off = value.get("off") if isinstance(value, dict) else None
        return {str(item).lower() for item in off if isinstance(item, str)} if isinstance(off, list) else set()

    def _set_on(self, runner_id: str, on: bool) -> None:
        off = sorted({item for item in self._off() if item != runner_id.lower()} | (set() if on else {runner_id.lower()}))
        write_json(self.root / RUNNER_STATE, {"off": off})

    @staticmethod
    def _registration(folder: Path) -> dict:
        info = read_json(folder / ".runner")
        return info if isinstance(info, dict) else {}

    @staticmethod
    def _repository(url: str) -> str:
        parts = [part for part in urllib.parse.urlsplit(url).path.split("/") if part]
        return "/".join(parts[:2]) if len(parts) >= 2 else ""

    def _local(self, folder: Path, table: ProcessTable, off: set) -> dict:
        info = self._registration(folder)
        url = str(info.get("gitHubUrl") or "")
        processes = table.under(folder)
        names = [name.lower() for _, name in processes]
        try:
            env = (folder / ".env").read_text(encoding="utf-8-sig").replace("\r\n", "\n")
        except OSError:
            env = ""
        return {"id": folder.name, "folder": str(folder), "name": str(info.get("agentName") or ""), "registered": bool(info),
                "repository": self._repository(url), "url": url, "on": folder.name.lower() not in off, "running": bool(processes),
                "busy": "runner.worker.exe" in names, "pid": next((pid for pid, name in processes if name.lower() == "runner.listener.exe"), 0),
                "version": runner_version(folder), "env": env, "github": None, "githubError": ""}

    # GitHub ---------------------------------------------------------------------------------------------

    def _get(self, path: str, ttl: int, raw: bool = False) -> tuple:
        if self.releases is None:
            raise RelayError(503, "GitHub에 연결하지 않았습니다.")
        return self.releases._get(path, ttl, raw)

    def _canonical(self, repository) -> str:
        """owner/name as GitHub has it now (a renamed repository answers under its new name)."""
        if not isinstance(repository, str) or not repository.strip():
            raise RelayError(400, "저장소(소유자/이름)가 필요합니다.")
        full = repository.strip()
        if full.lower().startswith(("http://", "https://")):
            full = self._repository(full)
        full = full[:-4] if full.lower().endswith(".git") else full
        status, answer = self._get(Releases._base(full), ttl=300)
        if status == 200 and isinstance(answer, dict) and answer.get("full_name"):
            return str(answer["full_name"])
        if status == 404:
            raise RelayError(404, f"GitHub에 {full} 저장소가 없거나 이 서버의 토큰으로 열 수 없습니다.")
        raise RelayError(502, Releases._problem(status, answer))

    def _github_runners(self, full: str, ttl: int = RUNNER_GITHUB_TTL) -> list:
        status, answer = self._get(f"{Releases._base(full)}/actions/runners?per_page=100", ttl)
        if status != 200 or not isinstance(answer, dict):
            hint = " (토큰에 저장소 관리 권한이 있어야 합니다)" if status in (401, 403, 404) else ""
            raise RelayError(502, Releases._problem(status, answer) + hint)
        return [item for item in answer.get("runners") or [] if isinstance(item, dict)]

    def _github_call(self, method: str, path: str) -> tuple:
        """One GitHub call that changes something (a registration token, a removal), never cached."""
        token = self.github.token() if self.github else ""
        if not token:
            raise RelayError(503, "GitHub 토큰이 없습니다 (RELAY_GITHUB_TOKEN 또는 이 PC의 git 자격 증명).")
        request = urllib.request.Request(self.config.github_api + path, data=b"" if method == "POST" else None, method=method,
                                         headers=self.github._headers("application/vnd.github+json", token))
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read(65536)
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise RelayError(502, f"GitHub에 연결하지 못했습니다: {getattr(error, 'reason', error)}")
        try:
            return status, json.loads(body.decode("utf-8")) if body else None
        except ValueError:
            return status, None

    def _web_url(self, full: str) -> str:
        return f"https://{self.config.github_web_host}/{full}"

    def _add_github(self, entry: dict) -> None:
        if not entry["registered"] or not entry["repository"]:
            return
        try:
            full = self._canonical(entry["repository"])
            entry["repository"], entry["url"] = full, self._web_url(full)
            match = next((item for item in self._github_runners(full) if str(item.get("name") or "").lower() == entry["name"].lower()), None)
            entry["github"] = ({"id": match.get("id"), "status": str(match.get("status") or ""), "busy": bool(match.get("busy")),
                                "labels": [str(label.get("name")) for label in match.get("labels") or [] if isinstance(label, dict)]}
                               if match else {"id": None, "status": "missing", "busy": False, "labels": []})
        except RelayError as error:
            entry["githubError"] = error.message

    # The page --------------------------------------------------------------------------------------------

    def list(self) -> dict:
        problem = self.available()
        answer = {"available": not problem, "message": problem, "root": str(self.root or ""), "task": None, "runners": [], "repositories": []}
        if problem:
            return answer
        table, off = ProcessTable(), self._off()
        runners = [self._local(folder, table, off) for folder in self.folders()]
        if runners and self.releases is not None:
            with ThreadPoolExecutor(max_workers=min(8, len(runners))) as pool:
                list(pool.map(self._add_github, runners))
        answer["runners"] = runners
        answer["task"] = self.task_info()
        answer["repositories"] = [{"repository": full, "name": name} for full, name in self.releases.projects()] if self.releases else []
        return answer

    def template(self, repository) -> dict:
        """What the page fills in for a new runner of a repository: folder, runner name and .env (the variable the repository's
        release workflow reads its build settings from, pointing at <설정 폴더>\\<저장소>.env)."""
        self._require()
        full = self._canonical(repository)
        name = full.split("/", 1)[1]
        folder, number = name, 2
        while (self.root / folder).exists():
            folder, number = f"{name}-{number}", number + 1
        computer = re.sub(r"[^A-Za-z0-9._-]", "-", os.environ.get("COMPUTERNAME") or socket.gethostname() or "PC")
        taken = set()
        try:
            taken = {str(item.get("name") or "").lower() for item in self._github_runners(full, ttl=0)}
        except RelayError:
            pass
        runner, number = f"{computer}-{name}"[:64], 2
        while runner.lower() in taken:
            runner, number = f"{computer}-{name}-{number}"[:64], number + 1
        status, workflow = self._get(f"{Releases._base(full)}/contents/.github/workflows/{RELEASE_WORKFLOW}", ttl=60, raw=True)
        variable = re.search(r"\$env:([A-Z][A-Z0-9_]*_ENV_PATH)\b", workflow) if status == 200 else None
        lines = []
        if self.config.runners_env_dir:
            lines.append(f"{variable.group(1) if variable else 'RELEASE_ENV_PATH'}={self.config.runners_env_dir / (name + '.env')}")
        lines.append("DOTNET_CLI_TELEMETRY_OPTOUT=1")
        return {"repository": full, "folder": folder, "name": runner, "env": "\n".join(lines) + "\n"}

    # Changes ----------------------------------------------------------------------------------------------

    @contextlib.contextmanager
    def _changing(self):
        """One change at a time, also against the logon task's start (another process)."""
        import msvcrt
        with self._lock:
            with open(self.root / ".runners.lock", "a+b") as handle:
                deadline = time.monotonic() + 120
                while True:
                    try:
                        handle.seek(0)
                        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                        break
                    except OSError:
                        if time.monotonic() > deadline:
                            raise RelayError(409, "다른 곳에서 러너를 바꾸는 중입니다. 잠시 뒤에 다시 하세요.")
                        time.sleep(0.5)
                try:
                    yield
                finally:
                    try:
                        handle.seek(0)
                        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                    except OSError:
                        pass

    @staticmethod
    def _env_text(env) -> str:
        if not isinstance(env, str) or len(env.encode("utf-8")) > RUNNER_ENV_MAX:
            raise RelayError(400, ".env 내용이 없거나 너무 깁니다.")
        lines = [line.strip() for line in env.replace("\r\n", "\n").split("\n")]
        for number, line in enumerate(lines, 1):
            if line and not line.startswith("#") and not RUNNER_ENV_LINE.match(line):
                raise RelayError(400, f".env {number}번째 줄은 이름=값 이어야 합니다: {line[:60]}")
        text = "\r\n".join(lines).strip()
        return text + "\r\n" if text else ""

    def _launch(self, folder: Path) -> None:
        """run.cmd, hidden in a console of its own, with this account's current environment, apart from the relay's job when
        Windows allows (the job of the logon task, for one, does not). Its output goes to _diag\\run.log."""
        run = folder / "run.cmd"
        if not run.is_file():
            raise RelayError(409, f"{folder.name}: run.cmd가 없습니다.")
        diag = folder / "_diag"
        diag.mkdir(exist_ok=True)
        output = diag / "run.log"
        try:
            large = output.stat().st_size > 1_048_576
        except OSError:
            large = False
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = 0
        environment = user_environment()
        with open(output, "wb" if large else "ab") as stream:
            stream.write(f"\r\n== {datetime.now():%Y-%m-%d %H:%M:%S} start (API server {VERSION}) ==\r\n".encode("utf-8"))
            stream.flush()
            for extra in (CREATE_BREAKAWAY_FROM_JOB, 0):
                try:
                    subprocess.Popen(["cmd.exe", "/d", "/c", str(run)], cwd=str(folder), env=environment, startupinfo=info,
                                     creationflags=subprocess.CREATE_NEW_CONSOLE | extra, stdin=subprocess.DEVNULL, stdout=stream,
                                     stderr=subprocess.STDOUT, close_fds=True)
                    return
                except OSError as error:
                    if not extra:
                        raise RelayError(500, f"{folder.name}을(를) 시작하지 못했습니다: {error}")

    @staticmethod
    def _exit_reason(folder: Path) -> str:
        try:
            lines = [line.strip() for line in console_text((folder / "_diag" / "run.log").read_bytes()[-8192:]).splitlines() if line.strip()]
        except OSError:
            return ""
        # Only what this start wrote (each start begins with a "== ... start" line).
        lines = lines[max((index for index, line in enumerate(lines) if line.startswith("== ")), default=-1) + 1:]
        if any(text in line.lower() for line in lines for text in RUNNER_SESSION_LEFT):
            return "GitHub에 이전 세션이 남아 있습니다. 2분쯤 뒤에 다시 시작하세요."
        return lines[-1].strip('"')[:200] if lines else ""

    def _await(self, folders: list, seconds: float = 25) -> dict:
        """Waits until each runner's listener has run a few seconds; {folder: why not} for the ones that did not."""
        seen = {folder: 0.0 for folder in folders}
        problems = {}
        deadline = time.monotonic() + seconds
        while seen and time.monotonic() < deadline:
            time.sleep(0.5)
            table = ProcessTable()
            for folder in list(seen):
                listening = any(name.lower() == "runner.listener.exe" for _, name in table.under(folder))
                if listening and not seen[folder]:
                    seen[folder] = time.monotonic()
                elif listening and time.monotonic() - seen[folder] >= 4:
                    del seen[folder]
                elif not listening and seen[folder]:
                    problems[folder] = self._exit_reason(folder) or "시작한 뒤 바로 끝났습니다."
                    del seen[folder]
        for folder, since in seen.items():
            if not since:
                problems[folder] = self._exit_reason(folder) or "시작하지 않았습니다."
        return problems

    def _start(self, folder: Path) -> str:
        if ProcessTable().under(folder):
            return f"{folder.name}은(는) 이미 실행 중입니다."
        if not (folder / ".runner").is_file():
            raise RelayError(409, f"{folder.name}은(는) 등록되지 않은 러너입니다.")
        self._launch(folder)
        problem = self._await([folder]).get(folder)
        if problem:
            raise RelayError(502, f"{folder.name}: {problem}")
        log.info("runner %s started", folder.name)
        return f"{folder.name}을(를) 시작했습니다."

    def _stop(self, folder: Path, force: bool) -> str:
        """Ctrl+Break to the runner's console, then ends what is left (run.cmd's cmd.exe, a job's programs). A running job is
        refused unless force. "" when it was not running."""
        table = ProcessTable()
        mine = table.under(folder)
        if not mine:
            return ""
        if not force and any(name.lower() == "runner.worker.exe" for _, name in mine):
            raise RelayError(409, f"{folder.name}이(가) 작업 중입니다. 멈추면 그 빌드가 취소됩니다.")
        shells = set()
        for pid, _ in mine:
            parent = table.parent(pid)
            while parent and table.name(parent).lower() == "cmd.exe" and parent not in shells:  # run.cmd
                shells.add(parent)
                parent = table.parent(parent)
        listener = next((pid for pid, name in mine if name.lower() == "runner.listener.exe"), mine[0][0])
        send_ctrl_break(listener)
        deadline = time.monotonic() + 20
        while True:
            table = ProcessTable()
            left = table.under(folder)
            if not any(name.lower() == "runner.listener.exe" for _, name in left) or time.monotonic() > deadline:
                break
            time.sleep(0.5)
        killed = any(name.lower() == "runner.listener.exe" for _, name in left)
        targets = {pid for pid, _ in left} | {pid for pid in shells if table.name(pid).lower() == "cmd.exe"}
        for pid in targets | table.descendants(targets):
            kill_process(pid)
        log.info("runner %s stopped%s", folder.name, " (killed)" if killed else "")
        return (f"{folder.name}을(를) 강제로 멈췄습니다. GitHub에 세션이 2분쯤 남아 그동안은 다시 시작되지 않습니다." if killed
                else f"{folder.name}을(를) 멈췄습니다.")

    def start(self, runner_id, force: bool = False) -> dict:
        folder = self.folder(runner_id)
        with self._changing():
            self._set_on(folder.name, True)
            return {"message": self._start(folder)}

    def stop(self, runner_id, force: bool = False) -> dict:
        folder = self.folder(runner_id)
        with self._changing():
            message = self._stop(folder, force)
            self._set_on(folder.name, False)
            return {"message": message or f"{folder.name}을(를) 껐습니다."}

    def restart(self, runner_id, force: bool = False) -> dict:
        folder = self.folder(runner_id)
        with self._changing():
            self._stop(folder, force)
            self._set_on(folder.name, True)
            return {"message": self._start(folder).replace("시작했습니다", "다시 시작했습니다")}

    def save(self, runner_id, env) -> dict:
        """Writes the runner's .env (variables for its jobs) and restarts it when it runs idle, so the jobs see them."""
        folder = self.folder(runner_id)
        text = self._env_text(env)
        with self._changing():
            atomic_write(folder / ".env", text.encode("utf-8"))
            mine = ProcessTable().under(folder)
            if not mine:
                return {"message": "저장했습니다."}
            if any(name.lower() == "runner.worker.exe" for _, name in mine):
                return {"message": "저장했습니다. 작업이 끝난 뒤 다시 시작하면 적용됩니다."}
            self._stop(folder, False)
            self._start(folder)
            return {"message": "저장하고 다시 시작했습니다."}

    def _template_runner(self) -> Path | None:
        """A runner whose files a new one is made from (hard links: no extra space): the newest version."""
        found = [folder for folder in self.folders()
                 if (folder / "bin" / "Runner.Listener.exe").is_file() and (folder / "externals").is_dir() and (folder / "config.cmd").is_file()]
        return max(found, key=lambda folder: tuple(int(part) for part in (runner_version(folder) or "0.0.0").split(".")), default=None)

    def _prepare(self, target: Path) -> None:
        package = self.config.runner_package
        if package:
            import zipfile
            if not package.is_file():
                raise RelayError(409, f"러너 파일이 없습니다: {package} (SETTINGS의 RUNNER_PACKAGE)")
            with zipfile.ZipFile(package) as archive:
                for member in archive.infolist():
                    relative = Path(member.filename)
                    if relative.is_absolute() or ".." in relative.parts or not relative.parts:
                        continue
                    destination = target / relative
                    if member.is_dir():
                        destination.mkdir(parents=True, exist_ok=True)
                        continue
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(member) as data, open(destination, "wb") as out:
                        shutil.copyfileobj(data, out)
            return
        template = self._template_runner()
        if template is None:
            raise RelayError(409, "새 러너를 만들 러너 파일이 없습니다. SETTINGS의 RUNNER_PACKAGE에 actions-runner-win-x64-*.zip 경로를 쓰세요.")
        target.mkdir(parents=True)
        for item in template.iterdir():
            if item.is_file() and not item.name.startswith(".") and item.name not in RUNNER_SKIP and item.suffix.lower() != ".log":
                shutil.copy2(item, target / item.name)
        for name in ("bin", "externals"):
            link_tree((template / name).resolve(), target / name)

    def _configure(self, target: Path, url: str, token: str, name: str) -> None:
        command = ["cmd.exe", "/d", "/c", str(target / "config.cmd"), "--unattended", "--url", url, "--token", token, "--name", name, "--work", "_work"]
        try:
            result = subprocess.run(command, cwd=str(target), env=user_environment(), stdin=subprocess.DEVNULL, capture_output=True, timeout=300,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except (OSError, subprocess.SubprocessError) as error:
            raise RelayError(502, f"러너를 등록하지 못했습니다: {error}")
        if result.returncode != 0 or not (target / ".runner").is_file():
            lines = [line.strip() for line in console_text(result.stdout + result.stderr).splitlines()
                     if line.strip() and token not in line and not set(line.strip()) <= set("-|_ ")]
            raise RelayError(502, "러너를 등록하지 못했습니다: " + (" / ".join(lines[-3:]) or f"종료 코드 {result.returncode}"))

    def add(self, repository, folder_name, name, env) -> dict:
        """A new runner for a repository: its files, its registration on GitHub (a token the relay asks for), its .env; then on."""
        self._require()
        full = self._canonical(repository)
        repo = full.split("/", 1)[1]
        folder_name = (folder_name or repo).strip() if isinstance(folder_name, str) or folder_name is None else ""
        name = (name or "").strip() if isinstance(name, str) or name is None else ""
        if not RUNNER_ID.match(folder_name) or folder_name.lower() == "env":
            raise RelayError(400, "폴더 이름은 영문·숫자·.-_ 로 64자까지 씁니다.")
        if not RUNNER_ID.match(name):
            raise RelayError(400, "러너 이름은 영문·숫자·.-_ 로 64자까지 씁니다.")
        text = self._env_text(env if env is not None else "")
        target = self.root / folder_name
        if target.exists():
            raise RelayError(409, f"{target} 폴더가 이미 있습니다.")
        if any(str(item.get("name") or "").lower() == name.lower() for item in self._github_runners(full, ttl=0)):
            raise RelayError(409, f"{full}에 이미 {name} 러너가 있습니다. 다른 이름을 쓰세요.")
        with self._changing():
            try:
                self._prepare(target)
                status, answer = self._github_call("POST", f"{Releases._base(full)}/actions/runners/registration-token")
                token = answer.get("token") if isinstance(answer, dict) else None
                if status != 201 or not token:
                    raise RelayError(502, "GitHub이 러너 등록 토큰을 주지 않았습니다. " + Releases._problem(status, answer))
                self._configure(target, self._web_url(full), str(token), name)
                atomic_write(target / ".env", text.encode("utf-8"))
            except BaseException:
                if target.exists():
                    remove_tree(target)
                raise
            log.info("runner %s registered for %s as %s", folder_name, full, name)
            self._set_on(folder_name, True)
            try:
                self._start(target)
                message = f"{name}을(를) 등록하고 시작했습니다."
            except RelayError as error:
                message = f"{name}을(를) 등록했지만 시작하지 못했습니다: {error.message}"
        return {"id": folder_name, "message": message}

    def remove(self, runner_id, force: bool = False) -> dict:
        """Stops the runner, removes it from its repository on GitHub and deletes its folder."""
        folder = self.folder(runner_id)
        info = self._registration(folder)
        with self._changing():
            self._stop(folder, force)
            repository, name = self._repository(str(info.get("gitHubUrl") or "")), str(info.get("agentName") or "")
            if repository and name:
                full = self._canonical(repository)
                match = next((item for item in self._github_runners(full, ttl=0) if str(item.get("name") or "").lower() == name.lower()), None)
                if match and match.get("id") is not None:
                    status, answer = self._github_call("DELETE", f"{Releases._base(full)}/actions/runners/{int(match['id'])}")
                    if status not in (204, 404):
                        raise RelayError(502, "GitHub에서 러너를 지우지 못했습니다. " + Releases._problem(status, answer))
            remove_tree(folder)
            self._set_on(folder.name, True)
        log.info("runner %s removed", folder.name)
        return {"message": f"{folder.name}을(를) 지웠습니다."}

    def start_on(self) -> list:
        """Starts every registered runner that is on and not running (the logon task): all at once, then waits for them."""
        with self._changing():
            table, off = ProcessTable(), self._off()
            started = [folder for folder in self.folders()
                       if (folder / ".runner").is_file() and folder.name.lower() not in off and not table.under(folder)]
            lines = []
            for folder in list(started):
                try:
                    self._launch(folder)
                except RelayError as error:
                    lines.append(error.message)
                    started.remove(folder)
            problems = self._await(started) if started else {}
            lines += [f"{folder.name}: {problems[folder]}" if folder in problems else f"{folder.name} 시작" for folder in started]
            return lines

    # The logon task --------------------------------------------------------------------------------------------

    def _task_command(self) -> tuple:
        python = Path(sys.executable).with_name("pythonw.exe")
        command = str(python if python.is_file() else Path(sys.executable))
        arguments = f'"{Path(__file__).resolve()}" --start-runners' + "".join(f' --set "{item}"' for item in self.config.set_args)
        return command, arguments

    def task_info(self, fresh: bool = False) -> dict:
        names, until = self._tasks
        if fresh or names is None or time.monotonic() > until:
            names = scheduled_tasks()
            self._tasks = (names, time.monotonic() + 60)
        exists = f"\\{RUNNERS_TASK}".lower() in (name.lower() for name in names)
        current = False
        if exists:
            xml = task_definition(RUNNERS_TASK)
            command, arguments = self._task_command()
            current = xml_value(xml, "Command").lower() == command.lower() and xml_value(xml, "Arguments").lower() == arguments.lower()
        legacy = [name[1:] for name in names if name.count("\\") == 1 and LEGACY_RUNNER_TASK.match(name[1:])]
        return {"name": RUNNERS_TASK, "exists": exists, "current": current, "legacy": legacy}

    def install_task(self) -> dict:
        """Puts the one logon task in place (pointing at this relay.py) and removes the per-runner tasks of earlier setups (their
        XML kept in RUNNERS_ROOT\\.backup\\tasks). Deleting a task leaves what it started running; runners that are on but not
        running are started."""
        self._require()
        info = self.task_info(fresh=True)
        with self._changing():
            backups = self.root / ".backup" / "tasks"
            for name in info["legacy"]:
                xml = task_definition(name)
                if xml:
                    backups.mkdir(parents=True, exist_ok=True)
                    (backups / (re.sub(r"[^A-Za-z0-9 ._-]", "_", name) + ".xml")).write_text(xml, encoding="utf-16")
                if run_hidden(["schtasks", "/Delete", "/TN", name, "/F"], 60) != 0:
                    raise RelayError(500, f"예전 작업 {name}을(를) 지우지 못했습니다.")
                log.info("legacy runner task %s removed (backup in %s)", name, backups)
            command, arguments = self._task_command()
            user = os.environ.get("USERNAME") or os.environ.get("USER") or ""
            user = f"{os.environ['USERDOMAIN']}\\{user}" if os.environ.get("USERDOMAIN") else user
            problem = register_task(RUNNERS_TASK, runners_task_xml(command, arguments, str(SCRIPT_DIR), user))
            if problem:
                raise RelayError(500, "로그온 작업을 만들지 못했습니다: " + problem)
            log.info("logon task %s set: %s %s", RUNNERS_TASK, command, arguments)
        lines = self.start_on()
        self._tasks = (None, 0.0)
        removed = f" · 예전 작업 {len(info['legacy'])}개 삭제" if info["legacy"] else ""
        started = f" · {len(lines)}개 시작" if lines else ""
        return {"message": f"로그온 작업 {RUNNERS_TASK} 하나로 러너를 시작합니다{removed}{started}.", "details": lines}


def start_runners_at_logon(config: Config) -> int:
    """--start-runners (the logon task RUNNERS_TASK): starts the runners that are on, then ends."""
    setup_logging(config, True)
    runners = Runners(config)
    problem = runners.available()
    if problem:
        log.warning("runners not started: %s", problem)
        return 1
    try:
        for line in runners.start_on():
            log.info("logon task: %s", line)
    except RelayError as error:
        log.warning("logon task: %s", error.message)
        return 1
    return 0


# ── EQ Status 설비 상태 ─────────────────────────────────────────────────────────────────────────────

class MySqlError(Exception):
    pass


class MySqlClient:
    """Just enough MySQL for one query, with the standard library: TLS when the server offers it, caching_sha2_password
    (MySQL 8 default; full authentication needs that TLS) or mysql_native_password, text result sets."""

    CLIENT_FLAGS = 0x1 | 0x200 | 0x2000 | 0x8000 | 0x80000  # long password, protocol 41, transactions, secure connection, plugin auth
    CLIENT_CONNECT_WITH_DB = 0x8
    CLIENT_SSL = 0x800

    def __init__(self, host: str, port: int, user: str, password: str, database: str, timeout: float = 8.0):
        self.seq = 0
        self.sock = socket.create_connection((host, int(port)), timeout=timeout)
        try:
            self._login(user, password or "", database)
        except Exception:
            self.close()
            raise

    def _exact(self, size: int) -> bytes:
        data = b""
        while len(data) < size:
            chunk = self.sock.recv(size - len(data))
            if not chunk:
                raise MySqlError("DB 연결이 끊겼습니다.")
            data += chunk
        return data

    def _read(self) -> bytes:
        payload = b""
        while True:
            header = self._exact(4)
            size = header[0] | header[1] << 8 | header[2] << 16
            self.seq = (header[3] + 1) & 0xFF
            payload += self._exact(size)
            if size < 0xFFFFFF:
                return payload

    def _write(self, payload: bytes) -> None:
        self.sock.sendall(struct.pack("<I", len(payload))[:3] + bytes([self.seq]) + payload)
        self.seq = (self.seq + 1) & 0xFF

    @staticmethod
    def _failure(packet: bytes) -> MySqlError:
        code = struct.unpack("<H", packet[1:3])[0]
        message = packet[9:] if packet[3:4] == b"#" else packet[3:]
        return MySqlError(f"MySQL {code}: {message.decode('utf-8', 'replace')}")

    @staticmethod
    def _scramble(plugin: str, password: str, nonce: bytes) -> bytes:
        if not password:
            return b""
        if plugin == "caching_sha2_password":
            first = hashlib.sha256(password.encode()).digest()
            second = hashlib.sha256(hashlib.sha256(first).digest() + nonce).digest()
        else:
            first = hashlib.sha1(password.encode()).digest()
            second = hashlib.sha1(nonce + hashlib.sha1(first).digest()).digest()
        return bytes(a ^ b for a, b in zip(first, second))

    def _login(self, user: str, password: str, database: str) -> None:
        packet = self._read()
        if packet[0] == 0xFF:
            raise self._failure(packet)
        pos = packet.index(b"\0", 1) + 1 + 4
        nonce = packet[pos:pos + 8]
        pos += 9
        server_flags = struct.unpack("<H", packet[pos:pos + 2])[0]
        pos += 5
        server_flags |= struct.unpack("<H", packet[pos:pos + 2])[0] << 16
        pos += 2
        auth_length = packet[pos]
        pos += 11
        rest = max(13, auth_length - 8)
        nonce += packet[pos:pos + rest - 1]
        plugin = packet[pos + rest:].split(b"\0")[0].decode() or "mysql_native_password"
        flags = self.CLIENT_FLAGS | (self.CLIENT_CONNECT_WITH_DB if database else 0)
        secure = False
        if server_flags & self.CLIENT_SSL:
            flags |= self.CLIENT_SSL
            self._write(struct.pack("<IIB", flags, 1 << 24, 45) + b"\0" * 23)
            # The DB's certificate is usually its own self-signed one: TLS here keeps the password off the wire.
            context = ssl.create_default_context()
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            self.sock = context.wrap_socket(self.sock)
            secure = True
        auth = self._scramble(plugin, password, nonce)
        body = struct.pack("<IIB", flags, 1 << 24, 45) + b"\0" * 23 + user.encode() + b"\0" + bytes([len(auth)]) + auth
        if database:
            body += database.encode() + b"\0"
        self._write(body + plugin.encode() + b"\0")
        while True:
            packet = self._read()
            if packet[0] == 0x00:
                return
            if packet[0] == 0xFF:
                raise self._failure(packet)
            if packet[0] == 0xFE:  # the server asks for another method
                name, _, data = packet[1:].partition(b"\0")
                plugin = name.decode()
                if plugin == "mysql_clear_password" and secure:
                    self._write(password.encode() + b"\0")
                elif plugin in ("caching_sha2_password", "mysql_native_password"):
                    self._write(self._scramble(plugin, password, data.rstrip(b"\0")))
                else:
                    raise MySqlError(f"지원하지 않는 DB 로그인 방식입니다: {plugin}")
                continue
            if packet[0] == 0x01 and packet[1:2] == b"\x03":
                continue  # caching_sha2 fast path: the OK packet follows
            if packet[0] == 0x01 and packet[1:2] == b"\x04":
                if not secure:
                    raise MySqlError("이 DB 계정은 TLS 연결에서만 로그인할 수 있습니다.")
                self._write(password.encode() + b"\0")
                continue
            raise MySqlError("DB 로그인 응답을 이해하지 못했습니다.")

    @staticmethod
    def _length(data: bytes, pos: int) -> tuple:
        first = data[pos]
        if first < 0xFB:
            return first, pos + 1
        if first == 0xFB:
            return None, pos + 1
        if first == 0xFC:
            return struct.unpack("<H", data[pos + 1:pos + 3])[0], pos + 3
        if first == 0xFD:
            return int.from_bytes(data[pos + 1:pos + 4], "little"), pos + 4
        return struct.unpack("<Q", data[pos + 1:pos + 9])[0], pos + 9

    def query(self, sql: str) -> list:
        """The rows of a SELECT, each a list of str or None."""
        self.seq = 0
        self._write(b"\x03" + sql.encode("utf-8"))
        packet = self._read()
        if packet[0] == 0xFF:
            raise self._failure(packet)
        if packet[0] == 0x00:
            return []
        count, _ = self._length(packet, 0)
        for _ in range(count):
            self._read()  # column definitions
        self._read()  # their end
        rows = []
        while True:
            packet = self._read()
            if packet[0] == 0xFE and len(packet) < 9:
                return rows
            if packet[0] == 0xFF:
                raise self._failure(packet)
            pos, row = 0, []
            for _ in range(count):
                size, pos = self._length(packet, pos)
                row.append(None if size is None else packet[pos:pos + size].decode("utf-8", "replace"))
                pos += size or 0
            rows.append(row)

    def close(self) -> None:
        try:
            self.seq = 0
            self._write(b"\x01")
        except Exception:
            pass
        try:
            self.sock.close()
        except Exception:
            pass


class EquipmentStatus:
    """EQ Status's equipment table: read from the DB at most every EQUIPMENT_REFRESH_SECONDS and kept, so any number of
    apps polling costs the DB one query per interval. A failed read keeps the last table for a while, then answers 503."""

    def __init__(self, config: Config):
        self.config = config
        self.lock = threading.Lock()
        self.current = None
        self.tried = 0.0
        self.error = ""
        self.connect = MySqlClient  # tests replace it

    def configured(self) -> bool:
        return bool(self.config.equipment_db_host)

    def snapshot(self) -> dict:
        if not self.configured():
            raise RelayError(404, "설비 상태 DB가 설정되지 않았습니다(API 서버 relay.py SETTINGS의 EQUIPMENT_DB_*).")
        interval = self.config.equipment_refresh_seconds
        with self.lock:
            if time.monotonic() - self.tried >= interval or self.current is None and not self.tried:
                self.tried = time.monotonic()
                try:
                    items = self._read()
                    body = json.dumps(items, ensure_ascii=False, sort_keys=True).encode("utf-8")
                    self.current = {"items": items, "etag": '"' + hashlib.sha256(body).hexdigest()[:32] + '"', "at": now()}
                    self.error = ""
                except Exception as error:
                    if not self.error:
                        log.warning("equipment status read failed: %s", error)
                    self.error = str(error) or error.__class__.__name__
            stale = self.current is None or self.error and (now() - self.current["at"]).total_seconds() > max(60, interval * 6)
            if stale:
                raise RelayError(503, "설비 상태 DB를 읽지 못했습니다: " + (self.error or "아직 읽은 적이 없습니다."))
            return self.current

    def _read(self) -> list:
        config = self.config
        if not re.fullmatch(r"[A-Za-z0-9_$]+(\.[A-Za-z0-9_$]+)?", config.equipment_table):
            raise MySqlError("EQUIPMENT_STATUS_TABLE은 표 이름(영문·숫자·_)이어야 합니다.")
        table = ".".join(f"`{part}`" for part in config.equipment_table.split("."))
        client = self.connect(config.equipment_db_host, config.equipment_db_port, config.equipment_db_user,
                              config.equipment_db_password, config.equipment_db_name)
        try:
            rows = client.query("SELECT `line`, `sdwt`, `prc`, `eqpid`, `status`, `Hr`, `Down_time`, `comment` FROM " + table +
                                " WHERE LOWER(COALESCE(`view`, '')) <> 'hide' ORDER BY `line`, `prc`, `eqpid`")
        finally:
            client.close()
        items = []
        for line, sdwt, process, equipment, status, hours, down_time, comment in rows:
            try:
                elapsed = float(hours or 0)
            except ValueError:
                elapsed = 0.0
            items.append({"line": line or "", "sdwt": sdwt or "", "process": process or "", "equipmentId": equipment or "",
                          "status": (status or "").strip().upper(), "hours": elapsed, "downTime": down_time or "", "comment": comment or ""})
        return items

    def state(self) -> str:
        if not self.configured():
            return "설비 상태: 꺼짐"
        if self.current is None:
            return "설비 상태: " + (f"DB 읽기 실패 — {self.error}" if self.error else "아직 요청 없음")
        text = f"설비 상태: {len(self.current['items'])}대 · {local_text(self.current['at'])} 읽음"
        return text + (f" · 마지막 읽기 실패 — {self.error}" if self.error else "")


# ── 서버 ─────────────────────────────────────────────────────────────────────────────────────────────

class RelayServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    # On Windows SO_REUSEADDR would let a second relay bind the same port; elsewhere it only skips TIME_WAIT.
    allow_reuse_address = os.name != "nt"

    def __init__(self, config: Config, handler):
        self.config = config
        self.reports = Reports(config.reports_dir)
        self.skills = Skills(config.skills_dir)
        self.plugins = Plugins(config.plugins_dir)
        self.announcements = Announcements(config.announcements_dir, config.editors)
        self.launcher = LauncherStore(config.launcher_dir, config.github_hosts)
        self.github = GitHubProxy(config, self.launcher)
        self.releases = Releases(config, self.launcher, self.github)
        self.equipment = EquipmentStatus(config)
        self.runners = Runners(config, self.github, self.releases)
        self.limiter = RateLimiter()
        self.notifier = Notifier(config.webhook_url)
        self.clients = Clients()
        self.started = now()
        super().__init__((config.bind, config.port), handler)


class Handler(http.server.BaseHTTPRequestHandler):
    server: RelayServer
    server_version = "TerminalNotesRelay/" + VERSION
    sys_version = ""
    timeout = 30

    # Routing ------------------------------------------------------------------------------------------

    def do_GET(self) -> None:
        self._dispatch("GET")

    def do_POST(self) -> None:
        self._dispatch("POST")

    def do_PUT(self) -> None:
        self._dispatch("PUT")

    def do_DELETE(self) -> None:
        self._dispatch("DELETE")

    def _dispatch(self, method: str) -> None:
        started = time.monotonic()
        self._status = 0
        self._body_read = False
        parsed = urllib.parse.urlsplit(self.path)
        self.query = urllib.parse.parse_qs(parsed.query)
        parts = [urllib.parse.unquote(part) for part in parsed.path.split("/") if part]
        try:
            self._route(method, parts)
        except _Handled:
            pass
        except RelayError as error:
            self._error(error.status, error.message)
        except Exception:
            log.exception("request failed: %s %s", method, parsed.path)
            self._error(500, "서버 오류가 났습니다. 중계 서버 로그를 확인하세요.")
        finally:
            # Every app asks for the announcement board each minute: those routine answers would bury the log, so the
            # status page lists the PCs instead and the log notes each PC's first contact.
            routine = method == "GET" and parsed.path in ("/api/announcements", "/health") and self._status in (200, 304)
            if not routine:
                log.info("%s %s %s %s %.0fms", self.client_address[0], method, parsed.path, self._status, (time.monotonic() - started) * 1000)

    def _route(self, method: str, parts: list) -> None:
        if parts == ["health"] and method == "GET":
            return self._text(200, "ok")
        if parts[:2] == ["api", "v3"]:
            # The GitHub relay: read-only, no app key (the launcher and every app use it), a generous limit per address.
            if method != "GET":
                raise RelayError(405, "GitHub 중계는 읽기(GET)만 합니다.")
            self._limit("github", 3000, 3600)
            self.server.clients.touch(self.client_address[0], self.headers.get("User-Agent", ""), "github")
            return self.server.github.request(self, parts[2:])
        if parts[:1] == ["launcher"]:
            return self._launcher(method, parts[1:])
        if parts == ["api", "v1", "equipment-status"]:
            return self._equipment(method)
        if parts[:1] == ["api"]:
            try:
                self._require_key()
            except RelayError:
                # Listed too: an app built with another .env's key reaches the relay but is refused.
                self.server.clients.touch(self.client_address[0], self.headers.get("User-Agent", ""), "badkey")
                raise
            return self._api(method, parts[1:])
        self._require_admin()
        return self._admin(method, parts)

    def _equipment(self, method: str) -> None:
        """EQ Status: the equipment table as {items, updatedAt}, with its own key (EQUIPMENT_API_KEY) instead of the app key."""
        server = self.server
        address, agent = self.client_address[0], self.headers.get("User-Agent", "")
        if method != "GET":
            raise RelayError(405, "설비 상태는 읽기(GET)만 합니다.")
        key = server.config.equipment_api_key
        if key and not secret_equals(self.headers.get("X-EQStatus-API-Key", ""), key):
            server.clients.touch(address, agent, "badkey")
            raise RelayError(401, "키가 맞지 않습니다.")
        self._limit("equipment", 6000, 3600)
        server.clients.touch(address, agent, "equipment")
        snapshot = server.equipment.snapshot()
        if self.headers.get("If-None-Match") == snapshot["etag"]:
            self._status = 304
            self.send_response(304)
            self.send_header("ETag", snapshot["etag"])
            self.end_headers()
            return None
        return self._json(200, {"items": snapshot["items"], "updatedAt": iso(snapshot["at"])}, {"ETag": snapshot["etag"]})

    def _api(self, method: str, parts: list) -> None:
        server = self.server
        address = self.client_address[0]
        server.clients.touch(address, self.headers.get("User-Agent", ""), parts[0] if parts else "")
        if parts == ["reports"] and method == "POST":
            self._limit("report", server.config.uploads_per_hour, 3600)
            content_type = self.headers.get("Content-Type", "")
            if "multipart/form-data" not in content_type:
                raise RelayError(400, "multipart/form-data가 필요합니다.")
            fields, files = parse_multipart(content_type, self._body())
            report = fields.get("report", "")
            if not report.strip() or len(report) > REPORT_MAX_CHARS:
                raise RelayError(400, "보고서가 비었거나 너무 깁니다.")
            logs = files.get("logs", (None, b""))[1] or None
            meta = server.reports.add(report, logs, self.client_address[0])
            log.info("report %s received (%s)", meta["Id"], meta["AppVersion"])
            server.notifier.send(f"[Terminal Notes] 새 문제 보고 · {meta['AppVersion'] or '버전 미상'} · {meta['Summary']}\n"
                                 f"{server.config.public_url}/reports/{meta['Id']}")
            return self._json(200, {"id": meta["Id"]})
        # The account the app runs as: private skills are only for their author.
        viewer = viewer_name(self.headers.get("X-TerminalNotes-User"))
        if parts == ["skills"] and method == "GET":
            return self._json(200, server.skills.all(viewer))
        if parts == ["skills", "source"] and method == "POST":
            # A GitHub link's instructions, for the share window to read (title, frontmatter) before sharing it.
            self._limit("source", 120, 3600)
            upload = self._json_body()
            link = upload.get("source") if isinstance(upload, dict) else None
            return self._json(200, {"content": fetch_github(link if isinstance(link, str) else "", server.config.github_token)})
        if len(parts) == 2 and parts[0] == "skills" and method == "GET":
            # The latest version; the app before 1.0.518 applies skills through this address.
            return self._json(200, server.skills.version(parts[1], None, count_download=True, viewer=viewer))
        if parts == ["skills", "withdrawn"] and method == "POST":
            upload = self._json_body()
            names = upload.get("names") if isinstance(upload, dict) else None
            if not isinstance(names, list) or len(names) > 500:
                raise RelayError(400, "형식이 올바르지 않습니다.")
            return self._json(200, {"withdrawn": server.skills.withdrawn(names, viewer)})
        if len(parts) == 3 and parts[0] == "skills" and parts[2] == "private" and method == "PUT":
            upload = self._json_body()
            private = upload.get("private") if isinstance(upload, dict) else None
            if not isinstance(private, bool):
                raise RelayError(400, "형식이 올바르지 않습니다.")
            server.skills.set_private(parts[1], private, viewer)
            log.info("skill %s made %s by %s", parts[1], "private" if private else "public", viewer or "?")
            return self._json(200, {"name": parts[1], "private": private})
        if len(parts) == 2 and parts[0] == "skills" and method == "DELETE":
            # Its author or a store admin.
            server.skills._existing(parts[1], viewer)
            if not server.skills.may_manage(server.skills.read(parts[1]), viewer, [user.lower() for user in server.config.skill_admins]):
                raise RelayError(403, "작성자나 스킬 관리 계정만 삭제할 수 있습니다.")
            server.skills.delete(parts[1])
            log.info("skill %s deleted by %s", parts[1], viewer or "?")
            return self._json(200, {"deleted": parts[1]})
        if len(parts) == 3 and parts[0] == "skills" and parts[2] == "versions" and method == "GET":
            return self._json(200, server.skills.versions(parts[1], viewer))
        if len(parts) == 4 and parts[0] == "skills" and parts[2] == "versions" and method == "GET":
            if not VERSION_NUMBER.match(parts[3]):
                raise RelayError(404, "그 버전을 찾지 못했습니다.")
            # ?apply=1 counts a download; looking at a version does not.
            return self._json(200, server.skills.version(parts[1], int(parts[3]), count_download=self.query.get("apply") == ["1"], viewer=viewer))
        if parts == ["skills"] and method == "POST":
            self._limit("skill", server.config.uploads_per_hour, 3600)
            result = server.skills.publish(self._json_body(), viewer, [user.lower() for user in server.config.skill_admins])
            if result["changed"]:
                log.info("skill %s v%s shared by %s", result["name"], result["version"], result["author"])
                # A private skill stays its author's: nothing about it goes out.
                if not (server.skills.read(result["name"]) or {}).get("private"):
                    kind = f"새 버전 v{result['version']}" if result["updated"] else "공유"
                    server.notifier.send(f"[Terminal Notes] 스킬 {kind} · {result['title']} · {result['author']}\n"
                                         f"{server.config.public_url}/skills/{result['name']}")
            return self._json(200, {"name": result["name"], "sha256": result["sha256"], "version": result["version"], "changed": result["changed"]})
        masters = [user.lower() for user in server.config.skill_admins]
        if parts == ["plugins", "source"] and method == "POST":
            # A plugin's GitHub (Enterprise) file, for the share window to read before sharing it.
            self._limit("source", 120, 3600)
            upload = self._json_body()
            link = upload.get("source") if isinstance(upload, dict) else ""
            link = link if isinstance(link, str) else ""
            content = fetch_github(link, server.config.github_token, plugin=True)
            return self._json(200, {"content": content, "extension": Path(urllib.parse.urlsplit(link.strip()).path).suffix.lower()})
        if parts == ["plugins"] and method == "GET":
            return self._json(200, server.plugins.all())
        if len(parts) == 2 and parts[0] == "plugins" and method == "GET":
            # ?install=1 counts an install; looking at the code does not.
            return self._json(200, server.plugins.get(parts[1], count_download=self.query.get("install") == ["1"]))
        if parts == ["plugins"] and method == "POST":
            self._limit("plugin", server.config.uploads_per_hour, 3600)
            result = server.plugins.publish(self._json_body(), viewer, masters)
            if result["changed"]:
                log.info("plugin %s v%s shared by %s", result["name"], result["version"], result["author"])
                server.notifier.send(f"[Terminal Notes] 플러그인 {'새 버전 v' + str(result['version']) if result['updated'] else '공유'} · "
                                     f"{result['title']} · {result['author']}")
            return self._json(200, {"name": result["name"], "version": result["version"], "changed": result["changed"]})
        if len(parts) == 2 and parts[0] == "plugins" and method == "DELETE":
            plugin = server.plugins.read(parts[1])
            if not plugin:
                raise RelayError(404, "플러그인을 찾지 못했습니다.")
            if not (viewer_name(plugin["author"]) == viewer or not masters or viewer in masters):
                raise RelayError(403, "작성자나 마스터 계정만 삭제할 수 있습니다.")
            server.plugins.delete(parts[1])
            log.info("plugin %s deleted by %s", parts[1], viewer or "?")
            return self._json(200, {"deleted": parts[1]})
        if parts == ["announcements"] and method == "GET":
            board = server.announcements.read()
            etag = '"' + board["revision"] + '"'
            server.clients.received(address, board["revision"])
            # How many PCs have this board, so an editor sees whether other PCs actually receive it.
            having = {"X-TerminalNotes-Seen-By": str(server.clients.having(board["revision"]))}
            if self.headers.get("If-None-Match") == etag:
                self._status = 304
                self.send_response(304)
                self.send_header("ETag", etag)
                self.send_header("X-TerminalNotes-Seen-By", having["X-TerminalNotes-Seen-By"])
                self.end_headers()
                return None
            return self._json(200, board, extra={"ETag": etag, **having})
        if parts == ["announcements"] and method == "PUT":
            self._limit("announce", 30, 3600)
            upload = self._json_body()
            if not isinstance(upload, dict):
                raise RelayError(400, "형식이 올바르지 않습니다.")
            board = server.announcements.save(upload.get("text"), upload.get("expectedRevision"), upload.get("expectedExists"),
                                              one_line(upload.get("author")))
            server.clients.received(address, board["revision"])
            server.notifier.send(f"[Terminal Notes] 공지 변경 · {one_line(upload.get('author'))}\n{server.config.public_url}/announcements")
            return self._json(200, board, extra={"X-TerminalNotes-Seen-By": str(server.clients.having(board["revision"]))})
        raise RelayError(404, "찾을 수 없는 주소입니다.")

    def _launcher(self, method: str, parts: list) -> None:
        """i-luncher: anyone who reaches the server reads the app list and notices; saving needs the admin password."""
        store = self.server.launcher
        self.server.clients.touch(self.client_address[0], self.headers.get("User-Agent", ""), "launcher")
        if parts == ["catalog"] and method == "GET":
            catalog = store.catalog()
            etag = f'"{catalog["revision"]}"'
            if self.headers.get("If-None-Match") == etag:
                self._status = 304
                self.send_response(304)
                self.send_header("ETag", etag)
                self.end_headers()
                return
            return self._json(200, catalog, {"ETag": etag})
        if parts == ["notices"] and method == "GET":
            return self._json(200, store.notices())
        if len(parts) == 2 and parts[0] == "media" and method == "GET":
            # The app pages' screenshots: open like the app list, named by content (never changes).
            path = store.media_file(parts[1])
            return self._send(200, path.read_bytes(), LAUNCHER_MEDIA_TYPES[path.suffix[1:]], {"ETag": f'"{path.stem}"'})
        if parts == ["media"] and method == "POST":
            self._require_admin()
            name = store.add_media(self._body())
            log.info("launcher image %s uploaded from %s", name, self.client_address[0])
            return self._json(201, {"name": name})
        if parts in (["catalog"], ["notices"]) and method == "PUT":
            self._require_admin()
            body = self._json_body()
            if not isinstance(body, dict):
                raise RelayError(400, "JSON 객체가 필요합니다.")
            revision = str(body.get("revision") or "")
            if parts == ["catalog"]:
                saved = store.save_catalog(body.get("apps"), revision)
                log.info("launcher catalog saved from %s: %d apps", self.client_address[0], len(saved["apps"]))
                return self._json(200, saved)
            return self._json(200, store.save_notices(body.get("text"), revision))
        raise RelayError(404 if parts not in (["catalog"], ["notices"], ["media"]) else 405, "찾을 수 없는 주소입니다.")

    def public_base(self) -> str:
        """How the client reached this server (scheme and Host): GitHub relay answers point back the same way."""
        host = (self.headers.get("Host") or "").strip()
        if not re.match(r"^[A-Za-z0-9.\-\[\]:]{1,255}$", host):
            return self.server.config.public_url
        return ("https" if self.server.config.tls_cert else "http") + "://" + host

    def _admin(self, method: str, parts: list) -> None:
        server = self.server
        if parts[:2] == ["admin", "api"]:
            return self._admin_api(method, parts[2:])
        if method == "POST":
            self._require_same_origin()
            form = self._form()
            if parts == ["apps"]:
                if form.get("kind") == "notices":
                    server.launcher.save_notices(form.get("text", ""), form.get("revision", ""))
                else:
                    try:
                        value = json.loads(form.get("json", ""))
                    except ValueError:
                        raise RelayError(400, "앱 목록 JSON이 올바르지 않습니다.")
                    server.launcher.save_catalog(value.get("apps") if isinstance(value, dict) else value, form.get("revision", ""))
                return self._redirect("/apps")
            if len(parts) == 3 and parts[0] == "reports" and parts[2] == "update":
                server.reports.update(parts[1], form.get("status"), form.get("note"))
                return self._redirect(f"/reports/{parts[1]}")
            if len(parts) == 3 and parts[0] == "reports" and parts[2] == "delete":
                server.reports.delete(parts[1])
                return self._redirect("/reports")
            if len(parts) == 3 and parts[0] == "skills" and parts[2] == "delete":
                server.skills.delete(parts[1])
                return self._redirect("/skills")
            if len(parts) == 3 and parts[0] == "skills" and parts[2] == "restore":
                version = form.get("version", "")
                if not VERSION_NUMBER.match(version):
                    raise RelayError(400, "버전이 올바르지 않습니다.")
                server.skills.restore(parts[1], int(version))
                return self._redirect(f"/skills/{parts[1]}")
            if parts == ["announcements"]:
                board = server.announcements.read()
                server.announcements.save(form.get("text", "").replace("\r\n", "\n"), form.get("revision", ""),
                                          form.get("exists") == "1" if "exists" in form else board["exists"], "관리자", admin=True)
                return self._redirect("/announcements")
            raise RelayError(404, "찾을 수 없는 주소입니다.")
        if method != "GET":
            raise RelayError(405, "지원하지 않는 요청입니다.")
        if parts in ([], ["reports"]):
            return self._html(200, *admin_reports(server, self.query))
        if len(parts) == 2 and parts[0] == "reports":
            return self._html(200, *admin_report(server, parts[1]))
        if len(parts) == 3 and parts[0] == "reports" and parts[2] == "logs.zip":
            folder = server.reports.folder(parts[1])
            if not (folder / "logs.zip").is_file():
                raise RelayError(404, "로그가 없습니다.")
            return self._file(folder / "logs.zip", "application/zip", f"TerminalNotes-{parts[1]}.zip")
        if parts == ["skills"]:
            return self._html(200, *admin_skills(server))
        if len(parts) == 2 and parts[0] == "skills":
            return self._html(200, *admin_skill(server, parts[1]))
        if len(parts) == 3 and parts[0] == "skills" and parts[2] == "SKILL.md":
            return self._text(200, server.skills.version(parts[1])["content"])
        if len(parts) == 5 and parts[0] == "skills" and parts[2] == "versions" and parts[4] == "SKILL.md" and VERSION_NUMBER.match(parts[3]):
            return self._text(200, server.skills.version(parts[1], int(parts[3]))["content"])
        if parts == ["announcements"]:
            return self._html(200, *admin_announcements(server))
        if len(parts) == 3 and parts[0] == "announcements" and parts[1] == "history":
            return self._text(200, server.announcements.version_text(parts[2]))
        if parts == ["status"]:
            return self._html(200, *admin_status(server))
        if parts == ["apps"]:
            return self._html(200, *admin_apps(server))
        raise RelayError(404, "찾을 수 없는 주소입니다.")

    def _admin_api(self, method: str, parts: list) -> None:
        """The admin pages' data as JSON for the API 서버 program's window, behind the same admin password (2.14).
        Changes take JSON bodies only: a page on another site cannot send JSON here without a CORS check this server never answers.
        The i-luncher app list and notices stay at /launcher/catalog and /launcher/notices."""
        server = self.server
        if method in ("POST", "PUT") and "application/json" not in self.headers.get("Content-Type", "").lower():
            raise RelayError(415, "JSON(application/json)으로 보내야 합니다.")

        def body_object() -> dict:
            value = self._json_body()
            if not isinstance(value, dict):
                raise RelayError(400, "JSON 객체가 필요합니다.")
            return value

        if parts == ["status"] and method == "GET":
            return self._json(200, admin_status_data(server))
        if parts == ["reports"] and method == "GET":
            return self._json(200, {"statuses": STATUSES, "reports": [report_json(item) for item in server.reports.all()]})
        if parts == ["reports", "analysis"] and method == "GET":
            days = min(max(as_int((self.query.get("days") or ["7"])[0], 7), 1), 365)
            return self._json(200, reports_analysis(server.reports, days))
        if len(parts) == 2 and parts[0] == "reports":
            if method == "GET":
                return self._json(200, dict(report_json(server.reports.meta(parts[1])), text=server.reports.text(parts[1])))
            if method == "PUT":
                body = body_object()
                status, note = body.get("status"), body.get("note")
                if status is not None and not isinstance(status, str) or note is not None and not isinstance(note, str):
                    raise RelayError(400, "형식이 올바르지 않습니다.")
                server.reports.update(parts[1], status, note)
                return self._json(200, report_json(server.reports.meta(parts[1])))
            if method == "DELETE":
                server.reports.delete(parts[1])
                return self._json(200, {"deleted": parts[1]})
        if len(parts) == 3 and parts[0] == "reports" and parts[2] == "logs.zip" and method == "GET":
            folder = server.reports.folder(parts[1])
            if not (folder / "logs.zip").is_file():
                raise RelayError(404, "로그가 없습니다.")
            return self._file(folder / "logs.zip", "application/zip", f"TerminalNotes-{parts[1]}.zip")
        if parts == ["skills"] and method == "GET":
            return self._json(200, server.skills.all())
        if len(parts) == 2 and parts[0] == "skills":
            if method == "GET":
                return self._json(200, {"skill": server.skills.version(parts[1]), "versions": server.skills.versions(parts[1])})
            if method == "DELETE":
                server.skills.delete(parts[1])
                log.info("skill %s deleted from the admin window", parts[1])
                return self._json(200, {"deleted": parts[1]})
        if len(parts) == 4 and parts[0] == "skills" and parts[2] == "versions" and method == "GET":
            if not VERSION_NUMBER.match(parts[3]):
                raise RelayError(404, "그 버전을 찾지 못했습니다.")
            return self._json(200, server.skills.version(parts[1], int(parts[3])))
        if len(parts) == 3 and parts[0] == "skills" and parts[2] == "restore" and method == "POST":
            version = body_object().get("version")
            if not isinstance(version, int) or isinstance(version, bool) or version < 1:
                raise RelayError(400, "버전이 올바르지 않습니다.")
            return self._json(200, {"name": parts[1], "version": server.skills.restore(parts[1], version)})
        if parts == ["announcements"]:
            if method == "GET":
                return self._json(200, dict(server.announcements.read(), history=server.announcements.history()[:50],
                                            editors=list(server.announcements.editors)))
            if method == "PUT":
                body = body_object()
                text, revision, exists = body.get("text"), body.get("revision"), body.get("exists")
                if not isinstance(text, str) or not isinstance(revision, str) or not isinstance(exists, bool):
                    raise RelayError(400, "형식이 올바르지 않습니다.")
                board = server.announcements.save(text.replace("\r\n", "\n"), revision, exists, "관리자", admin=True)
                return self._json(200, dict(board, history=server.announcements.history()[:50], editors=list(server.announcements.editors)))
        if len(parts) == 3 and parts[:2] == ["announcements", "history"] and method == "GET":
            return self._json(200, {"version": parts[2], "text": server.announcements.version_text(parts[2])})
        if parts == ["releases"]:
            if method == "GET":
                return self._json(200, {"releases": server.releases.list()})
            if method == "POST":
                body = body_object()
                return self._json(202, server.releases.release(body.get("repository"), body.get("version")))
        if parts == ["runners"] and method == "GET":
            return self._json(200, server.runners.list())
        if parts == ["runners", "template"] and method == "GET":
            return self._json(200, server.runners.template((self.query.get("repository") or [""])[0]))
        if parts[:1] == ["runners"] and method in ("POST", "PUT", "DELETE"):
            # They start programs and change Task Scheduler on the server PC: only from that PC (the API Manager there).
            self._require_this_pc()
            if parts == ["runners"] and method == "POST":
                body = body_object()
                return self._json(201, server.runners.add(body.get("repository"), body.get("folder"), body.get("name"), body.get("env")))
            if parts == ["runners", "task"] and method == "POST":
                return self._json(200, server.runners.install_task())
            if len(parts) == 2 and method == "PUT":
                return self._json(200, server.runners.save(parts[1], body_object().get("env")))
            if len(parts) == 2 and method == "DELETE":
                return self._json(200, server.runners.remove(parts[1], self.query.get("force") == ["1"]))
            if len(parts) == 3 and parts[2] in ("start", "stop", "restart") and method == "POST":
                force = body_object().get("force") is True
                return self._json(200, getattr(server.runners, parts[2])(parts[1], force))
        raise RelayError(404, "찾을 수 없는 주소입니다.")

    # Checks -------------------------------------------------------------------------------------------

    def _require_key(self) -> None:
        if not self.server.config.ingest_key:
            return
        if not secret_equals(self.headers.get("X-TerminalNotes-Key", ""), self.server.config.ingest_key):
            raise RelayError(401, "키가 맞지 않습니다.")

    def _require_admin(self) -> None:
        if not self.server.config.admin_password:
            return
        address = self.client_address[0]
        if self.server.limiter.count("admin-fail", address, 600) >= 10:
            raise RelayError(429, "비밀번호를 여러 번 틀렸습니다. 10분 뒤에 다시 시도하세요.")
        header = self.headers.get("Authorization", "")
        if header.lower().startswith("basic "):
            try:
                decoded = base64.b64decode(header[6:].strip()).decode("utf-8")
                if ":" in decoded and secret_equals(decoded.split(":", 1)[1], self.server.config.admin_password):
                    return
            except (ValueError, UnicodeDecodeError):
                pass
            self.server.limiter.allow("admin-fail", address, 1_000_000, 600)
        self._drain()
        self._status = 401
        body = "관리자 비밀번호가 필요합니다.".encode("utf-8")
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="Terminal Notes relay", charset="UTF-8"')
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        raise _Handled()

    def _require_this_pc(self) -> None:
        address = self.client_address[0]
        if not (is_loopback(address) or is_local_address(address)):
            raise RelayError(403, "러너는 서버 PC에서만 바꿀 수 있습니다.")

    def _require_same_origin(self) -> None:
        # A form post from another site would carry the browser's saved admin login: accept this page's own origin only.
        host = self.headers.get("Host", "")
        for header in ("Origin", "Referer"):
            value = self.headers.get(header)
            if value:
                if urllib.parse.urlsplit(value).netloc.lower() != host.lower():
                    raise RelayError(403, "다른 사이트에서 온 요청은 받지 않습니다.")
                return

    def _limit(self, bucket: str, limit: int, window: int) -> None:
        if not self.server.limiter.allow(bucket, self.client_address[0], limit, window):
            raise RelayError(429, "너무 자주 보냈습니다. 잠시 후 다시 시도하세요.")

    # Input ---------------------------------------------------------------------------------------------

    def _body(self) -> bytes:
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            # .NET sends JSON content of unknown length this way (HttpClient + JsonContent).
            return self._chunked_body()
        length = self.headers.get("Content-Length")
        if length is None:
            raise RelayError(411, "Content-Length가 필요합니다.")
        try:
            size = int(length)
        except ValueError:
            raise RelayError(400, "Content-Length가 올바르지 않습니다.")
        if size < 0 or size > MAX_BODY:
            raise RelayError(413, "한 번에 25MB까지 받습니다.")
        self._body_read = True
        return self._read_exactly(size)

    def _read_exactly(self, size: int) -> bytes:
        data, remaining = bytearray(), size
        while remaining > 0:
            chunk = self.rfile.read(min(remaining, 1 << 20))
            if not chunk:
                raise RelayError(400, "요청이 중간에 끊겼습니다.")
            data.extend(chunk)
            remaining -= len(chunk)
        return bytes(data)

    def _chunked_body(self) -> bytes:
        self._body_read = True
        data = bytearray()
        while True:
            line = self.rfile.readline(1024)
            if not line:
                raise RelayError(400, "요청이 중간에 끊겼습니다.")
            try:
                size = int(line.split(b";", 1)[0].strip(), 16)
            except ValueError:
                raise RelayError(400, "요청 형식이 올바르지 않습니다.")
            if size < 0 or len(data) + size > MAX_BODY:
                self.close_connection = True
                raise RelayError(413, "한 번에 25MB까지 받습니다.")
            if size == 0:
                while self.rfile.readline(1024) not in (b"\r\n", b"\n", b""):
                    pass  # trailer fields
                return bytes(data)
            data.extend(self._read_exactly(size))
            self.rfile.readline(8)  # the CRLF after each chunk

    def _json_body(self):
        try:
            return json.loads(self._body().decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            raise RelayError(400, "형식이 올바르지 않습니다.")

    def _form(self) -> dict:
        try:
            pairs = urllib.parse.parse_qs(self._body().decode("utf-8"), keep_blank_values=True)
        except UnicodeDecodeError:
            raise RelayError(400, "형식이 올바르지 않습니다.")
        return {key: values[-1] for key, values in pairs.items()}

    # Output --------------------------------------------------------------------------------------------

    def _send(self, status: int, body: bytes, content_type: str, extra: dict | None = None) -> None:
        self._status = status
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Cache-Control", "no-store")
        for key, value in (extra or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)

    def _json(self, status: int, value, extra: dict | None = None) -> None:
        self._send(status, json.dumps(value, ensure_ascii=False, default=str).encode("utf-8"), "application/json; charset=utf-8", extra)

    def _text(self, status: int, text: str) -> None:
        self._send(status, text.encode("utf-8"), "text/plain; charset=utf-8")

    def _html(self, status: int, title: str, body: str) -> None:
        self._send(status, page(title, body).encode("utf-8"), "text/html; charset=utf-8", {
            "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'",
            "X-Frame-Options": "DENY", "Referrer-Policy": "same-origin"})

    def _file(self, path: Path, content_type: str, download_name: str) -> None:
        data = path.read_bytes()
        quoted = urllib.parse.quote(download_name)
        self._send(200, data, content_type, {"Content-Disposition": f"attachment; filename*=UTF-8''{quoted}"})

    def _redirect(self, location: str) -> None:
        self._status = 303
        self.send_response(303)
        self.send_header("Location", location)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _drain(self) -> None:
        """Reads an unread request body before an early refusal, so the client gets the status instead of a reset."""
        if self._body_read:
            return
        self._body_read = True
        if self.headers.get("Transfer-Encoding"):
            # An unread chunked body cannot be skipped cheaply: answer, then close this connection.
            self.close_connection = True
            return
        try:
            remaining = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            self.close_connection = True
            return
        if not 0 < remaining <= MAX_BODY:
            if remaining:
                self.close_connection = True
            return
        try:
            while remaining > 0:
                chunk = self.rfile.read(min(remaining, 1 << 20))
                if not chunk:
                    break
                remaining -= len(chunk)
        except OSError:
            pass

    def _error(self, status: int, message: str) -> None:
        if self._status:  # a response was already started
            return
        self._drain()
        # The app and the admin window read JSON string bodies for 400/409 (same as the earlier relay); pages show plain text.
        if self.path.startswith(("/api/", "/admin/api/")):
            self._send(status, json.dumps(message, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")
        else:
            self._send(status, message.encode("utf-8"), "text/plain; charset=utf-8")

    def log_message(self, format: str, *args) -> None:  # access lines come from _dispatch
        pass


class _Handled(Exception):
    """The response was already written (for example the admin password challenge)."""


# ── 관리 페이지 ───────────────────────────────────────────────────────────────────────────────────────

def esc(value) -> str:
    return html.escape("" if value is None else str(value), quote=True)


def page(title: str, body: str) -> str:
    return f"""<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{esc(title)} · API 서버</title><style>
:root{{color-scheme:dark}}
body{{font-family:"Pretendard","Malgun Gothic",sans-serif;background:#161616;color:#F2F2F2;margin:0}}
header{{display:flex;gap:18px;align-items:center;padding:14px 24px;border-bottom:1px solid #2C2C2C;background:#1A1A1A}}
header b{{color:#E5C07B}} header a{{color:#BDBDBD;text-decoration:none}} header a:hover{{color:#E5C07B}}
main{{padding:22px 24px;max-width:1180px}} h1{{font-size:20px;margin:0 0 14px}} h2{{font-size:16px;margin:22px 0 10px;color:#E5C07B}}
a{{color:#E5C07B}} table{{border-collapse:collapse;width:100%}} th,td{{border-bottom:1px solid #2C2C2C;padding:8px;text-align:left;vertical-align:top}}
th{{color:#8C8C8C;font-weight:600}} small,.muted{{color:#8C8C8C}}
pre{{white-space:pre-wrap;background:#101010;border:1px solid #2C2C2C;border-radius:6px;padding:14px;overflow-x:auto}}
.badge{{display:inline-block;border:1px solid #3A3A3A;border-radius:10px;padding:1px 8px;font-size:12px}}
.badge.new{{border-color:#E5C07B;color:#E5C07B}} .badge.checking{{color:#F2F2F2}} .badge.resolved{{color:#8C8C8C}}
form.inline{{display:inline}} button,input,select,textarea{{font:inherit;background:#222;color:#F2F2F2;border:1px solid #3A3A3A;border-radius:6px;padding:5px 10px}}
button:hover{{border-color:#E5C07B}} textarea{{width:100%;box-sizing:border-box}} .cards{{display:flex;gap:14px;flex-wrap:wrap}}
.card{{background:#1C1C1C;border:1px solid #2C2C2C;border-radius:8px;padding:12px 16px;min-width:150px}} .card b{{font-size:22px;color:#E5C07B}}
</style></head><body><header><b>API 서버</b><a href="/reports">문제 보고</a><a href="/skills">스킬</a>
<a href="/announcements">공지</a><a href="/apps">i-luncher</a><a href="/status">상태</a></header><main><h1>{esc(title)}</h1>{body}</main></body></html>"""


def badge(status: str) -> str:
    return f'<span class="badge {esc(status)}">{esc(STATUSES.get(status, status))}</span>'


def admin_reports(server: RelayServer, query: dict) -> tuple:
    status, text = (query.get("status") or [""])[0], (query.get("q") or [""])[0].strip()
    reports = server.reports.all()
    counts = {key: sum(1 for item in reports if item["status"] == key) for key in STATUSES}
    shown = [item for item in reports if (not status or item["status"] == status) and
             (not text or text.lower() in (item["summary"] + " " + item["appVersion"] + " " + item["note"]).lower())]
    filters = " · ".join([f'<a href="/reports">전체 {len(reports)}</a>'] +
                         [f'<a href="/reports?status={key}">{label} {counts[key]}</a>' for key, label in STATUSES.items()])
    rows = "".join(report_row(item) for item in shown)
    search = (f'<form method="get" action="/reports"><input type="hidden" name="status" value="{esc(status)}">'
              f'<input name="q" value="{esc(text)}" placeholder="문제·버전·메모 검색"> <button>찾기</button></form>')
    table = (f"<table><tr><th>받은 시각</th><th>버전</th><th>문제</th><th>상태</th><th>로그</th></tr>{rows}</table>"
             if rows else "<p class='muted'>보고서가 없습니다.</p>")
    return "문제 보고", f"<p>{filters}</p>{search}{table}"


def report_row(item: dict) -> str:
    note = f'<br><small>{esc(item["note"][:80])}</small>' if item["note"] else ""
    logs = f'<a href="/reports/{item["id"]}/logs.zip">ZIP</a>' if item["hasLogs"] else "—"
    return (f'<tr><td>{esc(local_text(item["receivedAt"]))}</td><td>{esc(item["appVersion"])}</td>'
            f'<td><a href="/reports/{item["id"]}">{esc(item["summary"])}</a>{note}</td><td>{badge(item["status"])}</td><td>{logs}</td></tr>')


def admin_report(server: RelayServer, report_id: str) -> tuple:
    item = server.reports.meta(report_id)
    options = "".join(f'<option value="{key}"{" selected" if key == item["status"] else ""}>{label}</option>' for key, label in STATUSES.items())
    logs = f' · <a href="/reports/{item["id"]}/logs.zip">로그 ZIP 내려받기</a>' if item["hasLogs"] else ""
    body = (f'<p><a href="/reports">← 목록</a> · 받은 시각 {esc(local_text(item["receivedAt"]))} · 버전 {esc(item["appVersion"] or "—")} · '
            f'보낸 곳 {esc(item["remoteAddress"] or "—")} · {badge(item["status"])}{logs}</p>'
            f'<form method="post" action="/reports/{item["id"]}/update"><select name="status">{options}</select> '
            f'<input name="note" value="{esc(item["note"])}" placeholder="메모(원인·조치)" size="60"> <button>저장</button></form>'
            f'<pre>{esc(server.reports.text(report_id))}</pre>'
            f'<form class="inline" method="post" action="/reports/{item["id"]}/delete"><button>이 보고서 삭제</button></form>')
    return "보고서 " + report_id, body


def admin_skills(server: RelayServer) -> tuple:
    skills = server.skills.all()
    rows = "".join(
        f'<tr><td><a href="/skills/{skill["name"]}">{esc(skill["title"])}</a><br><small>{esc(skill["name"])}</small></td>'
        f'<td>{esc(skill["description"])}</td><td>{esc(skill["author"])}</td><td>v{skill["version"]}</td>'
        f'<td>{esc(local_text(parse_time(skill["updatedAt"])))}</td><td>{skill["downloads"]}</td></tr>' for skill in skills)
    table = (f"<table><tr><th>제목</th><th>설명</th><th>작성자</th><th>최신</th><th>업데이트</th><th>받은 수</th></tr>{rows}</table>"
             if rows else "<p class='muted'>아직 공유된 스킬이 없습니다.</p>")
    return "공유된 스킬", table


def admin_skill(server: RelayServer, name: str) -> tuple:
    skill = server.skills.version(name)
    rows = []
    for item in server.skills.versions(name):
        number = item["version"]
        action = ("<small>최신</small>" if number == skill["latestVersion"] else
                  f'<form class="inline" method="post" action="/skills/{name}/restore"><input type="hidden" name="version" value="{number}">'
                  f'<button>이 버전으로 되돌리기</button></form>')
        rows.append(f'<tr><td>v{number}</td><td>{esc(local_text(parse_time(item["publishedAt"])))}</td><td>{esc(item["author"])}</td>'
                    f'<td>{esc(item["note"])}</td><td><a href="/skills/{name}/versions/{number}/SKILL.md">원문</a></td><td>{action}</td></tr>')
    body = (f'<p><a href="/skills">← 목록</a> · 작성자 {esc(skill["author"])} · 최신 v{skill["latestVersion"]} · 받은 수 {skill["downloads"]}</p>'
            f'<pre>{esc(skill["content"])}</pre><h2>버전 기록</h2>'
            f'<table><tr><th>버전</th><th>올린 시각</th><th>작성자</th><th>바뀐 점</th><th></th><th></th></tr>{"".join(rows)}</table>'
            f'<p><form class="inline" method="post" action="/skills/{name}/delete"><button>스토어에서 삭제</button></form> '
            '<small>삭제한 스킬은 30일 동안 skills\\.trash에 남습니다. 되돌리기는 그 버전을 새 최신 버전으로 올립니다.</small></p>')
    return skill["title"], body


def admin_announcements(server: RelayServer) -> tuple:
    board = server.announcements.read()
    history = "".join(f'<li><a href="/announcements/history/{esc(version)}">{esc(version[:15])}</a></li>' for version in server.announcements.history()[:20])
    changed = f' 마지막 변경: {esc(local_text(parse_time(board["updatedAt"])))} · {esc(board["updatedBy"])}' if board["exists"] else ""
    editors = esc(", ".join(server.announcements.editors) or "앱에서 정한 계정")
    body = (f'<p class="muted">앱의 공지 버튼에 보이는 공지입니다. 앱에서는 공지 작성 계정({editors})만 고칠 수 있습니다.{changed}</p>'
            f'<form method="post" action="/announcements"><input type="hidden" name="revision" value="{esc(board["revision"])}">'
            f'<input type="hidden" name="exists" value="{"1" if board["exists"] else "0"}">'
            f'<textarea name="text" rows="22">{esc(board["text"])}</textarea><p><button>공지 저장</button></p></form>'
            + (f"<h2>이전 공지</h2><ul>{history}</ul>" if history else ""))
    return "공지", body


def admin_apps(server: RelayServer) -> tuple:
    catalog = server.launcher.catalog()
    notices = server.launcher.notices()
    rows = "".join(
        f'<tr><td>{esc(app.get("name"))}</td><td>{esc(app.get("id"))}</td><td>{esc(app.get("category") or "")}</td>'
        f'<td><a href="{esc(app.get("repository"))}">{esc(app.get("repository"))}</a></td></tr>' for app in catalog["apps"])
    document = json.dumps({"schemaVersion": 1, "apps": catalog["apps"]}, ensure_ascii=False, indent=2)
    repositories = ", ".join(sorted(server.launcher.repositories())) or "없음"
    body = ('<p class="muted">i-luncher가 보여 주는 앱 목록과 공지입니다. 런처의 앱 추가·편집·삭제(설정에 관리자 비밀번호)나 여기서 고칩니다.</p>'
            + (f'<table><tr><th>이름</th><th>id</th><th>분류</th><th>저장소</th></tr>{rows}</table>' if rows else '<p class="muted">아직 앱이 없습니다.</p>')
            + f'<h2>앱 목록 JSON</h2><form method="post" action="/apps"><input type="hidden" name="kind" value="catalog">'
            f'<input type="hidden" name="revision" value="{esc(catalog["revision"])}"><textarea name="json" rows="18">{esc(document)}</textarea>'
            '<p><button>앱 목록 저장</button></p></form>'
            '<h2>런처 공지</h2><p class="muted">첫 줄은 제목, 다음 줄부터 본문, 공지 사이는 --- 만 있는 줄입니다. 런처가 시작할 때와 30분마다 읽습니다.</p>'
            f'<form method="post" action="/apps"><input type="hidden" name="kind" value="notices"><input type="hidden" name="revision" value="{esc(notices["revision"])}">'
            f'<textarea name="text" rows="12">{esc(notices["text"])}</textarea><p><button>공지 저장</button></p></form>'
            f'<h2>GitHub 중계</h2><p class="muted">런처와 앱은 <code>{esc(server.config.public_url)}/api/v3</code>로 비공개 저장소의 릴리스를 토큰 없이 받습니다. '
            f'열린 저장소: 앱 목록({esc(repositories)}), LAUNCHER_GITHUB_REPOS({esc(", ".join(server.config.github_proxy_repos) or "없음")}), 토큰 계정의 저장소.</p>')
    return "i-luncher", body


def report_json(item: dict) -> dict:
    """A report for the admin window (times as ISO 8601, without the raw meta)."""
    return {"id": item["id"], "receivedAt": iso(item["receivedAt"]), "remoteAddress": item["remoteAddress"], "appVersion": item["appVersion"],
            "summary": item["summary"], "hasLogs": item["hasLogs"], "status": item["status"], "note": item["note"]}


def admin_status_data(server: RelayServer) -> dict:
    """What the 상태 page shows, for the admin window."""
    config = server.config
    reports = server.reports.all()
    board = server.announcements.read()
    return {
        "version": VERSION, "python": sys.version.split()[0], "startedAt": iso(server.started),
        "uptimeSeconds": int((now() - server.started).total_seconds()), "publicUrl": config.public_url,
        "counts": {"reports": len(reports), "newReports": sum(1 for item in reports if item["status"] == "new"),
                   "skills": len(server.skills.all()), "reportsBytes": folder_size(config.reports_dir)},
        "settings": config.summary() + [server.equipment.state()], "firewall": firewall_text(config),
        "githubRepositories": sorted(server.launcher.repositories()), "githubProxyRepositories": list(config.github_proxy_repos),
        "clients": [{"address": entry["address"], "version": entry.get("version") or "", "first": iso(entry["first"]), "last": iso(entry["last"]),
                     "requests": entry["requests"],
                     "announcement": "latest" if board["exists"] and entry["revision"] == board["revision"] else "older" if entry["revision"] else "",
                     "features": [FEATURES[feature] for feature in sorted(entry["features"])]} for entry in server.clients.all()],
    }


def admin_status(server: RelayServer) -> tuple:
    config = server.config
    reports = server.reports.all()
    cards = "".join(f'<div class="card"><small>{label}</small><br><b>{value}</b></div>' for label, value in (
        ("문제 보고", len(reports)), ("새 보고", sum(1 for item in reports if item["status"] == "new")),
        ("공유된 스킬", len(server.skills.all())), ("보고서 용량", f'{folder_size(config.reports_dir) / 1048576:.1f} MB')))
    uptime = now() - server.started
    lines = "".join(f"<li>{esc(line)}</li>" for line in config.summary() + [server.equipment.state(), firewall_text(config)])
    board = server.announcements.read()
    rows = "".join(
        f'<tr><td>{esc(entry["address"])}</td><td>{esc(entry["version"] or "1.0.518 이하")}</td>'
        f'<td>{esc(local_text(entry["last"]))}</td>'
        f'<td>{"최신" if board["exists"] and entry["revision"] == board["revision"] else "이전 공지" if entry["revision"] else "—"}</td>'
        f'<td>{esc(" · ".join(FEATURES[feature] for feature in sorted(entry["features"])))}</td></tr>' for entry in server.clients.all())
    clients = (f'<table><tr><th>PC 주소</th><th>앱 버전</th><th>마지막 접속</th><th>받은 공지</th><th>쓴 기능</th></tr>{rows}</table>'
               if rows else "<p class='muted'>아직 접속한 PC가 없습니다.</p>")
    return "상태", (f'<div class="cards">{cards}</div><h2>접속한 PC</h2>'
                     '<p class="muted">서버를 켠 뒤 앱이 접속한 PC입니다(앱은 1분마다 공지를 확인). '
                     '여기에 없는 PC는 이 서버에 접속하지 못하는 것입니다: 방화벽(아래)·앱의 서버 주소·앱 버전을 확인하세요.</p>'
                     f'{clients}<h2>설정</h2><ul>{lines}</ul>'
                     f'<p class="muted">버전 {VERSION} · Python {esc(sys.version.split()[0])} · 실행 {uptime.days}일 {uptime.seconds // 3600}시간</p>')


# ── 실행 ─────────────────────────────────────────────────────────────────────────────────────────────

def housekeeping(server: RelayServer, stop: threading.Event) -> None:
    first = True
    while not stop.is_set():
        try:
            removed = server.reports.housekeep(server.config.retention_days, server.config.reports_max_mb)
            server.skills.housekeep()
            server.plugins.housekeep()
            if removed:
                log.info("housekeeping removed %d old reports", removed)
        except Exception:
            log.exception("housekeeping failed")
        # A backup at the start and then once a day; nothing new is written while the store is unchanged.
        try:
            backups = store_backups(server.config)
            if first or not backups or time.time() - backups[0].stat().st_mtime >= 86400:
                made = backup_store(server.config)
                if made:
                    log.info("store backup %s", made)
        except Exception:
            log.exception("store backup failed")
        first = False
        stop.wait(3600)


def github_sync(server: RelayServer, stop: threading.Event) -> None:
    """Skills and plugins linked to a GitHub file follow it: shortly after the start, then every RELAY_GITHUB_SYNC_MINUTES minutes."""
    wait = min(5, server.config.github_sync_seconds)
    while not stop.wait(wait):
        wait = server.config.github_sync_seconds
        for name in server.skills.linked():
            try:
                number = server.skills.sync_source(name, server.config.github_token)
                if number:
                    log.info("skill %s v%s from GitHub", name, number)
            except Exception:
                log.exception("GitHub sync failed: %s", name)
        for name in server.plugins.linked():
            try:
                number = server.plugins.sync_source(name, server.config.github_token)
                if number:
                    log.info("plugin %s v%s from GitHub", name, number)
            except Exception:
                log.exception("GitHub sync failed: plugin %s", name)


def startup_folder() -> Path:
    return Path(os.environ.get("APPDATA", "")) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup"


def install_startup(set_args: list | None = None) -> None:
    """The hidden start at login runs this relay.py with its SETTINGS (and the --set values it was started with)."""
    if os.name != "nt":
        raise SystemExit("자동 실행 등록은 Windows에서만 합니다. Linux에서는 systemd 서비스로 등록하세요(README 참고).")
    pythonw = Path(sys.executable).with_name("pythonw.exe")
    runner = pythonw if pythonw.is_file() else Path(sys.executable)
    command = f'""{runner}"" ""{Path(__file__).resolve()}""' + "".join(f' --set ""{item}""' for item in set_args or [])
    script = (f'Set shell = CreateObject("WScript.Shell")\r\nshell.CurrentDirectory = "{SCRIPT_DIR}"\r\n'
              f'shell.Run "{command}", 0, False\r\n')
    folder = startup_folder()
    folder.mkdir(parents=True, exist_ok=True)
    (folder / STARTUP_SCRIPT).write_bytes(script.encode("utf-16"))  # the script's own CRLFs, not text mode's CRCRLF
    old = folder / OLD_STARTUP_SHORTCUT
    if old.is_file():
        old.unlink()
        print("이전 중계 서버(.NET) 자동 실행 바로 가기를 지웠습니다.")
    print(f"로그인 때 창 없이 자동 실행합니다: {folder / STARTUP_SCRIPT}")


def uninstall_startup(only_this: bool = False) -> None:
    """Removes the start at login; with only_this, only when it starts this relay.py (another folder's relay keeps its own)."""
    path = startup_folder() / STARTUP_SCRIPT
    if not path.is_file():
        print("등록된 자동 실행이 없습니다.")
        return
    if only_this:
        try:
            script = path.read_bytes().decode("utf-16", errors="replace")
        except OSError:
            script = ""
        if str(Path(__file__).resolve()).lower() not in script.lower():
            print("등록된 자동 실행은 다른 폴더의 중계 서버라 그대로 두었습니다.")
            return
    path.unlink()
    print("자동 실행을 해제했습니다.")


def matching_relays(listing: str, script: Path, own_pid: int) -> list:
    """The process IDs in 'pid<TAB>command line' lines whose command line names this relay.py by its full path (not the logon
    task's short --start-runners run, which is no server)."""
    target = str(script).lower()
    found = []
    for line in listing.splitlines():
        pid, _, command = line.partition("\t")
        if pid.strip().isdigit() and int(pid) != own_pid and target in command.lower() and "--start-runners" not in command.lower():
            found.append(int(pid))
    return found


def running_relays() -> list:
    """Python processes running this relay.py (Windows): the hidden start at login and --start name it by its full path."""
    if os.name != "nt":
        return []
    command = ("[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
               "Get-CimInstance Win32_Process -Filter \"Name like 'python%'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }")
    try:
        output = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", command], stdin=subprocess.DEVNULL,
                                capture_output=True, timeout=60, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    return matching_relays(output.decode("utf-8", errors="replace"), Path(__file__).resolve(), os.getpid())


def stop_running() -> int:
    """Stops every process running this relay.py; how many stopped."""
    return sum(1 for pid in running_relays() if run_hidden(["taskkill", "/PID", str(pid), "/F"], 30) == 0)


def health_url(config: Config) -> str:
    host = "127.0.0.1" if config.bind in ("", "0.0.0.0") else "::1" if config.bind == "::" else config.bind
    return f"{'https' if config.tls_cert else 'http'}://{'[' + host + ']' if ':' in host else host}:{config.port}/health"


def answers(config: Config, wait: float = 15) -> bool:
    """Whether this relay answers /health within the wait (asked directly, never through a proxy)."""
    handlers = [urllib.request.ProxyHandler({})]
    if config.tls_cert:
        # Its own address: a certificate made for the name apps use need not match it.
        handlers.append(urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
    opener = urllib.request.build_opener(*handlers)
    deadline = time.monotonic() + wait
    while True:
        try:
            with opener.open(health_url(config), timeout=2) as response:
                if response.status == 200:
                    return True
        except Exception:
            pass
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.5)


def start_relay(config: Config) -> int:
    """Windows, for the 서버 시작 script: registers the hidden start at login and opens the firewall, stops this relay if it
    runs (so a copied-in update takes effect), starts it now the way Windows will at each login, and waits for its answer."""
    if os.name != "nt":
        raise SystemExit("--start는 Windows에서 씁니다. Linux에서는 python3 relay.py를 서비스로 등록하세요(README 참고).")
    install_startup(config.set_args)
    if not is_loopback(config.bind):
        allow_firewall(config.port)
    if stop_running():
        print("실행 중이던 중계 서버를 멈추고 다시 시작합니다.")
        time.sleep(1)
    subprocess.Popen(["wscript.exe", str(startup_folder() / STARTUP_SCRIPT)], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if answers(config):
        scheme = "https" if config.tls_cert else "http"
        addresses = [config.host] if config.host else local_addresses() or ["이 PC의 IP"]
        print(f"\n중계 서버 {VERSION}이(가) 실행 중입니다: " + ", ".join(f"{scheme}://{address}:{config.port}" for address in addresses))
        if not config.host:
            print("앱 .env의 RELAY_HOST에 위 IP(이 PC의 내부 IP)를 쓰면 됩니다.")
        return 0
    print(f"\n중계 서버가 응답하지 않습니다. 로그를 확인하세요: {config.log_file}")
    try:
        for line in config.log_file.read_text(encoding="utf-8", errors="replace").splitlines()[-5:]:
            print("  " + line)
    except OSError:
        pass
    return 1


def restart_relay(config: Config) -> int:
    """For the ApiServer program: stops this relay.py if it runs and starts it again hidden, without touching the start at
    login or the firewall (no Windows prompt); 0 when it answers."""
    if os.name != "nt":
        raise SystemExit("--restart는 Windows에서 씁니다.")
    if stop_running():
        time.sleep(1)
    runner = Path(sys.executable).with_name("pythonw.exe")
    command = [str(runner if runner.is_file() else Path(sys.executable)), str(Path(__file__).resolve())]
    for item in config.set_args:
        command += ["--set", item]
    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) | getattr(subprocess, "CREATE_NO_WINDOW", 0)
    subprocess.Popen(command, cwd=str(SCRIPT_DIR), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     creationflags=flags, close_fds=True)
    if answers(config):
        print(f"API 서버 {VERSION} 실행 중: {health_url(config)[:-len('/health')]}")
        return 0
    print(f"API 서버가 응답하지 않습니다. 로그: {config.log_file}")
    return 1


# Windows allows inbound connections per program: a rule made for python.exe does not cover pythonw.exe (the startup
# runner) or the earlier .NET relay's exe. The relay's own rule opens its port for any program instead.

def firewall_rule_name(port: int) -> str:
    return f"TerminalNotesRelay-{port}"


def is_loopback(bind: str) -> bool:
    return bind in ("localhost", "::1") or bind.startswith("127.")


def run_hidden(command: list, timeout: int = 60) -> int | None:
    """Runs a short Windows command without a console window; None when it could not run."""
    try:
        return subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              timeout=timeout, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).returncode
    except (OSError, subprocess.SubprocessError):
        return None


def firewall_allowed(port: int) -> bool | None:
    """Whether the relay's inbound rule exists; None off Windows or when netsh could not be asked."""
    if os.name != "nt":
        return None
    code = run_hidden(["netsh", "advfirewall", "firewall", "show", "rule", f"name={firewall_rule_name(port)}"], 20)
    return None if code is None else code == 0


def firewall_text(config: Config) -> str:
    if is_loopback(config.bind):
        return "방화벽: 이 PC 안에서만 받습니다(RELAY_BIND)."
    allowed = firewall_allowed(config.port)
    if allowed:
        return f"방화벽: 다른 PC의 TCP {config.port} 접속 허용({firewall_rule_name(config.port)})"
    if allowed is None:
        return "방화벽: 확인하지 못했습니다(Windows가 아니면 운영체제 방화벽을 직접 확인하세요)."
    return f"방화벽: TCP {config.port} 허용 규칙 없음 — 다른 PC가 접속하지 못할 수 있습니다(python relay.py --allow-firewall)"


def is_admin() -> bool:
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, ImportError, OSError):
        return False


def allow_firewall(port: int) -> bool:
    """Adds an inbound TCP rule for the relay's port, for every network profile. Without administrator rights Windows asks
    once (UAC)."""
    if os.name != "nt":
        print(f"Windows가 아니므로 운영체제 방화벽에서 TCP {port}를 직접 여세요(예: sudo ufw allow {port}/tcp).")
        return False
    name = firewall_rule_name(port)
    if firewall_allowed(port):
        print(f"방화벽: 이미 다른 PC의 TCP {port} 접속을 허용했습니다({name}).")
        return True
    arguments = ["advfirewall", "firewall", "add", "rule", f"name={name}", "dir=in", "action=allow", "protocol=TCP",
                 f"localport={port}", "profile=any"]
    code = None
    if is_admin():
        run_hidden(["netsh", *arguments])
    else:
        print("방화벽에 포트를 열려면 관리자 승인이 필요합니다. Windows 확인 창에서 '예'를 누르세요(2분 안에).")
        # 1223 (ERROR_CANCELLED): the prompt was refused or Windows cancelled it after about two minutes.
        command = ("try { $p = Start-Process -FilePath netsh -ArgumentList '" + " ".join(arguments) +
                   "' -Verb RunAs -Wait -PassThru -WindowStyle Hidden; exit $p.ExitCode } catch { exit 1223 }")
        code = run_hidden(["powershell", "-NoProfile", "-NonInteractive", "-Command", command], 300)
    if firewall_allowed(port):
        print(f"방화벽: 다른 PC가 TCP {port}로 접속할 수 있게 허용했습니다({name}).")
        return True
    if code == 1223:
        print("Windows 확인 창에서 '예'를 누르지 않았습니다(2분 동안 응답이 없으면 Windows가 취소합니다). 다시 실행하고 '예'를 누르세요.")
    print("방화벽을 열지 못했습니다. 관리자 PowerShell에서 실행해도 됩니다(그룹 정책이 막으면 PC 관리자에게 요청):\n"
          f"  netsh {' '.join(arguments)}")
    return False


def setup_logging(config: Config, quiet: bool) -> None:
    log.setLevel(logging.INFO)
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    try:
        config.log_file.parent.mkdir(parents=True, exist_ok=True)
        file_handler = logging.handlers.RotatingFileHandler(config.log_file, maxBytes=1_048_576, backupCount=5, encoding="utf-8")
        file_handler.setFormatter(formatter)
        log.addHandler(file_handler)
    except OSError as error:
        print("로그 파일을 열지 못했습니다:", error, file=sys.stderr)
    if not quiet and sys.stdout:
        console = logging.StreamHandler(sys.stdout)
        console.setFormatter(formatter)
        log.addHandler(console)


def adopt_old_data(config: Config, extra_sources: list | None = None, include_defaults: bool = True) -> list:
    """Copies (never moves) a store left in an earlier place into the data folder, folder by folder, while the data folder has
    none of its own. Places, newest layout first: the 2.10–2.11 default folder (%LOCALAPPDATA%\\TerminalNotesRelay), the folder
    of the relay.py given to --settings-from, and beside this relay.py (before 2.10). Returns what was copied."""
    messages = []
    sources = (legacy_data_roots() if include_defaults else []) + [Path(path) for path in (extra_sources or [])] + [SCRIPT_DIR]
    for target in (config.reports_dir, config.skills_dir, config.plugins_dir, config.announcements_dir):
        try:
            relative = target.relative_to(config.data_root)
        except ValueError:
            continue  # A folder of its own, set in SETTINGS.
        try:
            if target.is_dir() and any(target.iterdir()):
                continue
        except OSError:
            continue
        for root in sources:
            old = root / relative
            try:
                if old.resolve() == target or not old.is_dir() or not any(old.iterdir()):
                    continue
            except OSError:
                continue
            staging = target.with_name(target.name + ".copying")
            try:
                shutil.rmtree(staging, ignore_errors=True)
                staging.parent.mkdir(parents=True, exist_ok=True)
                shutil.copytree(old, staging)
                if target.is_dir():
                    target.rmdir()
                staging.rename(target)
            except OSError:
                log.exception("could not copy the data in %s; it stays there", old)
                continue
            log.info("data copied from %s to %s", old, target)
            messages.append(f"{old}의 데이터를 {target}(으)로 복사했습니다(원본은 그대로 둡니다).")
            try:
                (root / MOVED_NOTE).write_text(
                    f"이 폴더의 스킬·공지·보고서는 {now().astimezone():%Y-%m-%d %H:%M}에 {config.data_root}(으)로 복사되었습니다.\r\n"
                    "중계 서버는 이제 그 폴더를 씁니다. 이 폴더에 남은 것은 예전 사본이라 고쳐도 반영되지 않습니다.\r\n", encoding="utf-8")
            except OSError:
                pass
            break
    return messages


# ── 자동 백업 ─────────────────────────────────────────────────────────────────────────────────────────

BACKUP_NAME = re.compile(r"^store-[0-9]{8}-[0-9]{6}\.zip$")


def store_folders(config: Config) -> list:
    return [("skills", config.skills_dir), ("plugins", config.plugins_dir), ("announcements", config.announcements_dir), ("launcher", config.launcher_dir)]


def store_backups(config: Config) -> list:
    """Backups in the backup folder, newest first."""
    try:
        found = [path for path in config.backup_dir.iterdir() if path.is_file() and BACKUP_NAME.match(path.name)]
    except OSError:
        return []
    return sorted(found, key=lambda path: path.name, reverse=True)


def backup_store(config: Config) -> Path | None:
    """A ZIP of the skills, plugins and announcements (reports are left out: large and short-lived). Made only when something
    changed since the newest backup, so the kept backups are distinct states; the oldest beyond RELAY_BACKUP_KEEP go."""
    import zipfile
    files = []
    for name, folder in store_folders(config):
        try:
            files += [(name, folder, path) for path in sorted(folder.rglob("*")) if path.is_file()]
        except OSError:
            continue
    if not files:
        return None  # An empty store is never backed up, so it cannot push the good backups out.
    digest = hashlib.sha256()
    for name, folder, path in files:
        stat = path.stat()
        digest.update(f"{name}/{path.relative_to(folder).as_posix()}\0{stat.st_size}\0{stat.st_mtime_ns}\n".encode("utf-8"))
    fingerprint = digest.hexdigest()
    backups = store_backups(config)
    if backups:
        try:
            with zipfile.ZipFile(backups[0]) as latest:
                if latest.comment.decode("ascii", "replace") == fingerprint:
                    return None
        except (OSError, zipfile.BadZipFile):
            pass
    config.backup_dir.mkdir(parents=True, exist_ok=True)
    target = config.backup_dir / f"store-{now().astimezone():%Y%m%d-%H%M%S}.zip"
    if target.exists():
        return None
    temporary = target.with_suffix(".zip.tmp")
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, folder, path in files:
            archive.write(path, f"{name}/{path.relative_to(folder).as_posix()}")
        archive.comment = fingerprint.encode("ascii")
    os.replace(temporary, target)
    for old in store_backups(config)[config.backup_keep:]:
        try:
            old.unlink()
        except OSError:
            pass
    return target


def restore_backup(config: Config, source: Path | None = None) -> list:
    """Puts a backup's folders back where they are empty or missing; a folder that still has content is left alone.
    Returns what happened, one line each."""
    import zipfile
    if source is None:
        backups = store_backups(config)
        if not backups:
            raise SystemExit(f"백업이 없습니다: {config.backup_dir}")
        source = backups[0]
    lines = []
    with zipfile.ZipFile(source) as archive:
        members = [info for info in archive.infolist() if not info.is_dir()]
        for name, folder in store_folders(config):
            own = [info for info in members if info.filename.startswith(name + "/")]
            if not own:
                continue
            try:
                if folder.is_dir() and any(folder.iterdir()):
                    lines.append(f"{name}: 지금 폴더({folder})에 내용이 있어 그대로 두었습니다. 덮어쓰려면 그 폴더를 다른 곳으로 옮긴 뒤 다시 실행하세요.")
                    continue
            except OSError as error:
                lines.append(f"{name}: 폴더를 확인하지 못했습니다 · {error}")
                continue
            staging = folder.with_name(folder.name + ".restoring")
            shutil.rmtree(staging, ignore_errors=True)
            for info in own:
                relative = Path(info.filename[len(name) + 1:])
                if relative.is_absolute() or ".." in relative.parts or not relative.parts:
                    continue  # Never outside the folder.
                destination = staging / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info) as data, open(destination, "wb") as out:
                    shutil.copyfileobj(data, out)
            if folder.is_dir():
                folder.rmdir()
            folder.parent.mkdir(parents=True, exist_ok=True)
            staging.rename(folder)
            lines.append(f"{name}: {source.name}에서 {len(own)}개 파일을 되살렸습니다.")
    return lines


def make_server(config: Config) -> RelayServer:
    adopt_old_data(config)
    server = RelayServer(config, Handler)
    if config.tls_cert:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(config.tls_cert, config.tls_key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
    return server


def parse_args(argv: list | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Terminal Notes 중계 서버(문제 보고 · 스킬 스토어 · 공지)")
    parser.add_argument("--set", action="append", metavar="키=값", help="이번 실행만 SETTINGS 값을 바꿈(여러 번 쓸 수 있음)")
    parser.add_argument("--settings-from", metavar="relay.py", help="예전 relay.py의 SETTINGS를 이 파일로 옮기고 끝냄")
    parser.add_argument("--port", type=int, help="포트(기본: RELAY_PORT 또는 2003)")
    parser.add_argument("--bind", help="받을 주소(기본: RELAY_BIND, 없으면 RELAY_HOST의 IP, 이름이면 0.0.0.0)")
    parser.add_argument("--data-dir", help="보고서 폴더")
    parser.add_argument("--skills-dir", help="스킬 폴더")
    parser.add_argument("--check", action="store_true", help="설정·방화벽만 확인하고 끝냄")
    parser.add_argument("--quiet", action="store_true", help="화면 로그 없이 파일에만 기록")
    parser.add_argument("--allow-firewall", action="store_true", help="다른 PC가 접속하도록 Windows 방화벽에 포트를 엶")
    parser.add_argument("--install-startup", action="store_true", help="Windows 로그인 때 자동 실행(방화벽도 엶)")
    parser.add_argument("--uninstall-startup", action="store_true", help="자동 실행 해제")
    parser.add_argument("--start", action="store_true", help="자동 실행 등록·방화벽 후 지금 창 없이 시작(실행 중이면 다시 시작, 서버 시작.cmd)")
    parser.add_argument("--stop", action="store_true", help="실행 중인 이 중계 서버를 멈추고 자동 실행 해제(서버 중지.cmd)")
    parser.add_argument("--restart", action="store_true", help="실행 중이면 멈추고 창 없이 다시 시작(자동 실행·방화벽은 그대로, ApiServer 프로그램이 씀)")
    parser.add_argument("--halt", action="store_true", help="실행 중인 이 서버만 멈춤(자동 실행 등록은 그대로)")
    parser.add_argument("--version", action="store_true", help="버전만 출력")
    parser.add_argument("--start-runners", action="store_true", help="켜 둔 GitHub 러너를 시작하고 끝냄(로그온 작업 GitHub Runners가 씀)")
    parser.add_argument("--backup", action="store_true", help="스킬·플러그인·공지를 지금 백업(ZIP)하고 끝냄")
    parser.add_argument("--restore-backup", nargs="?", const="", metavar="ZIP",
                        help="백업에서 비어 있거나 없는 스킬·플러그인·공지 폴더를 되살림(ZIP 생략 시 가장 최근 백업, 서버를 멈춘 뒤)")
    return parser.parse_args(argv)


def main(argv: list | None = None) -> int:
    if sys.version_info < (3, 8):
        print("Python 3.8 이상이 필요합니다.", file=sys.stderr)
        return 1
    # Redirected output (a log file, a service) gets UTF-8 instead of the console code page.
    for stream in (sys.stdout, sys.stderr):
        if stream is not None and hasattr(stream, "reconfigure") and not stream.isatty():
            stream.reconfigure(encoding="utf-8", errors="replace")
    args = parse_args(argv)
    if args.version:
        print(VERSION)
        return 0
    if args.halt:
        stopped = stop_running()
        print(f"멈춘 서버 {stopped}개")
        return 0
    if args.settings_from:
        copy_settings(Path(args.settings_from))
        return 0
    if args.uninstall_startup:
        uninstall_startup()
        return 0
    if args.stop:
        stopped = stop_running()
        uninstall_startup(only_this=True)
        print("중계 서버를 멈췄습니다." if stopped else "실행 중인 중계 서버가 없습니다.")
        return 0
    config = Config(args)
    if args.start_runners:
        # Before the address check: right after logon the network may not be up yet, and runners need none of it.
        return start_runners_at_logon(config)
    if args.allow_firewall:
        return 0 if allow_firewall(config.port) else 1
    if args.backup:
        made = backup_store(config)
        print(f"백업했습니다: {made}" if made else "바뀐 것이 없거나 스토어가 비어 있어 새 백업을 만들지 않았습니다.")
        return 0
    if args.restore_backup is not None:
        if answers(config, wait=1):
            print("중계 서버가 실행 중입니다. 서버 중지.cmd(또는 --stop)로 멈춘 뒤 다시 실행하세요.", file=sys.stderr)
            return 1
        for line in restore_backup(config, Path(args.restore_backup) if args.restore_backup else None):
            print(line)
        return 0
    try:
        config.validate()
    except SystemExit as problem:
        # Started hidden at login there is no console: the log keeps why the relay did not start.
        if not args.check:
            setup_logging(config, True)
            log.error("%s", problem)
        raise
    if args.start:
        return start_relay(config)
    if args.restart:
        return restart_relay(config)
    if args.install_startup:
        install_startup(config.set_args)
        # The startup runner is pythonw.exe, which no earlier "allow python" answer covers.
        if not is_loopback(config.bind):
            allow_firewall(config.port)
        return 0
    if args.check:
        print("\n".join(config.summary() + [firewall_text(config)]))
        return 0
    setup_logging(config, args.quiet or sys.stdout is None)
    try:
        server = make_server(config)
    except ssl.SSLError as error:
        log.error("HTTPS 인증서(RELAY_TLS_CERT·RELAY_TLS_KEY)를 읽지 못했습니다: %s", error)
        return 1
    except OSError as error:
        log.error("포트 %s을(를) 열지 못했거나 인증서 파일이 없습니다(이미 실행 중일 수 있습니다): %s", config.port, error)
        return 1
    stop = threading.Event()
    threading.Thread(target=housekeeping, args=(server, stop), name="housekeeping", daemon=True).start()
    threading.Thread(target=github_sync, args=(server, stop), name="github-sync", daemon=True).start()
    for line in config.summary():
        log.info(line)
    if not is_loopback(config.bind) and firewall_allowed(config.port) is False:
        log.warning(firewall_text(config))
    log.info("Terminal Notes 중계 서버 %s 시작", VERSION)
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        server.server_close()
        log.info("중계 서버 종료")
    return 0


if __name__ == "__main__":
    sys.exit(main())
