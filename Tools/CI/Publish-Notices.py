"""Validate the launcher notice feed and stage only its public JSON file."""

import argparse
import datetime
import hashlib
import json
import pathlib
import re
import subprocess
import sys
import unicodedata


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


def parse_notice_sections(raw):
    if len(raw) > MAX_SOURCE_BYTES:
        raise ValueError("Notice text exceeds 256 KiB")
    text = raw.decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    if not text.strip():
        return []
    blocks = [[]]
    for line in text.split("\n"):
        if line.strip() == "---":
            blocks.append([])
        else:
            blocks[-1].append(line)
    sections, titles = [], set()
    for index, lines in enumerate(blocks, 1):
        while lines and not lines[0].strip():
            lines.pop(0)
        if not lines:
            raise ValueError(f"Notice section {index} is empty; remove extra --- separators")
        title, body = lines[0].strip(), "\n".join(lines[1:]).strip()
        if not body:
            raise ValueError(f"Notice section {index} needs a title and a nonempty body below it")
        if len(title) > 120 or len(body) > 12000:
            raise ValueError(f"Notice section {index} title/body exceeds 120/12000 characters")
        identity = normalized_title(title)
        if identity in titles:
            raise ValueError(f"Notice section {index} has a duplicate normalized title")
        titles.add(identity)
        sections.append((title, body))
    return sections


def normalized_title(title):
    return " ".join(unicodedata.normalize("NFKC", title).casefold().split())


def read_notice_sections(source):
    with pathlib.Path(source).open("rb") as stream:
        raw = stream.read(MAX_SOURCE_BYTES + 1)
    return parse_notice_sections(raw)


def read_notice_text(source):
    sections = read_notice_sections(source)
    if not sections:
        return None
    if len(sections) != 1:
        raise ValueError("Notice text contains multiple sections; use read_notice_sections")
    return sections[0]


def notice_repository(source):
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
    return root, relative, git


def utc_timestamp(timestamp):
    published = datetime.datetime.fromisoformat(timestamp)
    if published.tzinfo is None:
        raise ValueError("Notice commit timestamp must include a time zone")
    return published.astimezone(datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def git_notice_metadata(source):
    root, relative, git = notice_repository(source)
    revision = int(git(root, "rev-list", "--count", "--first-parent", "HEAD", "--", relative))
    timestamp = git(root, "log", "--first-parent", "-1", "--format=%cI", "HEAD", "--", relative)
    if revision < 1 or not timestamp:
        raise ValueError("Notice text must have a committed Git history")
    return revision, utc_timestamp(timestamp)


def managed_text_notices(source, current_sections):
    root, relative, git = notice_repository(source)
    commits = git(root, "log", "--first-parent", "--reverse", "--format=%H %cI", "HEAD", "--", relative).splitlines()
    if not commits:
        raise ValueError("Notice text must have a committed Git history")
    multiple, last_single, legacy_title = False, None, None
    states, active, last_sections = {}, {}, None
    for ordinal, commit in enumerate(commits, 1):
        last_sections = None
        commit_id, timestamp = commit.split(" ", 1)
        # Read the exact blob by object ID; no historical path is interpreted by a shell.
        tree = git(root, "ls-tree", commit_id, "--", relative)
        if not tree:
            sections = []
        else:
            header = tree.split("\t", 1)[0].split()
            if len(header) != 3 or header[1] != "blob":
                continue
            if int(git(root, "cat-file", "-s", header[2])) > MAX_SOURCE_BYTES:
                continue
            result = subprocess.run(["git", "-C", str(root), "cat-file", "blob", header[2]], capture_output=True)
            if result.returncode != 0:
                raise ValueError("Could not read notice Git history blob")
            try:
                sections = parse_notice_sections(result.stdout)
            except ValueError:
                # An invalid historical edit was never publishable. A valid correction
                # must remain publishable without rewriting private repository history.
                continue
        last_sections = sections
        if len(sections) > 1 and not multiple:
            multiple, legacy_title = True, last_single
        published = utc_timestamp(timestamp)
        if not multiple:
            active = {}
            if sections:
                title, body = sections[0]
                last_single = normalized_title(title)
                states[TEXT_NOTICE_ID] = (sections[0], ordinal, published)
                active[TEXT_NOTICE_ID] = states[TEXT_NOTICE_ID]
            continue
        next_active = {}
        for title, body in sections:
            key = normalized_title(title)
            notice_id = TEXT_NOTICE_ID if key == legacy_title else "launcher-text-" + hashlib.sha256(key.encode("utf-8")).hexdigest()
            previous = states.get(notice_id)
            if previous is None or previous[0] != (title, body) or notice_id not in active:
                previous = ((title, body), ordinal, published)
            states[notice_id] = previous
            next_active[notice_id] = previous
        active = next_active
    if current_sections != last_sections:
        raise ValueError("Notice metadata requires the current TXT contents to be committed; commit the TXT first")
    if not multiple and len(current_sections) <= 1:
        # Preserve the original single-notice ID, path revision, and date exactly.
        revision, published = git_notice_metadata(source)
        return [{"id": TEXT_NOTICE_ID, "revision": revision, "title": title, "body": body,
                 "publishedAt": published, "important": True} for title, body in current_sections]
    notices = []
    for title, body in current_sections:
        key = normalized_title(title)
        notice_id = TEXT_NOTICE_ID if key == legacy_title else "launcher-text-" + hashlib.sha256(key.encode("utf-8")).hexdigest()
        _, revision, published = active[notice_id]
        notices.append({"id": notice_id, "revision": revision, "title": title, "body": body,
                        "publishedAt": published, "important": True})
    return notices


def build_feed(source, text_source=None):
    feed = validate(source)
    if text_source is not None:
        if any(notice["id"] == TEXT_NOTICE_ID for notice in feed["notices"]):
            raise ValueError(f"JSON notice ID {TEXT_NOTICE_ID!r} is reserved for the text notice")
        sections = read_notice_sections(text_source)
        managed = managed_text_notices(text_source, sections)
        manual_ids = {notice["id"] for notice in feed["notices"]}
        if any(notice["id"] in manual_ids for notice in managed):
            raise ValueError("A JSON notice ID is reserved for a managed TXT notice title")
        feed["notices"].extend(managed)
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
                        help="Optional UTF-8 notices: first nonblank line title, remaining lines body; standalone --- separates sections")
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
