"""Validate the launcher notice feed and stage only its public JSON file."""

import argparse
import datetime
import json
import pathlib
import re
import subprocess
import sys


MAX_SOURCE_BYTES = 256 * 1024
NOTICE_FIELDS = {"id", "revision", "title", "body", "publishedAt", "important"}
TEXT_NOTICE_ID = "launcher-text-notice"


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate field: {key}")
        result[key] = value
    return result


def reject_constant(value):
    raise ValueError(f"Invalid JSON number: {value}")


def exact_fields(value, fields, label):
    if not isinstance(value, dict) or set(value) != fields:
        raise ValueError(f"{label} must contain exactly: {', '.join(sorted(fields))}")


def validate(source):
    with pathlib.Path(source).open("rb") as stream:
        raw = stream.read(MAX_SOURCE_BYTES + 1)
    if len(raw) > MAX_SOURCE_BYTES:
        raise ValueError("Notice feed exceeds 256 KiB")
    feed = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object,
                      parse_constant=reject_constant)
    return validate_feed(feed)


def validate_feed(feed):
    exact_fields(feed, {"schemaVersion", "notices"}, "Feed")
    if type(feed["schemaVersion"]) is not int or feed["schemaVersion"] != 1:
        raise ValueError("schemaVersion must be integer 1")
    if not isinstance(feed["notices"], list) or len(feed["notices"]) > 100:
        raise ValueError("notices must be an array with at most 100 entries")
    ids = set()
    for index, notice in enumerate(feed["notices"]):
        label = f"Notice {index + 1}"
        exact_fields(notice, NOTICE_FIELDS, label)
        for field, limit in (("id", 80), ("title", 120), ("body", 12000)):
            value = notice[field]
            if not isinstance(value, str) or not value.strip() or len(value) > limit:
                raise ValueError(f"{label}: {field} must be nonempty text up to {limit} characters")
            # Reject unpaired surrogate escapes, which cannot be emitted as UTF-8.
            value.encode("utf-8")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,79}", notice["id"], re.ASCII):
            raise ValueError(f"{label}: invalid id")
        if notice["id"] in ids:
            raise ValueError(f"{label}: duplicate id")
        ids.add(notice["id"])
        if type(notice["revision"]) is not int or not 1 <= notice["revision"] <= 2147483647:
            raise ValueError(f"{label}: revision must be an integer from 1 to 2147483647")
        if type(notice["important"]) is not bool:
            raise ValueError(f"{label}: important must be boolean")
        timestamp = notice["publishedAt"]
        if not isinstance(timestamp, str) or not re.fullmatch(
                r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z", timestamp):
            raise ValueError(f"{label}: publishedAt must be an ISO UTC date ending in Z")
        try:
            datetime.datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        except ValueError as error:
            raise ValueError(f"{label}: publishedAt is not a valid calendar date") from error
    return feed


def read_notice_text(source):
    with pathlib.Path(source).open("rb") as stream:
        raw = stream.read(MAX_SOURCE_BYTES + 1)
    if len(raw) > MAX_SOURCE_BYTES:
        raise ValueError("Notice text exceeds 256 KiB")
    text = raw.decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    if not text.strip():
        return None
    title, separator, body = text.partition("\n")
    title, body = title.strip(), body.strip()
    if not title or not separator or not body:
        raise ValueError("Notice text needs a title on the first line and a nonempty body below it")
    if len(title) > 120 or len(body) > 12000:
        raise ValueError("Notice text title/body exceeds 120/12000 characters")
    return title, body


def git_notice_metadata(source):
    source = pathlib.Path(source).resolve()

    def git(directory, *arguments):
        result = subprocess.run(["git", "--literal-pathspecs", "-C", str(directory), *arguments],
                                capture_output=True, text=True, encoding="utf-8")
        if result.returncode != 0:
            raise ValueError("Could not read notice Git history; use a repository checkout with full history")
        return result.stdout.strip()

    root = pathlib.Path(git(source.parent, "rev-parse", "--show-toplevel")).resolve()
    relative = source.relative_to(root).as_posix()
    if git(root, "rev-parse", "--is-shallow-repository") != "false":
        raise ValueError("Notice metadata requires full Git history (checkout fetch-depth: 0)")
    revision = int(git(root, "rev-list", "--count", "--first-parent", "HEAD", "--", relative))
    timestamp = git(root, "log", "--first-parent", "-1", "--format=%cI", "HEAD", "--", relative)
    if revision < 1 or not timestamp:
        raise ValueError("Notice text must have a committed Git history")
    published = datetime.datetime.fromisoformat(timestamp)
    if published.tzinfo is None:
        raise ValueError("Notice commit timestamp must include a time zone")
    return revision, published.astimezone(datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def build_feed(source, text_source=None):
    feed = validate(source)
    if text_source is not None:
        if any(notice["id"] == TEXT_NOTICE_ID for notice in feed["notices"]):
            raise ValueError(f"JSON notice ID {TEXT_NOTICE_ID!r} is reserved for the text notice")
        content = read_notice_text(text_source)
        if content is not None:
            revision, published = git_notice_metadata(text_source)
            title, body = content
            feed["notices"].append({"id": TEXT_NOTICE_ID, "revision": revision, "title": title,
                                    "body": body, "publishedAt": published, "important": True})
    validate_feed(feed)
    if len(serialize_feed(feed)) > MAX_SOURCE_BYTES:
        raise ValueError("Published notice feed exceeds 256 KiB")
    return feed


def serialize_feed(feed):
    return json.dumps(feed, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=pathlib.Path)
    parser.add_argument("--text", type=pathlib.Path,
                        help="Optional UTF-8 notice text: first line title, remaining lines body")
    parser.add_argument("--output", type=pathlib.Path,
                        help="Empty staging directory; only notices.json will be written")
    args = parser.parse_args()
    try:
        feed = build_feed(args.source, args.text)
        if args.output is not None:
            public_json = serialize_feed(feed)
            if len(public_json) > MAX_SOURCE_BYTES:
                raise ValueError("Published notice feed exceeds 256 KiB")
            args.output.mkdir(parents=True, exist_ok=True)
            if any(args.output.iterdir()):
                raise ValueError("Output staging directory must be empty")
            (args.output / "notices.json").write_bytes(public_json)
        print(f"Validated {len(feed['notices'])} notices")
    except (OSError, ValueError) as error:
        print(f"Notice validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
