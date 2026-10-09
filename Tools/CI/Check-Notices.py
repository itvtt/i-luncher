"""Exercise the publication validator using isolated temporary feed files."""

import copy
import importlib.util
import json
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


if __name__ == "__main__":
    unittest.main()
