#!/usr/bin/env python3
"""Independent read-back oracle for the bounded plugin-write route."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


class Failure(Exception):
    pass


def under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def safe_file(value: str, label: str) -> Path:
    if not isinstance(value, str) or not os.path.isabs(value) or "\x00" in value:
        raise Failure(f"{label} path")
    path = Path(value).resolve()
    if not under(path, WORKSPACE) or path.is_symlink() or not path.is_file():
        raise Failure(f"{label} outside, linked, or missing")
    return path


def records(data: bytes, start: int, end: int, wanted: int, found: list[dict[str, bytes]]) -> None:
    position = start
    while position < end:
        if end - position < 8:
            raise Failure("truncated record")
        signature = data[position : position + 4].decode("ascii", errors="strict")
        size = struct.unpack_from("<I", data, position + 4)[0]
        record_end = position + (size if signature == "GRUP" else 24 + size)
        if record_end > end or record_end < position:
            raise Failure("record boundary")
        if signature == "GRUP":
            records(data, position + 24, record_end, wanted, found)
        elif signature == "NPC_" and record_end - position >= 24 and struct.unpack_from("<I", data, position + 12)[0] == wanted:
            found.append(subrecords(data, position + 24, record_end))
        position = record_end
    if position != end:
        raise Failure("parent boundary")


def subrecords(data: bytes, start: int, end: int) -> dict[str, bytes]:
    result: dict[str, bytes] = {}
    position = start
    extended: int | None = None
    while position < end:
        if end - position < 6:
            raise Failure("truncated subrecord")
        name = data[position : position + 4].decode("ascii", errors="strict")
        size = struct.unpack_from("<H", data, position + 4)[0]
        position += 6
        if name == "XXXX":
            if size != 4 or end - position < 4:
                raise Failure("invalid XXXX")
            extended = struct.unpack_from("<I", data, position)[0]
            position += 4
            continue
        actual = extended if extended is not None else size
        extended = None
        if actual > end - position:
            raise Failure("subrecord boundary")
        result[name] = data[position : position + actual]
        position += actual
    return result


def npc(path: Path) -> dict[str, bytes]:
    found: list[dict[str, bytes]] = []
    data = path.read_bytes()
    records(data, 0, len(data), 0x800, found)
    if len(found) != 1:
        raise Failure(f"expected one NPC, found {len(found)}")
    return found[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    args = parser.parse_args()
    try:
        source = safe_file(args.source, "source")
        output = safe_file(args.output, "output") if Path(args.output).exists() else None
        evidence = json.loads(safe_file(args.json, "evidence").read_text(encoding="utf-8-sig"))
        if not isinstance(evidence, dict):
            raise Failure("evidence root")
        if args.expect == "refused":
            if evidence.get("applied") is True or output is not None:
                raise Failure("refused write produced an artifact")
            print("RESULT PASS plugin-write refusal")
            return 0
        if evidence.get("applied") is not True or output is None:
            raise Failure("CLI did not apply")
        changes = evidence.get("changes")
        if not isinstance(changes, list) or not any(item.get("field") == "Sex" and item.get("after") == "female" for item in changes if isinstance(item, dict)):
            raise Failure("selected Sex change absent")
        actual = hashlib.sha256(output.read_bytes()).hexdigest()
        if evidence.get("outputSha256") != actual:
            raise Failure("output hash mismatch")
        source_npc, output_npc = npc(source), npc(output)
        if "ACBS" not in source_npc or "ACBS" not in output_npc:
            raise Failure("ACBS missing")
        source_flags = struct.unpack_from("<I", source_npc["ACBS"])[0]
        output_flags = struct.unpack_from("<I", output_npc["ACBS"])[0]
        if not output_flags & 1 or source_npc["ACBS"][4:] != output_npc["ACBS"][4:]:
            raise Failure("selected Female flag or ACBS preservation failed")
        for key, value in source_npc.items():
            if key != "ACBS" and output_npc.get(key) != value:
                raise Failure(f"unselected subrecord drifted: {key}")
        if set(source_npc) != set(output_npc) or source_flags & ~1 != output_flags & ~1:
            raise Failure("unselected binary surface drifted")
        print("RESULT PASS plugin-write independent NPC read-back")
        return 0
    except (Failure, OSError, UnicodeError, ValueError, struct.error) as error:
        print(f"RESULT FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
