"""Exercise the publication validator using isolated temporary feed files."""

import copy
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest


sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    "publish_notices", pathlib.Path(__file__).with_name("Publish-Notices.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)

NOTICE = {"id": "release-1", "revision": 1, "title": "공지",
          "body": "본문\n다음 줄", "publishedAt": "2026-10-09T00:00:00Z", "important": False}


class NoticeChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = pathlib.Path(self.temp.name) / "feed.json"

    def check_feed(self, feed, valid):
        self.source.write_text(json.dumps(feed, ensure_ascii=True), encoding="utf-8")
        if valid:
            self.assertEqual(feed, publisher.validate(self.source))
        else:
            with self.assertRaises(ValueError):
                publisher.validate(self.source)

    def test_valid(self):
        self.check_feed({"schemaVersion": 1, "notices": []}, True)
        self.check_feed({"schemaVersion": 1, "notices": [NOTICE]}, True)
        notice = dict(NOTICE, revision=2147483647, title="😀" * 120,
                      publishedAt="2024-02-29T23:59:59.1234567Z")
        self.check_feed({"schemaVersion": 1, "notices": [notice]}, True)

    def test_notice_fields(self):
        invalid = {
            "id": [None, "", " ", "-bad", "non ascii 한글", "a" * 81],
            "revision": [True, 0, -1, 2147483648, 1.0, "1"],
            "title": [None, "", " \n", "a" * 121, "\ud800"],
            "body": [None, "", " \n", "a" * 12001],
            "important": [None, 0, "false"],
            "publishedAt": [None, "2026-02-29T00:00:00Z", "2026-10-09T24:00:00Z",
                            "2026-10-09T00:00:00+00:00", "2026-10-09", "2026-10-09T00:00:60Z",
                            "2026-10-09T00:00:00.12345678Z", "0000-10-09T00:00:00Z"],
        }
        for field, values in invalid.items():
            for value in values:
                with self.subTest(field=field, value=repr(value)):
                    self.check_feed({"schemaVersion": 1, "notices": [dict(NOTICE, **{field: value})]}, False)
        for field in NOTICE:
            notice = copy.deepcopy(NOTICE)
            del notice[field]
            self.check_feed({"schemaVersion": 1, "notices": [notice]}, False)
        self.check_feed({"schemaVersion": 1, "notices": [dict(NOTICE, extra=1)]}, False)

    def test_feed_structure(self):
        for feed in [None, [], {}, {"schemaVersion": True, "notices": []},
                     {"schemaVersion": 1.0, "notices": []}, {"schemaVersion": 2, "notices": []},
                     {"schemaVersion": 1, "notices": {}},
                     {"schemaVersion": 1, "notices": [], "extra": 1},
                     {"schemaVersion": 1, "notices": [NOTICE, NOTICE]},
                     {"schemaVersion": 1, "notices": [None]},
                     {"schemaVersion": 1, "notices": [dict(NOTICE, id=f"id-{n}") for n in range(101)]}]:
            with self.subTest(feed=repr(feed)[:120]):
                self.check_feed(feed, False)

    def test_raw_input(self):
        for raw in [b'{"schemaVersion":1,"schemaVersion":1,"notices":[]}',
                    b'{"schemaVersion":NaN,"notices":[]}', b'\xff', b'{} trailing',
                    b' ' * (publisher.MAX_SOURCE_BYTES + 1),
                    json.dumps({"schemaVersion": 1, "notices": [NOTICE]}).replace(
                        '"revision": 1', '"revision": 1, "revision": 2').encode("utf-8")]:
            self.source.write_bytes(raw)
            with self.assertRaises(ValueError):
                publisher.validate(self.source)

    def test_staging(self):
        self.check_feed({"schemaVersion": 1, "notices": [NOTICE]}, True)
        output = pathlib.Path(self.temp.name) / "site"
        command = [sys.executable, str(pathlib.Path(__file__).with_name("Publish-Notices.py")),
                   str(self.source), "--output", str(output)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["notices.json"], [file.name for file in output.iterdir()])
        self.assertEqual({"schemaVersion": 1, "notices": [NOTICE]},
                         json.loads((output / "notices.json").read_text(encoding="utf-8")))
        original = (output / "notices.json").read_bytes()
        self.assertEqual(1, subprocess.run(command, capture_output=True).returncode)
        self.assertEqual(original, (output / "notices.json").read_bytes())
        self.source.write_text("{}", encoding="utf-8")
        bad_output = pathlib.Path(self.temp.name) / "invalid-site"
        self.assertEqual(1, subprocess.run(command[:-1] + [str(bad_output)],
                                          capture_output=True).returncode)
        self.assertFalse(bad_output.exists())


class TextNoticeChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.text = self.root / "공지.txt"
        self.source = self.root / "notices.json"
        self.source.write_text(json.dumps({"schemaVersion": 1, "notices": [NOTICE]}), encoding="utf-8")
        self.git("init", "--quiet")
        self.git("config", "user.name", "Notice Checks")
        self.git("config", "user.email", "notice-checks@example.invalid")
        self.git("config", "core.autocrlf", "false")

    def git(self, *arguments, timestamp=None):
        env = os.environ.copy()
        if timestamp:
            env["GIT_AUTHOR_DATE"] = env["GIT_COMMITTER_DATE"] = timestamp
        result = subprocess.run(["git", "-C", str(self.root), *arguments], env=env,
                                capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def commit_text(self, text, timestamp="2026-10-09T09:00:00+09:00"):
        self.text.write_text(text, encoding="utf-8")
        self.git("add", "--", self.text.name)
        self.git("commit", "--quiet", "-m", "Update notice", timestamp=timestamp)

    def test_text_parsing(self):
        self.text.write_bytes("\ufeff  제목 😀  \r\n  본문 \\\" <tag>\r\n\r\n두 번째 줄  \r\n".encode("utf-8"))
        self.assertEqual(("제목 😀", '본문 \\\" <tag>\n\n두 번째 줄'), publisher.read_notice_text(self.text))
        for text in ("", "\ufeff \r\n\t", "제목", "제목\n ", "\n본문", "a" * 121 + "\n본문",
                     "제목\n" + "a" * 12001):
            with self.subTest(text=text[:20]):
                self.text.write_text(text, encoding="utf-8")
                if not text.lstrip("\ufeff").strip():
                    self.assertIsNone(publisher.read_notice_text(self.text))
                else:
                    with self.assertRaises(ValueError):
                        publisher.read_notice_text(self.text)
        self.text.write_text("😀" * 120 + "\n" + "a" * 12000, encoding="utf-8")
        self.assertEqual(12000, len(publisher.read_notice_text(self.text)[1]))
        for raw in (b'\xff', b'a' * (publisher.MAX_SOURCE_BYTES + 1)):
            self.text.write_bytes(raw)
            with self.assertRaises(ValueError):
                publisher.read_notice_text(self.text)

    def test_revision_date_and_rerun(self):
        self.commit_text("제목\n본문")
        first = publisher.build_feed(self.source, self.text)
        self.assertEqual(NOTICE, first["notices"][0])
        managed = first["notices"][1]
        self.assertEqual(publisher.TEXT_NOTICE_ID, managed["id"])
        self.assertEqual(1, managed["revision"])
        self.assertEqual("2026-10-09T00:00:00Z", managed["publishedAt"])
        self.assertTrue(managed["important"])
        (self.root / "unrelated.txt").write_text("unrelated", encoding="utf-8")
        self.git("add", "--", "unrelated.txt")
        self.git("commit", "--quiet", "-m", "Unrelated", timestamp="2026-10-10T00:00:00Z")
        self.assertEqual(first, publisher.build_feed(self.source, self.text))
        self.commit_text("제목\n수정한 본문", "2026-10-10T01:00:00Z")
        self.assertEqual(2, publisher.build_feed(self.source, self.text)["notices"][1]["revision"])
        self.commit_text("", "2026-10-10T02:00:00Z")
        self.assertEqual([NOTICE], publisher.build_feed(self.source, self.text)["notices"])
        self.commit_text("새 제목\n재게시", "2026-10-10T03:00:00Z")
        repost = publisher.build_feed(self.source, self.text)["notices"][1]
        self.assertEqual(4, repost["revision"])
        self.assertEqual("2026-10-10T03:00:00Z", repost["publishedAt"])

    def test_reserved_id_and_feed_limit(self):
        self.commit_text("제목\n본문")
        for text in ("제목\n본문", ""):
            self.text.write_text(text, encoding="utf-8")
            self.source.write_text(json.dumps({"schemaVersion": 1, "notices": [
                dict(NOTICE, id=publisher.TEXT_NOTICE_ID)]}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "reserved"):
                publisher.build_feed(self.source, self.text)
        self.text.write_text("제목\n본문", encoding="utf-8")
        self.source.write_text(json.dumps({"schemaVersion": 1, "notices": [
            dict(NOTICE, id=f"notice-{n}") for n in range(100)]}), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "100"):
            publisher.build_feed(self.source, self.text)

    def test_history_requirements_and_first_parent(self):
        self.text.write_text("제목\n본문", encoding="utf-8")
        with self.assertRaises(ValueError):
            publisher.git_notice_metadata(self.text)
        self.commit_text("제목\n본문")
        branch = self.git("branch", "--show-current")
        self.git("checkout", "--quiet", "-b", "notice-edit")
        self.commit_text("제목\n분기 본문", "2026-10-10T01:00:00Z")
        self.git("checkout", "--quiet", branch)
        self.git("merge", "--quiet", "--no-ff", "notice-edit", "-m", "Merge notice",
                 timestamp="2026-10-10T02:00:00Z")
        revision, date = publisher.git_notice_metadata(self.text)
        self.assertEqual((2, "2026-10-10T02:00:00Z"), (revision, date))
        shallow = self.root / "shallow"
        self.git("clone", "--quiet", "--depth", "1", self.root.as_uri(), str(shallow))
        with self.assertRaisesRegex(ValueError, "full Git history"):
            publisher.git_notice_metadata(shallow / self.text.name)

    def test_text_staging_preserves_sources(self):
        self.commit_text("제목\n본문")
        original_json, original_text = self.source.read_bytes(), self.text.read_bytes()
        output = self.root / "site"
        command = [sys.executable, str(pathlib.Path(__file__).with_name("Publish-Notices.py")),
                   str(self.source), "--text", str(self.text), "--output", str(output)]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["notices.json"], [p.name for p in output.iterdir()])
        self.assertEqual(publisher.build_feed(self.source, self.text), publisher.validate(output / "notices.json"))
        self.assertEqual(original_json, self.source.read_bytes())
        self.assertEqual(original_text, self.text.read_bytes())


if __name__ == "__main__":
    unittest.main()
