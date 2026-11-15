"""Tests for relay.py with the standard library only:  python -m unittest test_relay  (run in the relay folder).

Each test runs a real relay on a loopback port with its own settings (--set) and temporary folders.
"""
from __future__ import annotations

import base64
import contextlib
import http.client
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path
from unittest import mock

import relay

KEY = "test-ingest-key"
ADMIN = "test-admin-password"


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class OldFromIsoformat(datetime):
    """datetime.fromisoformat before Python 3.11: no 'Z', and fractions of exactly 3 or 6 digits."""

    @classmethod
    def fromisoformat(cls, text):
        fraction = re.search(r"\.(\d+)", text)
        if text.endswith("Z") or fraction and len(fraction.group(1)) not in (3, 6):
            raise ValueError("Invalid isoformat string: " + repr(text))
        return datetime.fromisoformat(text)


def sets(*lines: str) -> list:
    """Settings for one test relay as --set options (the relay reads no .env)."""
    return [argument for line in lines for argument in ("--set", line)]


class RelayTest(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="relay test "))
        # The PC's real older data folders are never read or copied by a test.
        legacy = mock.patch.object(relay, "legacy_data_roots", return_value=[])
        legacy.start()
        self.addCleanup(legacy.stop)
        self.port = free_port()
        self.config = relay.Config(relay.parse_args(sets(
            f"RELAY_PORT={self.port}", f"RELAY_INGEST_KEY={KEY}", f"RELAY_ADMIN_PASSWORD={ADMIN}",
            f"RELAY_DATA_DIR={self.root / 'reports'}", f"RELAY_SKILLS_DIR={self.root / 'skills'}", f"RELAY_PLUGINS_DIR={self.root / 'plugins'}",
            f"RELAY_ANNOUNCEMENTS_DIR={self.root / 'announcements'}", f"RELAY_LOG_FILE={self.root / 'relay.log'}",
            f"RELAY_BACKUP_DIR={self.root / 'backups'}", "RELAY_REPORT_RETENTION_DAYS=90", "RELAY_UPLOADS_PER_HOUR=5", "MASTER_USERS=editor_one, DOMAIN\\editor_two") + ["--bind", "127.0.0.1"]))
        self.server = relay.make_server(self.config)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        shutil.rmtree(self.root, ignore_errors=True)

    # HTTP helpers ------------------------------------------------------------------------------------

    def call(self, method, path, body=None, headers=None, key=True, admin=False):
        request = urllib.request.Request(self.base + path, data=body, method=method, headers=dict(headers or {}))
        if key:
            request.add_header("X-TerminalNotes-Key", KEY)
        if admin:
            token = base64.b64encode(("admin:" + (admin if isinstance(admin, str) else ADMIN)).encode()).decode()
            request.add_header("Authorization", "Basic " + token)
        opener = urllib.request.build_opener(NoRedirect)
        try:
            with opener.open(request, timeout=10) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers

    def call_from(self, address, method, path, headers=None, key=KEY):
        """A request from another loopback address: the relay sees another PC."""
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10, source_address=(address, 0))
        try:
            connection.request(method, path, headers={**({"X-TerminalNotes-Key": key} if key else {}), **(headers or {})})
            response = connection.getresponse()
            return response.status, response.read(), response.headers
        finally:
            connection.close()

    def api_json(self, method, path, value=None, headers=None, key=True):
        body = None if value is None else json.dumps(value).encode("utf-8")
        merged = {"Content-Type": "application/json", **(headers or {})}
        status, data, response_headers = self.call(method, path, body, merged, key=key)
        return status, (json.loads(data.decode("utf-8")) if data else None), response_headers

    def form(self, path, fields, origin=None):
        body = urllib.parse.urlencode(fields).encode("utf-8")
        headers = {"Content-Type": "application/x-www-form-urlencoded"}
        if origin:
            headers["Origin"] = origin
        return self.call("POST", path, body, headers, key=False, admin=True)

    def upload_report(self, text, logs=None):
        boundary = "b3f2e0f2-9a2b-4c3e-8a77-0123456789ab"
        parts = [f'--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=report\r\n\r\n'.encode()
                 + text.encode("utf-8") + b"\r\n"]
        if logs is not None:
            parts.append(f"--{boundary}\r\nContent-Type: application/zip\r\nContent-Disposition: form-data; name=logs; filename=logs.zip; "
                         f"filename*=utf-8''logs.zip\r\n\r\n".encode() + logs + b"\r\n")
        body = b"".join(parts) + f"--{boundary}--\r\n".encode()
        return self.call("POST", "/api/reports", body, {"Content-Type": f'multipart/form-data; boundary="{boundary}"'})

    # Tests --------------------------------------------------------------------------------------------

    def test_health_and_keys(self):
        self.assertEqual(self.call("GET", "/health", key=False)[1], b"ok")
        status, body, _ = self.call("GET", "/api/skills", key=False)
        self.assertEqual(status, 401)
        self.assertEqual(json.loads(body), "키가 맞지 않습니다.")
        self.assertEqual(self.call("GET", "/reports", key=False)[0], 401)
        self.assertEqual(self.call("GET", "/reports", key=False, admin=True)[0], 200)

    def test_report_round_trip_and_triage(self):
        logs = bytes(range(256)) * 50 + b"\r\n--not-a-boundary\r\n"
        report = "앱 버전: 1.0.517\n[어떤 문제가 있었나요]\n한글 입력이 안 됩니다\n"
        status, body, _ = self.upload_report(report, logs)
        self.assertEqual(status, 200)
        report_id = json.loads(body)["id"]
        folder = self.root / "reports" / report_id
        self.assertEqual((folder / "logs.zip").read_bytes(), logs)
        self.assertEqual((folder / "report.txt").read_text(encoding="utf-8"), report)
        meta = json.loads((folder / "meta.json").read_text(encoding="utf-8"))
        self.assertEqual((meta["AppVersion"], meta["Summary"], meta["Status"]), ("1.0.517", "한글 입력이 안 됩니다", "new"))
        page = self.call("GET", "/reports", key=False, admin=True)[1].decode("utf-8")
        self.assertIn("한글 입력이 안 됩니다", page)
        self.assertEqual(self.call("GET", f"/reports/{report_id}/logs.zip", key=False, admin=True)[1], logs)
        self.assertEqual(self.form(f"/reports/{report_id}/update", {"status": "checking", "note": "IME 확인"}, self.base)[0], 303)
        self.assertEqual(self.server.reports.meta(report_id)["status"], "checking")
        self.assertIn("IME 확인", self.call("GET", "/reports?q=IME", key=False, admin=True)[1].decode("utf-8"))
        self.assertEqual(self.form(f"/reports/{report_id}/update", {"status": "resolved"}, "http://evil.example")[0], 403)
        self.assertEqual(self.form(f"/reports/{report_id}/delete", {}, self.base)[0], 303)
        self.assertFalse(folder.exists())

    def test_reports_analysis_counts_automatic_problems(self):
        automatic = ("[Terminal Notes 자동 문제 보고]\n앱 버전: v1.8.3\n[어떤 문제가 있었나요]\n자동 보고 · 비정상 종료 1 · 오류 2\n"
                     "문제: 비정상 종료 1 · 오류 2\n문제 이벤트: session.previous_abnormal_shutdown ×1, terminal_lifecycle_fault ×2\n")
        for text in (automatic, automatic.replace("오류 2", "오류 1").replace("terminal_lifecycle_fault ×2", "terminal_lifecycle_fault ×1"),
                     "앱 버전: v1.8.2\n[어떤 문제가 있었나요]\n저장이 안 됩니다\n"):
            self.assertEqual(self.upload_report(text)[0], 200)
        self.assertEqual(self.call("GET", "/admin/api/reports/analysis", key=False)[0], 401, "admin only")
        status, body, _ = self.call("GET", "/admin/api/reports/analysis?days=7", key=False, admin=True)
        self.assertEqual(status, 200, body)
        analysis = json.loads(body)
        self.assertEqual((analysis["total"], analysis["automatic"], analysis["manual"], analysis["new"]), (3, 2, 1, 3))
        self.assertEqual(analysis["problems"], [{"name": "오류", "count": 3}, {"name": "비정상 종료", "count": 2}])
        self.assertEqual(analysis["events"][0], {"name": "terminal_lifecycle_fault", "count": 3})
        self.assertEqual(analysis["versions"][0], {"name": "v1.8.3", "count": 2})
        self.assertEqual(sum(day["count"] for day in analysis["perDay"]), 3)

    def test_upload_limits_and_size(self):
        for _ in range(5):
            self.assertEqual(self.upload_report("[어떤 문제가 있었나요]\nx")[0], 200)
        self.assertEqual(self.upload_report("[어떤 문제가 있었나요]\nx")[0], 429)
        # Skill shares are counted apart from reports.
        upload = {"name": "after-reports", "title": "t", "description": "d", "body": "b", "author": "a"}
        self.assertEqual(self.api_json("POST", "/api/skills", upload)[0], 200)
        self.assertEqual(self.call("PUT", "/api/announcements", b"x" * 10, {"Content-Length": str(relay.MAX_BODY + 1)})[0], 413)

    def test_skill_store_versions(self):
        upload = {"name": "impala-query", "title": "Impala 조회", "description": "데이터레이크: \"Impala\"", "body": "- Impala로 조회한다.", "author": "alice"}
        status, result, _ = self.api_json("POST", "/api/skills", upload)
        self.assertEqual((status, result["version"], result["changed"]), (200, 1, True))
        # The same content again is no new version.
        self.assertEqual(self.api_json("POST", "/api/skills", upload)[1]["version"], 1)
        status, listed, _ = self.api_json("GET", "/api/skills")
        self.assertEqual([(skill["name"], skill["author"], skill["version"], skill["downloads"]) for skill in listed], [("impala-query", "alice", 1, 0)])
        # The card's short line: the first instruction, without the frontmatter, the title or the list mark.
        self.assertEqual(listed[0]["summary"], "Impala로 조회한다.")
        status, skill, _ = self.api_json("GET", "/api/skills/impala-query")
        self.assertTrue(skill["content"].startswith('---\nname: impala-query\ndescription: "데이터레이크: \\"Impala\\""\n---\n\n# Impala 조회\n\n- Impala로 조회한다.'))
        self.assertEqual((skill["sha256"], skill["body"], skill["version"]), (result["sha256"], "- Impala로 조회한다.", 1))
        self.assertEqual(self.api_json("GET", "/api/skills")[1][0]["downloads"], 1)
        status, message, _ = self.api_json("POST", "/api/skills", dict(upload, author="bob"))
        self.assertEqual((status, message), (409, "같은 영문 이름의 스킬이 이미 있습니다. 다른 이름을 쓰세요."))
        self.assertEqual(self.api_json("POST", "/api/skills", dict(upload, name="Bad Name"))[0], 400)
        # The author keeps editing: every change is a new version; the store shows only the latest.
        status, second, _ = self.api_json("POST", "/api/skills", dict(upload, body="- Impala로만 조회한다.", note="Hive 금지 추가", author="ALICE"))
        self.assertEqual((status, second["version"]), (200, 2))
        listed = self.api_json("GET", "/api/skills")[1]
        self.assertEqual([(skill["version"], skill["versionCount"], skill["note"]) for skill in listed], [(2, 2, "Hive 금지 추가")])
        versions = self.api_json("GET", "/api/skills/impala-query/versions")[1]
        self.assertEqual([(item["version"], item["note"]) for item in versions], [(2, "Hive 금지 추가"), (1, "")])
        old = self.api_json("GET", "/api/skills/impala-query/versions/1")[1]
        self.assertEqual((old["version"], old["latestVersion"], old["body"]), (1, 2, "- Impala로 조회한다."))
        downloads = self.api_json("GET", "/api/skills")[1][0]["downloads"]
        self.api_json("GET", "/api/skills/impala-query/versions/1?apply=1")
        self.assertEqual(self.api_json("GET", "/api/skills")[1][0]["downloads"], downloads + 1)
        self.assertEqual(self.api_json("GET", "/api/skills/impala-query/versions/9")[0], 404)
        self.assertEqual(self.api_json("GET", "/api/skills/impala-query/versions/x")[0], 404)
        # The admin can bring an old version back: it becomes the new latest version.
        self.assertIn("v2", self.call("GET", "/skills/impala-query", key=False, admin=True)[1].decode("utf-8"))
        self.assertEqual(self.form("/skills/impala-query/restore", {"version": "1"}, self.base)[0], 303)
        latest = self.api_json("GET", "/api/skills/impala-query")[1]
        self.assertEqual((latest["version"], latest["body"]), (3, "- Impala로 조회한다."))
        self.assertEqual(self.call("GET", "/skills/impala-query/versions/2/SKILL.md", key=False, admin=True)[1].decode("utf-8").count("Impala로만"), 1)
        self.assertEqual(self.form("/skills/impala-query/delete", {}, self.base)[0], 303)
        self.assertEqual(self.api_json("GET", "/api/skills")[1], [])
        self.assertEqual(len(list((self.root / "skills" / ".trash").iterdir())), 1)

    def test_chunked_json_body(self):
        # .NET HttpClient sends JsonContent without a length, chunked.
        upload = json.dumps({"name": "chunked-skill", "title": "t", "description": "d", "body": "b", "author": "a"}).encode("utf-8")
        request = urllib.request.Request(self.base + "/api/skills", data=iter([upload[:10], upload[10:]]), method="POST",
                                         headers={"Content-Type": "application/json", "X-TerminalNotes-Key": KEY})
        with urllib.request.urlopen(request, timeout=10) as response:
            self.assertEqual(json.loads(response.read())["version"], 1)
        self.assertEqual([skill["name"] for skill in self.api_json("GET", "/api/skills")[1]], ["chunked-skill"])

    def test_skill_from_earlier_relay_becomes_version_one(self):
        skill = self.root / "skills" / "dotnet-skill"
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text("---\nname: dotnet-skill\ndescription: x\n---\n\n# 닷넷\n\n- 원래 내용\n", encoding="utf-8")
        (skill / "meta.json").write_text(json.dumps({"Name": "dotnet-skill", "Title": "닷넷", "Description": "x", "Author": "a",
                                                     "CreatedAt": "2026-10-22T00:00:00.1234567+00:00", "UpdatedAt": "2026-10-22T00:00:00.1234567+00:00",
                                                     "Sha256": "AB"}), encoding="utf-8")
        self.assertEqual([(item["name"], item["version"]) for item in self.api_json("GET", "/api/skills")[1]], [("dotnet-skill", 1)])
        self.assertEqual([item["version"] for item in self.api_json("GET", "/api/skills/dotnet-skill/versions")[1]], [1])
        upload = {"name": "dotnet-skill", "title": "닷넷", "description": "x", "body": "- 고친 내용", "author": "a"}
        self.assertEqual(self.api_json("POST", "/api/skills", upload)[1]["version"], 2)
        self.assertEqual(self.api_json("GET", "/api/skills/dotnet-skill/versions/1")[1]["body"], "- 원래 내용")

    def equipment_db(self, rows, queries):
        class FakeDb:
            def __init__(self, *login):
                queries.append(login)

            def query(self, sql):
                queries.append(sql)
                return [list(row) for row in rows]

            def close(self):
                pass

        self.config.equipment_db_host = "db.example"
        self.config.equipment_db_user, self.config.equipment_db_password = "reader", "secret"
        self.config.equipment_db_name, self.config.equipment_table = "plant", "eqp_status"
        self.config.equipment_api_key = "eq-key"
        self.server.equipment.connect = FakeDb

    def test_equipment_status(self):
        path = "/api/v1/equipment-status"
        self.assertEqual(self.call("GET", path, key=False)[0], 404)  # off until EQUIPMENT_DB_HOST is set
        queries = []
        self.equipment_db([["S1", "S1DPN", "DPN", "EQ-1", "down ", "1.5", "2026-01-01 10:00:00", "alarm"],
                           ["S3", None, "ETH", "EQ-2", "RUN", "x", None, None]], queries)
        self.assertEqual(self.call("GET", path)[0], 401)  # its own key, not the Terminal Notes one
        headers = {"X-EQStatus-API-Key": "eq-key"}
        status, body, response = self.call("GET", path, headers=headers, key=False)
        self.assertEqual(status, 200)
        items = json.loads(body)["items"]
        self.assertEqual(items[0], {"line": "S1", "sdwt": "S1DPN", "process": "DPN", "equipmentId": "EQ-1", "status": "DOWN",
                                    "hours": 1.5, "downTime": "2026-01-01 10:00:00", "comment": "alarm"})
        self.assertEqual((items[1]["sdwt"], items[1]["hours"], items[1]["downTime"]), ("", 0.0, ""))
        self.assertEqual(queries[0], ("db.example", 3306, "reader", "secret", "plant"))
        self.assertIn("FROM `eqp_status`", queries[1])
        self.assertIn("'hide'", queries[1])
        # Unchanged: 304, and within EQUIPMENT_REFRESH_SECONDS the DB is not asked again.
        self.assertEqual(self.call("GET", path, headers={**headers, "If-None-Match": response["ETag"]}, key=False)[0], 304)
        self.assertEqual(len(queries), 2)
        self.assertIn("설비 상태: 2대", self.admin_json("GET", "/admin/api/status")[1]["settings"][-1])

    def test_equipment_status_db_failure(self):
        class Refused:
            def __init__(self, *login):
                raise ConnectionRefusedError("refused")

        self.equipment_db([], [])
        self.server.equipment.connect = Refused
        status, body, _ = self.call("GET", "/api/v1/equipment-status", headers={"X-EQStatus-API-Key": "eq-key"}, key=False)
        self.assertEqual(status, 503)
        self.assertIn("refused", json.loads(body))

    def test_settings_from_adds_new_keys(self):
        old = '# ── 설정 시작 ──\r\nSETTINGS = {\r\n    "RELAY_HOST": "10.0.0.1",\r\n    "RELAY_PORT": 2003\r\n}\r\n# ── 설정 끝 ──'
        new = relay.settings_block(Path(relay.__file__).read_text(encoding="utf-8"))
        merged, added = relay.with_new_settings(old, new)
        values = relay.settings_values(merged)
        self.assertEqual((values["RELAY_HOST"], values["EQUIPMENT_DB_PORT"], values["EQUIPMENT_DB_HOST"]), ("10.0.0.1", "3306", ""))
        self.assertIn("EQUIPMENT_STATUS_TABLE", added)
        self.assertNotIn("RELAY_HOST", added)
        self.assertEqual(relay.with_new_settings(merged, new), (merged, []))

    def test_announcements(self):
        status, board, headers = self.api_json("GET", "/api/announcements")
        self.assertEqual((status, board["exists"], board["revision"]), (200, False, "missing"))
        save = {"text": "## 2026-10-22\n새 공지", "expectedRevision": "missing", "expectedExists": False, "author": "someone"}
        self.assertEqual(self.api_json("PUT", "/api/announcements", save)[0], 403)
        status, board, _ = self.api_json("PUT", "/api/announcements", dict(save, author="DOMAIN\\editor_two"))
        self.assertEqual((status, board["exists"], board["text"]), (200, True, "## 2026-10-22\n새 공지"))
        status, fresh, headers = self.api_json("GET", "/api/announcements")
        self.assertEqual(fresh["revision"], relay.sha256_hex("## 2026-10-22\n새 공지".encode("utf-8")))
        self.assertEqual(self.call("GET", "/api/announcements", headers={"If-None-Match": headers["ETag"]})[0], 304)
        stale = dict(save, author="editor_one", expectedExists=True, expectedRevision="0" * 64, text="덮어쓰기")
        self.assertEqual(self.api_json("PUT", "/api/announcements", stale)[0], 409)
        good = dict(stale, expectedRevision=fresh["revision"], text="## 2026-10-23\n다음 공지")
        self.assertEqual(self.api_json("PUT", "/api/announcements", good)[0], 200)
        self.assertEqual(len(self.server.announcements.history()), 1)
        current = self.server.announcements.read()
        self.assertEqual(self.form("/announcements", {"text": "관리자 공지", "revision": current["revision"], "exists": "1"}, self.base)[0], 303)
        self.assertEqual(self.api_json("GET", "/api/announcements")[1]["text"], "관리자 공지")
        self.assertIn("관리자 공지", self.call("GET", "/announcements", key=False, admin=True)[1].decode("utf-8"))

    def test_announcement_reaches_other_pcs(self):
        save = {"text": "## 2026-10-22\n모두 보세요", "expectedRevision": "missing", "expectedExists": False, "author": "editor_one"}
        status, board, headers = self.api_json("PUT", "/api/announcements", save)
        self.assertEqual((status, headers["X-TerminalNotes-Seen-By"]), (200, "1"))  # the editor's PC has it
        status, body, headers = self.call_from("127.0.0.2", "GET", "/api/announcements", {"User-Agent": "TerminalNotes/1.0.519"})
        self.assertEqual((status, json.loads(body)["text"], headers["X-TerminalNotes-Seen-By"]), (200, "## 2026-10-22\n모두 보세요", "2"))
        etag = headers["ETag"]
        status, _, headers = self.call_from("127.0.0.2", "GET", "/api/announcements", {"If-None-Match": etag})
        self.assertEqual((status, headers["X-TerminalNotes-Seen-By"]), (304, "2"))  # an unchanged board still reports the count
        self.call_from("127.0.0.3", "GET", "/api/skills", key="wrong")
        page = self.call("GET", "/status", key=False, admin=True)[1].decode("utf-8")
        for expected in ("127.0.0.2", "1.0.519", "1.0.518 이하", "127.0.0.3", "키 틀림(거절)", "최신", "방화벽"):
            self.assertIn(expected, page)
        # A new version: the PCs that still have the old one are no longer counted.
        newer = dict(save, expectedExists=True, expectedRevision=board["revision"], text="## 2026-10-23\n다음 공지")
        self.assertEqual(self.api_json("PUT", "/api/announcements", newer)[2]["X-TerminalNotes-Seen-By"], "1")
        self.assertIn("이전 공지", self.call("GET", "/status", key=False, admin=True)[1].decode("utf-8"))

    def test_routine_polls_stay_out_of_the_log(self):
        with self.assertLogs("relay", level="INFO") as captured:
            for _ in range(3):
                self.call("GET", "/api/announcements")
            self.call("GET", "/api/skills")
            for _ in range(100):
                if any("GET /api/skills 200" in line for line in captured.output):
                    break
                time.sleep(0.02)
        self.assertEqual(sum("새 PC 접속: 127.0.0.1" in line for line in captured.output), 1)
        self.assertTrue(any("GET /api/skills 200" in line for line in captured.output))
        self.assertFalse(any("/api/announcements" in line for line in captured.output))

    @unittest.skipUnless(os.name == "nt", "Windows firewall")
    def test_firewall_rule_for_the_port(self):
        commands, answers = [], iter([1, 0, 0])
        original = relay.run_hidden, relay.is_admin
        self.addCleanup(lambda: (setattr(relay, "run_hidden", original[0]), setattr(relay, "is_admin", original[1])))
        relay.run_hidden = lambda command, timeout=60: commands.append(command) or next(answers)
        relay.is_admin = lambda: False
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            self.assertTrue(relay.allow_firewall(2003))
        self.assertIn("관리자 승인", printed.getvalue())
        self.assertEqual(commands[0], ["netsh", "advfirewall", "firewall", "show", "rule", "name=TerminalNotesRelay-2003"])
        elevated = " ".join(commands[1])
        for expected in ("-Verb RunAs", "name=TerminalNotesRelay-2003", "dir=in", "action=allow", "protocol=TCP", "localport=2003", "profile=any"):
            self.assertIn(expected, elevated)
        commands.clear()
        answers = iter([1, 0, 0])
        relay.is_admin = lambda: True
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertTrue(relay.allow_firewall(2003))
        self.assertEqual(commands[1][:5], ["netsh", "advfirewall", "firewall", "add", "rule"])
        # Nobody answered the prompt (Windows cancels it after about two minutes): say so, with the manual command.
        answers = iter([1, 1223, 1])
        relay.is_admin = lambda: False
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            self.assertFalse(relay.allow_firewall(2003))
        self.assertIn("'예'를 누르지 않았습니다", printed.getvalue())
        self.assertIn("netsh advfirewall firewall add rule name=TerminalNotesRelay-2003", printed.getvalue())
        self.assertIn("이 PC 안에서만", relay.firewall_text(self.config))  # the tests bind 127.0.0.1

    def admin_json(self, method, path, value=None, content_type="application/json"):
        body = None if value is None else json.dumps(value).encode("utf-8")
        status, data, headers = self.call(method, path, body, {"Content-Type": content_type} if body is not None else {}, key=False, admin=True)
        return status, (json.loads(data.decode("utf-8")) if data and "json" in headers.get("Content-Type", "") else data)

    def test_admin_api_for_the_program_window(self):
        # The admin password guards it like the pages.
        self.assertEqual(self.call("GET", "/admin/api/status", key=False)[0], 401)
        status, info = self.admin_json("GET", "/admin/api/status")
        self.assertEqual((status, info["version"], info["counts"]["reports"]), (200, relay.VERSION, 0))
        self.assertIn("settings", info)
        self.assertEqual(self.call("GET", "/api/announcements", key=True)[0], 200)
        clients = self.admin_json("GET", "/admin/api/status")[1]["clients"]
        self.assertTrue(clients and clients[0]["address"] == "127.0.0.1" and "공지" in clients[0]["features"])

        # Reports: list, read, triage, logs, delete.
        logs = b"zip-bytes"
        report_id = json.loads(self.upload_report("앱 버전: 1.1.5\n[어떤 문제가 있었나요]\n저장이 안 됩니다\n", logs)[1])["id"]
        status, listing = self.admin_json("GET", "/admin/api/reports")
        self.assertEqual(listing["statuses"], relay.STATUSES)
        self.assertEqual((listing["reports"][0]["id"], listing["reports"][0]["summary"], listing["reports"][0]["appVersion"]), (report_id, "저장이 안 됩니다", "1.1.5"))
        self.assertRegex(listing["reports"][0]["receivedAt"], r"^\d{4}-\d\d-\d\dT")
        self.assertIn("저장이 안 됩니다", self.admin_json("GET", f"/admin/api/reports/{report_id}")[1]["text"])
        status, updated = self.admin_json("PUT", f"/admin/api/reports/{report_id}", {"status": "checking", "note": "확인 중"})
        self.assertEqual((status, updated["status"], updated["note"]), (200, "checking", "확인 중"))
        self.assertEqual(self.admin_json("PUT", f"/admin/api/reports/{report_id}", {"status": "nope"})[0], 400)
        # A form post (what another site's page could send) is refused; only JSON changes anything.
        self.assertEqual(self.call("PUT", f"/admin/api/reports/{report_id}", b"status=resolved",
                                   {"Content-Type": "application/x-www-form-urlencoded"}, key=False, admin=True)[0], 415)
        self.assertEqual(self.call("GET", f"/admin/api/reports/{report_id}/logs.zip", key=False, admin=True)[1], logs)
        self.assertEqual(self.admin_json("DELETE", f"/admin/api/reports/{report_id}")[0], 200)
        status, missing = self.admin_json("GET", f"/admin/api/reports/{report_id}")
        self.assertEqual((status, missing), (404, "보고서를 찾지 못했습니다."))

        # Skills: list, versions, restore, delete.
        for body in ("첫 버전", "둘째 버전"):
            self.assertEqual(self.api_json("POST", "/api/skills", {"name": "admin-window", "title": "관리 창", "description": "d", "body": body,
                                                                 "author": "editor_one"}, headers={"X-TerminalNotes-User": "editor_one"})[0], 200)
        self.assertEqual([skill["name"] for skill in self.admin_json("GET", "/admin/api/skills")[1]], ["admin-window"])
        detail = self.admin_json("GET", "/admin/api/skills/admin-window")[1]
        self.assertEqual((detail["skill"]["latestVersion"], [item["version"] for item in detail["versions"]]), (2, [2, 1]))
        self.assertIn("첫 버전", self.admin_json("GET", "/admin/api/skills/admin-window/versions/1")[1]["content"])
        self.assertEqual(self.admin_json("POST", "/admin/api/skills/admin-window/restore", {"version": 1})[1]["version"], 3)
        self.assertEqual(self.admin_json("POST", "/admin/api/skills/admin-window/restore", {"version": "1"})[0], 400)
        self.assertEqual(self.admin_json("DELETE", "/admin/api/skills/admin-window")[0], 200)
        self.assertEqual(self.admin_json("GET", "/admin/api/skills")[1], [])

        # Announcements: save as the admin, refuse a stale revision, read an earlier one.
        board = self.admin_json("GET", "/admin/api/announcements")[1]
        status, saved = self.admin_json("PUT", "/admin/api/announcements", {"text": "점검 안내\r\n내일", "revision": board["revision"], "exists": board["exists"]})
        self.assertEqual((status, saved["text"], saved["updatedBy"]), (200, "점검 안내\n내일", "관리자"))
        self.assertEqual(self.admin_json("PUT", "/admin/api/announcements", {"text": "x", "revision": board["revision"], "exists": board["exists"]})[0], 409)
        self.assertEqual(self.admin_json("GET", "/admin/api/nothing")[0], 404)

    def test_admin_lockout(self):
        for _ in range(10):
            self.assertEqual(self.call("GET", "/status", key=False, admin="wrong")[0], 401)
        self.assertEqual(self.call("GET", "/status", key=False, admin=True)[0], 429)

    def test_earlier_relay_files_and_housekeeping(self):
        old = self.root / "reports" / "20200101-000000-0123abcd"
        old.mkdir(parents=True)
        (old / "report.txt").write_text("[어떤 문제가 있었나요]\n오래된 보고", encoding="utf-8")
        (old / "meta.json").write_text(json.dumps({"Id": old.name, "ReceivedAt": "2020-01-01T00:00:00.1234567+00:00",
                                                   "AppVersion": "1.0.1", "Summary": "오래된 보고", "HasLogs": False}), encoding="utf-8")
        kept = self.root / "reports" / "20200102-000000-0123abce"
        shutil.copytree(old, kept)
        meta = json.loads((kept / "meta.json").read_text(encoding="utf-8"))
        meta.update({"Id": kept.name, "Status": "checking"})
        (kept / "meta.json").write_text(json.dumps(meta), encoding="utf-8")
        listed = self.server.reports.all()
        self.assertEqual(listed[-1]["receivedAt"].year, 2020)
        skill = self.root / "skills" / "dotnet-skill"
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text("---\nname: dotnet-skill\ndescription: x\n---\n", encoding="utf-8")
        (skill / "meta.json").write_text(json.dumps({"Name": "dotnet-skill", "Title": "닷넷", "Description": "x", "Author": "a",
                                                     "CreatedAt": "2026-10-22T00:00:00.1234567+00:00", "UpdatedAt": "2026-10-22T00:00:00.1234567+00:00",
                                                     "Sha256": "AB"}), encoding="utf-8")
        self.assertEqual([item["name"] for item in self.api_json("GET", "/api/skills")[1]], ["dotnet-skill"])
        self.assertEqual(self.server.reports.housekeep(90, 0), 1)
        self.assertFalse(old.exists())
        self.assertTrue(kept.exists())

    def test_report_times_never_look_older(self):
        # Housekeeping deletes reports by this time. On Python 3.8-3.10 the .NET relay's shorter fractions did not parse,
        # and a report's meta.json is written just after its text.
        self.addCleanup(setattr, relay, "datetime", relay.datetime)
        relay.datetime = OldFromIsoformat
        for value, micro in (("2026-10-20T14:31:37.2427995+00:00", 242799), ("2026-10-20T14:31:37.24279+00:00", 242790),
                             ("2026-10-20T14:31:37.5Z", 500000), ("2026-10-20T14:31:37+09:00", 0)):
            self.assertEqual(relay.parse_time(value).microsecond, micro, value)
        old = self.root / "reports" / "20200101-000000-0123abd0"
        old.mkdir(parents=True)
        (old / "report.txt").write_text("[어떤 문제가 있었나요]\n오래된 보고", encoding="utf-8")
        (old / "meta.json").write_text(json.dumps({"Id": old.name, "ReceivedAt": "2020-01-01T00:00:00.12345+00:00"}), encoding="utf-8")
        writing = self.root / "reports" / (relay.now().strftime("%Y%m%d-%H%M%S") + "-0123abcf")
        writing.mkdir(parents=True)
        (writing / "report.txt").write_text("[어떤 문제가 있었나요]\n방금 받는 중", encoding="utf-8")
        self.assertEqual(self.server.reports.housekeep(90, 0), 1)
        self.assertFalse(old.exists())
        self.assertTrue(writing.exists())

    def test_skill_version_chosen_by_its_author(self):
        self.server.config.uploads_per_hour = 100  # more shares than the setUp limit of 5
        def share(version=None, body="- Trino로 조회한다.", author="alice"):
            upload = {"name": "trino-guide-alice", "title": "Trino 가이드", "description": "Trino 가이드", "body": body, "author": author}
            if version is not None:
                upload["version"] = version
            status, value, _ = self.api_json("POST", "/api/skills", upload)
            return status, value
        self.assertEqual(share()[1]["version"], 1)
        self.assertEqual(share(5, "- Trino로만 조회한다.")[1]["version"], 5)
        status, message = share(3, "- 검증한다.")
        self.assertEqual(status, 400)
        self.assertIn("v5", message)
        self.assertEqual(share("x", "- 검증한다.")[0], 400)
        self.assertEqual(share(True, "- 검증한다.")[0], 400)
        unchanged = share(9, "- Trino로만 조회한다.")[1]
        self.assertEqual((unchanged["version"], unchanged["changed"]), (5, False))
        self.assertEqual(share("7", "- 검증한다.")[1]["version"], 7)
        self.assertEqual(share(None, "- 결과를 남긴다.")[1]["version"], 8)
        self.assertEqual(share(20, "- 남의 것", author="bob")[0], 409)
        versions = self.api_json("GET", "/api/skills/trino-guide-alice/versions")[1]
        self.assertEqual([item["version"] for item in versions], [8, 7, 5, 1])

    def test_service_ip_from_settings(self):
        def config(*lines):
            return relay.Config(relay.parse_args(sets(f"RELAY_INGEST_KEY={KEY}", f"RELAY_ADMIN_PASSWORD={ADMIN}", *lines)))
        # The relay listens on RELAY_HOST, the address the app is built with; RELAY_BIND overrides it.
        self.assertEqual(config("RELAY_HOST=127.0.0.1").bind, "127.0.0.1")
        self.assertEqual(config("RELAY_HOST=localhost").bind, "127.0.0.1")
        self.assertEqual(config("RELAY_HOST=relay.example.com").bind, "0.0.0.0")
        self.assertEqual(config().bind, "0.0.0.0")
        self.assertEqual(config("RELAY_HOST=127.0.0.1", "RELAY_BIND=0.0.0.0").bind, "0.0.0.0")
        config("RELAY_HOST=127.0.0.1").validate()
        self.assertIn("이 IP로 받습니다", config("RELAY_HOST=127.0.0.1").address_text())
        # 192.0.2.0/24 is documentation space, never this PC's: say so instead of failing to open the port.
        with self.assertRaises(SystemExit) as refused:
            config("RELAY_HOST=192.0.2.10").validate()
        self.assertIn("이 PC의 IP가 아닙니다", str(refused.exception))
        self.assertIn("RELAY_HOST", str(refused.exception))
        everywhere = config("RELAY_HOST=192.0.2.10", "RELAY_BIND=0.0.0.0")
        everywhere.validate()
        self.assertIn("이 PC의 IP가 아닙니다", everywhere.address_text())
        self.assertIn("RELAY_HOST가 비어 있습니다", config().address_text())

    def test_no_app_key_takes_any_app(self):
        port = free_port()
        config = relay.Config(relay.parse_args(sets(
            f"RELAY_PORT={port}", f"RELAY_ADMIN_PASSWORD={ADMIN}", f"RELAY_DATA_DIR={self.root / 'nokey-reports'}",
            f"RELAY_SKILLS_DIR={self.root / 'nokey-skills'}", f"RELAY_PLUGINS_DIR={self.root / 'nokey-plugins'}", f"RELAY_ANNOUNCEMENTS_DIR={self.root / 'nokey-announcements'}",
            f"RELAY_LOG_FILE={self.root / 'nokey.log'}") + ["--bind", "127.0.0.1"]))
        config.validate()
        server = relay.make_server(config)
        threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect)
        try:
            with opener.open(f"http://127.0.0.1:{port}/api/skills", timeout=10) as response:
                self.assertEqual((response.status, json.loads(response.read())), (200, []))
            with self.assertRaises(urllib.error.HTTPError) as admin:
                opener.open(f"http://127.0.0.1:{port}/reports", timeout=10)
            self.assertEqual(admin.exception.code, 401, "the admin pages still ask for the password")
        finally:
            server.shutdown()
            server.server_close()
        # Nothing to fill in: no key, no password, no host (every IP of the PC).
        bare = relay.Config(relay.parse_args(sets(f"RELAY_PORT={port}")))
        bare.validate()
        self.assertEqual((bare.bind, bare.ingest_key, bare.admin_password, bare.editors), ("0.0.0.0", "", "", []))
        self.assertTrue(relay.Announcements(self.root / "bare-announcements", bare.editors).is_editor("anyone"))

    def test_private_skill_is_for_its_author_only(self):
        upload = {"name": "mine-alice", "title": "내 방식", "description": "d", "body": "- 나만 쓴다.", "author": "alice", "private": True,
                  "category": "데이터"}
        self.assertEqual(self.api_json("POST", "/api/skills", upload, headers={"X-TerminalNotes-User": "alice"})[0], 200)
        mine = self.api_json("GET", "/api/skills", headers={"X-TerminalNotes-User": "DOMAIN\\Alice"})[1]
        self.assertEqual([(skill["name"], skill["private"], skill["category"]) for skill in mine], [("mine-alice", True, "데이터")])
        self.assertEqual(self.api_json("GET", "/api/skills", headers={"X-TerminalNotes-User": "bob"})[1], [])
        self.assertEqual(self.api_json("GET", "/api/skills")[1], [], "an app that sends no account sees no private skill")
        for path in ("/api/skills/mine-alice", "/api/skills/mine-alice/versions", "/api/skills/mine-alice/versions/1"):
            self.assertEqual(self.api_json("GET", path, headers={"X-TerminalNotes-User": "bob"})[0], 404, path)
            self.assertEqual(self.api_json("GET", path, headers={"X-TerminalNotes-User": "alice"})[0], 200, path)
        # A new version keeps it private and in its category unless the author says otherwise; the admin pages see it.
        self.api_json("POST", "/api/skills", dict(upload, body="- 나만 쓴다. 표로.", private=None, category=None))
        self.assertEqual([skill["version"] for skill in self.server.skills.all() if skill["private"] and skill["category"] == "데이터"], [2])
        self.api_json("POST", "/api/skills", dict(upload, body="- 나만 쓴다. 표로.", private=False))
        self.assertEqual([skill["name"] for skill in self.api_json("GET", "/api/skills", headers={"X-TerminalNotes-User": "bob"})[1]], ["mine-alice"])

    def test_skill_origin_stays_with_the_skill(self):
        upload = {"name": "from-bob", "title": "받은 스킬", "description": "d", "body": "- 따라 한다.", "author": "alice",
                  "origin": " https://github.com/bob/skills\n"}
        self.assertEqual(self.api_json("POST", "/api/skills", upload)[0], 200)
        latest = lambda: self.api_json("GET", "/api/skills")[1][0]
        self.assertEqual((latest()["origin"], latest()["version"]), ("https://github.com/bob/skills", 1))
        self.assertEqual(self.api_json("GET", "/api/skills/from-bob")[1]["origin"], "https://github.com/bob/skills")
        # Changing it alone makes no new version; a new version without one keeps it, and "" removes it.
        self.api_json("POST", "/api/skills", dict(upload, origin="bob"))
        self.assertEqual((latest()["origin"], latest()["version"]), ("bob", 1))
        changed = dict(upload, body="- 따라 하고 고친다.", origin=None)
        self.api_json("POST", "/api/skills", changed)
        self.assertEqual((latest()["origin"], latest()["version"]), ("bob", 2))
        self.assertEqual(self.server.skills.restore("from-bob", 1), 3)
        self.assertEqual((latest()["origin"], latest()["version"]), ("bob", 3))
        self.assertEqual(self.api_json("POST", "/api/skills", dict(changed, origin="x" * 301))[0], 400)
        self.assertEqual(latest()["origin"], "bob")
        self.api_json("POST", "/api/skills", dict(upload, origin=""))
        self.assertEqual(latest()["origin"], "")

    def test_author_decides_who_sees_a_skill(self):
        alice, bob, master = ({"X-TerminalNotes-User": user} for user in ("alice", "bob", "editor_one"))
        sent = []
        self.server.notifier.send = sent.append
        upload = {"name": "note-alice", "title": "메모", "description": "d", "body": "- 짧게.", "author": "alice", "private": True}
        # Any account may write: bob is no master. Nothing goes out about a private skill.
        self.assertEqual(self.api_json("POST", "/api/skills", upload, headers=alice)[0], 200)
        self.assertEqual(sent, [])
        names = {"names": ["note-alice", "no-such-skill", 7, "BAD NAME"]}
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", names, headers=bob)[1], {"withdrawn": ["note-alice"]})
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", names, headers=master)[1], {"withdrawn": ["note-alice"]})
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", names, headers=alice)[1], {"withdrawn": []})
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", {"names": "x"}, headers=bob)[0], 400)
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", {"names": ["a"] * 501}, headers=bob)[0], 400)
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": False}, headers=bob)[0], 404)
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": "no"}, headers=alice)[0], 400)
        # The author opens it at any time: no new version, and everyone sees it.
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": False}, headers=alice)[1], {"name": "note-alice", "private": False})
        listed = self.api_json("GET", "/api/skills", headers=bob)[1]
        self.assertEqual([(skill["name"], skill["version"], skill["versionCount"], skill["private"]) for skill in listed], [("note-alice", 1, 1, False)])
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", names, headers=bob)[1], {"withdrawn": []})
        # A master changes it, but who sees it stays the author's; bob neither.
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": True}, headers=master)[0], 403)
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": True}, headers=bob)[0], 403)
        edit = dict(upload, body="- 표로.", author="editor_one", edit=True)
        self.assertEqual(self.api_json("POST", "/api/skills", edit, headers=master)[0], 403, "a master's new version may not hide it")
        self.assertEqual(self.api_json("POST", "/api/skills", dict(edit, private=False), headers=master)[1]["version"], 2)
        self.assertEqual(len(sent), 1, "a public skill's new version is announced")
        self.assertEqual(self.api_json("POST", "/api/skills", dict(edit, author="bob", private=None), headers=bob)[0], 403)
        # Private again: a master neither sees nor deletes it; the admin pages still see every skill.
        self.assertEqual(self.api_json("PUT", "/api/skills/note-alice/private", {"private": True}, headers=alice)[0], 200)
        self.assertEqual(self.api_json("GET", "/api/skills", headers=master)[1], [])
        self.assertEqual(self.api_json("DELETE", "/api/skills/note-alice", headers=master)[0], 404)
        self.assertEqual([skill["name"] for skill in self.server.skills.all()], ["note-alice"])
        # A deleted skill is not withdrawn: what PCs applied stays.
        self.assertEqual(self.api_json("DELETE", "/api/skills/note-alice", headers=alice)[0], 200)
        self.assertEqual(self.api_json("POST", "/api/skills/withdrawn", names, headers=bob)[1], {"withdrawn": []})

    def test_data_outlives_the_relay_folder(self):
        code, data = self.root / "code", self.root / "data"
        (code / "skills" / "old-skill").mkdir(parents=True)
        (code / "skills" / "old-skill" / "SKILL.md").write_text("old", encoding="utf-8")
        (code / "announcements").mkdir()
        self.addCleanup(setattr, relay, "SCRIPT_DIR", relay.SCRIPT_DIR)
        relay.SCRIPT_DIR = code
        config = relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={data}")))
        self.assertEqual((config.skills_dir, config.log_file), ((data / "skills").resolve(), (data / "relay.log").resolve()))
        relay.adopt_old_data(config)
        self.assertEqual((config.skills_dir / "old-skill" / "SKILL.md").read_text(encoding="utf-8"), "old")
        self.assertTrue((code / "skills" / "old-skill" / "SKILL.md").exists(), "the old folder is copied, never moved")
        self.assertFalse((data / "skills.copying").exists())
        self.assertFalse(config.announcements_dir.exists(), "an empty old folder is nothing to copy")
        # A new copy of the folder (nothing beside relay.py) keeps the store, and later data is never overwritten.
        (config.skills_dir / "new-skill").mkdir()
        (code / "skills" / "another").mkdir()
        relay.adopt_old_data(config)
        self.assertEqual(sorted(item.name for item in config.skills_dir.iterdir()), ["new-skill", "old-skill"])
        shutil.rmtree(code)
        relay.adopt_old_data(config)
        self.assertEqual(sorted(item.name for item in config.skills_dir.iterdir()), ["new-skill", "old-skill"])
        # Where relay.py already sits in the data folder (the home server) nothing moves; a folder of its own is left alone.
        relay.SCRIPT_DIR = data
        relay.adopt_old_data(relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={data}"))))
        relay.SCRIPT_DIR = code
        own = relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={data}", f"RELAY_SKILLS_DIR={self.root / 'own'}")))
        self.assertEqual(own.skills_dir, self.root / "own")
        relay.adopt_old_data(own)
        self.assertFalse((self.root / "own").exists())

    def test_store_lives_apart_from_the_relay_folder(self):
        # 2.12: one machine-wide data folder with its own name, never the relay's folder or a per-account one.
        with mock.patch.dict(os.environ, {"ProgramData": str(self.root / "ProgramData"), "LOCALAPPDATA": str(self.root / "Local")}):
            if os.name == "nt":
                self.assertEqual(relay.default_data_root(), self.root / "ProgramData" / "TerminalNotesRelayData")
            self.assertNotEqual(relay.default_data_root().name, relay.OLD_DATA_FOLDER)
        # The 2.10–2.11 default folder (the home server also had relay.py in it) is copied forward, never moved or overwritten.
        legacy, data = self.root / "Local" / "TerminalNotesRelay", self.root / "data"
        (legacy / "skills" / "kept-skill").mkdir(parents=True)
        (legacy / "skills" / "kept-skill" / "SKILL.md").write_text("kept", encoding="utf-8")
        (legacy / "announcements").mkdir()
        (legacy / "announcements" / "current.json").write_text("{}", encoding="utf-8")
        config = relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={data}")))
        with mock.patch.object(relay, "legacy_data_roots", return_value=[legacy]):
            copied = relay.adopt_old_data(config)
            self.assertEqual(len(copied), 2, copied)
            self.assertEqual((data / "skills" / "kept-skill" / "SKILL.md").read_text(encoding="utf-8"), "kept")
            self.assertTrue((legacy / "skills" / "kept-skill" / "SKILL.md").exists(), "the old folder stays as it was")
            self.assertTrue((legacy / relay.MOVED_NOTE).exists(), "the old folder says where the data went")
            (legacy / "skills" / "stale").mkdir()
            self.assertEqual(relay.adopt_old_data(config), [], "a data folder with content is never overwritten")
            self.assertEqual(sorted(item.name for item in (data / "skills").iterdir()), ["kept-skill"])
        # A store inside relay.py's folder is called out: replacing that folder would take it along.
        self.addCleanup(setattr, relay, "SCRIPT_DIR", relay.SCRIPT_DIR)
        relay.SCRIPT_DIR = data
        self.assertTrue(config.data_beside_code())
        self.assertTrue(any("주의" in line for line in config.summary()))
        relay.SCRIPT_DIR = self.root / "code"
        self.assertFalse(config.data_beside_code())
        self.assertFalse(any("주의" in line for line in config.summary()))

    def test_settings_from_brings_the_old_store(self):
        # A relay from before 2.10 kept its store beside itself; --settings-from copies it to where its settings now point.
        source = Path(relay.__file__).read_text(encoding="utf-8")
        old, new, data = self.root / "old" / "relay.py", self.root / "new" / "relay.py", self.root / "data"
        (old.parent / "skills" / "legacy-skill").mkdir(parents=True)
        (old.parent / "skills" / "legacy-skill" / "SKILL.md").write_text("legacy", encoding="utf-8")
        new.parent.mkdir()
        old.write_text(source.replace('"RELAY_DATA_ROOT": "",', f'"RELAY_DATA_ROOT": "{data.as_posix()}",'), encoding="utf-8", newline="")
        new.write_text(source, encoding="utf-8", newline="")
        result = subprocess.run([sys.executable, "-B", str(new), "--settings-from", str(old)], capture_output=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", "replace"))
        self.assertEqual((data / "skills" / "legacy-skill" / "SKILL.md").read_text(encoding="utf-8"), "legacy")
        self.assertIn(f'"RELAY_DATA_ROOT": "{data.as_posix()}",', new.read_text(encoding="utf-8"))

    def test_store_backups(self):
        data = self.root / "data"
        config = relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={data}", "RELAY_BACKUP_KEEP=2")))
        self.assertEqual(config.backup_dir, (data / "backups").resolve())
        self.assertIsNone(relay.backup_store(config), "an empty store is never backed up")
        skill = config.skills_dir / "one" / "SKILL.md"
        skill.parent.mkdir(parents=True)
        skill.write_text("v1", encoding="utf-8")
        (config.announcements_dir).mkdir(parents=True)
        (config.announcements_dir / "current.json").write_text('{"text": "공지"}', encoding="utf-8")
        moments = iter(datetime(2026, 11, 5, 1, 0, second) for second in range(0, 60))
        with mock.patch.object(relay, "now", side_effect=lambda: next(moments).replace(tzinfo=relay.timezone.utc)):
            first = relay.backup_store(config)
            self.assertIsNotNone(first)
            self.assertIsNone(relay.backup_store(config), "nothing changed: no new backup")
            for version in ("v2", "v3"):
                skill.write_text(version, encoding="utf-8")
                os.utime(skill, ns=(time.time_ns() + 10_000_000_000, time.time_ns() + 10_000_000_000) if version == "v2"
                         else (time.time_ns() + 20_000_000_000, time.time_ns() + 20_000_000_000))
                self.assertIsNotNone(relay.backup_store(config))
        backups = relay.store_backups(config)
        self.assertEqual(len(backups), 2, "only RELAY_BACKUP_KEEP backups are kept")
        self.assertNotIn(first, backups, "the oldest goes first")
        # The store vanished: the newest backup brings it back; a folder that still has content is left alone.
        shutil.rmtree(config.skills_dir)
        lines = relay.restore_backup(config)
        self.assertEqual(skill.read_text(encoding="utf-8"), "v3")
        self.assertTrue(any(line.startswith("skills:") and "되살렸습니다" in line for line in lines), lines)
        self.assertTrue(any(line.startswith("announcements:") and "그대로 두었습니다" in line for line in lines), lines)

    def test_skill_store_admin_deletes_from_the_app(self):
        upload = {"name": "gone", "title": "지울 스킬", "description": "d", "body": "- x", "author": "alice"}
        self.api_json("POST", "/api/skills", upload)
        self.server.config.skill_admins = ["test_08"]
        self.assertEqual(self.api_json("DELETE", "/api/skills/gone", headers={"X-TerminalNotes-User": "bob"})[0], 403)
        self.assertEqual(self.api_json("DELETE", "/api/skills/gone", headers={"X-TerminalNotes-User": "DOMAIN\\test_08"})[0], 200)
        self.assertEqual(self.api_json("GET", "/api/skills")[1], [])
        self.assertTrue(any((self.root / "skills" / ".trash").iterdir()), "kept 30 days in .trash")
        self.assertEqual(self.api_json("DELETE", "/api/skills/gone", headers={"X-TerminalNotes-User": "test_08"})[0], 404)

    def test_skill_author_and_admins_manage_it(self):
        self.server.config.skill_admins = ["test_07"]
        upload = {"name": "notes", "title": "메모", "description": "d", "body": "- x", "author": "alice"}
        self.assertEqual(self.api_json("POST", "/api/skills", upload, headers={"X-TerminalNotes-User": "alice"})[0], 200)
        # Someone else: the name is taken (sharing), not theirs to change (editing).
        other = dict(upload, body="- y", author="bob")
        self.assertEqual(self.api_json("POST", "/api/skills", other, headers={"X-TerminalNotes-User": "bob"})[0], 409)
        self.assertEqual(self.api_json("POST", "/api/skills", dict(other, edit=True), headers={"X-TerminalNotes-User": "bob"})[0], 403)
        # A store admin adds a version; the skill stays alice's.
        admin = dict(upload, body="- z", author="test_07", edit=True)
        response = self.api_json("POST", "/api/skills", admin, headers={"X-TerminalNotes-User": "DOMAIN\\test_07"})
        self.assertEqual((response[0], response[1]["version"]), (200, 2))
        skill = self.api_json("GET", "/api/skills")[1][0]
        self.assertEqual((skill["author"], skill["version"]), ("alice", 2))
        versions = self.api_json("GET", "/api/skills/notes/versions")[1]
        self.assertEqual([item["author"] for item in versions], ["test_07", "alice"])
        # Its author deletes it; bob may not.
        self.assertEqual(self.api_json("DELETE", "/api/skills/notes", headers={"X-TerminalNotes-User": "bob"})[0], 403)
        self.assertEqual(self.api_json("DELETE", "/api/skills/notes", headers={"X-TerminalNotes-User": "alice"})[0], 200)
        self.assertEqual(self.api_json("GET", "/api/skills")[1], [])

    def test_plugin_store(self):
        self.server.config.skill_admins = ["test_07"]
        code = "export const Hello = async () => ({})\n"
        upload = {"name": "hello-alice", "title": "인사", "description": "d", "code": code, "extension": ".ts", "author": "alice"}
        # Code runs inside OpenCode: only master accounts share it.
        self.assertEqual(self.api_json("POST", "/api/plugins", upload, headers={"X-TerminalNotes-User": "alice"})[0], 403)
        master = {"X-TerminalNotes-User": "DOMAIN\\test_07"}
        response = self.api_json("POST", "/api/plugins", dict(upload, author="test_07"), headers=master)
        self.assertEqual((response[0], response[1]["version"]), (200, 1))
        listed = self.api_json("GET", "/api/plugins")[1]
        self.assertEqual([(p["name"], p["version"], p["extension"], p["author"]) for p in listed], [("hello-alice", 1, ".ts", "test_07")])
        # The same code is no new version; changed code (now .js) is v2, and the old .ts file is gone.
        self.assertFalse(self.api_json("POST", "/api/plugins", dict(upload, author="test_07"), headers=master)[1]["changed"])
        js = dict(upload, author="test_07", code="export default async () => ({})\n", extension=".js", edit=True)
        self.assertEqual(self.api_json("POST", "/api/plugins", js, headers=master)[1]["version"], 2)
        got = self.api_json("GET", "/api/plugins/hello-alice?install=1")[1]
        self.assertEqual((got["version"], got["extension"], got["code"], got["downloads"]), (2, ".js", js["code"], 1))
        self.assertFalse((self.root / "plugins" / "hello-alice" / "plugin.ts").exists())
        self.assertEqual(sorted(p.name for p in (self.root / "plugins" / "hello-alice" / "versions").iterdir()), ["1", "2"])
        # Deleting: its author or a master, not someone else.
        self.assertEqual(self.api_json("DELETE", "/api/plugins/hello-alice", headers={"X-TerminalNotes-User": "bob"})[0], 403)
        self.assertEqual(self.api_json("DELETE", "/api/plugins/hello-alice", headers=master)[0], 200)
        self.assertEqual(self.api_json("GET", "/api/plugins")[1], [])
        self.assertEqual(self.api_json("GET", "/api/plugins/hello-alice")[0], 404)

    def test_github_links(self):
        cases = {
            "https://github.com/team/skills/blob/main/impala/SKILL.md": "https://api.github.com/repos/team/skills/contents/impala/SKILL.md?ref=main",
            "https://github.corp.example/team/skills/tree/dev/impala": "https://github.corp.example/api/v3/repos/team/skills/contents/impala/SKILL.md?ref=dev",
            "https://github.corp.example/team/skills": "https://github.corp.example/api/v3/repos/team/skills/contents/SKILL.md",
            "https://raw.githubusercontent.com/team/skills/main/a/b.md": "https://api.github.com/repos/team/skills/contents/a/b.md?ref=main",
            "https://github.corp.example/raw/team/skills/main/a.md": "https://github.corp.example/api/v3/repos/team/skills/contents/a.md?ref=main",
            "https://raw.github.corp.example/team/skills/main/a.md": "https://github.corp.example/api/v3/repos/team/skills/contents/a.md?ref=main",
        }
        for link, api in cases.items():
            self.assertEqual(relay.github_request(link).full_url, api, link)
        self.assertEqual(relay.github_request(next(iter(cases)), "secret").get_header("Authorization"), "token secret")
        for bad in ("", "github.com/x", "https://github.com/team"):
            with self.assertRaises(relay.RelayError):
                relay.github_request(bad)
        # A GitHub Enterprise stand-in: its API path serves the file; a linked skill follows it.
        site = self.root / "github"
        file = site / "api" / "v3" / "repos" / "team" / "skills" / "contents" / "impala" / "SKILL.md"
        file.parent.mkdir(parents=True)
        file.write_text("---\nname: impala-query\ndescription: \"데이터레이크: Impala\"\n---\n\n# Impala 조회\n\n- Impala로 조회한다.\n", encoding="utf-8")
        handler = lambda *args, **kwargs: http.server.SimpleHTTPRequestHandler(*args, directory=str(site), **kwargs)
        github = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=github.serve_forever, daemon=True).start()
        self.addCleanup(github.server_close)
        self.addCleanup(github.shutdown)
        link = f"http://127.0.0.1:{github.server_address[1]}/team/skills/blob/main/impala/SKILL.md"
        status, fetched, _ = self.api_json("POST", "/api/skills/source", {"source": link})
        self.assertEqual(status, 200)
        name, description, heading, body = relay.split_skill(fetched["content"])
        self.assertEqual((name, description, heading), ("impala-query", "데이터레이크: Impala", "Impala 조회"))
        upload = {"name": "impala-query", "title": heading, "description": description, "body": relay.body_for(body, heading),
                  "author": "alice", "source": link}
        self.assertEqual(self.api_json("POST", "/api/skills", upload)[1]["version"], 1)
        self.assertIsNone(self.server.skills.sync_source("impala-query"), "unchanged on GitHub: no new version")
        file.write_text(file.read_text(encoding="utf-8").replace("조회한다.", "조회하고 검증한다."), encoding="utf-8")
        self.assertEqual(self.server.skills.sync_source("impala-query"), 2)
        latest = self.api_json("GET", "/api/skills/impala-query")[1]
        self.assertEqual((latest["version"], latest["note"], latest["source"], latest["author"], latest["body"]),
                         (2, "깃허브 변경 반영", link, "alice", "- Impala로 조회하고 검증한다."))
        self.assertEqual(self.server.skills.linked(), ["impala-query"])
        file.unlink()
        self.assertIsNone(self.server.skills.sync_source("impala-query"))
        self.assertIn("찾지 못했습니다", self.api_json("GET", "/api/skills")[1][0]["sourceError"])
        self.assertEqual(self.api_json("POST", "/api/skills/source", {"source": link})[0], 400)

    def test_plugin_from_github(self):
        # A plugin's link is its .ts/.js file itself (no SKILL.md is added).
        self.assertEqual(relay.github_request("https://github.corp.example/team/plugins/blob/main/notify.ts", plugin=True).full_url,
                         "https://github.corp.example/api/v3/repos/team/plugins/contents/notify.ts?ref=main")
        for bad in ("https://github.corp.example/team/plugins", "https://github.com/team/plugins/blob/main/README.md"):
            with self.assertRaises(relay.RelayError):
                relay.github_request(bad, plugin=True)
        site = self.root / "github-plugins"
        file = site / "api" / "v3" / "repos" / "team" / "plugins" / "contents" / "notify.ts"
        file.parent.mkdir(parents=True)
        file.write_text("export const Notify = async () => ({})\n", encoding="utf-8")
        handler = lambda *args, **kwargs: http.server.SimpleHTTPRequestHandler(*args, directory=str(site), **kwargs)
        github = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=github.serve_forever, daemon=True).start()
        self.addCleanup(github.server_close)
        self.addCleanup(github.shutdown)
        link = f"http://127.0.0.1:{github.server_address[1]}/team/plugins/blob/main/notify.ts"
        status, fetched, _ = self.api_json("POST", "/api/plugins/source", {"source": link})
        self.assertEqual((status, fetched["extension"]), (200, ".ts"))
        upload = {"name": "notify-alice", "title": "알림", "description": "", "code": fetched["content"], "extension": ".ts",
                  "author": "alice", "source": link}
        master = {"X-TerminalNotes-User": "editor_one"}  # MASTER_USERS of this test relay
        self.assertEqual(self.api_json("POST", "/api/plugins", upload, headers=master)[1]["version"], 1)
        self.assertIsNone(self.server.plugins.sync_source("notify-alice"), "unchanged on GitHub: no new version")
        file.write_text("export const Notify = async () => ({ event: async () => {} })\n", encoding="utf-8")
        self.assertEqual(self.server.plugins.sync_source("notify-alice"), 2)
        latest = self.api_json("GET", "/api/plugins/notify-alice")[1]
        self.assertEqual((latest["version"], latest["note"], latest["source"], latest["author"]), (2, "깃허브 변경 반영", link, "alice"))
        self.assertIn("event", latest["code"])
        self.assertEqual(self.server.plugins.linked(), ["notify-alice"])
        # Shared again from code: "" stops following the link.
        self.api_json("POST", "/api/plugins", dict(upload, code="// mine\n", source="", edit=True), headers=master)
        self.assertEqual((self.api_json("GET", "/api/plugins")[1][0]["source"], self.server.plugins.linked()), ("", []))
        file.unlink()
        self.assertEqual(self.api_json("POST", "/api/plugins/source", {"source": link})[0], 400)

    def test_settings_live_in_relay_py(self):
        # No --set: the values are the file's own SETTINGS (no .env is read, even one beside it).
        self.assertEqual(relay.Config(relay.parse_args([])).port, relay.SETTINGS["RELAY_PORT"])
        with self.assertRaises(SystemExit):
            relay.Config(relay.parse_args(["--set", "no-equals-sign"]))
        # A new relay.py takes an earlier one's SETTINGS and keeps the rest of its own text.
        source = Path(relay.__file__).read_text(encoding="utf-8")
        old, new = self.root / "old" / "relay.py", self.root / "new" / "relay.py"
        for path in (old, new):
            path.parent.mkdir()
        old.write_text(source.replace('"RELAY_PORT": 2003,', '"RELAY_PORT": 6123,').replace('"RELAY_ADMIN_PASSWORD": "",', '"RELAY_ADMIN_PASSWORD": "secret",'),
                       encoding="utf-8", newline="")
        new.write_text(source.replace("VERSION = ", "# newer\nVERSION = "), encoding="utf-8", newline="")
        result = subprocess.run([sys.executable, "-B", str(new), "--settings-from", str(old)], capture_output=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", "replace"))
        moved = new.read_text(encoding="utf-8")
        self.assertIn('"RELAY_PORT": 6123,', moved)
        self.assertIn('"RELAY_ADMIN_PASSWORD": "secret",', moved)
        self.assertIn("# newer\nVERSION = ", moved)
        self.assertEqual(len(moved.splitlines()), len(source.splitlines()) + 1, "only the settings changed (and the marked line)")

    def test_card_excerpt(self):
        self.assertEqual(relay.excerpt("---\nname: x\ndescription: d\n---\n\n# 제목\n\n<!-- note -->\n---\n1. **결과는** `CSV`로 저장한다.\n- 다음"), "결과는 CSV로 저장한다.")
        self.assertEqual(relay.excerpt("# 제목만\n\n```\ncode\n```\n"), "code")
        self.assertEqual(relay.excerpt("# 제목만\n"), "")
        self.assertEqual(relay.excerpt("가" * 200, limit=10), "가" * 9 + "…")

    def test_start_and_stop_find_this_relay(self):
        script = Path(r"C:\Relay 폴더\relay.py")
        listing = "\n".join([
            '4242\t"C:\\Python311\\pythonw.exe" "C:\\Relay 폴더\\relay.py" --set "RELAY_PORT=5081"',
            '4343\t"C:\\Python311\\python.exe" "c:\\relay 폴더\\RELAY.PY" --start',
            '5151\t"C:\\Python311\\python.exe" "C:\\Other\\relay.py"',
            '6161\t"C:\\Python311\\python.exe" relay.py --stop',
            '7171\t"C:\\Python311\\pythonw.exe" "C:\\Relay 폴더\\relay.py" --start-runners',
            'not a pid\tC:\\Relay 폴더\\relay.py', ''])
        self.assertEqual(relay.matching_relays(listing, script, own_pid=4343), [4242])
        self.assertEqual(relay.health_url(self.config), f"http://127.0.0.1:{self.port}/health")
        self.assertTrue(relay.answers(self.config, wait=2))
        self.server.shutdown()
        self.assertFalse(relay.answers(self.config, wait=0))

    @unittest.skipUnless(os.name == "nt", "the start script is for Windows")
    def test_start_runs_hidden_and_stop_ends_it(self):
        # A start folder of its own: the PC's real start at login (another relay's) is never touched.
        # The hidden start keeps this run's --set values, so it never starts on the file's own SETTINGS here.
        folder, port = self.root / "startup", free_port()
        config = relay.Config(relay.parse_args(sets(
            f"RELAY_PORT={port}", "RELAY_BIND=127.0.0.1", f"RELAY_INGEST_KEY={KEY}", f"RELAY_ADMIN_PASSWORD={ADMIN}",
            f"RELAY_DATA_DIR={self.root / 'started-reports'}", f"RELAY_SKILLS_DIR={self.root / 'started-skills'}", f"RELAY_PLUGINS_DIR={self.root / 'started-plugins'}",
            f"RELAY_ANNOUNCEMENTS_DIR={self.root / 'started-announcements'}", f"RELAY_LOG_FILE={self.root / 'started.log'}",
            f"RELAY_BACKUP_DIR={self.root / 'started-backups'}")))
        other = "Set shell = CreateObject(\"WScript.Shell\")\r\nshell.Run \"\"\"C:\\Elsewhere\\relay.py\"\"\", 0, False\r\n"
        with mock.patch.object(relay, "startup_folder", return_value=folder):
            try:
                self.assertEqual(relay.start_relay(config), 0)
                self.assertTrue((folder / relay.STARTUP_SCRIPT).is_file())
                self.assertTrue(relay.running_relays())
                # Started again (after copying in an update): the running one is replaced, still one answering.
                self.assertEqual(relay.start_relay(config), 0)
            finally:
                stopped = relay.stop_running()
            self.assertGreaterEqual(stopped, 1)
            self.assertFalse(relay.answers(config, wait=0))
            relay.uninstall_startup(only_this=True)
            self.assertFalse((folder / relay.STARTUP_SCRIPT).exists())
            (folder / relay.STARTUP_SCRIPT).write_bytes(other.encode("utf-16"))
            relay.uninstall_startup(only_this=True)
            self.assertTrue((folder / relay.STARTUP_SCRIPT).exists(), "another folder's relay keeps its start at login")

    def test_multipart_parser(self):
        fields, files = relay.parse_multipart('multipart/form-data; boundary="x1"',
                                              b'--x1\r\nContent-Disposition: form-data; name="report"\r\n\r\n\xed\x95\x9c\r\n'
                                              b'--x1\r\nContent-Disposition: form-data; name="logs"; filename="l.zip"\r\n\r\n\x00\r\n\x01\r\n--x1--\r\n')
        self.assertEqual(fields, {"report": "한"})
        self.assertEqual(files, {"logs": ("l.zip", b"\x00\r\n\x01")})


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class FakeGitHub(http.server.BaseHTTPRequestHandler):
    """api.github.com for the GitHub relay tests: an account, two repositories, a release and its file in storage,
    owner1/app's release workflow (its project file, latest run, tags) and workflow starts."""
    seen: list = []
    dispatched: list = []
    run_status = "completed"
    choose_version = True

    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"null")
        FakeGitHub.dispatched.append((self.path, body, self.headers.get("Authorization")))
        if self.path == "/repos/owner1/app/actions/runners/registration-token" and self.headers.get("Authorization") == "Bearer gh-token":
            return self._json(201, {"token": "REG-TOKEN", "expires_at": "2026-01-01T01:00:00Z"})
        if self.path == "/repos/owner1/app/actions/workflows/release.yml/dispatches":
            if not FakeGitHub.choose_version and "inputs" in body:
                return self._json(422, {"message": "Unexpected inputs provided: [\"version\"]"})
            self.send_response(204)
            self.end_headers()
            return
        return self._json(404, {"message": "Not Found"})

    def do_DELETE(self):
        FakeGitHub.dispatched.append((self.path, None, self.headers.get("Authorization")))
        if self.path == "/repos/owner1/app/actions/runners/7" and self.headers.get("Authorization") == "Bearer gh-token":
            self.send_response(204)
            self.end_headers()
            return
        return self._json(404, {"message": "Not Found"})

    def _raw(self, text):
        data = text.encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        FakeGitHub.seen.append((self.path, self.headers.get("Authorization"), self.headers.get("Accept")))
        base = f"http://127.0.0.1:{self.server.server_address[1]}"
        authorized = self.headers.get("Authorization") == "Bearer gh-token"
        if self.path == "/user":
            return self._json(200, {"login": "Owner1"}) if authorized else self._json(401, {"message": "Bad credentials"})
        if self.path.startswith("/storage/"):
            if self.headers.get("Authorization"):
                return self._json(400, {"message": "Only one auth mechanism allowed"})
            data = b"release-bytes"
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if not authorized:
            return self._json(404, {"message": "Not Found"})
        if self.path in ("/repos/owner1/app", "/repos/other/tool"):
            return self._json(200, {"name": self.path.split("/")[-1], "full_name": self.path[7:], "url": f"{base}{self.path}", "default_branch": "main"})
        if self.path == "/repos/owner1/app/contents/.github/workflows/release.yml":
            version_input = "REQUESTED_VERSION: ${{ github.event.inputs.version }}\n" if FakeGitHub.choose_version else ""
            return self._raw("on:\n  workflow_dispatch:\n" + version_input + "run: build\\Get-NextVersion.ps1 -Project 'App\\App.csproj'\n"
                             "run: if ($env:OWNER1_ENV_PATH) { Copy-Item $env:OWNER1_ENV_PATH .env }\n")
        if self.path == "/repos/owner1/app/actions/runners?per_page=100":
            return self._json(200, {"total_count": 1, "runners": [{"id": 7, "name": "PC-app", "status": "online", "busy": False,
                                                                    "labels": [{"name": "self-hosted"}, {"name": "Windows"}]}]})
        if self.path == "/repos/owner1/app/contents/App/App.csproj":
            return self._raw("<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>")
        if self.path == "/repos/owner1/app/actions/workflows/release.yml/runs?per_page=1":
            return self._json(200, {"workflow_runs": [{"status": FakeGitHub.run_status, "conclusion": "success" if FakeGitHub.run_status == "completed" else None,
                                                       "event": "push", "created_at": "2026-01-01T00:00:00Z", "run_number": 7,
                                                       "html_url": "https://github.example.com/owner1/app/actions/runs/7"}]})
        if self.path in ("/repos/owner1/app/git/ref/tags/v1.2.3", "/repos/owner1/app/git/ref/tags/v1.2.5"):
            return self._json(200, {"ref": "refs/tags/" + self.path.rsplit("/", 1)[1]})
        if self.path == "/repos/owner1/app/releases/latest":
            return self._json(200, {"tag_name": "v1.2.3", "assets": [{"name": "App-win-x64.zip", "size": 13,
                                                                      "url": f"{base}/repos/owner1/app/releases/assets/7"}]})
        if self.path == "/repos/owner1/app/releases/assets/7":
            if self.headers.get("Accept") != "application/octet-stream":
                return self._json(200, {"name": "App-win-x64.zip"})
            self.send_response(302)
            self.send_header("Location", f"{base}/storage/app.zip?signature=x")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if self.path == "/repos/owner1/app/contents/%EA%B3%B5%EC%A7%80.txt?ref=main":
            data = "공지".encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        return self._json(404, {"message": "Not Found"})

    def _json(self, status, value):
        data = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class LauncherApiTest(unittest.TestCase):
    """i-luncher's app list and notices, and the GitHub relay that serves private releases to apps without a token."""

    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="relay launcher test "))
        legacy = mock.patch.object(relay, "legacy_data_roots", return_value=[])
        legacy.start()
        self.addCleanup(legacy.stop)
        self.github = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FakeGitHub)
        threading.Thread(target=self.github.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        FakeGitHub.seen, FakeGitHub.dispatched, FakeGitHub.run_status, FakeGitHub.choose_version = [], [], "completed", True
        self.port = free_port()
        self.config = relay.Config(relay.parse_args(sets(
            f"RELAY_PORT={self.port}", f"RELAY_ADMIN_PASSWORD={ADMIN}", f"RELAY_INGEST_KEY={KEY}", f"RELAY_DATA_ROOT={self.root}",
            f"RELAY_LOG_FILE={self.root / 'relay.log'}", "RELAY_GITHUB_TOKEN=gh-token",
            f"RELAY_GITHUB_API=http://127.0.0.1:{self.github.server_address[1]}",
            f"RUNNERS_ROOT={self.root / 'runners'}", f"RUNNERS_ENV_DIR={self.root / 'profiles'}") + ["--bind", "127.0.0.1"]))
        (self.root / "runners").mkdir()
        self.server = relay.make_server(self.config)
        threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.github.shutdown()
        self.github.server_close()
        shutil.rmtree(self.root, ignore_errors=True)

    def call(self, method, path, value=None, admin=False, headers=None):
        data = None if value is None else json.dumps(value).encode()
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=dict(headers or {}))
        if data is not None:
            request.add_header("Content-Type", "application/json")
        if admin:
            request.add_header("Authorization", "Basic " + base64.b64encode(f"admin:{ADMIN}".encode()).decode())
        try:
            with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers

    def test_catalog_needs_admin_to_change_and_refuses_stale_saves(self):
        status, body, headers = self.call("GET", "/launcher/catalog")
        self.assertEqual(status, 200, "read without the app key or a password")
        self.assertEqual(json.loads(body), {"schemaVersion": 1, "apps": [], "revision": ""})
        app = {"id": "deskcal", "name": "DeskCal", "repository": "https://github.com/owner1/DeskCal", "category": "일정"}
        self.assertEqual(self.call("PUT", "/launcher/catalog", {"apps": [app], "revision": ""})[0], 401)
        status, body, _ = self.call("PUT", "/launcher/catalog", {"apps": [app], "revision": ""}, admin=True)
        self.assertEqual(status, 200)
        saved = json.loads(body)
        self.assertEqual(saved["apps"], [app])
        status, body, headers = self.call("GET", "/launcher/catalog")
        self.assertEqual(json.loads(body)["apps"][0]["category"], "일정")
        self.assertEqual(headers["ETag"], f'"{saved["revision"]}"')
        self.assertEqual(self.call("GET", "/launcher/catalog", headers={"If-None-Match": headers["ETag"]})[0], 304)
        status, body, _ = self.call("PUT", "/launcher/catalog", {"apps": [], "revision": ""}, admin=True)
        self.assertEqual(status, 409, "a save from an older list is refused")
        for bad in ([{"id": "Bad Id", "name": "x", "repository": "r"}], [app, app], "apps"):
            self.assertEqual(self.call("PUT", "/launcher/catalog", {"apps": bad, "revision": saved["revision"]}, admin=True)[0], 400, bad)
        self.assertTrue((self.root / "launcher" / "catalog.json").is_file(), "kept in the data folder")

    def test_catalog_keeps_the_beta_and_new_badges(self):
        # The master sets them in ApiServer.exe's app list; i-luncher shows them on the cards.
        apps = [{"id": "swift", "name": "Swift", "repository": "https://github.com/owner1/Swift", "beta": True, "new": True},
                {"id": "plain", "name": "Plain", "repository": "https://github.com/owner1/Plain"}]
        status, body, _ = self.call("PUT", "/launcher/catalog", {"apps": apps, "revision": ""}, admin=True)
        self.assertEqual(status, 200, body)
        served = json.loads(self.call("GET", "/launcher/catalog")[1])["apps"]
        self.assertEqual((served[0]["beta"], served[0]["new"]), (True, True))
        self.assertNotIn("beta", served[1], "off means not written")

    def test_notices(self):
        self.assertEqual(json.loads(self.call("GET", "/launcher/notices")[1]), {"text": "", "revision": ""})
        status, body, _ = self.call("PUT", "/launcher/notices", {"text": "제목\r\n본문", "revision": ""}, admin=True)
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["text"], "제목\n본문")
        self.assertEqual(self.call("PUT", "/launcher/notices", {"text": "x", "revision": "old"}, admin=True)[0], 409)

    def test_github_relay_serves_the_token_account_and_rewrites_addresses(self):
        status, body, _ = self.call("GET", "/api/v3/repos/owner1/app/releases/latest")
        self.assertEqual(status, 200, body)
        release = json.loads(body)
        self.assertEqual(release["tag_name"], "v1.2.3")
        self.assertEqual(release["assets"][0]["url"], f"{self.base}/api/v3/repos/owner1/app/releases/assets/7", "asset urls point back here")
        status, body, _ = self.call("GET", "/api/v3/repos/owner1/app/releases/assets/7", headers={"Accept": "application/octet-stream"})
        self.assertEqual((status, body), (200, b"release-bytes"), "the file is streamed through")
        storage = [entry for entry in FakeGitHub.seen if entry[0].startswith("/storage/")]
        self.assertTrue(storage and storage[0][1] is None, "the token is not sent to the file storage")
        status, body, _ = self.call("GET", "/api/v3/repos/owner1/app/contents/" + urllib.parse.quote("공지.txt") + "?ref=main", headers={"Accept": "application/vnd.github.v3.raw"})
        self.assertEqual((status, body.decode()), (200, "공지"))

    def test_github_relay_opens_only_allowed_repositories_and_reads(self):
        self.assertEqual(self.call("GET", "/api/v3/repos/other/tool")[0], 404, "another account's repository is closed")
        self.call("PUT", "/launcher/catalog", {"apps": [{"id": "tool", "name": "Tool", "repository": "https://github.com/other/tool.git"}],
                                               "revision": ""}, admin=True)
        self.assertEqual(self.call("GET", "/api/v3/repos/other/tool")[0], 200, "a repository in the app list is open")
        self.assertEqual(self.call("GET", "/api/v3/user")[0], 404, "only repository reads")
        self.assertEqual(self.call("GET", "/api/v3/repos/owner1/app/issues")[0], 404, "only releases, files and trees")
        self.assertEqual(self.call("PUT", "/api/v3/repos/owner1/app", {})[0], 405, "never writes")
        self.assertTrue(all(entry[0] != "/repos/owner1/app/issues" for entry in FakeGitHub.seen), "refused paths never reach GitHub")

    def test_github_enterprise_host_comes_from_the_api_address(self):
        self.assertEqual((self.config.github_web_host, relay.Config(relay.parse_args([])).github_web_host), ("127.0.0.1:" + str(self.github.server_address[1]), "github.com"))
        enterprise = relay.Config(relay.parse_args(sets(f"RELAY_DATA_ROOT={self.root}", "RELAY_GITHUB_API=https://github.example.com/api/v3/")))
        self.assertEqual(enterprise.github_web_host, "github.example.com")
        store = relay.LauncherStore(self.root / "enterprise", enterprise.github_hosts)
        store.save_catalog([{"id": "a", "name": "A", "repository": "https://github.example.com/Team/App"},
                            {"id": "b", "name": "B", "repository": "https://github.com/owner/b.git"},
                            {"id": "c", "name": "C", "repository": "https://gitlab.example.com/x/c"}], "")
        self.assertEqual(store.repositories(), {"team/app", "owner/b"}, "repositories on the configured GitHub Enterprise host count too")

    # GitHub Actions runners ------------------------------------------------------------------------------

    def runner_folder(self, name, agent, repository):
        folder = self.root / "runners" / name
        folder.mkdir(parents=True)
        (folder / ".runner").write_text(json.dumps({"agentId": 7, "agentName": agent, "gitHubUrl": f"https://github.com/{repository}"}), encoding="utf-8-sig")
        for file in ("config.cmd", "run.cmd"):
            (folder / file).write_text("@echo off\r\n")
        return folder

    def runners(self):
        status, body, _ = self.call("GET", "/admin/api/runners", admin=True)
        self.assertEqual(status, 200, body)
        return json.loads(body)

    def fake(self, *patches):
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    @unittest.skipUnless(os.name == "nt", "the runners are Windows programs")
    def test_runners_are_listed_edited_started_and_stopped(self):
        folder = self.runner_folder("app", "PC-app", "owner1/app")
        (folder / ".env").write_bytes(b"APP_ENV_PATH=C:\\profiles\\app.env\r\n")
        (self.root / "runners" / "env").mkdir()  # the build settings folder is no runner
        launched, stopped = [], []
        self.fake(mock.patch.object(relay, "windows_processes", return_value=[]),
                  mock.patch.object(relay, "scheduled_tasks", return_value=["\\GitHub runner app", "\\Microsoft\\Windows\\GitHub runner x", "\\Other"]),
                  mock.patch.object(relay.Runners, "_launch", lambda runners, path: launched.append(path.name)),
                  mock.patch.object(relay.Runners, "_await", lambda runners, folders, seconds=25: {}),
                  mock.patch.object(relay.Runners, "_stop", lambda runners, path, force: stopped.append((path.name, force)) or ""))
        self.assertEqual(self.call("GET", "/admin/api/runners")[0], 401, "admin only")
        page = self.runners()
        self.assertTrue(page["available"])
        self.assertEqual([runner["id"] for runner in page["runners"]], ["app"])
        runner = page["runners"][0]
        self.assertEqual((runner["name"], runner["repository"], runner["on"], runner["running"], runner["env"]),
                         ("PC-app", "owner1/app", True, False, "APP_ENV_PATH=C:\\profiles\\app.env\n"))
        self.assertEqual((runner["github"]["id"], runner["github"]["status"]), (7, "online"), "its state on GitHub, matched by name")
        self.assertEqual(page["task"], {"name": relay.RUNNERS_TASK, "exists": False, "current": False, "legacy": ["GitHub runner app"]},
                         "only the top folder's per-runner tasks count as earlier ones")
        self.assertEqual(page["repositories"], [], "the app list is empty here")
        for body, code in (({"env": "not a variable"}, 400), ({"env": 5}, 400)):
            self.assertEqual(self.call("PUT", "/admin/api/runners/app", body, admin=True)[0], code, body)
        status, body, _ = self.call("PUT", "/admin/api/runners/app", {"env": "A_ENV_PATH=C:\\x.env\n# note\nDOTNET_CLI_TELEMETRY_OPTOUT=1"}, admin=True)
        self.assertEqual((status, json.loads(body)["message"]), (200, "저장했습니다."), "not running: nothing to restart")
        self.assertEqual((folder / ".env").read_bytes(), b"A_ENV_PATH=C:\\x.env\r\n# note\r\nDOTNET_CLI_TELEMETRY_OPTOUT=1\r\n")
        self.assertEqual(self.call("POST", "/admin/api/runners/app/stop", {}, admin=True)[0], 200)
        self.assertEqual((stopped, self.runners()["runners"][0]["on"]), ([("app", False)], False), "stopped means off: the logon task leaves it")
        self.assertEqual(self.call("POST", "/admin/api/runners/app/start", {}, admin=True)[0], 200)
        self.assertEqual((launched, self.runners()["runners"][0]["on"]), (["app"], True))
        self.assertEqual(self.call("POST", "/admin/api/runners/app/restart", {"force": True}, admin=True)[0], 200)
        self.assertEqual(stopped[-1], ("app", True))
        for path in ("/admin/api/runners/env/start", "/admin/api/runners/..%5Cx/start", "/admin/api/runners/missing/start"):
            self.assertEqual(self.call("POST", path, {}, admin=True)[0], 404, path)
        with mock.patch.object(relay, "is_loopback", return_value=False), mock.patch.object(relay, "is_local_address", return_value=False):
            self.assertEqual(self.call("POST", "/admin/api/runners/app/start", {}, admin=True)[0], 403, "changes only from the server PC")
            self.assertEqual(self.call("GET", "/admin/api/runners", admin=True)[0], 200, "looking is fine from anywhere")
        launched.clear()
        with mock.patch.object(relay.ProcessTable, "under", return_value=[(1, "Runner.Listener.exe")]):
            self.assertEqual(relay.Runners(self.config).start_on(), [], "the logon task starts nothing that runs already")
        self.assertEqual(relay.Runners(self.config).start_on(), ["app 시작"])
        self.assertEqual(launched, ["app"])

    @unittest.skipUnless(os.name == "nt", "the runners are Windows programs")
    def test_runner_is_added_and_removed(self):
        self.runner_folder("app", "PC-app", "owner1/app")
        configured = []

        def prepare(runners, target):
            target.mkdir()
            (target / "config.cmd").write_text("")
            (target / "run.cmd").write_text("")

        def configure(runners, target, url, token, name):
            configured.append((target.name, url, token, name))
            (target / ".runner").write_text(json.dumps({"agentName": name, "gitHubUrl": url}), encoding="utf-8")

        self.fake(mock.patch.object(relay, "windows_processes", return_value=[]), mock.patch.object(relay, "scheduled_tasks", return_value=[]),
                  mock.patch.object(relay.Runners, "_prepare", prepare), mock.patch.object(relay.Runners, "_configure", configure),
                  mock.patch.object(relay.Runners, "_launch", lambda runners, path: None),
                  mock.patch.object(relay.Runners, "_await", lambda runners, folders, seconds=25: {}),
                  mock.patch.dict(os.environ, {"COMPUTERNAME": "PC"}))
        status, body, _ = self.call("GET", "/admin/api/runners/template?repository=owner1/app", admin=True)
        self.assertEqual(status, 200, body)
        self.assertEqual(json.loads(body), {"repository": "owner1/app", "folder": "app-2", "name": "PC-app-2",
                                            "env": f"OWNER1_ENV_PATH={self.root / 'profiles' / 'app.env'}\nDOTNET_CLI_TELEMETRY_OPTOUT=1\n"},
                         "a free folder and name, and the variable the release workflow reads its settings from")
        new = {"repository": "owner1/app", "folder": "app-2", "name": "PC-app", "env": "X=1"}
        self.assertEqual(self.call("POST", "/admin/api/runners", new, admin=True)[0], 409, "the name is taken on GitHub")
        self.assertEqual(self.call("POST", "/admin/api/runners", dict(new, name="PC app"), admin=True)[0], 400)
        self.assertEqual(self.call("POST", "/admin/api/runners", dict(new, folder="app"), admin=True)[0], 409, "the folder exists")
        self.assertEqual(self.call("POST", "/admin/api/runners", dict(new, repository="owner1/none"), admin=True)[0], 404)
        status, body, _ = self.call("POST", "/admin/api/runners", dict(new, name="PC-app-2"), admin=True)
        self.assertEqual(status, 201, body)
        self.assertEqual(json.loads(body)["id"], "app-2")
        self.assertEqual(configured, [("app-2", f"https://{self.config.github_web_host}/owner1/app", "REG-TOKEN", "PC-app-2")],
                         "registered with a fresh token")
        self.assertEqual((self.root / "runners" / "app-2" / ".env").read_bytes(), b"X=1\r\n")
        self.assertEqual([runner["id"] for runner in self.runners()["runners"]], ["app", "app-2"])

        def failing(runners, target, url, token, name):
            raise relay.RelayError(502, "러너를 등록하지 못했습니다: Http response code: NotFound")

        with mock.patch.object(relay.Runners, "_configure", failing):
            self.assertEqual(self.call("POST", "/admin/api/runners", dict(new, folder="app-3", name="PC-app-3"), admin=True)[0], 502)
        self.assertFalse((self.root / "runners" / "app-3").exists(), "a failed registration leaves no folder")
        with mock.patch.object(relay.Runners, "_stop", lambda runners, path, force: ""):
            status, body, _ = self.call("DELETE", "/admin/api/runners/app", admin=True)
        self.assertEqual(status, 200, body)
        self.assertIn(("/repos/owner1/app/actions/runners/7", None, "Bearer gh-token"), FakeGitHub.dispatched, "removed on GitHub")
        self.assertFalse((self.root / "runners" / "app").exists())

    @unittest.skipUnless(os.name == "nt", "the runners are Windows programs")
    def test_one_logon_task_replaces_the_per_runner_tasks(self):
        self.runner_folder("app", "PC-app", "owner1/app")
        registered, deleted = [], []
        self.fake(mock.patch.object(relay, "windows_processes", return_value=[]),
                  mock.patch.object(relay, "scheduled_tasks", return_value=["\\GitHub runner app", "\\i-luncher build runner", "\\Keep me"]),
                  mock.patch.object(relay, "task_definition", lambda name: f'<?xml version="1.0" encoding="UTF-16"?><Task><Command>{name}</Command></Task>'),
                  mock.patch.object(relay, "run_hidden", lambda command, timeout=60: deleted.append(command[3]) or 0),
                  mock.patch.object(relay, "register_task", lambda name, xml: registered.append((name, xml)) or ""),
                  mock.patch.object(relay.Runners, "_launch", lambda runners, path: None),
                  mock.patch.object(relay.Runners, "_await", lambda runners, folders, seconds=25: {}))
        status, body, _ = self.call("POST", "/admin/api/runners/task", {}, admin=True)
        self.assertEqual(status, 200, body)
        self.assertEqual(deleted, ["GitHub runner app", "i-luncher build runner"], "only earlier runner tasks go")
        backups = sorted(path.name for path in (self.root / "runners" / ".backup" / "tasks").iterdir())
        self.assertEqual(backups, ["GitHub runner app.xml", "i-luncher build runner.xml"], "their definitions are kept")
        self.assertEqual(len(registered), 1)
        name, text = registered[0]
        import xml.etree.ElementTree as tree
        task = tree.fromstring(text.split("?>", 1)[1])
        spaces = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
        self.assertEqual(name, relay.RUNNERS_TASK)
        self.assertEqual(task.find("t:Actions/t:Exec/t:Arguments", spaces).text,
                         f'"{Path(relay.__file__).resolve()}" --start-runners' + "".join(f' --set "{item}"' for item in self.config.set_args))
        self.assertIsNotNone(task.find("t:Triggers/t:LogonTrigger", spaces))
        self.assertEqual(task.find("t:Settings/t:ExecutionTimeLimit", spaces).text, "PT0S", "it never ends what it started")

    def releases(self):
        status, body, _ = self.call("GET", "/admin/api/releases", admin=True)
        self.assertEqual(status, 200, body)
        return json.loads(body)["releases"]

    def release(self, repository, version):
        status, body, _ = self.call("POST", "/admin/api/releases", {"repository": repository, "version": version}, admin=True)
        return status, json.loads(body)

    def test_next_version_keeps_every_part_0_to_9(self):
        for latest, floor, expected in (((1, 2, 3), (1, 0, 0), "1.2.4"), ((1, 0, 9), None, "1.1.0"), ((1, 9, 9), None, "2.0.0"),
                                        ((1, 0, 26), None, "1.1.0"), ((1, 0, 3), (2, 0, 0), "2.0.0")):
            self.assertEqual(relay.next_release_version(latest, floor), expected, (latest, floor))

    def test_releases_show_the_next_version_and_start_the_workflow_with_it(self):
        apps = [{"id": "tool", "name": "Tool", "repository": "https://github.com/other/tool", "order": 2},
                {"id": "app", "name": "App", "repository": "https://github.com/owner1/app.git", "order": 1}]
        self.call("PUT", "/launcher/catalog", {"apps": apps, "revision": ""}, admin=True)
        self.assertEqual(self.call("GET", "/admin/api/releases")[0], 401, "admin only")
        app, tool = self.releases()
        self.assertEqual((app["repository"], app["name"], app["latest"], app["floor"], app["next"], app["chooseVersion"]),
                         ("owner1/app", "App", "1.2.3", "1.2.0", "1.2.4", True), "one past the last release, the project file being the floor")
        self.assertEqual((app["run"]["status"], app["run"]["conclusion"], app["error"]), ("completed", "success", ""))
        self.assertIn("릴리스 워크플로", tool["error"], "a repository without the workflow says so")
        for repository, version, code in (("owner1/app", "1.3", 400), ("owner1/app", "1.10.0", 400), ("owner1/app", "1.2.3", 409), ("owner1/app", "0.9.0", 409),
                                          ("owner1/app", "1.2.5", 409), ("owner1/unknown", "9.0.0", 404), ("other/tool", "1.0.0", 409)):
            self.assertEqual(self.release(repository, version)[0], code, (repository, version))
        FakeGitHub.run_status = "in_progress"
        self.assertEqual(self.release("owner1/app", "1.3.0")[0], 409, "one release at a time")
        FakeGitHub.run_status = "completed"
        self.assertEqual(FakeGitHub.dispatched, [], "nothing refused reaches GitHub")
        status, answer = self.release("OWNER1/app", "v1.3.0")
        self.assertEqual((status, answer["version"], answer["ref"]), (202, "1.3.0", "main"), answer)
        self.assertEqual(FakeGitHub.dispatched, [("/repos/owner1/app/actions/workflows/release.yml/dispatches",
                                                  {"ref": "main", "inputs": {"version": "1.3.0"}}, "Bearer gh-token")])
        self.assertEqual(self.release("owner1/app", "1.3.1")[0], 409, "a second start right after is refused")
        self.assertEqual((self.releases()[0]["run"]["status"], self.releases()[0]["run"]["version"]), ("requested", "1.3.0"),
                         "shown as requested until GitHub lists the new run")

    def test_release_workflow_without_a_version_input_takes_only_the_next_version(self):
        FakeGitHub.choose_version = False
        self.call("PUT", "/launcher/catalog", {"apps": [{"id": "app", "name": "App", "repository": "https://github.com/owner1/app"}], "revision": ""}, admin=True)
        self.assertFalse(self.releases()[0]["chooseVersion"])
        self.assertEqual(self.release("owner1/app", "2.0.0")[0], 409)
        self.assertEqual(self.release("owner1/app", "1.2.4")[0], 202)
        self.assertEqual(FakeGitHub.dispatched[-1][1], {"ref": "main"}, "started without inputs")


if __name__ == "__main__":
    unittest.main()
