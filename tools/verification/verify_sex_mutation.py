#!/usr/bin/env python3
"""Independently verify a dual-game NPC Female-flag mutation."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


class VerificationFailure(Exception):
    pass


def fail(message: str) -> None:
    raise VerificationFailure(message)


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.stat(), "st_file_attributes", 0)
        return path.is_symlink() or bool(attributes & 0x400)
    except OSError:
        return True


def safe_file(value: str, name: str) -> Path:
    if not isinstance(value, str) or not os.path.isabs(value) or "\x00" in value:
        fail(f"{name} path")
    path = Path(value).resolve()
    if not is_under(path, WORKSPACE) or is_reparse(path) or not path.is_file():
        fail(f"{name} outside, linked, or missing")
    return path


def read_cli(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"CLI JSON invalid: {error}")
    if not isinstance(value, dict):
        fail("CLI JSON root")
    return value


def subrecords(data: bytes, start: int, end: int) -> dict[str, bytes]:
    result: dict[str, bytes] = {}
    position = start
    extended: int | None = None
    while position < end:
        if end - position < 6:
            fail("truncated subrecord header")
        signature = data[position : position + 4].decode("ascii", errors="strict")
        size = struct.unpack_from("<H", data, position + 4)[0]
        position += 6
        if signature == "XXXX":
            if size != 4 or end - position < 4:
                fail("invalid XXXX subrecord")
            extended = struct.unpack_from("<I", data, position)[0]
            position += 4
            continue
        actual = extended if extended is not None else size
        extended = None
        if actual > end - position:
            fail(f"subrecord {signature} exceeds NPC boundary")
        result[signature] = data[position : position + actual]
        position += actual
    if position != end:
        fail("NPC payload trailing bytes")
    return result


def find_npc(data: bytes, start: int, end: int, form_id: int, found: list[dict[str, bytes]]) -> None:
    position = start
    while position < end:
        if end - position < 8:
            fail("truncated record header")
        signature = data[position : position + 4].decode("ascii", errors="strict")
        size = struct.unpack_from("<I", data, position + 4)[0]
        record_end = position + (size if signature == "GRUP" else 24 + size)
        if record_end > end or record_end < position:
            fail(f"{signature} exceeds parent")
        if signature == "GRUP":
            if record_end - position < 24:
                fail("truncated group header")
            find_npc(data, position + 24, record_end, form_id, found)
        elif signature == "NPC_" and record_end - position >= 24:
            if struct.unpack_from("<I", data, position + 12)[0] == form_id:
                found.append(subrecords(data, position + 24, record_end))
        position = record_end
    if position != end:
        fail("record boundary mismatch")


def parse_npc(path: Path, form_id: int) -> dict[str, bytes]:
    data = path.read_bytes()
    found: list[dict[str, bytes]] = []
    find_npc(data, 0, len(data), form_id, found)
    if len(found) != 1:
        fail(f"expected one target NPC, found {len(found)}")
    return found[0]


def verify_pair(source: Path, output: Path, form_id: int, expected: str) -> None:
    source_npc = parse_npc(source, form_id)
    output_npc = parse_npc(output, form_id)
    if "ACBS" not in source_npc or "ACBS" not in output_npc:
        fail("ACBS missing")
    source_acbs = source_npc["ACBS"]
    output_acbs = output_npc["ACBS"]
    if len(source_acbs) != len(output_acbs) or len(source_acbs) < 4:
        fail("ACBS shape changed")
    source_flags = struct.unpack_from("<I", source_acbs)[0]
    output_flags = struct.unpack_from("<I", output_acbs)[0]
    actual = "female" if output_flags & 1 else "male"
    if actual != expected:
        fail(f"output sex={actual}, expected={expected}")
    if source_acbs[4:] != output_acbs[4:]:
        fail("ACBS bytes outside flags drifted")
    if (source_flags & ~1) != (output_flags & ~1):
        fail("non-Female ACBS flag drift")
    for signature, source_value in source_npc.items():
        if signature == "ACBS":
            continue
        if output_npc.get(signature) != source_value:
            fail(f"untouched subrecord drifted: {signature}")
    if set(source_npc) != set(output_npc):
        fail("subrecord set changed")


def verify_allowed(args: argparse.Namespace) -> None:
    source = safe_file(args.source, "source")
    output = safe_file(args.output, "output")
    evidence = read_cli(safe_file(args.json, "CLI JSON"))
    if evidence.get("applied") is not True:
        fail("CLI did not report applied=true")
    changes = evidence.get("changes")
    if not isinstance(changes, list) or any(not isinstance(item, dict) for item in changes):
        fail("CLI changes shape")
    if not any(item.get("field") == "Sex" and item.get("after") == args.expected_sex for item in changes):
        fail("CLI change list lacks the expected sex mutation")
    output_hash = evidence.get("outputSha256")
    actual_hash = hashlib.sha256(output.read_bytes()).hexdigest()
    if not isinstance(output_hash, str) or output_hash.casefold() != actual_hash.casefold():
        fail("CLI output hash mismatch")
    verify_pair(source, output, int(args.form_id, 0), args.expected_sex)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--expected-sex", choices=("male", "female"), required=True)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    parser.add_argument("--reason", default="")
    args = parser.parse_args()
    try:
        verify_allowed(args)
    except (VerificationFailure, OSError, UnicodeError, ValueError, struct.error) as error:
        if args.expect == "refused":
            print(f"RESULT PASS sex-mutation refusal={error}")
            return 0
        print(f"RESULT FAIL {error}")
        return 1
    if args.expect == "refused":
        print("RESULT FAIL expected a refusal")
        return 1
    print("RESULT PASS sex-mutation ACBS/non-target preservation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
