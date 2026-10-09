"""Validate the launcher notice feed and stage only its public JSON file."""

import argparse
import datetime
import json
import pathlib
import re
import sys


MAX_SOURCE_BYTES = 256 * 1024
NOTICE_FIELDS = {"id", "revision", "title", "body", "publishedAt", "important"}


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path,
                        help="Empty staging directory; only notices.json will be written")
    args = parser.parse_args()
    try:
        feed = validate(args.source)
        if args.output is not None:
            public_json = json.dumps(feed, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
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
